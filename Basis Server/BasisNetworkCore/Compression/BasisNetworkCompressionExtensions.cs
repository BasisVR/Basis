using Basis.Scripts.Networking.Compression;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static BasisNetworkPrimitiveCompression;
using static SerializableBasis;
namespace Basis.Network.Core.Compression
{
    public static class BasisNetworkCompressionExtensions
    {
        /// <summary>
        /// Single API to handle all avatar decompression tasks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 DecompressAndProcessAvatarFaster(ServerSideSyncPlayerMessage syncMessage)
        {
            return ReadVectorFloatFromBytesFaster(ref syncMessage.avatarSerialization.array);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe Vector3 ReadVectorFloatFromBytesFaster(ref byte[] bytes)
        {
            fixed (byte* ptr = bytes)
            {
                float* floatPtr = (float*)ptr;
                return new Vector3(floatPtr[0], floatPtr[1], floatPtr[2]);
            }
        }
        /// <summary>
        /// Single API to handle all avatar decompression tasks.
        /// </summary>
        public static Vector3 DecompressAndProcessAvatar(ServerSideSyncPlayerMessage syncMessage)
        {
            return ReadVectorFloatFromBytesFaster(ref syncMessage.avatarSerialization.array);
        }
        // Manual conversion of bytes to Vector3 (without BitConverter)
        public static Vector3 ReadVectorFloatFromBytes(ref byte[] bytes)
        {
            int offset = 0;
            EnsureSize(bytes, offset + 12);

            float x = ReadFloatFromBytes(ref bytes, ref offset);
            float y = ReadFloatFromBytes(ref bytes, ref offset);
            float z = ReadFloatFromBytes(ref bytes, ref offset);

            return new Vector3(x, y, z);
        }
        // Manual bytes to float conversion (without BitConverter)
        private static unsafe float ReadFloatFromBytes(ref byte[] bytes, ref int offset)
        {
            // Reconstruct the uint from the byte array
            uint intValue = (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);

            // Convert the uint back to float using a pointer cast
            float result = *(float*)&intValue;
            offset += 4;
            return result;
        }

        public static BasisRangedUshortFloatData BasisRangedUshortFloatData = new BasisRangedUshortFloatData(-1f, 1f, 0.001f);
        public static int LengthSize = 90;
        public static int LengthBytes = LengthSize * 4; // Initialize LengthBytes first
        public static byte[] StoredBytes = new byte[LengthBytes];

        // Object pool for byte arrays to avoid allocation during runtime
        private static readonly ObjectPool<byte[]> byteArrayPool = new ObjectPool<byte[]>(() => new byte[LengthBytes]);

        // Manual conversion of ushort to bytes (without BitConverter)
        public static void WriteUshortVectorFloatToBytes(Vector3 values, BasisRangedUshortFloatData compressor, ref byte[] bytes, ref int offset)
        {
            EnsureSize(ref bytes, offset + 6);
            ushort compressedX = compressor.Compress(values.x);
            ushort compressedY = compressor.Compress(values.y);
            ushort compressedZ = compressor.Compress(values.z);

            // Manually write the compressed values to the byte array (no BitConverter)
            bytes[offset] = (byte)(compressedX & 0xFF);           // Low byte
            bytes[offset + 1] = (byte)(compressedX >> 8 & 0xFF); // High byte

            bytes[offset + 2] = (byte)(compressedY & 0xFF);           // Low byte
            bytes[offset + 3] = (byte)(compressedY >> 8 & 0xFF);    // High byte

            bytes[offset + 4] = (byte)(compressedZ & 0xFF);           // Low byte
            bytes[offset + 5] = (byte)(compressedZ >> 8 & 0xFF);    // High byte

            offset += 6;
        }

        // Manual conversion of bytes to ushort (without BitConverter)
        public static Vector3 ReadUshortVectorFloatFromBytes(ref byte[] bytes, BasisRangedUshortFloatData compressor, ref int offset)
        {
            EnsureSize(bytes, offset + 6);

            ushort compressedX = (ushort)(bytes[offset] | bytes[offset + 1] << 8);
            ushort compressedY = (ushort)(bytes[offset + 2] | bytes[offset + 3] << 8);
            ushort compressedZ = (ushort)(bytes[offset + 4] | bytes[offset + 5] << 8);

            offset += 6;

            return new Vector3(
                compressor.Decompress(compressedX),
                compressor.Decompress(compressedY),
                compressor.Decompress(compressedZ)
            );
        }

        // Manual conversion of Vector3 to bytes (without BitConverter)
        public static void WriteVectorFloatToBytes(Vector3 values, ref byte[] bytes, ref int offset)
        {
            EnsureSize(ref bytes, offset + 12);

            // Manually write the float values to bytes (no BitConverter)
            WriteFloatToBytes(values.x, ref bytes, ref offset);
            WriteFloatToBytes(values.y, ref bytes, ref offset);
            WriteFloatToBytes(values.z, ref bytes, ref offset);
        }
        // Manual conversion of bytes to quaternion (without BitConverter)
        public static Quaternion ReadQuaternionFromBytes(ref byte[] bytes, BasisRangedUshortFloatData compressor, ref int offset)
        {
            EnsureSize(bytes, offset + 14);

            float x = ReadFloatFromBytes(ref bytes, ref offset);
            float y = ReadFloatFromBytes(ref bytes, ref offset);
            float z = ReadFloatFromBytes(ref bytes, ref offset);

            ushort compressedW = (ushort)(bytes[offset] | bytes[offset + 1] << 8);
            offset += 2;

            return new Quaternion(x, y, z, compressor.Decompress(compressedW));
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
            bytes[offset + 1] = (byte)(compressedW >> 8 & 0xFF); // High byte
            offset += 2;
        }

        // Write muscles to bytes (no BitConverter)
        public static void WriteMusclesToBytes(float[] muscles, ref byte[] bytes, ref int offset)
        {
            int requiredLength = muscles.Length * sizeof(float);
            EnsureSize(ref bytes, offset + requiredLength);

            // Manually copy float values as bytes
            for (int i = 0; i < muscles.Length; i++)
            {
                WriteFloatToBytes(muscles[i], ref bytes, ref offset);
            }
        }

        // Read muscles from bytes (no BitConverter)
        public static void ReadMusclesFromBytes(ref byte[] bytes, ref float[] muscles, ref int offset)
        {
            int requiredLength = bytes.Length - offset;
            if (muscles == null || muscles.Length * sizeof(float) != requiredLength)
                muscles = new float[requiredLength / sizeof(float)];

            EnsureSize(bytes, offset + requiredLength);

            // Manually read float values from bytes
            for (int i = 0; i < muscles.Length; i++)
            {
                muscles[i] = ReadFloatFromBytes(ref bytes, ref offset);
            }
        }

        // Manual float to bytes conversion
        // Manual float to bytes conversion (without BitConverter)
        private static unsafe void WriteFloatToBytes(float value, ref byte[] bytes, ref int offset)
        {
            // Convert the float to a uint using its bitwise representation
            uint intValue = *(uint*)&value;

            // Manually write the bytes
            bytes[offset] = (byte)(intValue & 0xFF);
            bytes[offset + 1] = (byte)(intValue >> 8 & 0xFF);
            bytes[offset + 2] = (byte)(intValue >> 16 & 0xFF);
            bytes[offset + 3] = (byte)(intValue >> 24 & 0xFF);
            offset += 4;
        }

        // Ensure the byte array is large enough to hold the data
        private static void EnsureSize(ref byte[] bytes, int requiredSize)
        {
            if (bytes == null || bytes.Length < requiredSize)
            {
                // Reuse pooled byte arrays
                bytes = byteArrayPool.Get();
                Array.Resize(ref bytes, requiredSize);
            }
        }

        // Ensure the byte array is large enough for reading
        private static void EnsureSize(byte[] bytes, int requiredSize)
        {
            if (bytes == null || bytes.Length < requiredSize)
            {
                throw new ArgumentException("Byte array is too small for the required size.");
            }
        }

        // Object pool for byte arrays to avoid allocation during runtime
        private class ObjectPool<T>
        {
            private readonly Func<T> createFunc;
            private readonly Stack<T> pool;
            private readonly object lockObj = new object(); // Lock object for thread safety

            public ObjectPool(Func<T> createFunc)
            {
                this.createFunc = createFunc ?? throw new ArgumentNullException(nameof(createFunc));
                pool = new Stack<T>();
            }

            public T Get()
            {
                lock (lockObj)
                {
                    return pool.Count > 0 ? pool.Pop() : createFunc();
                }
            }

            public void Return(T item)
            {
                if (item == null)
                    throw new ArgumentNullException(nameof(item));

                lock (lockObj)
                {
                    pool.Push(item);
                }
            }
        }
    }
}
