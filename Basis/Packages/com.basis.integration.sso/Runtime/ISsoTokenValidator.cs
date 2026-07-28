using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Integration.Sso
{
    /// <summary>
    /// Verifies an OIDC <c>id_token</c>: signature against the issuer's JWKS plus the standard
    /// claim checks (iss / aud / exp / nonce). Kept behind an interface so the signature backend
    /// can be swapped — the built-in <see cref="BasisRsaJwksTokenValidator"/> uses only
    /// System.Security.Cryptography; a jose-jwt–backed validator can be substituted when that
    /// library is present (see docs/sso-spec.md §6/§7).
    /// </summary>
    public interface ISsoTokenValidator
    {
        Task<SsoTokenValidationResult> ValidateIdTokenAsync(
            string idToken, SsoTokenValidationParameters parameters, CancellationToken ct);
    }

    public sealed class SsoTokenValidationParameters
    {
        public string Issuer;
        /// <summary>Expected audience — the OIDC client id.</summary>
        public string Audience;
        public string JwksUri;
        public string ExpectedNonce;
        /// <summary>Tolerance applied to exp/iat to absorb clock drift.</summary>
        public System.TimeSpan ClockSkew = System.TimeSpan.FromMinutes(2);
    }

    public sealed class SsoTokenValidationResult
    {
        public bool Valid;
        public string Error;
        public string Subject;
        /// <summary>All id_token claims, values normalised to lists (array claims and scalars alike).</summary>
        public Dictionary<string, List<string>> Claims;

        public static SsoTokenValidationResult Fail(string error) =>
            new SsoTokenValidationResult { Valid = false, Error = error };
    }
}
