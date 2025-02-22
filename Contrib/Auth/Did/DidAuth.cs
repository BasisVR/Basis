#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;
using Debug = System.Diagnostics.Debug;

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
		public async Task<VerifyResponseResult> VerifyResponse(
			Response response,
			Challenge challenge
		)
		{
			var document = await ResolveDid(challenge.Identity);
			var pubkey = RetrieveKey(document, response.DidUrlFragment);
			var (isVerified, verifySigErr) = VerifySignature(
				pubkey,
				challenge.Nonce,
				response.Signature
			);
			if (!isVerified)
			{
				return VerifyResponseResult.Success;
			}
			return VerifyResponseResult.Success;
		}

		private (bool, DidSignatureErr?) VerifySignature(
			JsonWebKey pubkey,
			Nonce nonce,
			Signature signature
		)
		{
			throw new NotImplementedException("todo: do the cryptography");
		}

		private JsonWebKey RetrieveKey(DidDocument document, DidUrlFragment keyId)
		{
			JsonWebKey pubkeyJwk;
			if (keyId.V.Equals(string.Empty))
			{
				if (document.Pubkeys.Count != 1)
				{
					throw new DidResolveException(DidResolveErr.AmbiguousFragment);
				}
				pubkeyJwk = document.Pubkeys.First().Value;
			}
			if (!document.Pubkeys.TryGetValue(keyId, out pubkeyJwk))
			{
				throw new DidResolveException(DidResolveErr.NoSuchFragment);
			}
			return pubkeyJwk;
		}

		private async Task<DidDocument> ResolveDid(Did identity)
		{
			var segments = identity.V.Split(
				separator: ":",
				count: 3,
				StringSplitOptions.None
			);
			if (segments.Length != 3 || segments[0] != "did")
			{
				throw new DidResolveException(DidResolveErr.InvalidPrefix);
			}
			var method = segments[1] switch
			{
				"key" => DidMethodKind.Key,
				_ => throw new DidResolveException(DidResolveErr.UnsupportedMethod),
			};
			var resolver = Resolvers[method];
			return await resolver.ResolveDocument(identity);
		}

		/// Errors related to validating to the signature itself.
		public enum DidSignatureErr { }

		/// Errors related to resolving a Did document from a Did.
		public enum DidResolveErr
		{
			InvalidPrefix,
			UnsupportedMethod,
			NoSuchFragment,
			AmbiguousFragment,
		}

		public sealed class DidResolveException : Exception
		{
			public DidResolveErr Error { get; }

			public DidResolveException(DidResolveErr error)
				: base(error.ToString())
			{
				Error = error;
			}
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

	/// Possible return values VerifyResponse method.
	public enum VerifyResponseResult
	{
		/// The verification was successful
		Success,

		/// Was unable to resolve the Did to a DidDocument.
		FailedToResolveDid,

		/// The fragment in the response didn't exist in the DID Document resolved
		/// from the challenge's identity.
		NoSuchFragment,

		/// The response signature did not match the challenge nonce
		MismatchedNonce,

		/// The verification failed due to an invalid signature
		InvalidSig,
	}
}
