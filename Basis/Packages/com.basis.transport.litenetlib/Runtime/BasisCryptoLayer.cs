#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Basis.Contrib.Crypto;
using LiteNetLib.Layers;

namespace Basis.Network.Core
{
	/// Per-endpoint AEAD encryption applied at the LiteNetLib socket boundary.
	/// Each connection has its own pair of ChaCha20-Poly1305 keys (one per
	/// direction) established by an X25519 handshake; see <see cref="BasisCryptoHandshake"/>.
	///
	/// Only the user-data-bearing packet properties are encrypted (Unreliable,
	/// Channeled, Merged, CompactMerged). Connection setup, NAT, MTU and out-of-band probe packets
	/// stay cleartext so the handshake itself never depends on a key being present.
	///
	/// Wire layout of an encrypted datagram:
	///   [byte 0 : LiteNetLib header (cleartext, authenticated as AAD)]
	///   [bytes 1..n : ciphertext]
	///   [16 bytes : Poly1305 tag]
	///   [8 bytes  : little-endian nonce counter]
	public sealed class BasisCryptoLayer : PacketLayerBase
	{
		public const int CounterSize = 8;
		public const int Overhead = BasisAeadCipher.TagSize + CounterSize;

		private const byte PropertyMask = 0x1F;
		// Mirrors LiteNetLib.PacketProperty values used for user-data-bearing datagrams.
		private const byte PropUnreliable = 0;
		private const byte PropChanneled = 1;
		private const byte PropMerged = 12;
		private const byte PropCompactMerged = 18;

		private sealed class Session
		{
			public BasisAeadCipher Send = null!;
			public BasisAeadCipher Recv = null!;
			public long SendCounter;
		}

		// Keyed by address+port only. NetPeer (seen outbound) and the plain IPEndPoint seen
		// inbound / at install hash differently under non-native sockets; this comparer makes
		// all three resolve to the same session without allocating per packet.
		private readonly ConcurrentDictionary<IPEndPoint, Session> _sessions
			= new ConcurrentDictionary<IPEndPoint, Session>(EndpointComparer.Instance);

		public BasisCryptoLayer() : base(Overhead) { }

		private sealed class EndpointComparer : IEqualityComparer<IPEndPoint>
		{
			public static readonly EndpointComparer Instance = new EndpointComparer();

			public bool Equals(IPEndPoint x, IPEndPoint y)
			{
				if (ReferenceEquals(x, y)) return true;
				if (x is null || y is null) return false;
				return x.Port == y.Port && x.Address.Equals(y.Address);
			}

			public int GetHashCode(IPEndPoint ep)
			{
				if (ep is null) return 0;
				unchecked { return (ep.Address.GetHashCode() * 397) ^ ep.Port; }
			}
		}

		public int SessionCount => _sessions.Count;

		/// <param name="initialSendCounter">
		/// First nonce counter to use. Pass a value strictly greater than any counter
		/// previously used with these keys when re-installing the same keys for a
		/// reconnect, so a (key, nonce) pair is never reused.
		/// </param>
		public void SetEndpointKeys(IPEndPoint endpoint, byte[] sendKey, byte[] recvKey, long initialSendCounter = 0)
		{
			if (endpoint == null) return;
			var session = new Session
			{
				Send = new BasisAeadCipher(sendKey),
				Recv = new BasisAeadCipher(recvKey),
				SendCounter = initialSendCounter
			};
			if (_sessions.TryRemove(endpoint, out var old)) DisposeSession(old);
			_sessions[endpoint] = session;
		}

		public bool HasEndpoint(IPEndPoint endpoint) => endpoint != null && _sessions.ContainsKey(endpoint);

		public void RemoveEndpoint(IPEndPoint endpoint)
		{
			if (endpoint != null && _sessions.TryRemove(endpoint, out var session)) DisposeSession(session);
		}

		public void RemapEndpoint(IPEndPoint oldEndpoint, IPEndPoint newEndpoint)
		{
			if (oldEndpoint == null || newEndpoint == null) return;
			if (_sessions.TryRemove(oldEndpoint, out var session)) _sessions[newEndpoint] = session;
		}

		public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
		{
			if (length < 1) return;
			byte header = data[offset];
			if (!IsEncryptable((byte)(header & PropertyMask))) return;
			if (endPoint == null || !_sessions.TryGetValue(endPoint, out var session)) return;

			long counter = Interlocked.Increment(ref session.SendCounter);
			Span<byte> nonce = stackalloc byte[BasisAeadCipher.NonceSize];
			WriteCounter(nonce, counter);

			int tagOffset = offset + length;
			session.Send.Seal(nonce, header, data, offset + 1, length - 1, data, tagOffset);
			WriteCounterBytes(data, tagOffset + BasisAeadCipher.TagSize, counter);
			length += Overhead;
		}

		public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
		{
			if (length < 1) return;
			byte header = data[0];
			if (!IsEncryptable((byte)(header & PropertyMask))) return;
			if (endPoint == null || !_sessions.TryGetValue(endPoint, out var session)) return;

			if (length < 1 + Overhead)
			{
				length = 0;
				return;
			}

			int tagOffset = length - Overhead;
			int counterOffset = length - CounterSize;
			long counter = ReadCounterBytes(data, counterOffset);
			Span<byte> nonce = stackalloc byte[BasisAeadCipher.NonceSize];
			WriteCounter(nonce, counter);

			int payloadLength = tagOffset - 1;
			if (!session.Recv.Open(nonce, header, data, 1, payloadLength, data, tagOffset))
			{
				length = 0;
				return;
			}
			length -= Overhead;
		}

		private static bool IsEncryptable(byte property)
			=> property == PropUnreliable || property == PropChanneled ||
			   property == PropMerged || property == PropCompactMerged;

		private static void DisposeSession(Session session)
		{
			session.Send.Dispose();
			session.Recv.Dispose();
		}

		private static void WriteCounter(Span<byte> nonce, long counter)
		{
			nonce.Clear();
			ulong c = (ulong)counter;
			nonce[0] = (byte)c;
			nonce[1] = (byte)(c >> 8);
			nonce[2] = (byte)(c >> 16);
			nonce[3] = (byte)(c >> 24);
			nonce[4] = (byte)(c >> 32);
			nonce[5] = (byte)(c >> 40);
			nonce[6] = (byte)(c >> 48);
			nonce[7] = (byte)(c >> 56);
		}

		private static void WriteCounterBytes(byte[] buffer, int offset, long counter)
		{
			ulong c = (ulong)counter;
			buffer[offset] = (byte)c;
			buffer[offset + 1] = (byte)(c >> 8);
			buffer[offset + 2] = (byte)(c >> 16);
			buffer[offset + 3] = (byte)(c >> 24);
			buffer[offset + 4] = (byte)(c >> 32);
			buffer[offset + 5] = (byte)(c >> 40);
			buffer[offset + 6] = (byte)(c >> 48);
			buffer[offset + 7] = (byte)(c >> 56);
		}

		private static long ReadCounterBytes(byte[] buffer, int offset)
		{
			ulong c = buffer[offset]
				| ((ulong)buffer[offset + 1] << 8)
				| ((ulong)buffer[offset + 2] << 16)
				| ((ulong)buffer[offset + 3] << 24)
				| ((ulong)buffer[offset + 4] << 32)
				| ((ulong)buffer[offset + 5] << 40)
				| ((ulong)buffer[offset + 6] << 48)
				| ((ulong)buffer[offset + 7] << 56);
			return (long)c;
		}
	}
}
