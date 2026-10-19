#nullable enable

using System;

namespace Basis.Contrib.Crypto
{
	/// ChaCha20-Poly1305 AEAD bound to a single 32-byte key, reusable across many packets.
	/// Backed by <see cref="BasisChaCha20Poly1305"/> on every platform, so a packet costs no
	/// allocation and a forged one is rejected without an exception.
	///
	/// Seal/Open operate in place on a caller-owned buffer; the 16-byte tag is written
	/// to / read from a caller-supplied location. Instances are safe for concurrent use.
	public sealed class BasisAeadCipher : IDisposable
	{
		public const int KeySize = BasisChaCha20Poly1305.KeySize;
		public const int NonceSize = BasisChaCha20Poly1305.NonceSize;
		public const int TagSize = BasisChaCha20Poly1305.TagSize;

		private readonly uint[] _key;

		public BasisAeadCipher(ReadOnlySpan<byte> key)
		{
			_key = BasisChaCha20Poly1305.ExpandKey(key);
		}

		/// Encrypts <paramref name="length"/> bytes at <paramref name="buffer"/>[<paramref name="offset"/>]
		/// in place and writes the authentication tag to <paramref name="tagDest"/>[<paramref name="tagOffset"/>].
		public void Seal(ReadOnlySpan<byte> nonce, byte aad, byte[] buffer, int offset, int length, byte[] tagDest, int tagOffset)
		{
			Span<byte> aadSpan = stackalloc byte[1];
			aadSpan[0] = aad;
			BasisChaCha20Poly1305.Seal(_key, nonce, aadSpan, new Span<byte>(buffer, offset, length), new Span<byte>(tagDest, tagOffset, TagSize));
		}

		/// Decrypts <paramref name="length"/> bytes at <paramref name="buffer"/>[<paramref name="offset"/>]
		/// in place, verifying against the tag at <paramref name="tag"/>[<paramref name="tagOffset"/>].
		/// Returns false (and leaves the buffer untouched) on tag mismatch or out-of-range arguments.
		public bool Open(ReadOnlySpan<byte> nonce, byte aad, byte[] buffer, int offset, int length, byte[] tag, int tagOffset)
		{
			if (buffer == null || tag == null || nonce.Length != NonceSize || offset < 0 || length < 0 || offset > buffer.Length - length || tagOffset < 0 || tagOffset > tag.Length - TagSize)
			{
				return false;
			}
			Span<byte> aadSpan = stackalloc byte[1];
			aadSpan[0] = aad;
			return BasisChaCha20Poly1305.Open(_key, nonce, aadSpan, new Span<byte>(buffer, offset, length), new ReadOnlySpan<byte>(tag, tagOffset, TagSize));
		}

		public void Dispose()
		{
		}
	}
}
