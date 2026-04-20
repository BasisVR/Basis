using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteNetLib.Utils
{
    public static class FastBitConverter
    {
#if (LITENETLIB_UNSAFE || NETCOREAPP3_1 || NET5_0 || NETCOREAPP3_0_OR_GREATER) && !BIGENDIAN
#if LITENETLIB_UNSAFE
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void GetBytes<T>(byte[] bytes, int startIndex, T value) where T : unmanaged
        {
            int size = sizeof(T);
            if (bytes.Length < startIndex + size)
                ThrowIndexOutOfRangeException();
#if NET8_0_OR_GREATER
            Unsafe.WriteUnaligned(ref bytes[startIndex], value);
#elif NETCOREAPP3_1 || NET5_0 || NETCOREAPP3_0_OR_GREATER
            Unsafe.As<byte, T>(ref bytes[startIndex]) = value;
#else
            fixed (byte* ptr = &bytes[startIndex])
            {
#if UNITY_ANDROID
                // On some android systems, assigning *(T*)ptr throws a NRE if
                // the ptr isn't aligned (i.e. if Position is 1,2,3,5, etc.).
                // Here we have to use memcpy.
                //
                // => we can't get a pointer of a struct in C# without
                //    marshalling allocations
                // => instead, we stack allocate an array of type T and use that
                // => stackalloc avoids GC and is very fast. it only works for
                //    value types, but all blittable types are anyway.
                T* valueBuffer = stackalloc T[1] { value };
                UnsafeUtility.MemCpy(ptr, valueBuffer, size);
#else
                *(T*)ptr = value;
#endif
            }
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe T Read<T>(byte[] data, int offset) where T : unmanaged
        {
            if (data.Length < offset + sizeof(T))
                ThrowIndexOutOfRangeException();
#if NET8_0_OR_GREATER
            return Unsafe.ReadUnaligned<T>(ref data[offset]);
#elif NETCOREAPP3_1 || NET5_0 || NETCOREAPP3_0_OR_GREATER
            return Unsafe.As<byte, T>(ref data[offset]);
#else
            fixed (byte* ptr = &data[offset])
            {
                return *(T*)ptr;
            }
#endif
        }
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetBytes<T>(byte[] bytes, int startIndex, T value) where T : unmanaged
        {
            if (bytes.Length < startIndex + Unsafe.SizeOf<T>())
                ThrowIndexOutOfRangeException();
            Unsafe.As<byte, T>(ref bytes[startIndex]) = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(byte[] data, int offset) where T : unmanaged
        {
            if (data.Length < offset + Unsafe.SizeOf<T>())
                ThrowIndexOutOfRangeException();
            return Unsafe.As<byte, T>(ref data[offset]);
        }
#endif

        private static void ThrowIndexOutOfRangeException() => throw new IndexOutOfRangeException();
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe void GetBytes<T>(byte[] data, int position, T value) where T : unmanaged
        {
            fixed (byte* ptr = &data[position])
            {
                *(T*)ptr = value;
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct ConverterHelperDouble
        {
            [FieldOffset(0)]
            public ulong Along;

            [FieldOffset(0)]
            public double Adouble;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct ConverterHelperFloat
        {
            [FieldOffset(0)]
            public int Aint;

            [FieldOffset(0)]
            public float Afloat;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteLittleEndian(byte[] buffer, int offset, ulong data)
        {
#if BIGENDIAN
            buffer[offset + 7] = (byte)(data);
            buffer[offset + 6] = (byte)(data >> 8);
            buffer[offset + 5] = (byte)(data >> 16);
            buffer[offset + 4] = (byte)(data >> 24);
            buffer[offset + 3] = (byte)(data >> 32);
            buffer[offset + 2] = (byte)(data >> 40);
            buffer[offset + 1] = (byte)(data >> 48);
            buffer[offset    ] = (byte)(data >> 56);
#else
            buffer[offset] = (byte)(data);
            buffer[offset + 1] = (byte)(data >> 8);
            buffer[offset + 2] = (byte)(data >> 16);
            buffer[offset + 3] = (byte)(data >> 24);
            buffer[offset + 4] = (byte)(data >> 32);
            buffer[offset + 5] = (byte)(data >> 40);
            buffer[offset + 6] = (byte)(data >> 48);
            buffer[offset + 7] = (byte)(data >> 56);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteLittleEndian(byte[] buffer, int offset, int data)
        {
#if BIGENDIAN
            buffer[offset + 3] = (byte)(data);
            buffer[offset + 2] = (byte)(data >> 8);
            buffer[offset + 1] = (byte)(data >> 16);
            buffer[offset    ] = (byte)(data >> 24);
#else
            buffer[offset] = (byte)(data);
            buffer[offset + 1] = (byte)(data >> 8);
            buffer[offset + 2] = (byte)(data >> 16);
            buffer[offset + 3] = (byte)(data >> 24);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteLittleEndian(byte[] buffer, int offset, short data)
        {
#if BIGENDIAN
            buffer[offset + 1] = (byte)(data);
            buffer[offset    ] = (byte)(data >> 8);
#else
            buffer[offset] = (byte)(data);
            buffer[offset + 1] = (byte)(data >> 8);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetBytes(byte[] bytes, int startIndex, double value)
        {
            ConverterHelperDouble ch = new ConverterHelperDouble { Adouble = value };
            WriteLittleEndian(bytes, startIndex, ch.Along);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetBytes(byte[] bytes, int startIndex, float value)
        {
            ConverterHelperFloat ch = new ConverterHelperFloat { Afloat = value };
            WriteLittleEndian(bytes, startIndex, ch.Aint);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetBytes(byte[] bytes, int startIndex, short value)
        {
            WriteLittleEndian(bytes, startIndex, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetBytes(byte[] bytes, int startIndex, ushort value)
        {
            WriteLittleEndian(bytes, startIndex, (short)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetBytes(byte[] bytes, int startIndex, int value)
        {
            WriteLittleEndian(bytes, startIndex, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetBytes(byte[] bytes, int startIndex, uint value)
        {
            WriteLittleEndian(bytes, startIndex, (int)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetBytes(byte[] bytes, int startIndex, long value)
        {
            WriteLittleEndian(bytes, startIndex, (ulong)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetBytes(byte[] bytes, int startIndex, ulong value)
        {
            WriteLittleEndian(bytes, startIndex, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe T Read<T>(byte[] data, int offset) where T : unmanaged
        {
            fixed (byte* ptr = &data[offset])
            {
                return *(T*)ptr;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReadUShort(byte[] data, int offset)
        {
#if BIGENDIAN
            return (ushort)(data[offset + 1] | (data[offset] << 8));
#else
            return (ushort)(data[offset] | (data[offset + 1] << 8));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReadShort(byte[] data, int offset)
        {
#if BIGENDIAN
            return (short)(data[offset + 1] | (data[offset] << 8));
#else
            return (short)(data[offset] | (data[offset + 1] << 8));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt(byte[] data, int offset)
        {
#if BIGENDIAN
            return data[offset + 3] | (data[offset + 2] << 8) | (data[offset + 1] << 16) | (data[offset] << 24);
#else
            return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadUInt(byte[] data, int offset)
        {
            return (uint)ReadInt(data, offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadLong(byte[] data, int offset)
        {
#if BIGENDIAN
            return (long)(
                (ulong)data[offset + 7] |
                ((ulong)data[offset + 6] << 8) |
                ((ulong)data[offset + 5] << 16) |
                ((ulong)data[offset + 4] << 24) |
                ((ulong)data[offset + 3] << 32) |
                ((ulong)data[offset + 2] << 40) |
                ((ulong)data[offset + 1] << 48) |
                ((ulong)data[offset] << 56));
#else
            return (long)(
                (ulong)data[offset] |
                ((ulong)data[offset + 1] << 8) |
                ((ulong)data[offset + 2] << 16) |
                ((ulong)data[offset + 3] << 24) |
                ((ulong)data[offset + 4] << 32) |
                ((ulong)data[offset + 5] << 40) |
                ((ulong)data[offset + 6] << 48) |
                ((ulong)data[offset + 7] << 56));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadULong(byte[] data, int offset)
        {
            return (ulong)ReadLong(data, offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ReadFloat(byte[] data, int offset)
        {
            ConverterHelperFloat ch = new ConverterHelperFloat { Aint = ReadInt(data, offset) };
            return ch.Afloat;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ReadDouble(byte[] data, int offset)
        {
            ConverterHelperDouble ch = new ConverterHelperDouble { Along = (ulong)ReadLong(data, offset) };
            return ch.Adouble;
        }
#endif
    }
}
