using Basis.Network.Core;
using Basis.Scripts.Networking.Compression;
using BasisNetworkClientConsole;
using LiteNetLib;
using static BasisNetworkPrimitiveCompression;
using static SerializableBasis;

namespace Basis.Network
{
    public static class MovementSender
    {
        public static byte[] AvatarMessage = new byte[LocalAvatarSyncMessage.AvatarSyncSize + 1];
        public static Quaternion Rotation = new Quaternion(0, 0, 0, 1);
        public static float[] FloatArray = new float[LocalAvatarSyncMessage.StoredBones];
        public static ushort[] UshortArray = new ushort[LocalAvatarSyncMessage.StoredBones];
        private const ushort UShortMin = ushort.MinValue; // 0
        private const ushort UShortMax = ushort.MaxValue; // 65535
        private const ushort ushortRangeDifference = UShortMax - UShortMin;
        public static BasisRangedUshortFloatData RotationCompression = new BasisRangedUshortFloatData(-1f, 1f, 0.001f);
        public static Vector3 MinPosition = new Vector3(30, 30, 30);
        public static Vector3 MaxPosition = new Vector3(80, 80, 80);
        public static int LengthUshortBytes = LocalAvatarSyncMessage.StoredBones * 2; // Initialize LengthBytes first
        public static Vector3[] PlayersCurrentPosition;
        public static void Initialize(int clientCount)
        {
            PlayersCurrentPosition = new Vector3[clientCount];
            for (int Index = 0; Index < PlayersCurrentPosition.Length; Index++)
            {
                Random random = new Random();
                PlayersCurrentPosition[Index] = Randomizer.GetRandomOffset();
            }
        }

        public static void Process(NetPeer[] peers)
        {
            int Peers = peers.Length;
            for (int i = 0; i < Peers; i++)
            {
                SendMovement(peers[i], i);
            }
        }

        private static void SendMovement(NetPeer peer, int index)
        {
            if (peer == null) return;

            int offset = 0;
            Vector3 delta = Randomizer.GetRandomOffset();
            PlayersCurrentPosition[index] = PlayersCurrentPosition[index] +  delta;

            WriteVectorFloatToBytes(PlayersCurrentPosition[index], ref AvatarMessage, ref offset);
            WriteQuaternionToBytes(Rotation, ref AvatarMessage, ref offset, RotationCompression);
            WriteUShortsToBytes(UshortArray, ref AvatarMessage, ref offset);
            CompressScale(1, ref AvatarMessage, ref offset);

            peer.Send(AvatarMessage, BasisNetworkCommons.MovementChannel, DeliveryMethod.Sequenced);
        }
        // Ensure the byte array is large enough to hold the data
        private static void EnsureSize(ref byte[] bytes, int requiredSize)
        {
            if (bytes == null)
            {
                bytes = new byte[requiredSize];
                return;
            }
            if (bytes.Length < requiredSize)
            {
                Array.Resize(ref bytes, requiredSize);
            }
        }
        // Manual conversion of quaternion to bytes (without BitConverter)
        public static void WriteQuaternionToBytes(Quaternion rotation, ref byte[] bytes, ref int offset, BasisRangedUshortFloatData compressor)
        {
            EnsureSize(ref bytes, offset + 14);
            ushort compressedW = compressor.Compress(rotation.value.w);

            // Write the quaternion's components
            WriteFloatToBytes(rotation.value.x, ref bytes, ref offset);
            WriteFloatToBytes(rotation.value.y, ref bytes, ref offset);
            WriteFloatToBytes(rotation.value.z, ref bytes, ref offset);

            // Write the compressed 'w' component
            bytes[offset] = (byte)(compressedW & 0xFF);           // Low byte
            bytes[offset + 1] = (byte)((compressedW >> 8) & 0xFF); // High byte
            offset += 2;
        }
        public static void WriteVectorFloatToBytes(Vector3 values, ref byte[] bytes, ref int offset)
        {
            EnsureSize(ref bytes, offset + 12);
            WriteFloatToBytes(values.x, ref bytes, ref offset);//4
            WriteFloatToBytes(values.y, ref bytes, ref offset);//8
            WriteFloatToBytes(values.z, ref bytes, ref offset);//12
        }

        private unsafe static void WriteFloatToBytes(float value, ref byte[] bytes, ref int offset)
        {
            // Convert the float to a uint using its bitwise representation
            uint intValue = *((uint*)&value);

            // Manually write the bytes
            bytes[offset] = (byte)(intValue & 0xFF);
            bytes[offset + 1] = (byte)((intValue >> 8) & 0xFF);
            bytes[offset + 2] = (byte)((intValue >> 16) & 0xFF);
            bytes[offset + 3] = (byte)((intValue >> 24) & 0xFF);
            offset += 4;
        }
        public static void WriteUShortsToBytes(ushort[] values, ref byte[] bytes, ref int offset)
        {
            EnsureSize(ref bytes, offset + LengthUshortBytes);

            // Manually copy ushort values as bytes
            for (int index = 0; index < LocalAvatarSyncMessage.StoredBones; index++)
            {
                WriteUShortToBytes(values[index], ref bytes, ref offset);
            }
        }
        // Manual ushort to bytes conversion (without BitConverter)
        private unsafe static void WriteUShortToBytes(ushort value, ref byte[] bytes, ref int offset)
        {
            // Manually write the bytes
            bytes[offset] = (byte)(value & 0xFF);
            bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
            offset += 2;
        }
        public static void CompressScale(float Scale, ref byte[] bytes, ref int Offset)
        {
            //we can squeeze out more 
            const float MinimumValueSupported = 0.005f;
            const float MaximumValueSupported = 150;
            const float valueDiffence = MaximumValueSupported - MinimumValueSupported;

            //basis does not support ununiform scaling, if your avatar is not uniform you need to get help. - dooly
            float value = Math.Clamp(Scale, MinimumValueSupported, MaximumValueSupported);
            float normalized = (value - MinimumValueSupported) / valueDiffence; // 0..1
            ushort ScaleUshort = (ushort)(normalized * ushortRangeDifference);

            WriteUShortToBytes(ScaleUshort, ref AvatarMessage, ref Offset);
        }
    }
}
