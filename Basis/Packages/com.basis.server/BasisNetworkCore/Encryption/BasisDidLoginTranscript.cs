#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;

namespace Basis.Network.Core
{
	/// The bytes a client signs with its DID key to log in to a server.
	///
	/// Besides the server's nonce, the signature covers both X25519 keys of the connection, a hash of
	/// the connect payload the client sent (its connection key and ReadyMessage), the server ID and
	/// the client DID, under a fixed domain tag. A server that relays the signature somewhere else can
	/// only log in there with the player's own connection key, which it cannot send with, and the
	/// player's own connect payload, so it can neither speak nor appear as the player. The client does
	/// not check the server ID yet, so that field alone does not stop a relay.
	///
	/// Layout: tag, then each field as a u16 little-endian length followed by its bytes.
	public static class BasisDidLoginTranscript
	{
		/// Shortest nonce a client accepts; below this a signature could be collected ahead of time.
		public const int MinNonceSize = 32;

		public const int PayloadHashSize = 32;

		private static readonly byte[] Tag = Encoding.ASCII.GetBytes("basis-did-login-v2");

		public const int LegacyNonceSize = 256;

		public static bool IsLegacyNonce(ReadOnlySpan<byte> nonce) => nonce.Length == LegacyNonceSize && !nonce.StartsWith((ReadOnlySpan<byte>)Tag);

		public static byte[] HashConnectionPayload(byte[] data, int offset, int count)
		{
			using (SHA256 sha = SHA256.Create())
			{
				return sha.ComputeHash(data, offset, count);
			}
		}

		public static byte[] Build(
			string clientDid,
			string serverId,
			ReadOnlySpan<byte> nonce,
			ReadOnlySpan<byte> clientEphemeralPublic,
			ReadOnlySpan<byte> serverEphemeralPublic,
			ReadOnlySpan<byte> connectionPayloadHash)
		{
			byte[] clientDidBytes = Encoding.UTF8.GetBytes(clientDid ?? string.Empty);
			byte[] serverIdBytes = Encoding.UTF8.GetBytes(serverId ?? string.Empty);

			int length = Tag.Length
				+ 2 + clientDidBytes.Length
				+ 2 + serverIdBytes.Length
				+ 2 + nonce.Length
				+ 2 + clientEphemeralPublic.Length
				+ 2 + serverEphemeralPublic.Length
				+ 2 + connectionPayloadHash.Length;
			byte[] transcript = new byte[length];
			int offset = 0;
			Tag.CopyTo(transcript, 0);
			offset += Tag.Length;
			Append(transcript, ref offset, clientDidBytes);
			Append(transcript, ref offset, serverIdBytes);
			Append(transcript, ref offset, nonce);
			Append(transcript, ref offset, clientEphemeralPublic);
			Append(transcript, ref offset, serverEphemeralPublic);
			Append(transcript, ref offset, connectionPayloadHash);
			return transcript;
		}

		private static void Append(byte[] transcript, ref int offset, ReadOnlySpan<byte> field)
		{
			if (field.Length > ushort.MaxValue)
			{
				throw new ArgumentException("Transcript field too long");
			}
			transcript[offset] = (byte)field.Length;
			transcript[offset + 1] = (byte)(field.Length >> 8);
			offset += 2;
			field.CopyTo(transcript.AsSpan(offset));
			offset += field.Length;
		}
	}
}
