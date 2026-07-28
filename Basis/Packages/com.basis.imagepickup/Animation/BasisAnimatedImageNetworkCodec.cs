using System;
using System.IO;
using Basis.Scripts.Common;
using Unity.Collections;

namespace Basis.ImagePickup
{
    internal static class BasisAnimatedImageNetworkCodec
    {
        public const int AnimationHeaderSize =
            1 + BasisGuid128.SerializedSize + 1 + sizeof(int) * 2 + sizeof(long);
        public const int AnimationChunkHeaderSize =
            1 + BasisGuid128.SerializedSize + sizeof(int) * 2;

        public static void WriteGuid(BinaryWriter writer, Guid value)
        {
            BasisGuid128 id = BasisGuid128.FromGuid(value);
            writer.Write(id.Low);
            writer.Write(id.High);
        }

        public static void WriteGuid(NativeArray<byte> destination, int offset, BasisGuid128 value)
        {
            for (int i = 0; i < 8; i++)
                destination[offset + i] = (byte)(value.Low >> (i * 8));
            for (int i = 0; i < 8; i++)
                destination[offset + i + 8] = (byte)(value.High >> (i * 8));
        }

        public static void WriteInt32(NativeArray<byte> destination, int offset, int value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }

        public static void WriteInt64(NativeArray<byte> destination, int offset, long value)
        {
            ulong unsigned = (ulong)value;
            for (int i = 0; i < 8; i++)
                destination[offset + i] = (byte)(unsigned >> (i * 8));
        }

        public static int ReadInt32(NativeArray<byte> source, int offset)
        {
            return source[offset]
                | (source[offset + 1] << 8)
                | (source[offset + 2] << 16)
                | (source[offset + 3] << 24);
        }

        public static long ReadInt64(NativeArray<byte> source, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value |= (ulong)source[offset + i] << (i * 8);
            return (long)value;
        }
    }
}
