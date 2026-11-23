using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Discovers how much uplink image transfers may actually take before they start hurting anything else.
    ///
    /// A fixed byte budget is either too slow on a good line or still too fast on a bad one, so the rate is
    /// probed instead of assumed: it ramps up while the link stays quiet and backs off as soon as the link
    /// shows strain. Two signals say "strain", and they catch different halves of the problem:
    ///
    /// Round-trip time above the quietest round trip seen recently is the gradient signal. An over-filled
    /// uplink queues before it drops, and that queuing delay is exactly what voice and pose feel first, so
    /// steering to a small target delay is the same thing as staying out of everyone else's way. Because the
    /// round trip is measured against the server, the number also rises when the server is the congested
    /// party — which wants the same response.
    ///
    /// Depth of this client's own outgoing reliable queue is the emergency signal. It moves the moment the
    /// transport stops draining as fast as the chunks arrive, without waiting a round trip for the news, and
    /// it is what the transport shell exposes in place of LiteNetLib's per-peer loss counters.
    ///
    /// This governs the local uplink only. The relay's fan-out budget stays a fixed ceiling — a fast local
    /// line is no reason to make a server forward more.
    /// </summary>
    [AutoStaticsCleanup]
    internal static partial class BasisImagePickupLinkProbe
    {
        /// <summary>
        /// Rolling per-slot minima of the round trip. The baseline is the smallest of them, so it tracks a
        /// path that genuinely got slower while refusing to adopt a delay this transfer caused itself: a
        /// single expiring slot would take whatever the round trip happened to be at expiry — congestion
        /// included — and then ramp merrily into it.
        /// </summary>
        [NoAutoStaticsCleanup] private static readonly float[] _baselineSlots = new float[BaselineSlotCount];
        private const int BaselineSlotCount = 4;

        private static int _baselineSlot;
        private static float _baselineSlotExpiry;
        private static float _baseRoundTripMs;
        private static float _rateBytesPerSecond;
        private static float _lastSampleTime;
        private static float _queuingDelayMs;
        private static int _queuedPackets;
        private static bool _bucketLimited;
        private static bool _backlogEpisode;
        private static int _backoffQueuedPackets;

        internal static float DiscoveredUplinkBytesPerSecond =>
            _rateBytesPerSecond > 0f
                ? _rateBytesPerSecond
                : BasisImagePickupSettings.StartingUplinkBudgetBytesPerSecond;

        internal static float BaseRoundTripMs => _baseRoundTripMs;
        internal static float QueuingDelayMs => _queuingDelayMs;
        internal static void Reset()
        {
            _baseRoundTripMs = 0f;
            _baselineSlotExpiry = 0f;
            _baselineSlot = 0;
            for (int i = 0; i < BaselineSlotCount; i++)
                _baselineSlots[i] = 0f;
            _rateBytesPerSecond = BasisImagePickupSettings.StartingUplinkBudgetBytesPerSecond;
            _lastSampleTime = 0f;
            _queuingDelayMs = 0f;
            _queuedPackets = 0;
            _bucketLimited = false;
            _backlogEpisode = false;
            _backoffQueuedPackets = 0;
        }

        internal static void NoteBucketLimited()
        {
            _bucketLimited = true;
        }

        internal static bool IsBacklogged(int queuedPackets)
        {
            return queuedPackets >= BacklogLimit(DiscoveredUplinkBytesPerSecond);
        }

        internal static bool ServerLinkBacklogged()
        {
            NetPeer peer = BasisNetworkManagement.LocalPlayerPeer;
            if (peer == null)
            {
                return false;
            }
            return IsBacklogged(peer.GetPacketsCountInQueue(BasisNetworkCommons.DirectSceneServerChannel, DeliveryMethod.ReliableOrdered));
        }

        /// <summary>Reads the server link and folds one sample into the estimate.</summary>
        internal static void Tick(float now)
        {
            NetPeer peer = BasisNetworkManagement.LocalPlayerPeer;
            if (peer == null)
            {
                _lastSampleTime = 0f;
                return;
            }

            int queued = peer.GetPacketsCountInQueue(
                BasisNetworkCommons.DirectSceneServerChannel,
                DeliveryMethod.ReliableOrdered
            );
            if (Observe(now, peer.RoundTripTime, queued, _bucketLimited))
            {
                _bucketLimited = false;
            }
        }

        /// <summary>
        /// Queue depth that counts as the transport falling behind at <paramref name="rateBytesPerSecond"/>:
        /// more than one control interval of data still waiting to go out. Scaling with the rate is what
        /// lets one threshold mean the same thing on a 1 Mb/s line and a 25 Gb/s one.
        /// </summary>
        internal static int BacklogLimit(float rateBytesPerSecond)
        {
            float inFlight =
                rateBytesPerSecond
                * BasisImagePickupSettings.LinkProbeIntervalSeconds
                / BasisNetworkCommons.MaxUnfragmentedPayload;
            return Mathf.Max(
                BasisImagePickupSettings.LinkProbeQueueBackoffPackets,
                Mathf.CeilToInt(inFlight)
            );
        }

        /// <summary>
        /// Folds one round trip into the rolling minima and republishes the baseline. Slots rotate on a
        /// timer, each keeping the smallest round trip it saw, so the baseline only rises once every slot in
        /// <see cref="BasisImagePickupSettings.LinkProbeBaselineWindowSeconds"/> of history has risen.
        /// </summary>
        private static void UpdateBaseline(float now, float roundTripMs)
        {
            float slotSeconds =
                BasisImagePickupSettings.LinkProbeBaselineWindowSeconds / BaselineSlotCount;

            if (_baselineSlotExpiry <= 0f || now - _baselineSlotExpiry >= slotSeconds * BaselineSlotCount)
            {
                for (int i = 0; i < BaselineSlotCount; i++)
                    _baselineSlots[i] = roundTripMs;
                _baselineSlot = 0;
                _baselineSlotExpiry = now + slotSeconds;
            }
            else
            {
                while (now >= _baselineSlotExpiry)
                {
                    _baselineSlot = (_baselineSlot + 1) % BaselineSlotCount;
                    _baselineSlots[_baselineSlot] = roundTripMs;
                    _baselineSlotExpiry += slotSeconds;
                }
            }

            if (roundTripMs < _baselineSlots[_baselineSlot])
                _baselineSlots[_baselineSlot] = roundTripMs;

            float lowest = _baselineSlots[0];
            for (int i = 1; i < BaselineSlotCount; i++)
            {
                if (_baselineSlots[i] < lowest)
                    lowest = _baselineSlots[i];
            }
            _baseRoundTripMs = lowest;
        }

        /// <summary>
        /// One control step. A backlog halves the rate once when it appears and again only if the queue keeps
        /// growing, since the queue drains at its own pace and halving on every sample until it empties would
        /// collapse the rate to the floor. Queuing delay over the target brakes at
        /// <see cref="BasisImagePickupSettings.LinkProbeBackoffFraction"/> per second; under it the rate climbs
        /// at <see cref="BasisImagePickupSettings.LinkProbeRampFraction"/> per second, but only when the bucket
        /// was what held sends back, so an idle link cannot talk the estimate up to the ceiling. Returns
        /// whether the sample was taken.
        /// </summary>
        internal static bool Observe(float now, float roundTripMs, int queuedPackets, bool bucketLimited = true)
        {
            if (_rateBytesPerSecond <= 0f)
            {
                _rateBytesPerSecond = BasisImagePickupSettings.StartingUplinkBudgetBytesPerSecond;
            }
            if (_lastSampleTime <= 0f)
            {
                _lastSampleTime = now;
                return false;
            }
            float elapsed = now - _lastSampleTime;
            if (elapsed < BasisImagePickupSettings.LinkProbeIntervalSeconds)
            {
                return false;
            }
            _lastSampleTime = now;
            _queuedPackets = queuedPackets;
            elapsed = Mathf.Min(elapsed, BasisImagePickupSettings.LinkProbeMaxStepSeconds);
            if (roundTripMs > 0f)
            {
                UpdateBaseline(now, roundTripMs);
                _queuingDelayMs = Mathf.Max(0f, roundTripMs - _baseRoundTripMs);
            }
            if (queuedPackets >= BacklogLimit(_rateBytesPerSecond))
            {
                if (!_backlogEpisode || queuedPackets > _backoffQueuedPackets)
                {
                    _rateBytesPerSecond *= BasisImagePickupSettings.LinkProbeQueueBackoffFactor;
                }
                _backlogEpisode = true;
                _backoffQueuedPackets = queuedPackets;
            }
            else
            {
                _backlogEpisode = false;
                float target = BasisImagePickupSettings.TargetQueuingDelayMs;
                float offTarget = Mathf.Clamp(target > 0f ? (target - _queuingDelayMs) / target : 0f, -1f, 1f);
                if (offTarget < 0f)
                {
                    _rateBytesPerSecond *= Mathf.Pow(1f + BasisImagePickupSettings.LinkProbeBackoffFraction, offTarget * elapsed);
                }
                else if (bucketLimited)
                {
                    _rateBytesPerSecond *= Mathf.Pow(1f + BasisImagePickupSettings.LinkProbeRampFraction, offTarget * elapsed);
                }
            }
            _rateBytesPerSecond = Mathf.Clamp(_rateBytesPerSecond, BasisImagePickupSettings.MinUplinkBudgetBytesPerSecond, BasisImagePickupSettings.MaxUplinkBudgetBytesPerSecond);
            return true;
        }
    }
}
