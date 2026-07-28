using System;
using System.Security.Cryptography;
using System.Text;

namespace Basis.Integration.Sso
{
    /// <summary>Small helpers shared across the OIDC flow: base64url and CSPRNG values.</summary>
    internal static class BasisSsoUtil
    {
        public static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        public static byte[] Base64UrlDecode(string input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            string s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException("Invalid base64url string.");
            }
            return Convert.FromBase64String(s);
        }

        /// <summary>CSPRNG bytes, base64url-encoded — used for PKCE verifier, state, nonce.</summary>
        public static string RandomUrlToken(int byteLength = 32)
        {
            byte[] buffer = new byte[byteLength];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(buffer);
            return Base64UrlEncode(buffer);
        }

        /// <summary>PKCE S256 challenge for a given code verifier.</summary>
        public static string Sha256Challenge(string codeVerifier)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
            return Base64UrlEncode(hash);
        }
    }
}
