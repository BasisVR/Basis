using System.Diagnostics;
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
        }

        // Precompute compressed scale once; reused for all messages.
        private static readonly ushort CompressedScale = CompressScaleOnce(1f);

        public static void Initialize(int clientCount)
        {
            PlayersCurrentPosition = new Vector3[clientCount];
            ActivePlayerData = new PlayerData[clientCount];

            for (int i = 0; i < clientCount; i++)
            {
                PlayersCurrentPosition[i] = Randomizer.GetRandomOffset();
                ActivePlayerData[i] = Generate();
            }
        }
        public static PlayerData Generate()
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

            // Build the full initial payload (position, bone rotations, scale, hips rotation)
            WriteInitialPayload(ref message, phase);

            return new PlayerData
            {
                Writer = new NetDataWriter(),
                Message = message,
                PhaseOffset = phase,
            };
        }

        private static void WriteInitialPayload(ref LocalAvatarSyncMessage message, float phase)
        {
            // Make sure buffer is correct size for High
            int size = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
            if (message.array == null || message.array.Length != size)
                message.array = new byte[size];

            double time = AnimTimer.Elapsed.TotalSeconds;

            // 1) Position (after the recent flip this is the HIPS WORLD position)
            int offset = 0;
            WritePosition(Randomizer.GetRandomOffset(), ref message.array, ref offset);

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
        public static void ProcessSingle(NetPeer peer, int index)
        {
            if (peer == null) return;

            double time = AnimTimer.Elapsed.TotalSeconds;
            float phase = ActivePlayerData[index].PhaseOffset;

            // Update position
            PlayersCurrentPosition[index] += Randomizer.GetRandomOffset();

            var msg = ActivePlayerData[index].Message;

            // 1) Position (first 12 bytes)
            int offset = 0;
            WritePosition(PlayersCurrentPosition[index], ref msg.array, ref offset);

            // 2) Animated bone rotations (natural pose + idle animation)
            FakePoseGenerator.WriteBoneRotations(msg.array, RotationRegionOffset, BitQuality.High, time, phase);

            // 3) Scale unchanged

            // 4) Animated hips rotation
            FakePoseGenerator.WriteCompressedHipsRotation(msg.array, HipsRotationOffset, time, phase);

            // Serialize and send — channel encodes quality (High) and no additional data
            var writer = ActivePlayerData[index].Writer;
            writer.Reset();
            writer.Put(ActivePlayerData[index].SequenceByte);
            unchecked { ActivePlayerData[index].SequenceByte++; }
            msg.SerializeForChannel(writer, BitQuality.High);

            byte channel = BasisNetworkCommons.GetPlayerAvatarChannelForQuality((int)BitQuality.High, false);
            peer.Send(writer, channel, DeliveryMethod.Unreliable);

            ActivePlayerData[index].Message = msg;
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
            // MovementSender only emits default-scale fake avatars; 0x4000 is Posit16 1.0. Use the shared codec before supporting other scales here.
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
