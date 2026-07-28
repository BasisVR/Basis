using System.Collections.Concurrent;
using Basis.Network.Core;
using Basis.Network.Core.Compression;
using K4os.Compression.LZ4;
using static SerializableBasis;

namespace Basis.Network
{
    public static class MessageHandler
    {
        // ── Face-data observer (BASIS_EMIT_FACE / BASIS_FACE_OBSERVE_ONLY test modes) ──
        // Counts every avatar frame per downlink path and verifies the counter embedded in
        // the synthetic face payload is strictly increasing per (observer, sender) pair, so
        // a run proves both delivery and ordering of AdditionalAvatarData end to end.
        // Observe-only: join a real server as a spectator and report whether OTHER clients'
        // (e.g. Unity builds under test) additional data is reaching the wire at all.
        public static bool ObserveOnly;

        private static bool Sniffing => MovementSender.EmitFaceData || ObserveOnly;
        public static long PoseOnlyKeyframes;       // even avatar channels (no additional section)
        public static long FaceKeyframesSmall;      // odd byte-id channels (7/9/11/13)
        public static long FaceKeyframesLarge;      // odd ushort-id channels (42/44/46/48)
        public static long FaceDeltas;              // DeltaAvatarChannel frames with the additional bit
        public static long PoseOnlyDeltas;          // DeltaAvatarChannel frames without it
        public static long FaceViaBundleKeyframes;  // inner keyframes inside channel-52 bundles
        public static long FaceViaBundleDeltas;     // inner deltas inside channel-52 bundles
        public static long BundlesParsed;
        public static long UplinkNacksReceived;     // server asked us to re-key (lost uplink baseline)
        public static long MonotonicViolations;     // face counter went backwards for a pair
        public static long ParseFailures;
        public static long LargeSenderFaceReceipts; // receipts whose sender id needs a ushort (>255)

        private static long sLastFaceLogTicks;
        private static readonly ConcurrentDictionary<long, int> sLastCounterPerPair = new();

        public static void ResetStats()
        {
            PoseOnlyKeyframes = 0; FaceKeyframesSmall = 0; FaceKeyframesLarge = 0;
            FaceDeltas = 0; PoseOnlyDeltas = 0; FaceViaBundleKeyframes = 0; FaceViaBundleDeltas = 0;
            BundlesParsed = 0; UplinkNacksReceived = 0; MonotonicViolations = 0; ParseFailures = 0;
            LargeSenderFaceReceipts = 0;
            sLastCounterPerPair.Clear();
        }

        public static long TotalFaceReceipts =>
            Interlocked.Read(ref FaceKeyframesSmall) + Interlocked.Read(ref FaceKeyframesLarge)
            + Interlocked.Read(ref FaceDeltas)
            + Interlocked.Read(ref FaceViaBundleKeyframes) + Interlocked.Read(ref FaceViaBundleDeltas);

        public static string Summary()
        {
            return "[FaceObserver] face: " +
                   $"kfSmall={Interlocked.Read(ref FaceKeyframesSmall)} kfLarge={Interlocked.Read(ref FaceKeyframesLarge)} " +
                   $"delta={Interlocked.Read(ref FaceDeltas)} bundleKf={Interlocked.Read(ref FaceViaBundleKeyframes)} bundleDelta={Interlocked.Read(ref FaceViaBundleDeltas)} " +
                   $"| pose-only: kf={Interlocked.Read(ref PoseOnlyKeyframes)} delta={Interlocked.Read(ref PoseOnlyDeltas)} " +
                   $"| bundles={Interlocked.Read(ref BundlesParsed)} nacks={Interlocked.Read(ref UplinkNacksReceived)} " +
                   $"largeSenderFace={Interlocked.Read(ref LargeSenderFaceReceipts)} " +
                   $"| violations={Interlocked.Read(ref MonotonicViolations)} parseFail={Interlocked.Read(ref ParseFailures)}";
        }

        public static void OnDisconnect(NetPeer peer, DisconnectInfo info)
        {
            BNL.LogError($"Peer {peer.Id} disconnected.");
        }

        public static void OnReceive(ConsoleClientIdentity identity, int clientIndex, NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            if (peer.Id != 0) return;

            switch (channel)
            {
                case BasisNetworkCommons.AuthIdentityChannel:
                    AuthIdentityMessage(identity, peer, reader);
                    return; // already recycled inside
                case BasisNetworkCommons.metaDataChannel:
                    if (identity != null)
                    {
                        identity.Authenticated = true;
                    }
                    break;
                case BasisNetworkCommons.DeltaAvatarChannel:
                    if (reader.AvailableBytes >= 1 && reader.PeekByte() == BasisNetworkCommons.DeltaControlUplinkKeyframeRequest)
                    {
                        Interlocked.Increment(ref UplinkNacksReceived);
                        MovementSender.RequestKeyframe(clientIndex);
                    }
                    else if (Sniffing)
                    {
                        SniffDelta(clientIndex, reader.RawData, reader.Position, reader.AvailableBytes, viaBundle: false);
                    }
                    break;
                case BasisNetworkCommons.PlayerAvatarVeryLowChannel:
                case BasisNetworkCommons.PlayerAvatarLowChannel:
                case BasisNetworkCommons.PlayerAvatarMediumChannel:
                case BasisNetworkCommons.PlayerAvatarHighChannel:
                case BasisNetworkCommons.PlayerAvatarVeryLowLargeChannel:
                case BasisNetworkCommons.PlayerAvatarLowLargeChannel:
                case BasisNetworkCommons.PlayerAvatarMediumLargeChannel:
                case BasisNetworkCommons.PlayerAvatarHighLargeChannel:
                    Interlocked.Increment(ref PoseOnlyKeyframes);
                    NoteVoiceRange(clientIndex, reader, channel);
                    break;
                case BasisNetworkCommons.PlayerAvatarVeryLowAdditionalChannel:
                case BasisNetworkCommons.PlayerAvatarLowAdditionalChannel:
                case BasisNetworkCommons.PlayerAvatarMediumAdditionalChannel:
                case BasisNetworkCommons.PlayerAvatarHighAdditionalChannel:
                case BasisNetworkCommons.PlayerAvatarVeryLowAdditionalLargeChannel:
                case BasisNetworkCommons.PlayerAvatarLowAdditionalLargeChannel:
                case BasisNetworkCommons.PlayerAvatarMediumAdditionalLargeChannel:
                case BasisNetworkCommons.PlayerAvatarHighAdditionalLargeChannel:
                    if (Sniffing)
                    {
                        SniffKeyframe(clientIndex, reader.RawData, reader.Position, reader.AvailableBytes, channel, viaBundle: false);
                    }
                    break;
                case BasisNetworkCommons.CompressedAvatarBundleChannel:
                    if (Sniffing)
                    {
                        SniffBundle(clientIndex, reader);
                    }
                    break;
                case BasisNetworkCommons.AvatarChannel:
                    // HVR's reliable/low-frequency path (handshake, variable definitions,
                    // low-freq updates, high-frequency upgrades) — counting these splits
                    // "HVR networking never started" from "only the high-freq path is dead".
                    if (Sniffing)
                    {
                        SniffAvatarChannel(clientIndex, reader);
                    }
                    break;
                case BasisNetworkCommons.DisconnectionChannel:
                    break;
                default:
                    break;
            }

            reader.Recycle();
        }

        // AvatarChannel (15) receipts, keyed by the first payload byte — for HVR that byte is the
        // packet id (1=WearerReady, 2=RemoteRequestsInitialization, 8=NewVariables,
        // 10..13=UpdatedVariables, 14=UpgradeToHighFrequency, 16=HighFrequencyValues).
        public static readonly ConcurrentDictionary<int, long> AvatarChannelByPacketId = new();
        public static long AvatarChannelTotal;

        private static void SniffAvatarChannel(int clientIndex, NetPacketReader reader)
        {
            try
            {
                var sadm = new ServerAvatarDataMessage();
                sadm.Deserialize(reader);
                Interlocked.Increment(ref AvatarChannelTotal);
                byte[] payload = sadm.avatarDataMessage.payload;
                int packetId = payload != null && payload.Length > 0 ? payload[0] : -1;
                AvatarChannelByPacketId.AddOrUpdate(packetId, 1, (_, n) => n + 1);

                long total = Interlocked.Read(ref AvatarChannelTotal);
                if (total <= 5 || total % 50 == 0)
                {
                    BNL.Log($"[FaceObserver] ch15 from player {sadm.playerIdMessage.playerID} msgIndex={sadm.avatarDataMessage.messageIndex} " +
                            $"packetId={packetId} bytes={payload?.Length ?? 0} | ch15 totals: {string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(AvatarChannelByPacketId, kv => kv.Key), kv => $"id{kv.Key}={kv.Value}"))}");
                }
            }
            catch (System.Exception ex)
            {
                Interlocked.Increment(ref ParseFailures);
                BNL.LogError($"[FaceObserver] ch15 sniff failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Decodes one channel-52 bundle exactly like the Unity client
        /// (BasisNetworkHandleCompressedBundle): [count:1][rawLen:2-LE][LZ4([chan:1][len:2-LE][bytes]*)],
        /// then routes each inner message through the same keyframe/delta sniffers.
        /// </summary>
        private static void SniffBundle(int clientIndex, NetPacketReader reader)
        {
            try
            {
                if (reader.AvailableBytes < 3) return;
                byte[] raw = reader.RawData;
                int pos = reader.Position;
                ushort rawLen = (ushort)(raw[pos + 1] | (raw[pos + 2] << 8));
                int compressedLen = reader.AvailableBytes - 3;
                if (rawLen == 0 || compressedLen <= 0) return;

                byte[] scratch = new byte[rawLen];
                int decoded = LZ4Codec.Decode(raw.AsSpan(pos + 3, compressedLen), scratch.AsSpan(0, rawLen));
                if (decoded != rawLen)
                {
                    Interlocked.Increment(ref ParseFailures);
                    return;
                }
                long bundleNumber = Interlocked.Increment(ref BundlesParsed);

                // Contents of the first few bundles, for the run report (join bursts arrive at the
                // cold VeryLow tier, so early bundles are expected to carry stripped frames).
                if (bundleNumber <= 5)
                {
                    var channelsSeen = new System.Text.StringBuilder();
                    int probe = 0;
                    while (probe + 3 <= decoded)
                    {
                        byte ch = scratch[probe];
                        ushort len = (ushort)(scratch[probe + 1] | (scratch[probe + 2] << 8));
                        if (len == 0 || probe + 3 + len > decoded) break;
                        channelsSeen.Append(ch).Append(':').Append(len).Append(' ');
                        probe += 3 + len;
                    }
                    BNL.Log($"[FaceObserver] bundle -> {channelsSeen}");
                }

                int offset = 0;
                while (offset + 3 <= decoded)
                {
                    byte innerChannel = scratch[offset];
                    ushort msgLen = (ushort)(scratch[offset + 1] | (scratch[offset + 2] << 8));
                    offset += 3;
                    if (msgLen == 0 || offset + msgLen > decoded) break;

                    if (innerChannel == BasisNetworkCommons.DeltaAvatarChannel)
                    {
                        SniffDelta(clientIndex, scratch, offset, msgLen, viaBundle: true);
                    }
                    else if (BasisNetworkCommons.ChannelHasAdditionalData(innerChannel))
                    {
                        SniffKeyframe(clientIndex, scratch, offset, msgLen, innerChannel, viaBundle: true);
                    }
                    else
                    {
                        Interlocked.Increment(ref PoseOnlyKeyframes);
                    }
                    offset += msgLen;
                }
            }
            catch (System.Exception ex)
            {
                Interlocked.Increment(ref ParseFailures);
                BNL.LogError($"[FaceObserver] bundle sniff failed: {ex.Message}");
            }
        }

        /// <summary>Parses one per-quality keyframe frame the way the real client does and records its additional data.</summary>
        private static void SniffKeyframe(int clientIndex, byte[] buffer, int start, int length, byte channel, bool viaBundle)
        {
            try
            {
                var inner = new NetDataReader(buffer, start, start + length);
                var ssm = new ServerSideSyncPlayerMessage();
                ssm.Deserialize(inner, BasisNetworkCommons.GetQualityFromChannel(channel),
                    BasisNetworkCommons.ChannelHasAdditionalData(channel), BasisNetworkCommons.IsLargePlayerIdChannel(channel));
                if (inner.AvailableBytes != 0)
                {
                    Interlocked.Increment(ref ParseFailures);
                    BNL.LogError($"[FaceObserver] keyframe on ch{channel} left {inner.AvailableBytes} unread bytes");
                    return;
                }

                if (viaBundle) Interlocked.Increment(ref FaceViaBundleKeyframes);
                else if (BasisNetworkCommons.IsLargePlayerIdChannel(channel)) Interlocked.Increment(ref FaceKeyframesLarge);
                else Interlocked.Increment(ref FaceKeyframesSmall);

                ReportAdditional(clientIndex, ssm.playerIdMessage.playerID, ssm.avatarSerialization, viaBundle ? "BUNDLE-KF" : "KEYFRAME");
            }
            catch (System.Exception ex)
            {
                Interlocked.Increment(ref ParseFailures);
                BNL.LogError($"[FaceObserver] keyframe sniff failed on ch{channel}: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses a downlink delta frame far enough to reach its additional-data tail (no baseline
        /// needed — the delta body is self-delimiting) and records what rode along.
        /// </summary>
        private static void SniffDelta(int clientIndex, byte[] buffer, int start, int length, bool viaBundle)
        {
            try
            {
                var inner = new NetDataReader(buffer, start, start + length);
                if (!inner.TryGetByte(out byte header) || BasisNetworkCommons.IsDeltaControlHeader(header))
                {
                    return;
                }
                byte quality = BasisNetworkCommons.DeltaHeaderQuality(header);
                var q = (BasisAvatarBitPacking.BitQuality)quality;
                if (!BasisAvatarBitPacking.IsValidQuality(q)) return;
                bool hasAdditional = BasisNetworkCommons.DeltaHeaderHasAdditionalData(header);
                bool largeId = BasisNetworkCommons.DeltaHeaderLargeId(header);

                ushort playerId;
                if (largeId) { if (!inner.TryGetUShort(out playerId)) return; }
                else { if (!inner.TryGetByte(out byte b)) return; playerId = b; }
                if (!inner.TryGetByte(out _)) return; // interval
                if (!inner.TryGetByte(out _)) return; // sequence
                if (!inner.TryGetByte(out _)) return; // baseSeq

                int bodyLen = BasisAvatarDeltaCompression.DeltaBodyLength(inner.RawData, inner.Position, inner.AvailableBytes, q);
                if (bodyLen < 0 || bodyLen > inner.AvailableBytes)
                {
                    Interlocked.Increment(ref ParseFailures);
                    return;
                }
                inner.SkipBytes(bodyLen);

                if (!hasAdditional)
                {
                    Interlocked.Increment(ref PoseOnlyDeltas);
                    if (inner.AvailableBytes != 0) Interlocked.Increment(ref ParseFailures);
                    return;
                }

                var lasm = new LocalAvatarSyncMessage();
                lasm.DeserializeAdditionalData(inner);
                if (inner.AvailableBytes != 0)
                {
                    Interlocked.Increment(ref ParseFailures);
                    BNL.LogError($"[FaceObserver] delta left {inner.AvailableBytes} unread bytes after additional section");
                    return;
                }

                if (viaBundle) Interlocked.Increment(ref FaceViaBundleDeltas);
                else Interlocked.Increment(ref FaceDeltas);
                ReportAdditional(clientIndex, playerId, lasm, viaBundle ? "BUNDLE-DELTA" : "DELTA");
            }
            catch (System.Exception ex)
            {
                Interlocked.Increment(ref ParseFailures);
                BNL.LogError($"[FaceObserver] delta sniff failed: {ex.Message}");
            }
        }

        private static void ReportAdditional(int clientIndex, ushort fromPlayer, LocalAvatarSyncMessage lasm, string path)
        {
            if (lasm.AdditionalAvatarDataSize == 0 || lasm.AdditionalAvatarDatas == null)
            {
                Interlocked.Increment(ref ParseFailures);
                BNL.LogError($"[FaceObserver] {path} frame flagged additional but section was empty");
                return;
            }

            if (fromPlayer > byte.MaxValue) Interlocked.Increment(ref LargeSenderFaceReceipts);

            var ad = lasm.AdditionalAvatarDatas[0];
            int counter = ad.array != null && ad.array.Length >= 4 ? ad.array[2] | (ad.array[3] << 8) : -1;

            // Strictly-increasing check per (observer, sender). Counters wrap at 65536 —
            // treat a huge backward jump as the wrap, anything else as a violation.
            if (counter >= 0)
            {
                long key = ((long)clientIndex << 32) | fromPlayer;
                int last = sLastCounterPerPair.AddOrUpdate(key, counter, (_, prev) =>
                {
                    if (counter <= prev && prev - counter < 30000)
                    {
                        Interlocked.Increment(ref MonotonicViolations);
                        BNL.LogError($"[FaceObserver] counter regressed for observer#{clientIndex} sender {fromPlayer}: {prev} -> {counter} ({path})");
                    }
                    return counter;
                });
                _ = last;
            }

            // Log at most ~1/s so a healthy stream doesn't flood the console.
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long lastLog = Interlocked.Read(ref sLastFaceLogTicks);
            if (now - lastLog < System.Diagnostics.Stopwatch.Frequency) return;
            if (Interlocked.CompareExchange(ref sLastFaceLogTicks, now, lastLog) != lastLog) return;

            BNL.Log($"[FaceObserver] client#{clientIndex} sender={fromPlayer} via {path} counter={counter} linked={lasm.LinkedAvatarIndex} | {Summary()}");
        }

        public static void AuthIdentityMessage(ConsoleClientIdentity identity, NetPeer peer, NetPacketReader reader)
        {
            if (identity != null && identity.TryRespondToChallenge(reader, out NetDataWriter writer))
            {
                peer.Send(writer, BasisNetworkCommons.AuthIdentityChannel, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                BNL.LogError("Failed to respond to auth challenge!");
            }
            reader.Recycle();
        }
        /// <summary>
        /// Voice range, derived from the server's own distance tiering instead of decoding positions.
        /// High and Medium avatar quality are only sent to peers inside MediumQualityDistance, which is
        /// the voice radius, so receiving that tier is proof the sender is close enough to hear. Lower
        /// tiers are ignored: those players are either far away or being quality-shed, and in both
        /// cases they should not be added as voice recipients.
        /// The player id is read straight out of the buffer so the reader stays untouched for the
        /// sniffing paths that run after this.
        /// </summary>
        private static void NoteVoiceRange(int clientIndex, NetPacketReader reader, byte channel)
        {
            if (!Basis.Config.ConfigManager.SimulateVoice) return;

            bool nearTier =
                channel == BasisNetworkCommons.PlayerAvatarHighChannel ||
                channel == BasisNetworkCommons.PlayerAvatarMediumChannel ||
                channel == BasisNetworkCommons.PlayerAvatarHighLargeChannel ||
                channel == BasisNetworkCommons.PlayerAvatarMediumLargeChannel;
            if (!nearTier) return;

            bool large = BasisNetworkCommons.IsLargePlayerIdChannel(channel);
            int pos = reader.Position;
            byte[] raw = reader.RawData;
            if (raw == null || pos + (large ? 2 : 1) > raw.Length) return;

            ushort playerId = large ? (ushort)(raw[pos] | (raw[pos + 1] << 8)) : raw[pos];
            Basis.Network.MovementSender.VoiceSender.NoteAudible(clientIndex, playerId);
        }

    }
}
