#nullable enable

using Basis.Contrib.Crypto;
using Newtonsoft.Json;
using SigningAlgorithm = Basis.Contrib.Crypto.SigningAlgorithm;

namespace Basis.Contrib.Auth.DecentralizedIds
{
	public sealed class JsonWebKey
	{
		[JsonProperty("kty")]
		public string? Kty { get; set; }

		[JsonProperty("kid")]
		public string? Kid { get; set; }

		[JsonProperty("alg")]
		public string? Alg { get; set; }

		[JsonProperty("use")]
		public string? Use { get; set; }

		/// Public portion
		[JsonProperty("x")]
		public string? X { get; set; }

		/// Private portion
		[JsonProperty("d")]
		public string? D { get; set; }

		[JsonProperty("crv")]
		public string? Crv { get; set; }

		// Symmetric key parameter
		[JsonProperty("k")]
		public string? K { get; set; }

		public static JsonSerializerSettings SerializerSettings =>
			new()
			{
				NullValueHandling = NullValueHandling.Ignore,
				Formatting = Formatting.None,
			};

		public string Serialize()
		{
			return JsonConvert.SerializeObject(this, SerializerSettings);
		}

		public static JsonWebKey? Deserialize(string json)
		{
			return JsonConvert.DeserializeObject<JsonWebKey>(json, SerializerSettings);
		}

		/// Returns null if the algorithm is unknown.
		public SigningAlgorithm? GetAlgorithm()
		{
			if (Kty == "OKP" && Crv == "Ed25519")
			{
				return SigningAlgorithm.Ed25519;
			}
			return null;
		}

		public PubKey DecodePubkey()
		{
			return new PubKey(Base64UrlSafe.Decode(X ?? ""));
		}

		public PrivKey DecodePrivkey()
		{
			return new PrivKey(Base64UrlSafe.Decode(D ?? ""));
		}

		public bool IsPubkey()
		{
			return D is null;
		}
	}
}
