#nullable enable

using Newtonsoft.Json;

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

		// Ed25519 parameters
		[JsonProperty("x")]
		public string? X { get; set; }

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
	}
}
