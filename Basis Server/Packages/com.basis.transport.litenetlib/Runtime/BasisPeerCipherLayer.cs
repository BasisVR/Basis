#nullable enable

using System.Net;
using Basis.Contrib.Crypto;
using LiteNetLib.Layers;

namespace Basis.Network.Core
{
	/// One direction of an AEAD-protected connection, installed on a single LiteNetLib peer with
	/// <c>SetOutboundPacketLayer</c> (seals) or <c>SetInboundPacketLayer</c> (opens). Same wire
	/// layout as <see cref="BasisCryptoLayer"/>, but bound to the peer instead of an endpoint, so it
	/// follows the connection across an address change.
	///
	/// Created without a key it only reserves room: outbound it seals nothing yet, inbound it drops
	/// every user-data datagram until <see cref="SetKey"/> supplies the key. Once keyed, outbound
	/// seals every datagram. Inbound drops user data that does not open with the key, and after the
	/// first datagram that does open it drops anything unsealed, control packets included, so only
	/// the key holder can keep the connection alive. A counter already seen, or more than
	/// <see cref="ReplayWindowSize"/> behind the newest, is dropped too (RFC 4303, section 3.4.3);
	/// the window only moves on datagrams that authenticate.
	public sealed class BasisPeerCipherLayer : PacketLayerBase
	{
		public const int ReplayWindowSize = BasisReplayWindow.Size;
		private const byte PropertyMask = 0x1F;
		private const byte PropShutdownOk = 13;
		private const byte PropPeerNotFound = 14;

		private readonly BasisReplayWindow _window = new BasisReplayWindow();
		private volatile BasisAeadCipher? _cipher;
		private volatile bool _proven;
		private long _sendCounter;

		public BasisPeerCipherLayer() : base(BasisCryptoLayer.Overhead)
		{
		}

		public BasisPeerCipherLayer(byte[] key) : this()
		{
			SetKey(key);
		}

		public bool HasKey => _cipher != null;

		public bool Proven => _proven;

		public void SetKey(byte[] key)
		{
			_cipher = new BasisAeadCipher(key);
		}

		public override int ExtraSizeFor(byte property) => _cipher == null || IsPeerNotFound(property) ? 0 : BasisCryptoLayer.Overhead;

		public override int InboundExtraSizeFor(byte property) => _proven && !IsPeerNotFound(property) ? BasisCryptoLayer.Overhead : 0;

		public override bool AllowsEndPointChange => _proven;

		private static bool IsPeerNotFound(byte header) => (header & PropertyMask) == PropPeerNotFound;

		public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
		{
			BasisAeadCipher? cipher = _cipher;
			if (cipher == null || length < 1 || IsPeerNotFound(data[offset])) return;
			BasisCryptoLayer.Seal(cipher, ref _sendCounter, data, offset, ref length);
		}

		public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
		{
			if (length < 1) return;
			bool userData = BasisCryptoLayer.IsEncryptableHeader(data[0]);
			BasisAeadCipher? cipher = _cipher;
			if (cipher == null)
			{
				if (userData) length = 0;
				return;
			}
			if (BasisCryptoLayer.TryOpen(cipher, _window, data, ref length))
			{
				_proven = true;
				return;
			}
			if (userData || (_proven && (data[0] & PropertyMask) != PropShutdownOk))
			{
				length = 0;
			}
		}
	}
}
