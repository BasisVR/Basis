using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Basis.Integration.Sso
{
    /// <summary>
    /// Default <see cref="ISsoTokenValidator"/>: RS256 verification against the issuer's JWKS
    /// using only System.Security.Cryptography (no third-party dependency, compiles as-is on the
    /// Desktop/PCVR scripting runtime). JWKS is cached per uri and re-fetched once on an unknown
    /// key id (handles IdP key rotation).
    /// </summary>
    public sealed class BasisRsaJwksTokenValidator : ISsoTokenValidator
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly TimeSpan JwksTtl = TimeSpan.FromHours(1);

        private readonly Dictionary<string, CachedJwks> _cache = new Dictionary<string, CachedJwks>();
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

        private sealed class CachedJwks
        {
            public DateTime FetchedAtUtc;
            public JArray Keys;
        }

        public async Task<SsoTokenValidationResult> ValidateIdTokenAsync(
            string idToken, SsoTokenValidationParameters p, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(idToken)) return SsoTokenValidationResult.Fail("id_token is empty.");
            if (p == null) return SsoTokenValidationResult.Fail("validation parameters missing.");

            string[] parts = idToken.Split('.');
            if (parts.Length != 3) return SsoTokenValidationResult.Fail("id_token is not a well-formed JWS.");

            JObject header, payload;
            try
            {
                header = JObject.Parse(Encoding.UTF8.GetString(BasisSsoUtil.Base64UrlDecode(parts[0])));
                payload = JObject.Parse(Encoding.UTF8.GetString(BasisSsoUtil.Base64UrlDecode(parts[1])));
            }
            catch (Exception e)
            {
                return SsoTokenValidationResult.Fail($"id_token could not be decoded: {e.Message}");
            }

            string alg = (string)header["alg"];
            if (!string.Equals(alg, "RS256", StringComparison.Ordinal))
                return SsoTokenValidationResult.Fail($"unsupported id_token alg '{alg}' (expected RS256).");
            string kid = (string)header["kid"];

            // ── Signature ──────────────────────────────────────────────────
            bool verified;
            try
            {
                verified = await VerifySignatureAsync(parts, kid, p.JwksUri, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                return SsoTokenValidationResult.Fail($"signature verification failed: {e.Message}");
            }
            if (!verified) return SsoTokenValidationResult.Fail("id_token signature is invalid.");

            // ── Standard claims ────────────────────────────────────────────
            string iss = (string)payload["iss"];
            if (!string.Equals(iss, p.Issuer, StringComparison.Ordinal))
                return SsoTokenValidationResult.Fail($"issuer mismatch (token '{iss}', expected '{p.Issuer}').");

            if (!AudienceContains(payload["aud"], p.Audience))
                return SsoTokenValidationResult.Fail("audience does not include this client id.");

            if (!TryGetUnixTime(payload["exp"], out DateTime exp))
                return SsoTokenValidationResult.Fail("id_token has no exp.");
            if (DateTime.UtcNow > exp + p.ClockSkew)
                return SsoTokenValidationResult.Fail("id_token is expired.");

            if (payload["nbf"] != null && TryGetUnixTime(payload["nbf"], out DateTime nbf)
                && DateTime.UtcNow + p.ClockSkew < nbf)
                return SsoTokenValidationResult.Fail("id_token is not yet valid (nbf).");

            if (!string.IsNullOrEmpty(p.ExpectedNonce))
            {
                string nonce = (string)payload["nonce"];
                if (!string.Equals(nonce, p.ExpectedNonce, StringComparison.Ordinal))
                    return SsoTokenValidationResult.Fail("nonce mismatch.");
            }

            string sub = (string)payload["sub"];
            if (string.IsNullOrEmpty(sub))
                return SsoTokenValidationResult.Fail("id_token has no sub.");

            return new SsoTokenValidationResult
            {
                Valid = true,
                Subject = sub,
                Claims = NormalizeClaims(payload),
            };
        }

        private async Task<bool> VerifySignatureAsync(string[] parts, string kid, string jwksUri, CancellationToken ct)
        {
            byte[] signedData = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            byte[] signature = BasisSsoUtil.Base64UrlDecode(parts[2]);

            JObject jwk = await ResolveKeyAsync(kid, jwksUri, allowRefresh: true, ct);
            if (jwk == null) return false;

            RSAParameters rsaParams = new RSAParameters
            {
                Modulus = BasisSsoUtil.Base64UrlDecode((string)jwk["n"]),
                Exponent = BasisSsoUtil.Base64UrlDecode((string)jwk["e"]),
            };
            using RSA rsa = RSA.Create();
            rsa.ImportParameters(rsaParams);
            return rsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        private async Task<JObject> ResolveKeyAsync(string kid, string jwksUri, bool allowRefresh, CancellationToken ct)
        {
            JArray keys = await GetKeysAsync(jwksUri, forceRefresh: false, ct);
            JObject match = FindKey(keys, kid);
            if (match == null && allowRefresh)
            {
                keys = await GetKeysAsync(jwksUri, forceRefresh: true, ct);
                match = FindKey(keys, kid);
            }
            return match;
        }

        private static JObject FindKey(JArray keys, string kid)
        {
            if (keys == null) return null;
            JObject onlyRsa = null;
            int rsaCount = 0;
            foreach (JToken k in keys)
            {
                if (k is not JObject jwk) continue;
                if (!string.Equals((string)jwk["kty"], "RSA", StringComparison.Ordinal)) continue;
                rsaCount++;
                onlyRsa = jwk;
                if (!string.IsNullOrEmpty(kid) && string.Equals((string)jwk["kid"], kid, StringComparison.Ordinal))
                    return jwk;
            }
            // No kid in the header (or no match) but exactly one RSA key: unambiguous.
            return (string.IsNullOrEmpty(kid) && rsaCount == 1) ? onlyRsa : null;
        }

        private async Task<JArray> GetKeysAsync(string jwksUri, bool forceRefresh, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(jwksUri)) return null;

            await _cacheLock.WaitAsync(ct);
            try
            {
                if (!forceRefresh
                    && _cache.TryGetValue(jwksUri, out CachedJwks cached)
                    && DateTime.UtcNow - cached.FetchedAtUtc < JwksTtl)
                {
                    return cached.Keys;
                }

                using HttpResponseMessage resp = await Http.GetAsync(jwksUri, ct);
                resp.EnsureSuccessStatusCode();
                string body = await resp.Content.ReadAsStringAsync();
                JArray keys = JObject.Parse(body)["keys"] as JArray ?? new JArray();
                _cache[jwksUri] = new CachedJwks { FetchedAtUtc = DateTime.UtcNow, Keys = keys };
                return keys;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        private static bool AudienceContains(JToken aud, string expected)
        {
            if (aud == null || string.IsNullOrEmpty(expected)) return false;
            if (aud.Type == JTokenType.String)
                return string.Equals((string)aud, expected, StringComparison.Ordinal);
            if (aud is JArray arr)
            {
                foreach (JToken a in arr)
                    if (string.Equals((string)a, expected, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool TryGetUnixTime(JToken token, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (token == null) return false;
            try
            {
                long seconds = token.Value<long>();
                utc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                return true;
            }
            catch { return false; }
        }

        internal static Dictionary<string, List<string>> NormalizeClaims(JObject payload)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in payload.Properties())
            {
                var values = new List<string>();
                JToken v = property.Value;
                if (v is JArray arr)
                {
                    foreach (JToken item in arr)
                        if (item.Type != JTokenType.Null) values.Add(item.ToString());
                }
                else if (v.Type != JTokenType.Null)
                {
                    values.Add(v.Type == JTokenType.String ? (string)v : v.ToString(Newtonsoft.Json.Formatting.None));
                }
                result[property.Name] = values;
            }
            return result;
        }
    }
}
