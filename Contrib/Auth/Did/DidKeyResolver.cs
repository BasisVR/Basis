#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Basis.Contrib.Crypto;
using Base128 = WojciechMikołajewicz.Base128;
using Base58 = SimpleBase.Base58;
using Debug = System.Diagnostics.Debug;
using Did = Basis.Contrib.Auth.DecentralizedIds.Newtypes.Did;
using DidUrlFragment = Basis.Contrib.Auth.DecentralizedIds.Newtypes.DidUrlFragment;
using Ed25519 = Basis.Contrib.Crypto.Ed25519;
using StringSplitOptions = System.StringSplitOptions;

namespace Basis.Contrib.Auth.DecentralizedIds
{
	/// Implements resolution of a did:key to the various information stored in it
	public sealed class DidKeyResolver : IDidMethod
	{
		public const string PREFIX = "did:key:";

		/// https://github.com/multiformats/multicodec/blob/master/table.csv#L98
		const byte ED25519_MULTIFORMAT_CODE = 0xED;

		/// https://datatracker.ietf.org/doc/html/draft-multiformats-multibase#appendix-D.1
		const char BASE58_BTC_MULTIBASE_CODE = 'z';

		public DidMethodKind Kind => DidMethodKind.Key;

		public Task<DidDocument> ResolveDocument(Did did)
		{
			// Task is immediately complete, we don't need any io.
			return Task.FromResult(Helper(did));
		}

		private static DidDocument Helper(Did did)
		{
			var parts = did.V.Split(
				separator: PREFIX,
				count: 2,
				StringSplitOptions.RemoveEmptyEntries
			);
			Debug.Assert(parts.Length == 1, "expected string to start with did:key");
			var multibasePart = parts[0];
			var multibaseChar = multibasePart[0];
			// did:key uses base58-btc encoding, see the spec here:
			// https://w3c-ccg.github.io/did-method-key/#format
			if (multibaseChar != BASE58_BTC_MULTIBASE_CODE)
			{
				throw new DidKeyDecodeException(DidKeyDecodeError.NotBase58Btc);
			}
			// Again, did:key uses base58-btc encoding, see the spec here:
			// https://w3c-ccg.github.io/did-method-key/#format
			var multicodecPrefixed = Base58.Bitcoin.Decode(multibasePart[1..]);
			if (
				!Base128.TryReadUInt16(
					multicodecPrefixed,
					out ushort codecId,
					out int prefixLen
				)
			)
			{
				throw new DidKeyDecodeException(DidKeyDecodeError.VarintWouldOverflow);
			}
			// For now we only support Ed25519 pubkeys.
			if (codecId != ED25519_MULTIFORMAT_CODE)
			{
				throw new DidKeyDecodeException(
					DidKeyDecodeError.UnsupportedPubkeyType
				);
			}
			var pubkeyBytes = multicodecPrefixed[prefixLen..];
			if (pubkeyBytes.Length != Ed25519.PubkeySize)
			{
				throw new DidKeyDecodeException(DidKeyDecodeError.WrongPubkeyLen);
			}

			var pubkeys = new Dictionary<DidUrlFragment, JsonWebKey>
			{
				{ new DidUrlFragment(multibasePart), CreateEd25519Jwk(pubkeyBytes) },
			};
			return new DidDocument(
				Pubkeys: new ReadOnlyDictionary<DidUrlFragment, JsonWebKey>(pubkeys)
			);
		}

		public static Did EncodePubkeyAsDid(PubKey pubKey)
		{
			var nBytesForMultiformat = Base128.GetRequiredBytesUInt32(
				ED25519_MULTIFORMAT_CODE
			);
			byte[] withMultiformatCode = new byte[
				pubKey.V.Length + nBytesForMultiformat
			];
			Base128.WriteUInt32(
				withMultiformatCode.AsSpan()[..nBytesForMultiformat],
				ED25519_MULTIFORMAT_CODE,
				out int _
			);
			pubKey.V.CopyTo(withMultiformatCode.AsSpan()[nBytesForMultiformat..]);

			var base58Encoded = Base58.Bitcoin.Encode(withMultiformatCode);

			var s = new StringBuilder(PREFIX);
			s.Append(BASE58_BTC_MULTIBASE_CODE);
			s.Append(base58Encoded);
			return new Did(s.ToString());
		}

		/// See
		private static JsonWebKey CreateEd25519Jwk(byte[] pubkeyBytes)
		{
			Debug.Assert(pubkeyBytes.Length == Ed25519.PubkeySize);
			var key = new JsonWebKey
			{
				Kty = "OKP",
				Crv = "Ed25519",
				X = Base64UrlSafe.Encode(pubkeyBytes),
			};
			return key;
		}
	}

	public enum DidKeyDecodeError
	{
		/// The public key type is not supported.
		UnsupportedPubkeyType,

		/// Decoding the multicodec varint of the pubkey type overflowed.
		VarintWouldOverflow,

		/// The did key's method specific identifier should have been base58-btc
		/// encoded, but was not.
		NotBase58Btc,

		/// The number of bytes in the pubkey did not match the number of bytes
		/// expected for the key type.
		WrongPubkeyLen,
	}

	public sealed class DidKeyDecodeException : System.Exception
	{
		public DidKeyDecodeError Error { get; }

		public DidKeyDecodeException(DidKeyDecodeError error)
			: base(error.ToString())
		{
			Error = error;
		}
	}
}
