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

        private const ushort UShortMin = ushort.MinValue;   // 0
        private const ushort UShortMax = ushort.MaxValue;   // 65535
        private const ushort UShortRangeDifference = UShortMax - UShortMin;

        public static Vector3[] PlayersCurrentPosition;
        public static PlayerData[] ActivePlayerData;

        public struct PlayerData
        {
            public NetDataWriter Writer;
            public LocalAvatarSyncMessage Message;
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

            // Build the static parts once (muscles default, scale default, rotation default)
            WriteInitialPayload(ref message);

            return new PlayerData
            {
                Writer = new NetDataWriter(),
                Message = message
            };
        }

        private static void WriteInitialPayload(ref LocalAvatarSyncMessage message)
        {
            if (message.array.Length != ClientManager.Size)
            {
                message.array = new byte[ClientManager.Size];
            }
            // Layout:
            int offset = 0;

            // Position (placeholder; will be overwritten each tick)
            WritePosition(Randomizer.GetRandomOffset(), ref message.array, ref offset);
        }
        public static void ProcessSingle(NetPeer peer, int index)
        {
            if (peer == null) return;

            // Update position
            PlayersCurrentPosition[index] += Randomizer.GetRandomOffset();

            // Overwrite just the position region in the message buffer (first 12 bytes)
            int offset = 0;
            var msg = ActivePlayerData[index].Message;

            WritePosition(PlayersCurrentPosition[index], ref msg.array, ref offset);

            // Serialize and send
            var writer = ActivePlayerData[index].Writer;
            writer.Reset();
            msg.Serialize(writer, BitQuality.High);

            peer.Send(writer, BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.Sequenced);

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
            const float Min = 0.005f;
            const float Max = 150f;
            const float Range = Max - Min;

            float clamped = scale;
            float normalized = (clamped - Min) / Range;

            ushort compressed = (ushort)(normalized * UShortRangeDifference);
            return compressed;
        }

        public static void WriteUShort(ushort value, ref byte[] bytes, ref int offset)
        {
            bytes[offset++] = (byte)value;
            bytes[offset++] = (byte)(value >> 8);
        }
    }
}
