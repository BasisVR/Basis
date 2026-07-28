using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Basis.Integration.Sso
{
    /// <summary>
    /// The persisted result of a successful sign-in. Stored encrypted-at-rest by
    /// <see cref="BasisSsoSessionStore"/>. Holds just enough to (a) silently renew via the
    /// refresh token, (b) launch offline while the access token is still valid, and
    /// (c) resolve the user's stable id / display-name seed / access-control claims without
    /// re-contacting the IdP.
    /// </summary>
    [Serializable]
    public sealed class BasisSsoSession
    {
        /// <summary>Bumped when the on-disk shape changes so stale sessions are discarded.</summary>
        [JsonProperty("v")] public int Version = 1;

        /// <summary>The issuer this session was minted against; a config issuer change invalidates it.</summary>
        [JsonProperty("issuer")] public string Issuer = string.Empty;

        /// <summary>OIDC subject — the stable per-user id used to namespace the DID binding.</summary>
        [JsonProperty("sub")] public string Sub = string.Empty;

        [JsonProperty("idToken")] public string IdToken = string.Empty;
        [JsonProperty("accessToken")] public string AccessToken = string.Empty;
        [JsonProperty("refreshToken")] public string RefreshToken = string.Empty;

        [JsonProperty("accessTokenExpiresAtUtc")] public DateTime AccessTokenExpiresAtUtc = DateTime.MinValue;

        /// <summary>Null when the IdP did not advertise a refresh-token lifetime.</summary>
        [JsonProperty("refreshTokenExpiresAtUtc")] public DateTime? RefreshTokenExpiresAtUtc;

        /// <summary>
        /// Claims captured at sign-in (id_token + UserInfo), values normalised to a list so
        /// array claims like <c>groups</c> and scalar claims share one shape.
        /// </summary>
        [JsonProperty("claims")] public Dictionary<string, List<string>> Claims = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool AccessTokenValid => DateTime.UtcNow < AccessTokenExpiresAtUtc;

        [JsonIgnore]
        public bool RefreshTokenValid =>
            !string.IsNullOrEmpty(RefreshToken)
            && (RefreshTokenExpiresAtUtc == null || DateTime.UtcNow < RefreshTokenExpiresAtUtc.Value);

        public IReadOnlyList<string> GetClaim(string name)
        {
            if (!string.IsNullOrEmpty(name) && Claims != null && Claims.TryGetValue(name, out List<string> values) && values != null)
                return values;
            return Array.Empty<string>();
        }

        public string GetFirstClaim(string name)
        {
            IReadOnlyList<string> values = GetClaim(name);
            return values.Count > 0 ? values[0] : null;
        }

        public IReadOnlyList<string> Groups => GetClaim("groups");
    }
}
