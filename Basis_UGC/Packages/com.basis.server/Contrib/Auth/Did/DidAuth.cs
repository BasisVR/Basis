#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;
using Empty = System.ValueTuple;

namespace Basis.Contrib.Auth.DecentralizedIds
{
	/// Configuration for [`DidAuthentication`].
	public sealed record Config
	{
		public CryptoRng Rng { get; init; } = CryptoRng.Create();
		public IDictionary<DidMethodKind, IDidMethod> Resolvers { get; init; } =
			new Dictionary<DidMethodKind, IDidMethod>()
			{
				// We will add more did methods in the future, like did:web
				{ DidMethodKind.Key, new DidKeyResolver() },
			};
	}

	public readonly struct DidResolveErr : IDidVerifyErr
	{
		public readonly E e;

		public DidResolveErr(E e) => this.e = e;

		public static implicit operator DidResolveErr(E e) => new(e);

		public enum E
		{
			/// Another generic error happened during DID document resolution.
			Other,

			/// Did method is not supported.
			UnsupportedMethod,

			/// Did had an invalid prefix.
			InvalidPrefix,
		}
	}

	public readonly struct DidFragmentErr : IDidVerifyErr
	{
		public readonly E e;

		public DidFragmentErr(E e) => this.e = e;

		public static implicit operator DidFragmentErr(E e) => new(e);

		public enum E
		{
			/// No such fragment was present in the DID document.
			AmbiguousFragment,

			/// The given fragment was ambiguous.
			NoSuchFragment,
		}
	}

	public readonly struct DidSignatureErr : IDidVerifyErr
	{
		public readonly E e;

		public DidSignatureErr(E e) => this.e = e;

		public static implicit operator DidSignatureErr(E e) => new(e);

		public enum E
		{
			InvalidSignature,
			UnsupportedSignatureAlgorithm,
		}
	}

	public interface IDidVerifyErr { }

	// TODO(@thebutlah): Create and implement an `IChallengeResponseAuth`
	// interface. This interface should live in basis core.
	public sealed class DidAuthentication
	{
		/// Number of bytes in a nonce. This is currently 256 bits.
		// TODO(@thebutlah): Decide if its too performance intensive to use 256
		// bits, and if 128 bit would be sufficient.
		const ushort NONCE_LEN = 256 / sizeof(byte);

		// We store the rng to make deterministic testing and seeding possible.
		readonly CryptoRng Rng;

		// We support possibly multiple did resolvers.
		readonly IDictionary<DidMethodKind, IDidMethod> Resolvers;

		public DidAuthentication(Config cfg)
		{
			Rng = cfg.Rng;
			Resolvers = cfg.Resolvers;
		}

		public Challenge MakeChallenge(Did identity)
		{
			var nonce = new byte[NONCE_LEN];
			Rng.GetBytes(nonce);
			return new Challenge(Identity: identity, Nonce: new Nonce(nonce));
		}

		/// Compares the response against the original challenge.
		///
		/// Ensures that:
		/// * The response signature matches the public keys of the challenge
		///   identity.
		/// * The response signature payload matches the nonce in the challenge
		///
		/// It is the caller's responsibility to keep track of which challenges
		/// should be held for which responses.
		public async Task<Result<Empty, IDidVerifyErr>> VerifyResponse(
			Response response,
			Challenge challenge
		)
		{
			var resolveResult = await ResolveDid(challenge.Identity);
			if (resolveResult.IsErr)
			{
				return resolveResult.GetErr;
			}
			DidDocument document = resolveResult.GetOk;

			var pubkeyResult = RetrieveKey(document, response.DidUrlFragment);
			if (pubkeyResult.IsErr)
			{
				return pubkeyResult.GetErr;
			}
			var pubkey = pubkeyResult.GetOk;

			var sigResult = VerifySignature(
				pubkey,
				challenge.Nonce,
				response.Signature
			);
			if (sigResult.IsErr)
			{
				return sigResult.GetErr;
			}
			return new Empty();
		}

		private Result<Empty, DidSignatureErr> VerifySignature(
			JsonWebKey pubkey,
			Nonce nonce,
			Signature signature
		)
		{
			switch (pubkey.GetAlgorithm())
			{
				case SigningAlgorithm.Ed25519:
					if (
						Ed25519.Verify(
							pubkey: pubkey.DecodePubkey(),
							sig: signature,
							payload: new Payload(nonce.V)
						)
					)
					{
						return new Empty();
					}
					else
					{
						return new DidSignatureErr(DidSignatureErr.E.InvalidSignature);
					}
				default:
					return new DidSignatureErr(
						DidSignatureErr.E.UnsupportedSignatureAlgorithm
					);
			}
		}

		private static Result<JsonWebKey, DidFragmentErr> RetrieveKey(
			DidDocument document,
			DidUrlFragment keyId
		)
		{
			if (document.Pubkeys.Count == 1)
			{
				return document.Pubkeys.First().Value;
			}
			if (!document.Pubkeys.TryGetValue(keyId, out JsonWebKey pubkey))
			{
				return new DidFragmentErr(DidFragmentErr.E.NoSuchFragment);
			}
			return pubkey;
		}

		private async Task<Result<DidDocument, DidResolveErr>> ResolveDid(Did identity)
		{
			var segments = identity.V.Split(
				separator: ":",
				count: 3,
				StringSplitOptions.None
			);
			if (segments.Length != 3 || segments[0] != "did")
			{
				return new DidResolveErr(DidResolveErr.E.InvalidPrefix);
			}
			DidMethodKind? methodOrNull = segments[1] switch
			{
				"key" => DidMethodKind.Key,
				_ => null,
			};
			if (methodOrNull is null)
			{
				return new DidResolveErr(DidResolveErr.E.UnsupportedMethod);
			}
			var method = methodOrNull.Value;

			var resolver = Resolvers[method];
			return await resolver.ResolveDocument(identity);
		}
	}

	/// Challenges are a randomized nonce. The nonce will be the payload
	/// that is signed by the user's private key. Generating a random nonce
	/// for every authentication attempt ensures that an attacker cannot
	/// perform a [replay attack](https://en.wikipedia.org/wiki/Replay_attack).
	///
	/// Challenges also track the identity of the party that the challenge was
	/// sent to, so that later the signature's public key can be compared to
	/// the identity's public key.
	public record Challenge(Did Identity, Nonce Nonce);

	public record Response(
		/// The raw bytes of the signature. For ed25519 this is 64 bytes long.
		Signature Signature,
		/// The particular key in the user's did document. If the empty string,
		/// it is implied that there is only one key in the document and that
		/// this single key should be what is used as the pub key.
		///
		/// Examples:
		/// * `""`
		/// * `"key-0"`
		/// * `"z6MkhaXgBZDvotDkL5257faiztiGiC2QtKLGpbnnEGta2doK"`
		DidUrlFragment DidUrlFragment
	);
}
