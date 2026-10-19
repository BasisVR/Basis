#nullable enable

using System;
using System.Buffers.Binary;
using System.Threading;
using Basis.Contrib.Crypto;

namespace Basis.Network.Core
{
	public sealed class BasisMessageCipher
	{
		public const int CounterSize = 8;
		public const int Overhead = BasisAeadCipher.TagSize + CounterSize;

		private const byte Domain = 0x4D;

		private readonly BasisAeadCipher _cipher;
		private readonly long[] _sendCounters;
		private readonly BasisReplayWindow[] _windows;

		public BasisMessageCipher(byte[] key, int streams = 1)
		{
			if (streams < 1 || streams > byte.MaxValue + 1) throw new ArgumentOutOfRangeException(nameof(streams));
			_cipher = new BasisAeadCipher(key);
			_sendCounters = new long[streams];
			_windows = new BasisReplayWindow[streams];
			for (int index = 0; index < streams; index++)
			{
				_windows[index] = new BasisReplayWindow();
			}
		}

		public void Seal(int stream, byte channel, DeliveryMethod method, byte[] buffer, int offset, int length)
		{
			long counter = Interlocked.Increment(ref _sendCounters[stream]);
			Span<byte> nonce = stackalloc byte[BasisAeadCipher.NonceSize];
			WriteNonce(nonce, counter, stream, channel, method);
			int tagOffset = offset + length;
			_cipher.Seal(nonce, Domain, buffer, offset, length, buffer, tagOffset);
			BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(buffer, tagOffset + BasisAeadCipher.TagSize, CounterSize), counter);
		}

		public bool TryOpen(int stream, byte channel, DeliveryMethod method, byte[] buffer, int offset, int length, out int plainLength)
		{
			plainLength = length - Overhead;
			if (plainLength < 0) return false;
			int tagOffset = offset + plainLength;
			long counter = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(buffer, tagOffset + BasisAeadCipher.TagSize, CounterSize));
			if (!_windows[stream].CouldAccept(counter)) return false;
			Span<byte> nonce = stackalloc byte[BasisAeadCipher.NonceSize];
			WriteNonce(nonce, counter, stream, channel, method);
			return _cipher.Open(nonce, Domain, buffer, offset, plainLength, buffer, tagOffset) && _windows[stream].Accept(counter);
		}

		private static void WriteNonce(Span<byte> nonce, long counter, int stream, byte channel, DeliveryMethod method)
		{
			BinaryPrimitives.WriteInt64LittleEndian(nonce, counter);
			nonce[8] = channel;
			nonce[9] = (byte)method;
			nonce[10] = (byte)stream;
			nonce[11] = Domain;
		}
	}
}
