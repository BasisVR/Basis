using System;
using System.Security.Cryptography;

namespace Basis.Shims
{
	public static class BasisHashShim
	{
		public const int MaxLength = 1024 * 1024;

		public static byte[] Sha256(byte[] data)
		{
			Check(data, nameof(data));
			using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(data);
		}

		public static byte[] HmacSha256(byte[] key, byte[] data)
		{
			Check(key, nameof(key));
			Check(data, nameof(data));
			using (HMACSHA256 hmac = new HMACSHA256(key)) return hmac.ComputeHash(data);
		}

		static void Check(byte[] bytes, string name)
		{
			if (bytes == null) throw new ArgumentNullException(name);
			if (bytes.Length > MaxLength) throw new ArgumentOutOfRangeException(name, "Hashes take at most " + MaxLength + " bytes");
		}
	}
}
