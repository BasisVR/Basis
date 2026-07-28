using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Basis.Scripts.Common
{
    /// <summary>
    /// Blittable 128-bit identifier for Burst jobs and native containers.
    /// Byte serialization matches <see cref="Guid.TryWriteBytes(Span{byte})"/> and
    /// <see cref="Guid.ToByteArray"/>. Changing that layout requires a protocol version bump.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct BasisGuid128 : IEquatable<BasisGuid128>
    {
        public const int SerializedSize = 16;

        public readonly ulong Low;
        public readonly ulong High;

        public BasisGuid128(ulong low, ulong high)
        {
            Low = low;
            High = high;
        }

        public static BasisGuid128 FromGuid(Guid value)
        {
            Span<byte> bytes = stackalloc byte[SerializedSize];
            value.TryWriteBytes(bytes);
            return ReadFrom(bytes);
        }

        public static BasisGuid128 ReadFrom(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < SerializedSize)
                throw new ArgumentException("A GUID requires 16 bytes.", nameof(bytes));

            return new BasisGuid128(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(sizeof(ulong)))
            );
        }

        public Guid ToGuid()
        {
            Span<byte> bytes = stackalloc byte[SerializedSize];
            WriteTo(bytes);
            return new Guid(bytes);
        }

        public void WriteTo(Span<byte> destination)
        {
            if (destination.Length < SerializedSize)
                throw new ArgumentException("A GUID requires 16 bytes.", nameof(destination));

            BinaryPrimitives.WriteUInt64LittleEndian(destination, Low);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(sizeof(ulong)), High);
        }

        public bool Equals(BasisGuid128 other)
        {
            return Low == other.Low && High == other.High;
        }

        public override bool Equals(object obj)
        {
            return obj is BasisGuid128 other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Low ^ (int)(Low >> 32);
                return (hash * 397) ^ (int)High ^ (int)(High >> 32);
            }
        }

        public override string ToString()
        {
            return ToGuid().ToString();
        }

        public static bool operator ==(BasisGuid128 left, BasisGuid128 right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(BasisGuid128 left, BasisGuid128 right)
        {
            return !left.Equals(right);
        }
    }
}
