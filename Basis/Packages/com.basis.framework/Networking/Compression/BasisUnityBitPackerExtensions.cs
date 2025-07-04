using System;
using System.Collections.Generic;
using static BasisNetworkPrimitiveCompression;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Compression
{
    public static class BasisUnityBitPackerExtensions
    {
        // public static int LengthFloatBytes = LocalAvatarSyncMessage.StoredBones * 4; // Initialize LengthBytes first
        public static int LengthUshortBytes = LocalAvatarSyncMessage.StoredBones * 2; // Initialize LengthBytes first

        // Object pool for byte arrays to avoid allocation during runtime
        private static readonly ObjectPool<byte[]> byteArrayPool = new ObjectPool<byte[]>(() => new byte[LengthUshortBytes]);
        // Manual conversion of Vector3 to bytes (without BitConverter)
        public static void WriteVectorFloatToBytes(UnityEngine.Vector3 values, ref byte[] bytes, ref int offset)
        {
            EnsureSize(ref bytes, offset + 12);
            WriteFloatToBytes(values.x, ref bytes, ref offset);//4
            WriteFloatToBytes(values.y, ref bytes, ref offset);//8
            WriteFloatToBytes(values.z, ref bytes, ref offset);//12
        }

        // Manual conversion of bytes to Vector3 (without BitConverter)
        public static Unity.Mathematics.float3 ReadVectorFloatFromBytes(ref byte[] bytes, ref int offset)
        {
            EnsureSize(bytes, offset + 12);
            float x = ReadFloatFromBytes(ref bytes, ref offset);//4
            float y = ReadFloatFromBytes(ref bytes, ref offset);//8
            float z = ReadFloatFromBytes(ref bytes, ref offset);//12

            return new Unity.Mathematics.float3(x, y, z);
        }

        // Manual conversion of quaternion to bytes (without BitConverter)
        public static void WriteQuaternionToBytes(Unity.Mathematics.quaternion rotation, ref byte[] bytes, ref int offset, BasisRangedUshortFloatData compressor)
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

        // Manual conversion of bytes to quaternion (without BitConverter)
        public static Unity.Mathematics.quaternion ReadQuaternionFromBytes(ref byte[] bytes, BasisRangedUshortFloatData compressor, ref int offset)
        {
            EnsureSize(bytes, offset + 12 + 2);

            float x = ReadFloatFromBytes(ref bytes, ref offset);//4
            float y = ReadFloatFromBytes(ref bytes, ref offset);//8
            float z = ReadFloatFromBytes(ref bytes, ref offset);//12

            ushort compressedW = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
            offset += 2;//2

            return new Unity.Mathematics.quaternion(x, y, z, compressor.Decompress(compressedW));
        }
        // Write ushort array to bytes (no BitConverter)
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
        public unsafe static void WriteUShortToBytes(ushort value, ref byte[] bytes, ref int offset)
        {
            // Manually write the bytes
            bytes[offset] = (byte)(value & 0xFF);
            bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
            offset += 2;
        }
        // Manual float to bytes conversion (without BitConverter)
        public unsafe static void WriteFloatToBytes(float value, ref byte[] bytes, ref int offset)
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

        // Read muscles from bytes (no BitConverter)
        public static void ReadMusclesFromBytes(ref byte[] bytes, ref ushort[] muscles, ref int offset)
        {
            if (muscles == null || muscles.Length != LocalAvatarSyncMessage.StoredBones)
            {
                muscles = new ushort[LocalAvatarSyncMessage.StoredBones];
            }
            EnsureSize(bytes, offset + LengthUshortBytes - 2);

            // Manually read float values from bytes
            for (int Index = 0; Index < LocalAvatarSyncMessage.StoredBones - 1; Index++)
            {
                muscles[Index] = ReadUShortFromBytes(ref bytes, ref offset);
                // UnityEngine.BasisDebug.Log("" + muscles[i]);
            }
        }

        // Manual bytes to float conversion (without BitConverter)
        public unsafe static float ReadFloatFromBytes(ref byte[] bytes, ref int offset)
        {
            // Reconstruct the uint from the byte array
            uint intValue = (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

            // Convert the uint back to float using a pointer cast
            float result = *((float*)&intValue);
            offset += 4;
            return result;
        }
        // Read muscles from bytes as ushort (no BitConverter)
        public static void ReadMusclesFromBytesAsUShort(ref byte[] bytes, ref ushort[] muscles, ref int offset)
        {
            if (muscles == null || muscles.Length != LocalAvatarSyncMessage.StoredBones)
            {
                muscles = new ushort[LocalAvatarSyncMessage.StoredBones];
            }
            EnsureSize(bytes, offset + LengthUshortBytes - 2);

            // Manually read ushort values from bytes
            for (int index = 0; index < LocalAvatarSyncMessage.StoredBones - 1; index++)
            {
                muscles[index] = ReadUShortFromBytes(ref bytes, ref offset);
                // UnityEngine.BasisDebug.Log("" + muscles[index]);
            }
        }

        // Manual bytes to ushort conversion (without BitConverter)
        public static ushort ReadUShortFromBytes(ref byte[] bytes, ref int offset)
        {
            // Reconstruct the ushort from the byte array
            ushort result = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
            offset += 2;
            return result;
        }
        // Ensure the byte array is large enough to hold the data
        public static void EnsureSize(ref byte[] bytes, int requiredSize)
        {
            if (bytes == null || bytes.Length < requiredSize)
            {
                // Reuse pooled byte arrays
                bytes = byteArrayPool.Get();
                Array.Resize(ref bytes, requiredSize);
            }
        }

        // Ensure the byte array is large enough for reading
        public static void EnsureSize(byte[] bytes, int requiredSize)
        {
            if (bytes.Length < requiredSize)
            {
                throw new ArgumentException("Byte array is too small for the required size. Current Size is " + bytes.Length + " But Required " + requiredSize);
            }
        }

        // Object pool for byte arrays to avoid allocation during runtime
        public class ObjectPool<T>
        {
            private readonly Func<T> createFunc;
            private readonly Stack<T> pool;

            public ObjectPool(Func<T> createFunc)
            {
                this.createFunc = createFunc;
                this.pool = new Stack<T>();
            }

            public T Get()
            {
                return pool.Count > 0 ? pool.Pop() : createFunc();
            }

            public void Return(T item)
            {
                pool.Push(item);
            }
        }
    }
    public static class BasisUnityBitPackerExtensionsUnsafe
    {
        public static int LengthUshortBytes = LocalAvatarSyncMessage.StoredBones * 2;

        private static readonly ObjectPool<byte[]> byteArrayPool = new ObjectPool<byte[]>(() => new byte[LengthUshortBytes]);

        public unsafe static void WriteVectorFloatToBytes(UnityEngine.Vector3 values, ref byte[] bytes, ref int offset)
        {
            EnsureSize(ref bytes, offset + 12);
            fixed (byte* ptr = &bytes[offset])
            {
                *((float*)ptr) = values.x;
                *((float*)(ptr + 4)) = values.y;
                *((float*)(ptr + 8)) = values.z;
            }
            offset += 12;
        }

        public unsafe static Unity.Mathematics.float3 ReadVectorFloatFromBytes(ref byte[] bytes, ref int offset)
        {
            EnsureSize(bytes, offset + 12);
            Unity.Mathematics.float3 result;
            fixed (byte* ptr = &bytes[offset])
            {
                result = new Unity.Mathematics.float3(
                    *((float*)ptr),
                    *((float*)(ptr + 4)),
                    *((float*)(ptr + 8))
                );
            }
            offset += 12;
            return result;
        }

        public unsafe static void WriteQuaternionToBytes(Unity.Mathematics.quaternion rotation, ref byte[] bytes, ref int offset, BasisRangedUshortFloatData compressor)
        {
            EnsureSize(ref bytes, offset + 14);
            fixed (byte* ptr = &bytes[offset])
            {
                *((float*)ptr) = rotation.value.x;
                *((float*)(ptr + 4)) = rotation.value.y;
                *((float*)(ptr + 8)) = rotation.value.z;
            }
            offset += 12;

            ushort compressedW = compressor.Compress(rotation.value.w);
            bytes[offset++] = (byte)(compressedW & 0xFF);
            bytes[offset++] = (byte)((compressedW >> 8) & 0xFF);
        }

        public unsafe static Unity.Mathematics.quaternion ReadQuaternionFromBytes(ref byte[] bytes, BasisRangedUshortFloatData compressor, ref int offset)
        {
            EnsureSize(bytes, offset + 14);
            float x, y, z;
            fixed (byte* ptr = &bytes[offset])
            {
                x = *((float*)ptr);
                y = *((float*)(ptr + 4));
                z = *((float*)(ptr + 8));
            }
            offset += 12;

            ushort compressedW = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
            offset += 2;

            float w = compressor.Decompress(compressedW);
            return new Unity.Mathematics.quaternion(x, y, z, w);
        }

        public unsafe static void WriteUShortsToBytes(ushort[] values, ref byte[] bytes, ref int offset)
        {
            EnsureSize(ref bytes, offset + LengthUshortBytes);
            fixed (byte* ptr = &bytes[offset])
            fixed (ushort* src = values)
            {
                Buffer.MemoryCopy(src, ptr, LengthUshortBytes, LengthUshortBytes);
            }
            offset += LengthUshortBytes;
        }
        public unsafe static void WriteUShortToBytes(ushort value, ref byte[] bytes, ref int offset)
        {
            const int ushortSize = sizeof(ushort); // = 2
            EnsureSize(ref bytes, offset + ushortSize);

            fixed (byte* ptr = &bytes[offset])
            {
                *(ushort*)ptr = value;
            }

            offset += ushortSize;
        }
        public unsafe static void ReadMusclesFromBytes(ref byte[] bytes, ref ushort[] muscles, ref int offset)
        {
            if (muscles == null || muscles.Length != LocalAvatarSyncMessage.StoredBones)
                muscles = new ushort[LocalAvatarSyncMessage.StoredBones];

            EnsureSize(bytes, offset + LengthUshortBytes);
            fixed (byte* ptr = &bytes[offset])
            fixed (ushort* dst = muscles)
            {
                Buffer.MemoryCopy(ptr, dst, LengthUshortBytes, LengthUshortBytes);
            }
            offset += LengthUshortBytes;
        }

        private static void EnsureSize(ref byte[] bytes, int requiredSize)
        {
            if (bytes == null || bytes.Length < requiredSize)
            {
                bytes = byteArrayPool.Get();
                Array.Resize(ref bytes, requiredSize);
            }
        }

        private static void EnsureSize(byte[] bytes, int requiredSize)
        {
            if (bytes.Length < requiredSize)
            {
                throw new ArgumentException($"Byte array is too small. Required: {requiredSize}, Actual: {bytes.Length}");
            }
        }

        private class ObjectPool<T>
        {
            private readonly Func<T> createFunc;
            private readonly Stack<T> pool = new Stack<T>();

            public ObjectPool(Func<T> createFunc) => this.createFunc = createFunc;

            public T Get() => pool.Count > 0 ? pool.Pop() : createFunc();

            public void Return(T item) => pool.Push(item);
        }
    }
}
