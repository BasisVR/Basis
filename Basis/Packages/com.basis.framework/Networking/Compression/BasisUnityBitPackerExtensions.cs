using System;
using System.Collections.Generic;
using static BasisNetworkPrimitiveCompression;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Compression
{
    public static class BasisUnityBitPackerExtensions
    {
       // public static int LengthFloatBytes = LocalAvatarSyncMessage.StoredBones * 4; // Initialize LengthBytes first
       //  public static int LengthUshortBytes = LocalAvatarSyncMessage.StoredBones * 2; // Initialize LengthBytes first
      
        // Object pool for byte arrays to avoid allocation during runtime
        private static readonly ObjectPool<byte[]> byteArrayPool = new ObjectPool<byte[]>(() => new byte[LocalAvatarSyncMessage.StoredBones * 2]);
        // Manual conversion of Vector3 to bytes (without BitConverter)
        public static void WriteVectorFloatToBytes(UnityEngine. Vector3 values, ref byte[] bytes, ref int offset)
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
        public static void WriteUShortsToBytes(int BoneCount,ushort[] values, ref byte[] bytes, ref int offset)
        {
            EnsureSize(ref bytes, offset + (BoneCount * 2));

            // Manually copy ushort values as bytes
            for (int index = 0; index < BoneCount; index++)
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
        // Manual float to bytes conversion (without BitConverter)
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

        // Read muscles from bytes (no BitConverter)
        public static void ReadMusclesFromBytes(int ushortlength,ref byte[] bytes, ref ushort[] muscles, ref int offset)
        {
            if (muscles == null || muscles.Length < ushortlength)
            {
                muscles = new ushort[ushortlength];
            }
            EnsureSize(bytes, offset + ushortlength);

            // Manually read float values from bytes
            for (int Index = 0; Index < ushortlength; Index++)
            {
                muscles[Index] = ReadUShortFromBytes(ref bytes, ref offset);
                // UnityEngine.BasisDebug.Log("" + muscles[i]);
            }
        }

        // Manual bytes to float conversion (without BitConverter)
        private unsafe static float ReadFloatFromBytes(ref byte[] bytes, ref int offset)
        {
            // Reconstruct the uint from the byte array
            uint intValue = (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

            // Convert the uint back to float using a pointer cast
            float result = *((float*)&intValue);
            offset += 4;
            return result;
        }
        // Read muscles from bytes as ushort (no BitConverter)
        public static void ReadMusclesFromBytesAsUShort(int ushortlength, ref byte[] bytes, ref ushort[] muscles, ref int offset)
        {
            if (muscles == null || muscles.Length < ushortlength)
            {
                muscles = new ushort[ushortlength];
            }
            EnsureSize(bytes, offset + ushortlength);

            // Manually read ushort values from bytes
            for (int index = 0; index < ushortlength; index++)
            {
                muscles[index] = ReadUShortFromBytes(ref bytes, ref offset);
                // UnityEngine.BasisDebug.Log("" + muscles[index]);
            }
        }

        // Manual bytes to ushort conversion (without BitConverter)
        private static ushort ReadUShortFromBytes(ref byte[] bytes, ref int offset)
        {
            // Reconstruct the ushort from the byte array
            ushort result = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
            offset += 2;
            return result;
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
            if (bytes.Length < requiredSize)
            {
                throw new ArgumentException("Byte array is too small for the required size. Current Size is " + bytes.Length + " But Required " + requiredSize);
            }
        }

        // Object pool for byte arrays to avoid allocation during runtime
        private class ObjectPool<T>
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
}
