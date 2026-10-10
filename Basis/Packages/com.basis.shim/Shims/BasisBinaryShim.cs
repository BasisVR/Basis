using System;
using System.Buffers.Binary;
using UnityEngine;

namespace Basis.Shims
{
	public static class BasisBinaryShim
	{
		public static int WriteInt32(byte[] buffer, int offset, int value)
		{
			BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(buffer, offset, 4), value);
			return offset + 4;
		}

		public static int ReadInt32(byte[] buffer, int offset)
		{
			return BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buffer, offset, 4));
		}

		public static int WriteUInt16(byte[] buffer, int offset, ushort value)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, offset, 2), value);
			return offset + 2;
		}

		public static ushort ReadUInt16(byte[] buffer, int offset)
		{
			return BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(buffer, offset, 2));
		}

		public static int WriteSingle(byte[] buffer, int offset, float value)
		{
			return WriteInt32(buffer, offset, BitConverter.SingleToInt32Bits(value));
		}

		public static float ReadSingle(byte[] buffer, int offset)
		{
			return BitConverter.Int32BitsToSingle(ReadInt32(buffer, offset));
		}

		public static int WriteVector3(byte[] buffer, int offset, Vector3 value)
		{
			Span<byte> span = new Span<byte>(buffer, offset, 12);
			BinaryPrimitives.WriteInt32LittleEndian(span, BitConverter.SingleToInt32Bits(value.x));
			BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), BitConverter.SingleToInt32Bits(value.y));
			BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), BitConverter.SingleToInt32Bits(value.z));
			return offset + 12;
		}

		public static Vector3 ReadVector3(byte[] buffer, int offset)
		{
			ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, offset, 12);
			return new Vector3(
				BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span)),
				BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4))),
				BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8))));
		}

		public static int WriteQuaternion(byte[] buffer, int offset, Quaternion value)
		{
			Span<byte> span = new Span<byte>(buffer, offset, 16);
			BinaryPrimitives.WriteInt32LittleEndian(span, BitConverter.SingleToInt32Bits(value.x));
			BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), BitConverter.SingleToInt32Bits(value.y));
			BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), BitConverter.SingleToInt32Bits(value.z));
			BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12), BitConverter.SingleToInt32Bits(value.w));
			return offset + 16;
		}

		public static Quaternion ReadQuaternion(byte[] buffer, int offset)
		{
			ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, offset, 16);
			return new Quaternion(
				BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span)),
				BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4))),
				BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8))),
				BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12))));
		}

		public static int WriteInt32s(byte[] buffer, int offset, int[] values)
		{
			if (values == null)
			{
				throw new ArgumentNullException(nameof(values));
			}
			Span<byte> span = new Span<byte>(buffer, offset, values.Length * 4);
			for (int i = 0; i < values.Length; i++)
			{
				BinaryPrimitives.WriteInt32LittleEndian(span.Slice(i * 4), values[i]);
			}
			return offset + values.Length * 4;
		}

		public static int ReadInt32s(byte[] buffer, int offset, int[] into)
		{
			if (into == null)
			{
				throw new ArgumentNullException(nameof(into));
			}
			ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, offset, into.Length * 4);
			for (int i = 0; i < into.Length; i++)
			{
				into[i] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i * 4));
			}
			return offset + into.Length * 4;
		}

		public static int WriteSingles(byte[] buffer, int offset, float[] values)
		{
			if (values == null)
			{
				throw new ArgumentNullException(nameof(values));
			}
			Span<byte> span = new Span<byte>(buffer, offset, values.Length * 4);
			for (int i = 0; i < values.Length; i++)
			{
				BinaryPrimitives.WriteInt32LittleEndian(span.Slice(i * 4), BitConverter.SingleToInt32Bits(values[i]));
			}
			return offset + values.Length * 4;
		}

		public static int ReadSingles(byte[] buffer, int offset, float[] into)
		{
			if (into == null)
			{
				throw new ArgumentNullException(nameof(into));
			}
			ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, offset, into.Length * 4);
			for (int i = 0; i < into.Length; i++)
			{
				into[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i * 4)));
			}
			return offset + into.Length * 4;
		}

		public static int WriteVector3s(byte[] buffer, int offset, Vector3[] values)
		{
			if (values == null)
			{
				throw new ArgumentNullException(nameof(values));
			}
			Span<byte> span = new Span<byte>(buffer, offset, values.Length * 12);
			for (int i = 0; i < values.Length; i++)
			{
				Vector3 value = values[i];
				Span<byte> slot = span.Slice(i * 12);
				BinaryPrimitives.WriteInt32LittleEndian(slot, BitConverter.SingleToInt32Bits(value.x));
				BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(4), BitConverter.SingleToInt32Bits(value.y));
				BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(8), BitConverter.SingleToInt32Bits(value.z));
			}
			return offset + values.Length * 12;
		}

		public static int ReadVector3s(byte[] buffer, int offset, Vector3[] into)
		{
			if (into == null)
			{
				throw new ArgumentNullException(nameof(into));
			}
			ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, offset, into.Length * 12);
			for (int i = 0; i < into.Length; i++)
			{
				ReadOnlySpan<byte> slot = span.Slice(i * 12);
				into[i] = new Vector3(
					BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(slot)),
					BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(slot.Slice(4))),
					BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(slot.Slice(8))));
			}
			return offset + into.Length * 12;
		}

		public static int WriteQuaternions(byte[] buffer, int offset, Quaternion[] values)
		{
			if (values == null)
			{
				throw new ArgumentNullException(nameof(values));
			}
			Span<byte> span = new Span<byte>(buffer, offset, values.Length * 16);
			for (int i = 0; i < values.Length; i++)
			{
				Quaternion value = values[i];
				Span<byte> slot = span.Slice(i * 16);
				BinaryPrimitives.WriteInt32LittleEndian(slot, BitConverter.SingleToInt32Bits(value.x));
				BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(4), BitConverter.SingleToInt32Bits(value.y));
				BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(8), BitConverter.SingleToInt32Bits(value.z));
				BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(12), BitConverter.SingleToInt32Bits(value.w));
			}
			return offset + values.Length * 16;
		}

		public static int ReadQuaternions(byte[] buffer, int offset, Quaternion[] into)
		{
			if (into == null)
			{
				throw new ArgumentNullException(nameof(into));
			}
			ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, offset, into.Length * 16);
			for (int i = 0; i < into.Length; i++)
			{
				ReadOnlySpan<byte> slot = span.Slice(i * 16);
				into[i] = new Quaternion(
					BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(slot)),
					BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(slot.Slice(4))),
					BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(slot.Slice(8))),
					BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(slot.Slice(12))));
			}
			return offset + into.Length * 16;
		}
	}
}
