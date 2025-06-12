#nullable enable

// Yes, this whole approach is cursed and inefficient. YOLOSWAG.

namespace Basis.Contrib.Auth.DecentralizedIds
{
	/// Base64 url-safe encode and decode.
	public sealed class Base64UrlSafe
	{
		public static string Encode(byte[] bytes)
		{
			string base64 = System.Convert.ToBase64String(bytes);
			return base64
				.TrimEnd('=') // Remove padding
				.Replace('+', '-') // Convert + to -
				.Replace('/', '_'); // Convert / to _
		}

		public static byte[] Decode(string str)
		{
			string base64 = str.Replace('-', '+') // Restore + from -
				.Replace('_', '/'); // Restore / from _

			// Add padding if needed
			switch (base64.Length % 4)
			{
				case 0:
					break; // No padding needed
				case 2:
					base64 += "==";
					break;
				case 3:
					base64 += "=";
					break;
				default:
					throw new System.FormatException("Invalid base64url string length");
			}

			return System.Convert.FromBase64String(base64);
		}
	}
}
