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
    internal static class BasisImagePickupLinkProbe
    {
        /// <summary>
        /// Rolling per-slot minima of the round trip. The baseline is the smallest of them, so it tracks a
        /// path that genuinely got slower while refusing to adopt a delay this transfer caused itself: a
        /// single expiring slot would take whatever the round trip happened to be at expiry — congestion
        /// included — and then ramp merrily into it.
        /// </summary>
        private static readonly float[] _baselineSlots = new float[BaselineSlotCount];
        private const int BaselineSlotCount = 4;

        private static int _baselineSlot;
        private static float _baselineSlotExpiry;
        private static float _baseRoundTripMs;
        private static float _rateBytesPerSecond;
        private static float _lastSampleTime;
        private static float _queuingDelayMs;
        private static int _queuedPackets;

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
            Observe(now, peer.RoundTripTime, queued);
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
        /// One control step. While the outgoing queue is deeper than
        /// <see cref="BasisImagePickupSettings.LinkProbeQueueBackoffPackets"/> the rate is halved and
        /// nothing else applies; otherwise it moves toward
        /// <see cref="BasisImagePickupSettings.TargetQueuingDelayMs"/> of queuing delay. Either way it never
        /// leaves the configured floor and ceiling — so a transfer always makes some progress, and a fast
        /// LAN cannot talk the estimate into something the rest of the client will not survive.
        ///
        /// The baseline round trip is whatever the first effective sample reports, so a probe that starts
        /// during congestion measures from a congested baseline until the window re-arms. That is the usual
        /// trade for delay-based control and is why the baseline expires rather than only ever falling.
        /// </summary>
        internal static void Observe(float now, float roundTripMs, int queuedPackets)
        {
            if (_rateBytesPerSecond <= 0f)
                _rateBytesPerSecond = BasisImagePickupSettings.StartingUplinkBudgetBytesPerSecond;

            if (_lastSampleTime <= 0f)
            {
                _lastSampleTime = now;
                return;
            }

            float elapsed = now - _lastSampleTime;
            if (elapsed < BasisImagePickupSettings.LinkProbeIntervalSeconds)
                return;
            _lastSampleTime = now;
            _queuedPackets = queuedPackets;

            if (roundTripMs > 0f)
            {
                UpdateBaseline(now, roundTripMs);
                _queuingDelayMs = Mathf.Max(0f, roundTripMs - _baseRoundTripMs);
            }

            if (queuedPackets >= BacklogLimit(_rateBytesPerSecond))
            {
                // The backlog reading replaces the delay response rather than being averaged with it. A
                // step that both ramped up and then halved would settle at the ramp constant no matter how
                // badly the queue was backed up, since the two cancel; yielding outright decays toward the
                // floor for as long as the transport stays behind.
                _rateBytesPerSecond *= BasisImagePickupSettings.LinkProbeQueueBackoffFactor;
            }
            else
            {
                float target = BasisImagePickupSettings.TargetQueuingDelayMs;
                float offTarget = target > 0f ? (target - _queuingDelayMs) / target : 0f;
                float step = Mathf.Max(
                    BasisImagePickupSettings.LinkProbeRampBytesPerSecond,
                    _rateBytesPerSecond * BasisImagePickupSettings.LinkProbeRampFraction
                );
                _rateBytesPerSecond += step * Mathf.Clamp(offTarget, -1f, 1f) * elapsed;
            }

            _rateBytesPerSecond = Mathf.Clamp(
                _rateBytesPerSecond,
                BasisImagePickupSettings.MinUplinkBudgetBytesPerSecond,
                BasisImagePickupSettings.MaxUplinkBudgetBytesPerSecond
            );
        }
    }
}
