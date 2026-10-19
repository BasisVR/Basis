#nullable enable

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Basis.Contrib.Crypto
{
	public static class BasisChaCha20Poly1305
	{
		public const int KeySize = 32;
		public const int NonceSize = 12;
		public const int TagSize = 16;

		private const uint Sigma0 = 0x61707865, Sigma1 = 0x3320646e, Sigma2 = 0x79622d32, Sigma3 = 0x6b206574;

		public static uint[] ExpandKey(ReadOnlySpan<byte> key)
		{
			if (key.Length != KeySize) throw new ArgumentException($"Key must be {KeySize} bytes", nameof(key));
			uint[] words = new uint[8];
			for (int index = 0; index < 8; index++)
			{
				words[index] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(index * 4));
			}
			return words;
		}

		public static void Seal(uint[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> data, Span<byte> tag)
		{
			CheckArguments(key, nonce, tag.Length);
			uint n0 = BinaryPrimitives.ReadUInt32LittleEndian(nonce), n1 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4)), n2 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8));
			ApplyKeyStream(key, n0, n1, n2, data);
			ComputeTag(key, n0, n1, n2, aad, data, tag);
		}

		public static bool Open(uint[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, Span<byte> data, ReadOnlySpan<byte> tag)
		{
			CheckArguments(key, nonce, tag.Length);
			uint n0 = BinaryPrimitives.ReadUInt32LittleEndian(nonce), n1 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4)), n2 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8));
			Span<byte> expected = stackalloc byte[TagSize];
			ComputeTag(key, n0, n1, n2, aad, data, expected);
			int difference = 0;
			for (int index = 0; index < TagSize; index++)
			{
				difference |= expected[index] ^ tag[index];
			}
			if (difference != 0) return false;
			ApplyKeyStream(key, n0, n1, n2, data);
			return true;
		}

		public static void Poly1305(ReadOnlySpan<byte> oneTimeKey, ReadOnlySpan<byte> message, Span<byte> tag)
		{
			if (oneTimeKey.Length != 32) throw new ArgumentException("One-time key must be 32 bytes", nameof(oneTimeKey));
			if (tag.Length < TagSize) throw new ArgumentException("Tag must be at least 16 bytes", nameof(tag));
			Poly1305State mac = new Poly1305State(oneTimeKey);
			mac.Update(message, false);
			mac.Finish(tag);
		}

		public static void Block(uint[] key, uint counter, ReadOnlySpan<byte> nonce, Span<byte> output)
		{
			CheckArguments(key, nonce, TagSize);
			if (output.Length < 64) throw new ArgumentException("Output must be at least 64 bytes", nameof(output));
			Span<uint> words = stackalloc uint[16];
			Block(key, counter, BinaryPrimitives.ReadUInt32LittleEndian(nonce), BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4)), BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8)), words);
			for (int index = 0; index < 16; index++)
			{
				BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(index * 4), words[index]);
			}
		}

		private static void CheckArguments(uint[] key, ReadOnlySpan<byte> nonce, int tagLength)
		{
			if (key == null || key.Length != 8) throw new ArgumentException("Key must be an expanded 32-byte key", nameof(key));
			if (nonce.Length != NonceSize) throw new ArgumentException($"Nonce must be {NonceSize} bytes", nameof(nonce));
			if (tagLength < TagSize) throw new ArgumentException($"Tag must be {TagSize} bytes", "tag");
		}

		private static void ApplyKeyStream(uint[] key, uint n0, uint n1, uint n2, Span<byte> data)
		{
			Span<uint> block = stackalloc uint[16];
			uint counter = 1;
			int offset = 0;
			while (offset < data.Length)
			{
				Block(key, counter++, n0, n1, n2, block);
				int take = Math.Min(64, data.Length - offset);
				if (take == 64)
				{
					Span<byte> chunk = data.Slice(offset, 64);
					for (int word = 0; word < 16; word++)
					{
						Span<byte> part = chunk.Slice(word * 4, 4);
						BinaryPrimitives.WriteUInt32LittleEndian(part, BinaryPrimitives.ReadUInt32LittleEndian(part) ^ block[word]);
					}
				}
				else
				{
					for (int index = 0; index < take; index++)
					{
						data[offset + index] ^= (byte)(block[index >> 2] >> ((index & 3) << 3));
					}
				}
				offset += take;
			}
			block.Clear();
		}

		private static void ComputeTag(uint[] key, uint n0, uint n1, uint n2, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ciphertext, Span<byte> tag)
		{
			Span<uint> block = stackalloc uint[16];
			Block(key, 0, n0, n1, n2, block);
			Span<byte> oneTimeKey = stackalloc byte[32];
			for (int index = 0; index < 8; index++)
			{
				BinaryPrimitives.WriteUInt32LittleEndian(oneTimeKey.Slice(index * 4), block[index]);
			}
			block.Clear();
			Poly1305State mac = new Poly1305State(oneTimeKey);
			oneTimeKey.Clear();
			mac.Update(aad, true);
			mac.Update(ciphertext, true);
			Span<byte> lengths = stackalloc byte[16];
			BinaryPrimitives.WriteUInt64LittleEndian(lengths, (ulong)aad.Length);
			BinaryPrimitives.WriteUInt64LittleEndian(lengths.Slice(8), (ulong)ciphertext.Length);
			mac.Update(lengths, true);
			mac.Finish(tag);
		}

		private static void Block(uint[] key, uint counter, uint n0, uint n1, uint n2, Span<uint> output)
		{
			uint x0 = Sigma0, x1 = Sigma1, x2 = Sigma2, x3 = Sigma3;
			uint x4 = key[0], x5 = key[1], x6 = key[2], x7 = key[3], x8 = key[4], x9 = key[5], x10 = key[6], x11 = key[7];
			uint x12 = counter, x13 = n0, x14 = n1, x15 = n2;
			for (int round = 0; round < 10; round++)
			{
				QuarterRound(ref x0, ref x4, ref x8, ref x12);
				QuarterRound(ref x1, ref x5, ref x9, ref x13);
				QuarterRound(ref x2, ref x6, ref x10, ref x14);
				QuarterRound(ref x3, ref x7, ref x11, ref x15);
				QuarterRound(ref x0, ref x5, ref x10, ref x15);
				QuarterRound(ref x1, ref x6, ref x11, ref x12);
				QuarterRound(ref x2, ref x7, ref x8, ref x13);
				QuarterRound(ref x3, ref x4, ref x9, ref x14);
			}
			output[0] = x0 + Sigma0;
			output[1] = x1 + Sigma1;
			output[2] = x2 + Sigma2;
			output[3] = x3 + Sigma3;
			output[4] = x4 + key[0];
			output[5] = x5 + key[1];
			output[6] = x6 + key[2];
			output[7] = x7 + key[3];
			output[8] = x8 + key[4];
			output[9] = x9 + key[5];
			output[10] = x10 + key[6];
			output[11] = x11 + key[7];
			output[12] = x12 + counter;
			output[13] = x13 + n0;
			output[14] = x14 + n1;
			output[15] = x15 + n2;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
		{
			a += b; d ^= a; d = (d << 16) | (d >> 16);
			c += d; b ^= c; b = (b << 12) | (b >> 20);
			a += b; d ^= a; d = (d << 8) | (d >> 24);
			c += d; b ^= c; b = (b << 7) | (b >> 25);
		}

		private struct Poly1305State
		{
			private const uint Mask26 = 0x3ffffff;
			private readonly uint r0, r1, r2, r3, r4, s1, s2, s3, s4, pad0, pad1, pad2, pad3;
			private uint h0, h1, h2, h3, h4;

			public Poly1305State(ReadOnlySpan<byte> key)
			{
				r0 = Read(key, 0) & 0x3ffffff;
				r1 = (Read(key, 3) >> 2) & 0x3ffff03;
				r2 = (Read(key, 6) >> 4) & 0x3ffc0ff;
				r3 = (Read(key, 9) >> 6) & 0x3f03fff;
				r4 = (Read(key, 12) >> 8) & 0x00fffff;
				s1 = r1 * 5;
				s2 = r2 * 5;
				s3 = r3 * 5;
				s4 = r4 * 5;
				pad0 = Read(key, 16);
				pad1 = Read(key, 20);
				pad2 = Read(key, 24);
				pad3 = Read(key, 28);
				h0 = h1 = h2 = h3 = h4 = 0;
			}

			public void Update(ReadOnlySpan<byte> message, bool zeroPad)
			{
				int full = message.Length & ~15;
				for (int offset = 0; offset < full; offset += 16)
				{
					AddBlock(message.Slice(offset, 16), 1u << 24);
				}
				int rest = message.Length - full;
				if (rest == 0) return;
				Span<byte> last = stackalloc byte[16];
				last.Clear();
				message.Slice(full).CopyTo(last);
				if (zeroPad)
				{
					AddBlock(last, 1u << 24);
				}
				else
				{
					last[rest] = 1;
					AddBlock(last, 0);
				}
			}

			private void AddBlock(ReadOnlySpan<byte> block, uint hibit)
			{
				h0 += Read(block, 0) & Mask26;
				h1 += (Read(block, 3) >> 2) & Mask26;
				h2 += (Read(block, 6) >> 4) & Mask26;
				h3 += (Read(block, 9) >> 6) & Mask26;
				h4 += (Read(block, 12) >> 8) | hibit;

				ulong d0 = (ulong)h0 * r0 + (ulong)h1 * s4 + (ulong)h2 * s3 + (ulong)h3 * s2 + (ulong)h4 * s1;
				ulong d1 = (ulong)h0 * r1 + (ulong)h1 * r0 + (ulong)h2 * s4 + (ulong)h3 * s3 + (ulong)h4 * s2;
				ulong d2 = (ulong)h0 * r2 + (ulong)h1 * r1 + (ulong)h2 * r0 + (ulong)h3 * s4 + (ulong)h4 * s3;
				ulong d3 = (ulong)h0 * r3 + (ulong)h1 * r2 + (ulong)h2 * r1 + (ulong)h3 * r0 + (ulong)h4 * s4;
				ulong d4 = (ulong)h0 * r4 + (ulong)h1 * r3 + (ulong)h2 * r2 + (ulong)h3 * r1 + (ulong)h4 * r0;

				d1 += d0 >> 26; h0 = (uint)d0 & Mask26;
				d2 += d1 >> 26; h1 = (uint)d1 & Mask26;
				d3 += d2 >> 26; h2 = (uint)d2 & Mask26;
				d4 += d3 >> 26; h3 = (uint)d3 & Mask26;
				ulong carry = (d4 >> 26) * 5 + h0;
				h4 = (uint)d4 & Mask26;
				h0 = (uint)carry & Mask26;
				h1 += (uint)(carry >> 26);
			}

			public void Finish(Span<byte> tag)
			{
				uint c = h1 >> 26; h1 &= Mask26;
				h2 += c; c = h2 >> 26; h2 &= Mask26;
				h3 += c; c = h3 >> 26; h3 &= Mask26;
				h4 += c; c = h4 >> 26; h4 &= Mask26;
				h0 += c * 5; c = h0 >> 26; h0 &= Mask26;
				h1 += c;

				uint g0 = h0 + 5; c = g0 >> 26; g0 &= Mask26;
				uint g1 = h1 + c; c = g1 >> 26; g1 &= Mask26;
				uint g2 = h2 + c; c = g2 >> 26; g2 &= Mask26;
				uint g3 = h3 + c; c = g3 >> 26; g3 &= Mask26;
				uint g4 = h4 + c - (1u << 26);

				uint mask = (g4 >> 31) - 1;
				g0 &= mask; g1 &= mask; g2 &= mask; g3 &= mask; g4 &= mask;
				mask = ~mask;
				h0 = (h0 & mask) | g0;
				h1 = (h1 & mask) | g1;
				h2 = (h2 & mask) | g2;
				h3 = (h3 & mask) | g3;
				h4 = (h4 & mask) | g4;

				uint w0 = h0 | (h1 << 26);
				uint w1 = (h1 >> 6) | (h2 << 20);
				uint w2 = (h2 >> 12) | (h3 << 14);
				uint w3 = (h3 >> 18) | (h4 << 8);

				ulong f = (ulong)w0 + pad0;
				BinaryPrimitives.WriteUInt32LittleEndian(tag, (uint)f);
				f = (ulong)w1 + pad1 + (f >> 32);
				BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(4), (uint)f);
				f = (ulong)w2 + pad2 + (f >> 32);
				BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(8), (uint)f);
				f = (ulong)w3 + pad3 + (f >> 32);
				BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(12), (uint)f);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			private static uint Read(ReadOnlySpan<byte> source, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
		}
	}
}
