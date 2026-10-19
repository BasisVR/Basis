using System;
using System.Buffers;
using K4os.Compression.LZ4;

namespace Basis.Shims
{
	public static class BasisCompressionShim
	{
		public const int MaxLength = 1024 * 1024;
		const byte Stored = 0, Lz4 = 1;
		public static byte[] Compress(byte[] data) => Compress(data, 0, data == null ? 0 : data.Length);
		public static byte[] Compress(byte[] data, int offset, int count)
		{
			if (data == null) throw new ArgumentNullException(nameof(data));
			if (offset < 0 || count < 0 || count > data.Length - offset) throw new ArgumentOutOfRangeException(nameof(count));
			if (count > MaxLength) throw new ArgumentOutOfRangeException(nameof(count), "Compress takes at most " + MaxLength + " bytes");
			byte[] scratch = ArrayPool<byte>.Shared.Rent(LZ4Codec.MaximumOutputSize(count));
			try
			{
				int encoded = count == 0 ? 0 : LZ4Codec.Encode(data, offset, count, scratch, 0, scratch.Length, LZ4Level.L00_FAST), header = 1 + LengthSize(count);
				byte[] packed;
				if (encoded > 0 && header + encoded <= count)
				{
					packed = new byte[header + encoded];
					packed[0] = Lz4;
					WriteLength(packed, 1, count);
					Buffer.BlockCopy(scratch, 0, packed, header, encoded);
				}
				else
				{
					packed = new byte[1 + count];
					packed[0] = Stored;
					Buffer.BlockCopy(data, offset, packed, 1, count);
				}
				return packed;
			}
			finally { ArrayPool<byte>.Shared.Return(scratch); }
		}
		public static byte[] Decompress(byte[] data) => data == null ? null : Decompress(data, 0, data.Length);
		public static byte[] Decompress(byte[] data, int offset, int count)
		{
			if (data == null || offset < 0 || count < 1 || count > data.Length - offset) return null;
			int at = offset + 1, end = offset + count;
			if (data[offset] == Stored)
			{
				if (count - 1 > MaxLength) return null;
				byte[] raw = new byte[count - 1];
				Buffer.BlockCopy(data, at, raw, 0, raw.Length);
				return raw;
			}
			if (data[offset] != Lz4 || !TryReadLength(data, ref at, end, out int length) || length > (long)(end - at) * 255) return null;
			byte[] decoded = new byte[length];
			return LZ4Codec.Decode(data, at, end - at, decoded, 0, length) == length ? decoded : null;
		}
		static int LengthSize(int value)
		{
			int size = 1;
			while ((value >>= 7) != 0) size++;
			return size;
		}
		static void WriteLength(byte[] buffer, int at, int value)
		{
			for (; value >= 0x80; value >>= 7) buffer[at++] = (byte)(value | 0x80);
			buffer[at] = (byte)value;
		}
		static bool TryReadLength(byte[] buffer, ref int at, int end, out int value)
		{
			value = 0;
			for (int shift = 0; shift <= 14 && at < end; shift += 7)
			{
				int part = buffer[at++];
				value |= (part & 0x7F) << shift;
				if (part < 0x80) return value >= 1 && value <= MaxLength;
			}
			return false;
		}
	}
}
