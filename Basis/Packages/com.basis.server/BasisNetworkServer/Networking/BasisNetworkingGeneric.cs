using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using static SerializableBasis;

namespace Basis.Network.Server.Generic
{
    public static class BasisNetworkingGeneric
    {
        private const int MissingPeerReportIntervalSeconds = 10;
        private static readonly long MissingPeerReportIntervalTicks = TimeSpan.TicksPerSecond * MissingPeerReportIntervalSeconds;
        private static long _missingPeerCount;
        private static long _missingPeerNextReportTicks;

        private static void ReportMissingPeer()
        {
            Interlocked.Increment(ref _missingPeerCount);

            long now = DateTime.UtcNow.Ticks;
            long next = Interlocked.Read(ref _missingPeerNextReportTicks);
            if (now < next)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref _missingPeerNextReportTicks, now + MissingPeerReportIntervalTicks, next) != next)
            {
                return;
            }

            long dropped = Interlocked.Exchange(ref _missingPeerCount, 0);
            if (dropped > 0)
            {
                BNL.Log($"Missing Peer! dropped {dropped} targeted message(s) for peers that were not authenticated in the last {MissingPeerReportIntervalSeconds}s.");
            }
        }

        [ThreadStatic]
        private static List<NetPeer> _targetedClients;
        private static List<NetPeer> GetTargetedList()
        {
            if (_targetedClients == null) _targetedClients = new List<NetPeer>();
            else _targetedClients.Clear();
            return _targetedClients;
        }

        [ThreadStatic]
        private static HashSet<ushort> _seenRecipients;
        private static HashSet<ushort> GetSeenSet()
        {
            if (_seenRecipients == null) _seenRecipients = new HashSet<ushort>();
            else _seenRecipients.Clear();
            return _seenRecipients;
        }

        // ── Non-image scene and avatar egress backstop ───────────────────────────────────────────────
        // Per-sender token bucket on the bytes this relay fans out, charged the same way the image
        // governor charges (payload × recipients). Sized by MaxSceneRelayMegabitsPerSecondPerPlayer and
        // MaxAvatarRelayMegabitsPerSecondPerPlayer; 0 turns either off.
        private sealed class SceneEgressBucket { public double Tokens; public long LastTicks; }
        private static readonly ConcurrentDictionary<ushort, SceneEgressBucket> _sceneEgress =
            new ConcurrentDictionary<ushort, SceneEgressBucket>();
        private static readonly ConcurrentDictionary<ushort, SceneEgressBucket> _avatarEgress =
            new ConcurrentDictionary<ushort, SceneEgressBucket>();
        private const double SceneMegabitsToBytes = 125_000.0;
        private const double SceneBurstSeconds = 2.0;

        private static bool SceneEgressAllowed(ushort senderId, long bytes) => EgressAllowed(_sceneEgress, NetworkServer.Configuration?.MaxSceneRelayMegabitsPerSecondPerPlayer ?? 0, senderId, bytes);
        private static bool AvatarEgressAllowed(ushort senderId, long bytes) => EgressAllowed(_avatarEgress, NetworkServer.Configuration?.MaxAvatarRelayMegabitsPerSecondPerPlayer ?? 0, senderId, bytes);
        private static bool PayloadTooLarge(int length)
        {
            int max = NetworkServer.Configuration?.MaxRelayPayloadBytes ?? 0;
            return max > 0 && length > max;
        }
        private static bool EgressAllowed(ConcurrentDictionary<ushort, SceneEgressBucket> buckets, int megabits, ushort senderId, long bytes)
        {
            if (megabits <= 0 || bytes <= 0)
            {
                return true; // disabled, or nothing to charge
            }

            double ratePerSecond = megabits * SceneMegabitsToBytes;
            SceneEgressBucket bucket = buckets.GetOrAdd(senderId, _ => new SceneEgressBucket
            {
                Tokens = ratePerSecond * SceneBurstSeconds,
                LastTicks = DateTime.UtcNow.Ticks,
            });

            lock (bucket)
            {
                long now = DateTime.UtcNow.Ticks;
                double elapsed = (now - bucket.LastTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsed > 0)
                {
                    bucket.LastTicks = now;
                    double ceiling = ratePerSecond * SceneBurstSeconds;
                    bucket.Tokens = Math.Min(ceiling, bucket.Tokens + ratePerSecond * elapsed);
                }

                // Gate on having credit rather than on the whole charge fitting, and let the bucket go
                // negative: a single wide fan-out can exceed the burst, and demanding it fit would
                // stall that sender forever. The long-run average is still exactly the budget.
                if (bucket.Tokens <= 0)
                {
                    return false;
                }
                bucket.Tokens -= bytes;
                return true;
            }
        }

        /// <summary>Drops a departed peer's scene-egress bucket so a recycled id starts clean.</summary>
        public static void RemovePeerSceneEgress(int peerId)
        {
            if (peerId >= 0 && peerId <= ushort.MaxValue)
            {
                _sceneEgress.TryRemove((ushort)peerId, out _);
                _avatarEgress.TryRemove((ushort)peerId, out _);
            }
        }

        public static void HandleScene(NetPacketReader Reader, DeliveryMethod DeliveryMethod, NetPeer sender, byte broadcastChannel = BasisNetworkCommons.SceneChannel)
        {
            SceneDataMessage SceneDataMessage = new SceneDataMessage();
            SceneDataMessage.Deserialize(Reader);
            Reader.Recycle();

            byte[] payload = SceneDataMessage.payload;
            int payloadLength = (payload != null) ? payload.Length : 0;

            // Observe only — the relay below is untouched, so a cache miss, a rejection or a
            // malformed payload can never interfere with the live send.
            bool isImageTraffic = BasisNetworkImageCache.IsImageTraffic(SceneDataMessage.messageIndex);
            if (isImageTraffic && BasisNetworkImageCache.IsAnimationPayload(payload, payloadLength) && BasisNetworkImageCache.IsGifBlockedFor(sender))
            {
                return;
            }
            if (isImageTraffic)
            {
                BasisNetworkImageCache.Observe(
                    (ushort)sender.Id,
                    payload,
                    payloadLength,
                    SceneDataMessage.recipients,
                    SceneDataMessage.recipientsSize
                );
            }

            // Server-side floor under the client's own pacing. The sharer decides how to spend its
            // budget — only it knows how the fan-out splits between relayed and direct peers — but a
            // client that ignores the budget entirely must not be able to spend the server's egress
            // on our behalf. Charged on fan-out, because that is what the relay actually costs.
            //
            // The untargeted branch below broadcasts to the snapshot minus the sender, so the fan-out
            // is one less than its length.
            NetPeer[] egressSnapshot = NetworkServer.PeerSnapshot;
            int fanOut = SceneDataMessage.recipientsSize != 0
                ? SceneDataMessage.recipientsSize
                : (egressSnapshot != null ? egressSnapshot.Length - 1 : 0);
            long egressBytes = (long)payloadLength * Math.Max(1, fanOut);

            if (isImageTraffic)
            {
                // Image traffic has its own governor (advertised budget + per-owner buckets).
                if (!BasisImageBandwidthGovernor.TryConsumeEgress((ushort)sender.Id, egressBytes))
                {
                    return;
                }
            }
            else if (PayloadTooLarge(payloadLength) || !SceneEgressAllowed((ushort)sender.Id, egressBytes))
            {
                // Everything else on this channel is interactive scene state measured in tens of
                // bytes; this backstop caps a modified client that broadcasts arbitrary scene
                // payloads to the whole room.
                return;
            }

            ServerSceneDataMessage serverSceneDataMessage = new ServerSceneDataMessage
            {
                sceneDataMessage = new RemoteSceneDataMessage()
                {
                    messageIndex = SceneDataMessage.messageIndex,
                    payload = payload,
                    payloadLength = payloadLength
                },
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)sender.Id,
                }
            };

            byte Channel = broadcastChannel;
            NetDataWriter Writer = NetworkServer.RentWriter();
            serverSceneDataMessage.Serialize(Writer);
            if (SceneDataMessage.recipientsSize != 0)
            {
                List<NetPeer> targetedClients = GetTargetedList();
                HashSet<ushort> seen = GetSeenSet();

                int recipientsLength = SceneDataMessage.recipientsSize;
                for (int index = 0; index < recipientsLength; index++)
                {
                    ushort recipient = SceneDataMessage.recipients[index];
                    if (!seen.Add(recipient))
                    {
                        continue;
                    }
                    if (NetworkServer.AuthenticatedPeers.TryGetValue(recipient, out NetPeer client))
                    {
                        targetedClients.Add(client);
                    }
                    else
                    {
                        ReportMissingPeer();
                    }
                }

                if (targetedClients.Count > 0)
                {
                    NetworkServer.BroadcastMessageToClients(Writer, Channel, ref targetedClients, DeliveryMethod);
                }
            }
            else
            {
                NetworkServer.BroadcastMessageToClients(Writer, Channel, sender, NetworkServer.PeerSnapshot, DeliveryMethod);
            }
            NetworkServer.ReturnWriter(Writer);
            serverSceneDataMessage.sceneDataMessage.Release();
        }
        public static void HandleAvatar(NetPacketReader Reader, DeliveryMethod DeliveryMethod, NetPeer sender, byte broadcastChannel = BasisNetworkCommons.AvatarChannel)
        {
            AvatarDataMessage avatarDataMessage = new AvatarDataMessage();
            avatarDataMessage.Deserialize(Reader);
            Reader.Recycle();
            int avatarPayloadLength = avatarDataMessage.payload?.Length ?? 0;
            NetPeer[] avatarSnapshot = NetworkServer.PeerSnapshot;
            int avatarFanOut = avatarDataMessage.recipientsSize != 0 ? avatarDataMessage.recipientsSize : (avatarSnapshot != null ? avatarSnapshot.Length - 1 : 0);
            if (PayloadTooLarge(avatarPayloadLength) || !AvatarEgressAllowed((ushort)sender.Id, (long)avatarPayloadLength * Math.Max(1, avatarFanOut)))
            {
                return;
            }
            ServerAvatarDataMessage serverAvatarDataMessage = new ServerAvatarDataMessage
            {
                avatarDataMessage = new RemoteAvatarDataMessage()
                {
                    messageIndex = avatarDataMessage.messageIndex,
                    payload = avatarDataMessage.payload,
                    PlayerIdMessage = avatarDataMessage.PlayerIdMessage,
                    AvatarLinkIndex = avatarDataMessage.AvatarLinkIndex,
                },
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)sender.Id
                }
            };
            byte Channel = broadcastChannel;
            NetDataWriter Writer = NetworkServer.RentWriter();
            serverAvatarDataMessage.Serialize(Writer);
            if (avatarDataMessage.recipientsSize != 0)
            {
                List<NetPeer> targetedClients = GetTargetedList();
                HashSet<ushort> seen = GetSeenSet();

                int recipientsLength = avatarDataMessage.recipientsSize;
                for (int index = 0; index < recipientsLength; index++)
                {
                    ushort recipient = avatarDataMessage.recipients[index];
                    if (!seen.Add(recipient))
                    {
                        continue;
                    }
                    if (NetworkServer.AuthenticatedPeers.TryGetValue(recipient, out NetPeer client))
                    {
                        targetedClients.Add(client);
                    }
                    else
                    {
                        ReportMissingPeer();
                    }
                }

                if (targetedClients.Count > 0)
                {
                    NetworkServer.BroadcastMessageToClients(Writer, Channel, ref targetedClients, DeliveryMethod);
                }
            }
            else
            {
                NetworkServer.BroadcastMessageToClients(Writer, Channel, sender, NetworkServer.PeerSnapshot, DeliveryMethod);
            }
            NetworkServer.ReturnWriter(Writer);
        }
    }
}
