#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Xunit;

namespace Basis.Contrib.Auth.DecentralizedIds
{
	public class DidKeyTests
	{
		// See https://w3c-ccg.github.io/did-method-key/#ed25519-x25519
		static List<(string, JsonWebKey)> TestVectors()
		{
			return new List<(string, JsonWebKey)>
			{
				(
					"did:key:z6MkiTBz1ymuepAQ4HEHYSF1H8quG5GLVVQR3djdX3mDooWp",
					new JsonWebKey()
					{
						Kty = "OKP",
						Crv = "Ed25519",
						X = "O2onvM62pC1io6jQKm8Nc2UyFXcd4kOmOsBIoYtZ2ik",
					}
				),
			};
		}

		[Fact]
		public async Task DidKeyTestVectors()
		{
			var resolver = new DidKeyResolver();
			foreach (var (inputDid, expectedJwk) in TestVectors())
			{
				var expectedFragment = new DidUrlFragment(
					inputDid.Split(
						DidKeyResolver.PREFIX,
						count: 2,
						options: StringSplitOptions.RemoveEmptyEntries
					)[0]
				);
				var document = await resolver.ResolveDocument(new Did(inputDid));
				Debug.Assert(document.Pubkeys.Count == 1);
				var resolvedJwk = document.Pubkeys[expectedFragment];
				Debug.Assert(
					JsonSerializer.Serialize(resolvedJwk)
						== JsonSerializer.Serialize(expectedJwk),
					"resolved JWK did not match expected JWK"
				);
			}
		}

		[Fact]
		public void DidKeyTestEncode()
		{
			foreach (var (expectedDid, jwkInput) in TestVectors())
			{
				var pubkeyBytes = Base64UrlSafe.Decode(
					jwkInput.X ?? throw new Exception("the examples are not null")
				);
				var encodedDid = DidKeyResolver.EncodePubkeyAsDid(
					new PubKey(pubkeyBytes)
				);
				Debug.Assert(
					expectedDid.Equals(encodedDid.V),
					$"encoded was {encodedDid}, expected {expectedDid}"
				);
			}
		}
	}
}
