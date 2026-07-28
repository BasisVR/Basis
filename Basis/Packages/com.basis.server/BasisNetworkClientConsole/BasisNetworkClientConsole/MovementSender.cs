using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.Networking.Compression;
using BasisNetworkClientConsole;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static SerializableBasis;

namespace Basis.Network
{
    public static class MovementSender
    {
        public static Quaternion Rotation = new Quaternion(0, 0, 0, 1);

        public static Vector3[] PlayersCurrentPosition;
        public static PlayerData[] ActivePlayerData;

        // Animation timer — shared across all players, per-player phase offsets provide variety
        private static readonly Stopwatch AnimTimer = Stopwatch.StartNew();

        // Precomputed byte offsets into the packet for High quality
        private static readonly int RotationRegionOffset = BasisAvatarBitPacking.WritePosition; // 12
        private static readonly int ScaleOffset = BasisAvatarBitPacking.WritePosition
            + BasisBoneRotationCompression.RotationBytes(BitQuality.High);
        // After flip: this is the HIPS WORLD rotation slot (was "body rotation"
        // = root world rotation). 7-byte smallest-three quaternion.
        private static readonly int HipsRotationOffset = ScaleOffset + BasisAvatarBitPacking.WriteScale;
        // 6 bytes — 3 signed shorts at ±1m. Default zero bytes already decode
        // to zero delta thanks to the signed encoding, so we don't need to
        // write anything synthetic here for fake clients.
        private static readonly int HipsLocalDeltaOffset = HipsRotationOffset + BasisAvatarBitPacking.WriteRotation;
        // 7-byte smallest-three quaternion for hips local-rotation delta.
        // Default zero bytes do NOT decode to identity (the encoding treats
        // them as a saturated-low drop-X quat) — so the test client writes an
        // explicit identity once at init.
        private static readonly int HipsLocalRotationOffset = HipsLocalDeltaOffset + BasisAvatarBitPacking.WriteHipsDelta;

        public struct PlayerData
        {
            public NetDataWriter Writer;
            public LocalAvatarSyncMessage Message;
            public byte SequenceByte;
            public float PhaseOffset;
            // v42 uplink delta state — mirrors the real client: a full keyframe every
            // UplinkKeyframeIntervalMs on the High channel (which the server snapshots as the
            // baseline), dirty-mask deltas against it on DeltaAvatarChannel in between.
            public byte[] Baseline;
            public byte BaselineSeq;
            public bool HasBaseline;
            public long LastKeyframeTicks;
            public byte[] DeltaScratch;
            public bool ForceKeyframe;
            // Per-sender strictly-increasing face counter embedded in the synthetic
            // AdditionalAvatarData payload; the observer verifies monotonicity per sender.
            public int FaceCounter;
            public AdditionalAvatarData[] FaceScratch;
        }

        // Send v42 uplink deltas like a real client (false = legacy all-keyframe uploads).
        public static bool UseUplinkDeltas = true;
        private const int UplinkKeyframeIntervalMs = 500;
        private static readonly long UplinkKeyframeIntervalTicks = Stopwatch.Frequency * UplinkKeyframeIntervalMs / 1000;

        // Attach a synthetic AdditionalAvatarData (face-tracking shaped: [16][timing][values...])
        // to every send, mirroring how the real client ships HVR high-frequency variables. The
        // observer side (MessageHandler) logs when these arrive, so a server+2-client run proves
        // additional data end-to-end over real UDP. Off by default — this is a load tester.
        public static bool EmitFaceData = false;

        // BASIS_FACE_SPACING: pin client i at (i * spacing, 1, 0) and stop the random walk, so a
        // run can hold every sender/receiver pair at an exact distance tier (High ≤10m,
        // Medium ≤30m, Low ≤50m, VeryLow beyond) to prove tier-dependent stripping live.
        public static float PinSpacingMeters = 0f;

        /// <summary>Server NACK (DeltaControlUplinkKeyframeRequest) → next send is a keyframe.</summary>
        public static void RequestKeyframe(int index)
        {
            if (ActivePlayerData == null || index < 0 || index >= ActivePlayerData.Length) return;
            ActivePlayerData[index].ForceKeyframe = true;
        }

        // Precompute compressed scale once; reused for all messages.
        private static readonly ushort CompressedScale = CompressScaleOnce(1f);

        public static void Initialize(int clientCount)
        {
            PlayersCurrentPosition = new Vector3[clientCount];
            ActivePlayerData = new PlayerData[clientCount];

            for (int i = 0; i < clientCount; i++)
            {
                PlayersCurrentPosition[i] = PinSpacingMeters > 0f
                    ? new Vector3 { x = i * PinSpacingMeters, y = 1f, z = 0f }
                    : Randomizer.GetSpawnPosition(Basis.Config.ConfigManager.SpawnRadiusMeters);
                ActivePlayerData[i] = Generate(i);
            }
        }
        /// <summary>
        /// Builds a starting payload. Pass the player's index so the pose carries the position that
        /// player was actually spawned at — the server reads the join pose to decide what quality
        /// every other player should be sent at, so a mismatch here makes the whole join snapshot
        /// tier from the wrong place.
        /// </summary>
        public static PlayerData Generate(int playerIndex = -1)
        {
            var message = new LocalAvatarSyncMessage
            {
                DataQualityLevel = (byte)BitQuality.High,
                AdditionalAvatarDatas = null,
                AdditionalAvatarDataSize = 0,
                LinkedAvatarIndex = 0,
                array = new byte[ClientManager.Size],
            };

            // Per-player random phase offset so idle animations aren't synchronized
            float phase = (float)(Random.Shared.NextDouble() * MathF.PI * 2f);

            Scripts.Networking.Compression.Vector3 spawn =
                (playerIndex >= 0 && PlayersCurrentPosition != null && playerIndex < PlayersCurrentPosition.Length)
                    ? PlayersCurrentPosition[playerIndex]
                    : Randomizer.GetSpawnPosition(Basis.Config.ConfigManager.SpawnRadiusMeters);

            // Build the full initial payload (position, bone rotations, scale, hips rotation)
            WriteInitialPayload(ref message, phase, spawn);

            return new PlayerData
            {
                Writer = new NetDataWriter(),
                Message = message,
                PhaseOffset = phase,
            };
        }

        private static void WriteInitialPayload(ref LocalAvatarSyncMessage message, float phase, Scripts.Networking.Compression.Vector3 spawn)
        {
            // Make sure buffer is correct size for High
            int size = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
            if (message.array == null || message.array.Length != size)
                message.array = new byte[size];

            double time = AnimTimer.Elapsed.TotalSeconds;

            // 1) Position (after the recent flip this is the HIPS WORLD position)
            int offset = 0;
            WritePosition(spawn, ref message.array, ref offset);

            // 2) Bone rotations: natural standing pose with idle animation
            FakePoseGenerator.WriteBoneRotations(message.array, RotationRegionOffset, BitQuality.High, time, phase);

            // 3) Scale
            WriteScaleUShort(CompressedScale, message.array, ScaleOffset);

            // 4) Hips world rotation: slight body orientation
            FakePoseGenerator.WriteCompressedHipsRotation(message.array, HipsRotationOffset, time, phase);

            // 5) Hips local-position delta — left as zero bytes; the receiver's
            //    signed-short decode treats that as a zero delta, so no synthetic
            //    write is required for fake clients.

            // 6) Hips local-rotation delta — must be an explicit identity, since
            //    smallest-three on all-zero bytes does NOT decode to identity.
            //    Set once here; the test client never animates this channel.
            WriteIdentityQuaternion(message.array, HipsLocalRotationOffset);
        }

        /// <summary>
        /// Writes the identity quaternion (0,0,0,1) into a 7-byte smallest-three
        /// slot. Identity has w as the largest component (= 1), so:
        ///   index byte = 3 (drop w)
        ///   three small components = 0 → quantized = midpoint = 32768
        /// </summary>
        private static void WriteIdentityQuaternion(byte[] dst, int offset)
        {
            // QuantizeSmall(0f) = midpoint = 32768 = 0x8000 → lo 0x00, hi 0x80
            dst[offset] = 3;
            dst[offset + 1] = 0x00;
            dst[offset + 2] = 0x80;
            dst[offset + 3] = 0x00;
            dst[offset + 4] = 0x80;
            dst[offset + 5] = 0x00;
            dst[offset + 6] = 0x80;
        }
        private static void WriteScaleUShort(ushort value, byte[] buffer, int byteOffset)
        {
            buffer[byteOffset + 0] = (byte)value;
            buffer[byteOffset + 1] = (byte)(value >> 8);
        }
        /// <summary>
        /// Voice traffic, which the harness previously left out entirely — a silent crowd is not what
        /// a real instance costs the server. Basis culls voice on the CLIENT: each player tells the
        /// server which peers are close enough to hear it, and the server routes only to that list.
        /// So the simulation has to do the same — build a recipient list from the spawn positions
        /// inside the audible radius, then transmit Opus-sized frames on the voice channel.
        ///
        /// Only a slice of the crowd talks at once, because everyone talking simultaneously is not a
        /// realistic load; it is a synthetic worst case that would swamp the measurement of everything
        /// else. Raise VoiceTalkingPercent to 100 if that worst case is what you want to see.
        /// </summary>
        public static class VoiceSender
        {
            private static ushort[][] _recipients;
            private static bool[] _participates;
            private static bool[] _talking;
            private static bool[] _joinsChorus;
            private static double[] _nextSwitchMs;
            private static byte[] _seq;
            private static int[] _silentUnits;
            private static long[] _micCursor;
            private static byte[] _frame;
            private static int _built;

            // Shared clock: chorus events are global, and the driver threads each have their own
            // stopwatch origin, so their elapsed values cannot be compared against one another.
            private static readonly Stopwatch VoiceClock = Stopwatch.StartNew();
            private static readonly object ChorusLock = new object();
            private static double _chorusUntilMs;
            private static double _nextChorusMs = -1;

            /// <summary>
            /// Independent per-person bursts produce a smooth, low concurrency that never spikes — but
            /// crowds are correlated. Everyone sings happy birthday, cheers, or laughs at the same
            /// moment, and that simultaneous peak is the load the server actually has to survive; a
            /// model that only ever produces the quiet average never tests it. Baseline conversation
            /// is punctuated by chorus events where most of the crowd talks at once.
            /// </summary>
            private static bool ChorusActive(double nowMs)
            {
                if (!Basis.Config.ConfigManager.VoiceChorusEnabled) return false;
                if (nowMs < Volatile.Read(ref _chorusUntilMs)) return true;
                if (nowMs < Volatile.Read(ref _nextChorusMs)) return false;

                lock (ChorusLock)
                {
                    if (nowMs < _chorusUntilMs) return true;
                    if (_nextChorusMs < 0)
                    {
                        // First scheduling pass: don't open the run mid-song.
                        _nextChorusMs = nowMs + Random.Shared.Next(
                            Basis.Config.ConfigManager.VoiceChorusIntervalMinMs,
                            Math.Max(Basis.Config.ConfigManager.VoiceChorusIntervalMinMs + 1,
                                     Basis.Config.ConfigManager.VoiceChorusIntervalMaxMs));
                        return false;
                    }
                    if (nowMs < _nextChorusMs) return false;

                    int min = Basis.Config.ConfigManager.VoiceChorusDurationMinMs;
                    int max = Math.Max(min + 1, Basis.Config.ConfigManager.VoiceChorusDurationMaxMs);
                    _chorusUntilMs = nowMs + Random.Shared.Next(min, max);

                    int gapMin = Basis.Config.ConfigManager.VoiceChorusIntervalMinMs;
                    int gapMax = Math.Max(gapMin + 1, Basis.Config.ConfigManager.VoiceChorusIntervalMaxMs);
                    _nextChorusMs = _chorusUntilMs + Random.Shared.Next(gapMin, gapMax);
                    return true;
                }
            }

            public static bool InChorus => Volatile.Read(ref _chorusUntilMs) > VoiceClock.Elapsed.TotalMilliseconds;

            // 48 kHz mono, 20 ms frames, matching the real client's encoder settings.
            private const int SampleRate = 48000;
            private const int Channels = 1;
            private const int FrameSamples = SampleRate / 1000 * 20;
            private static byte[][] _opusFrames;
            private static int _opusFrameCount;
            public static int OpusAverageFrameBytes { get; private set; }

            /// <summary>
            /// Real Opus rather than random bytes. Random payloads are the wrong size distribution
            /// (Opus is VBR) and, more importantly, are undecodable — a real player joining the load
            /// test would get decoder errors instead of audio. A sine sweep encoded once at startup
            /// gives frames that decode to an audible tone and vary in size the way speech does; the
            /// frames are shared by every simulated client because the encoder is the expensive part
            /// and the bytes on the wire are what the server is being measured on.
            /// </summary>
            private static void BuildOpusFrames()
            {
                // A full second of audio, so replaying it does not repeat a single frame forever.
                const int frames = 50;
                var encoded = new List<byte[]>(frames);
                try
                {
                    var encoder = new OpusSharp.Core.Dynamic.OpusEncoder(
                        SampleRate, Channels, OpusSharp.Core.OpusPredefinedValues.OPUS_APPLICATION_AUDIO);
                    encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_BITRATE, Basis.Config.ConfigManager.VoiceBitrate);
                    encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_COMPLEXITY, 5);
                    encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_INBAND_FEC, 1);

                    float[] pcm = new float[FrameSamples];
                    byte[] scratch = new byte[FrameSamples * 4];
                    double phase = 0;
                    long total = 0;

                    for (int f = 0; f < frames; f++)
                    {
                        // Sweep 180-260 Hz across the second: a fixed tone encodes to an
                        // unrealistically small and perfectly constant frame.
                        double hz = 180.0 + 80.0 * f / frames;
                        double step = 2.0 * Math.PI * hz / SampleRate;
                        for (int i = 0; i < FrameSamples; i++)
                        {
                            pcm[i] = (float)(Math.Sin(phase) * 0.25);
                            phase += step;
                            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                        }

                        int len = encoder.Encode(pcm, FrameSamples, scratch, scratch.Length);
                        if (len <= 0) continue;
                        byte[] frame = new byte[len];
                        Buffer.BlockCopy(scratch, 0, frame, 0, len);
                        encoded.Add(frame);
                        total += len;
                    }

                    if (encoded.Count > 0)
                    {
                        OpusAverageFrameBytes = (int)(total / encoded.Count);
                        BNL.Log($"Opus voice ready: {encoded.Count} frames, avg {OpusAverageFrameBytes} bytes @ {Basis.Config.ConfigManager.VoiceBitrate} bps.");
                    }
                }
                catch (Exception ex)
                {
                    // No native opus for this platform: fall back rather than killing the run, but say
                    // so, because the traffic shape is then only approximately right.
                    BNL.LogError($"Opus encoder unavailable ({ex.Message}); falling back to fixed-size synthetic frames.");
                    encoded.Clear();
                }

                if (encoded.Count == 0)
                {
                    byte[] fallback = new byte[Math.Max(1, Basis.Config.ConfigManager.VoiceBytesPerFrame)];
                    Random.Shared.NextBytes(fallback);
                    encoded.Add(fallback);
                    OpusAverageFrameBytes = fallback.Length;
                }

                _opusFrames = encoded.ToArray();
                _opusFrameCount = _opusFrames.Length;
            }

            public static void Initialize(int clientCount)
            {
                _recipients = new ushort[clientCount][];
                _participates = new bool[clientCount];
                _talking = new bool[clientCount];
                _nextSwitchMs = new double[clientCount];
                _seq = new byte[clientCount];
                _silentUnits = new int[clientCount];
                _audible = new ConcurrentDictionary<ushort, long>[clientCount];
                _nextRebuildMs = new double[clientCount];
                for (int i = 0; i < clientCount; i++) _audible[i] = new ConcurrentDictionary<ushort, long>();
                _built = 0;

                BuildOpusFrames();

                int percent = Math.Clamp(Basis.Config.ConfigManager.VoiceParticipantPercent, 0, 100);
                int chorusPercent = Math.Clamp(Basis.Config.ConfigManager.VoiceChorusPercent, 0, 100);
                _joinsChorus = new bool[clientCount];
                for (int i = 0; i < clientCount; i++)
                {
                    _participates[i] = Random.Shared.Next(100) < percent;
                    _joinsChorus[i] = Random.Shared.Next(100) < chorusPercent;
                    // Start everyone silent and stagger the first burst, so a run does not open with
                    // the entire crowd unmuting on the same tick.
                    _talking[i] = false;
                    _nextSwitchMs[i] = Random.Shared.Next(0, Math.Max(1, Basis.Config.ConfigManager.VoiceSilenceMaxMs));
                }

                _micCursor = new long[clientCount];
                if (Basis.Config.ConfigManager.VoiceUseSystemMicrophone)
                {
                    bool started = MicrophoneCapture.Start(
                        Basis.Config.ConfigManager.VoiceMicrophoneDevice,
                        Basis.Config.ConfigManager.VoiceFrameMs,
                        Basis.Config.ConfigManager.VoiceBitrate);

                    if (started)
                    {
                        int participants = 0;
                        long newest = MicrophoneCapture.NewestFrameIndex();
                        for (int i = 0; i < clientCount; i++)
                        {
                            _micCursor[i] = newest;
                            if (_participates[i]) participants++;
                        }
                        BNL.Log($"[Mic] One capture feeding all {participants} voice participant(s); burst clock and the {Basis.Config.ConfigManager.VoiceRangeMeters} m recipient range are unchanged.");
                    }
                }
            }

            /// <summary>
            /// Every voice participant transmits the single shared capture, so a listener hears real
            /// audio from whichever bots are inside VoiceRangeMeters of them rather than having to find
            /// one designated speaker. Range culling and the talk/silence burst clock are untouched, so
            /// the crowd load profile is the same as a synthetic run.
            /// </summary>
            public static bool IsMicClient(int index)
            {
                var flags = _participates;
                return MicrophoneCapture.Active && flags != null && index >= 0 && index < flags.Length && flags[index];
            }

            public static void SyncMicCursor(int index)
            {
                var cursors = _micCursor;
                if (cursors == null || index < 0 || index >= cursors.Length) return;
                cursors[index] = MicrophoneCapture.NewestFrameIndex();
            }

            /// <summary>
            /// Speech is bursty: a person says something for a few seconds, then listens. Modelling it
            /// as a fixed always-on subset gets the average bitrate roughly right but none of the
            /// shape — no silence gaps, no changing set of speakers, and every recipient list exercised
            /// continuously rather than intermittently. Each participant alternates burst/silence with
            /// randomised durations, so who is talking keeps changing and most are quiet at any moment.
            /// </summary>
            public static bool IsTalking(int index, double nowMs)
            {
                if (_participates == null || index >= _participates.Length || !_participates[index]) return false;

                // Alone in the world: nobody is inside the audible radius, so there is no one to talk
                // to and a real client transmits nothing at all. Hold the burst clock too, so an
                // isolated player does not silently burn through its talk window and come back wrong.
                ushort[] audience = _recipients?[index];
                if (audience == null || audience.Length == 0)
                {
                    _talking[index] = false;
                    return false;
                }

                // A chorus overrides the personal burst clock — that is the point of it.
                if (_joinsChorus[index] && ChorusActive(VoiceClock.Elapsed.TotalMilliseconds))
                {
                    return true;
                }

                if (nowMs >= _nextSwitchMs[index])
                {
                    _talking[index] = !_talking[index];
                    int min = _talking[index] ? Basis.Config.ConfigManager.VoiceTalkBurstMinMs : Basis.Config.ConfigManager.VoiceSilenceMinMs;
                    int max = _talking[index] ? Basis.Config.ConfigManager.VoiceTalkBurstMaxMs : Basis.Config.ConfigManager.VoiceSilenceMaxMs;
                    if (max <= min) max = min + 1;
                    _nextSwitchMs[index] = nowMs + Random.Shared.Next(min, max);
                }
                return _talking[index];
            }

            // Who each simulated client can currently hear, and when they were last seen. Keyed by the
            // server-assigned player id, so real players land in here exactly like simulated ones.
            private static ConcurrentDictionary<ushort, long>[] _audible;
            private static double[] _nextRebuildMs;

            /// <summary>
            /// Called when avatar traffic arrives about <paramref name="playerId"/> at a quality tier
            /// the server only sends to nearby peers.
            ///
            /// This is how a real player joining gets heard. Rather than decoding positions, it reuses
            /// the distance work the SERVER already did: High/Medium avatar quality is only sent inside
            /// MediumQualityDistance, which is the voice radius, so simply receiving that tier proves
            /// the sender is in range. It also tracks people moving, and needs no special case for
            /// real players — they announce themselves by being audible.
            /// </summary>
            public static void NoteAudible(int clientIndex, ushort playerId)
            {
                var map = _audible;
                if (map == null || clientIndex < 0 || clientIndex >= map.Length) return;
                map[clientIndex][playerId] = VoiceClock.ElapsedMilliseconds;
            }

            /// <summary>
            /// Rebuilds the recipient list from who is currently audible and republishes it when it
            /// changes. Returns true once the client has a list to transmit against.
            /// </summary>
            public static bool RefreshRecipients(NetPeer peer, NetPeer[] peers, int index, double nowMs)
            {
                if (_recipients == null || index >= _recipients.Length) return false;

                bool first = _recipients[index] == null;
                if (!first && nowMs < _nextRebuildMs[index]) return true;
                _nextRebuildMs[index] = nowMs + Basis.Config.ConfigManager.VoiceRecipientRefreshMs;

                // Seed from the simulated crowd's fixed spawn positions. Those clients are the bulk of
                // the population and their avatar traffic may be tiered below Medium even when they are
                // in range, since quality also drops under server load shedding.
                float rangeSq = Basis.Config.ConfigManager.VoiceRangeMeters * Basis.Config.ConfigManager.VoiceRangeMeters;
                HashSet<ushort> near = new HashSet<ushort>();
                if (PlayersCurrentPosition != null && index < PlayersCurrentPosition.Length)
                {
                    Vector3 self = PlayersCurrentPosition[index];
                    for (int j = 0; j < peers.Length && j < PlayersCurrentPosition.Length; j++)
                    {
                        if (j == index) continue;
                        NetPeer other = Volatile.Read(ref peers[j]);
                        if (other == null) continue;
                        Vector3 p = PlayersCurrentPosition[j];
                        float dx = p.x - self.x, dy = p.y - self.y, dz = p.z - self.z;
                        if (dx * dx + dy * dy + dz * dz <= rangeSq) near.Add((ushort)other.RemoteId);
                    }
                }

                // Add anyone we can currently hear who is not part of the simulated crowd — a real
                // player, or one that moved into range. Stale entries drop out so someone who walked
                // away stops receiving our voice.
                var map = _audible?[index];
                if (map != null)
                {
                    long now = VoiceClock.ElapsedMilliseconds;
                    long stale = Basis.Config.ConfigManager.VoiceAudibleTimeoutMs;
                    foreach (var kv in map)
                    {
                        if (now - kv.Value > stale) { map.TryRemove(kv.Key, out _); continue; }
                        near.Add(kv.Key);
                    }
                }

                ushort self_id = (ushort)peer.RemoteId;
                near.Remove(self_id);

                ushort[] updated = new ushort[near.Count];
                near.CopyTo(updated);
                Array.Sort(updated);

                ushort[] previous = _recipients[index];
                if (previous != null && previous.Length == updated.Length)
                {
                    bool same = true;
                    for (int i = 0; i < updated.Length; i++) { if (previous[i] != updated[i]) { same = false; break; } }
                    if (same) return true;
                }

                _recipients[index] = updated;
                if (first) Interlocked.Increment(ref _built);
                SendRecipients(peer, index);
                return true;
            }

            public static void SendRecipients(NetPeer peer, int index)
            {
                ushort[] list = _recipients?[index];
                if (list == null) return;

                // The count is byte-width on the small channel, so anything past 255 recipients has
                // to go out on the large one or the server reads a truncated list.
                bool large = list.Length > byte.MaxValue;
                NetDataWriter writer = new NetDataWriter();
                if (large) writer.Put((ushort)list.Length);
                else writer.Put((byte)list.Length);
                for (int i = 0; i < list.Length; i++) writer.Put(list[i]);

                peer.Send(writer,
                    large ? BasisNetworkCommons.AudioRecipientsLargeChannel : BasisNetworkCommons.AudioRecipientsChannel,
                    DeliveryMethod.ReliableOrdered);
            }

            public static void NoteSilence(int index)
            {
                if (_silentUnits == null || index < 0 || index >= _silentUnits.Length) return;
                if (_silentUnits[index] < byte.MaxValue) _silentUnits[index]++;
            }

            public static void SendFrame(NetPeer peer, int index)
            {
                if (_opusFrameCount == 0 || _recipients?[index] == null || _recipients[index].Length == 0) return;

                // Walk the encoded second so consecutive frames differ, as real speech does, and
                // stagger the starting point per client so the crowd isn't phase-locked.
                byte[] frame = _opusFrames[(_seq[index] + index) % _opusFrameCount];
                SendEncoded(peer, index, frame);
            }

            public static int SendMicFrames(NetPeer peer, int index, int maxFrames)
            {
                if (_recipients?[index] == null || _recipients[index].Length == 0) return 0;

                int sent = 0;
                for (int f = 0; f < maxFrames; f++)
                {
                    if (!MicrophoneCapture.TryRead(ref _micCursor[index], out byte[] frame, out bool isSpeech))
                        break;

                    if (isSpeech) SendEncoded(peer, index, frame);
                    else NoteSilence(index);
                    sent++;
                }
                return sent;
            }

            private static void SendEncoded(NetPeer peer, int index, byte[] frame)
            {
                byte seq = _seq[index]++;
                byte silence = (byte)_silentUnits[index];
                _silentUnits[index] = 0;

                NetDataWriter writer = new NetDataWriter();
                writer.Put(seq);
                writer.Put(silence);
                writer.Put(frame);
                peer.Send(writer, BasisNetworkCommons.VoiceChannel, DeliveryMethod.Sequenced);
            }

            public static int BuiltCount => Volatile.Read(ref _built);
        }

        public static void ProcessSingle(NetPeer peer, int index)
        {
            if (peer == null) return;

            ref PlayerData pd = ref ActivePlayerData[index];

            double time = AnimTimer.Elapsed.TotalSeconds;
            float phase = pd.PhaseOffset;

            // Update position (held fixed when pinned to a distance tier)
            if (PinSpacingMeters <= 0f)
            {
                PlayersCurrentPosition[index] += Randomizer.GetRandomOffset();
            }

            var msg = pd.Message;

            // 1) Position (first 12 bytes)
            int offset = 0;
            WritePosition(PlayersCurrentPosition[index], ref msg.array, ref offset);

            // 2) Animated bone rotations (natural pose + idle animation, all 51 bones fresh per send)
            FakePoseGenerator.WriteBoneRotations(msg.array, RotationRegionOffset, BitQuality.High, time, phase);

            // 3) Scale unchanged

            // 4) Animated hips rotation
            FakePoseGenerator.WriteCompressedHipsRotation(msg.array, HipsRotationOffset, time, phase);

            byte seq = pd.SequenceByte;
            unchecked { pd.SequenceByte++; }

            // Face-data test mode: ride one AdditionalAvatarData on this frame, exactly like the
            // real client ships HVR high-frequency face variables (messageIndex 1, payload
            // [16][timing][counter…]). The per-sender counter lets the observer verify ordering.
            bool hasAdditional = false;
            if (EmitFaceData)
            {
                int counter = unchecked((ushort)(++pd.FaceCounter));
                pd.FaceScratch ??= new AdditionalAvatarData[1];
                pd.FaceScratch[0] = new AdditionalAvatarData
                {
                    messageIndex = 1,
                    array = new byte[] { 16, 1, (byte)(counter & 0xFF), (byte)((counter >> 8) & 0xFF), 200, 150, 100 },
                };
                msg.AdditionalAvatarDatas = pd.FaceScratch;
                msg.LinkedAvatarIndex = 0;
                hasAdditional = true;
            }
            else
            {
                msg.AdditionalAvatarDatas = null;
                msg.AdditionalAvatarDataSize = 0;
            }

            long now = Stopwatch.GetTimestamp();
            bool keyframe = !UseUplinkDeltas
                || pd.ForceKeyframe
                || !pd.HasBaseline
                || pd.Baseline == null
                || pd.Baseline.Length != msg.array.Length
                || now - pd.LastKeyframeTicks >= UplinkKeyframeIntervalTicks;

            int deltaLen = -1;
            if (!keyframe)
            {
                int cap = BasisAvatarDeltaCompression.MaxDeltaSize(BitQuality.High);
                if (pd.DeltaScratch == null || pd.DeltaScratch.Length < cap)
                    pd.DeltaScratch = new byte[cap];
                deltaLen = BasisAvatarDeltaCompression.BuildDelta(pd.Baseline, msg.array, BitQuality.High, pd.DeltaScratch, 0);
                if (deltaLen < 0 || deltaLen >= msg.array.Length) keyframe = true;
            }

            var writer = pd.Writer;
            writer.Reset();
            if (keyframe)
            {
                // Full keyframe on the High channel — the server snapshots it as this
                // sender's uplink delta baseline. Odd channel when additional data rides along.
                writer.Put(seq);
                msg.SerializeForChannel(writer, BitQuality.High);
                byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality((int)BitQuality.High, hasAdditional);
                peer.Send(writer, channel, DeliveryMethod.Unreliable);

                if (UseUplinkDeltas)
                {
                    if (pd.Baseline == null || pd.Baseline.Length != msg.array.Length)
                        pd.Baseline = new byte[msg.array.Length];
                    System.Array.Copy(msg.array, pd.Baseline, msg.array.Length);
                    pd.BaselineSeq = seq;
                    pd.HasBaseline = true;
                    pd.LastKeyframeTicks = now;
                    pd.ForceKeyframe = false;
                }
            }
            else
            {
                // v42 uplink delta: [hdr][seq][baseSeq][body][additional?] on DeltaAvatarChannel.
                writer.Put(BasisNetworkCommons.BuildDeltaHeader((int)BitQuality.High, hasAdditional, false));
                writer.Put(seq);
                writer.Put(pd.BaselineSeq);
                writer.Put(pd.DeltaScratch, 0, deltaLen);
                if (hasAdditional) msg.SerializeAdditionalOnly(writer);
                peer.Send(writer, BasisNetworkCommons.DeltaAvatarChannel, DeliveryMethod.Unreliable);
            }

            pd.Message = msg;
        }

        public static void WritePosition(Scripts.Networking.Compression.Vector3 position, ref byte[] buffer, ref int offset)
        {
            unsafe
            {
                fixed (byte* dst = &buffer[offset])
                {
                    float* f = (float*)dst;
                    f[0] = position.x;
                    f[1] = position.y;
                    f[2] = position.z;
                }
            }
            offset += 12;
        }

        public unsafe static void WriteQuaternionToBytes(Quaternion q, ref byte[] bytes, ref int offset)
        {
            fixed (byte* ptr = &bytes[offset])
            {
                *((float*)ptr) = float.IsNaN(q.value.x) ? 0f : q.value.x;
                *((float*)(ptr + 4)) = float.IsNaN(q.value.y) ? 0f : q.value.y;
                *((float*)(ptr + 8)) = float.IsNaN(q.value.z) ? 0f : q.value.z;
                *((float*)(ptr + 12)) = float.IsNaN(q.value.w) ? 1f : q.value.w;
            }

            offset += 16;
        }

        private static ushort CompressScaleOnce(float scale)
        {
            if (scale != 1f)
            {
                throw new System.ArgumentOutOfRangeException(nameof(scale), scale, "MovementSender only supports precomputed scale 1.0.");
            }

            return 0x4000;
        }

        public static void WriteUShort(ushort value, ref byte[] bytes, ref int offset)
        {
            bytes[offset++] = (byte)value;
            bytes[offset++] = (byte)(value >> 8);
        }
    }
}
