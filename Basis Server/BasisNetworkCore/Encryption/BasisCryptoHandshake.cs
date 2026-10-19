#nullable enable

using System;
using System.Text;
using Basis.Contrib.Crypto;

namespace Basis.Network.Core
{
	/// X25519 + HKDF-SHA256 key agreement for the encrypted peer-to-peer (direct) link, and for
	/// the client-to-server key set up during DID login. The two peers
	/// exchange ephemeral public keys through the server's signalling
	/// channel and each derives the same two directional keys, because ECDH(myPriv,
	/// peerPub) is symmetric; the transcript (both public keys) is folded into the HKDF
	/// salt for channel binding.
	public static class BasisCryptoHandshake
	{
		public const int PublicKeySize = BasisX25519.KeySize;
		public const int PrivateKeySize = BasisX25519.KeySize;
		public const int KeySize = BasisAeadCipher.KeySize;

		private static readonly byte[] InfoAB = Encoding.ASCII.GetBytes("basis-crypto-v1-ab");
		private static readonly byte[] InfoBA = Encoding.ASCII.GetBytes("basis-crypto-v1-ba");
		private static readonly byte[] InfoClientToServer = Encoding.ASCII.GetBytes("basis-server-link-v1-c2s");

		public static void GenerateKeyPair(out byte[] privateKey, out byte[] publicKey)
			=> BasisX25519.GenerateKeyPair(out privateKey, out publicKey);

		/// Derives the directional keys for a peer-to-peer link. Role is decided by
		/// public-key ordering so both ends agree without extra signalling.
		public static bool DerivePeerKeys(
			ReadOnlySpan<byte> myPrivate,
			ReadOnlySpan<byte> myPublic,
			ReadOnlySpan<byte> peerPublic,
			out byte[] sendKey,
			out byte[] recvKey)
		{
			sendKey = Array.Empty<byte>();
			recvKey = Array.Empty<byte>();
			try
			{
				int cmp = Compare(myPublic, peerPublic);
				if (cmp == 0) return false;
				bool iAmA = cmp < 0;

				ReadOnlySpan<byte> aPub = iAmA ? myPublic : peerPublic;
				ReadOnlySpan<byte> bPub = iAmA ? peerPublic : myPublic;

				byte[] shared = BasisX25519.Agree(myPrivate, peerPublic);
				byte[] salt = Concat(aPub, bPub);
				byte[] keyAB = BasisHkdf.DeriveKey(shared, salt, InfoAB, KeySize);
				byte[] keyBA = BasisHkdf.DeriveKey(shared, salt, InfoBA, KeySize);

				if (iAmA)
				{
					sendKey = keyAB;
					recvKey = keyBA;
				}
				else
				{
					sendKey = keyBA;
					recvKey = keyAB;
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		/// Derives the client-to-server key for a server connection, on each end from its own
		/// ephemeral private key and the other end's public key; both public keys, client first,
		/// are the HKDF salt, so the key is bound to the keys the login signature covers.
		public static bool DeriveClientKey(ReadOnlySpan<byte> clientPrivate, ReadOnlySpan<byte> clientPublic, ReadOnlySpan<byte> serverPublic, out byte[] key)
			=> DeriveClientToServerKey(clientPrivate, serverPublic, clientPublic, serverPublic, out key);

		public static bool DeriveServerKey(ReadOnlySpan<byte> serverPrivate, ReadOnlySpan<byte> clientPublic, ReadOnlySpan<byte> serverPublic, out byte[] key)
			=> DeriveClientToServerKey(serverPrivate, clientPublic, clientPublic, serverPublic, out key);

		private static bool DeriveClientToServerKey(
			ReadOnlySpan<byte> myPrivate,
			ReadOnlySpan<byte> peerPublic,
			ReadOnlySpan<byte> clientPublic,
			ReadOnlySpan<byte> serverPublic,
			out byte[] key)
		{
			key = Array.Empty<byte>();
			if (myPrivate.Length != PrivateKeySize || peerPublic.Length != PublicKeySize ||
				clientPublic.Length != PublicKeySize || serverPublic.Length != PublicKeySize)
			{
				return false;
			}
			try
			{
				// BouncyCastle throws on an all-zero shared secret (a low-order peer key).
				byte[] shared = BasisX25519.Agree(myPrivate, peerPublic);
				key = BasisHkdf.DeriveKey(shared, Concat(clientPublic, serverPublic), InfoClientToServer, KeySize);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
		{
			var result = new byte[a.Length + b.Length];
			a.CopyTo(result);
			b.CopyTo(result.AsSpan(a.Length));
			return result;
		}

		private static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
		{
			int n = Math.Min(a.Length, b.Length);
			for (int i = 0; i < n; i++)
			{
				int d = a[i] - b[i];
				if (d != 0) return d;
			}
			return a.Length - b.Length;
		}
	}
}
