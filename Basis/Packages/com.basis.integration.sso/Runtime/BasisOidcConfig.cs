using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace Basis.Integration.Sso
{
    /// <summary>
    /// Runtime-loaded OIDC connection settings for the SSO login gate. Shipped with the
    /// build as a JSON file so an org admin can point the client at their IdP (Okta or any
    /// generic OIDC issuer) without a rebuild. See docs/sso-spec.md §5 for the schema.
    ///
    /// Load precedence (later wins): the copy under <see cref="Application.streamingAssetsPath"/>
    /// bundled with the build is the default; a copy under
    /// <see cref="Application.persistentDataPath"/> overrides it for per-machine tweaks.
    /// </summary>
    [Serializable]
    public sealed class BasisOidcConfig
    {
        public const string FileName = "basis-sso.json";

        [JsonProperty("issuer")] public string Issuer = string.Empty;
        [JsonProperty("clientId")] public string ClientId = string.Empty;

        /// <summary>
        /// Optional. Some IdPs require it in the token exchange even for a PKCE "public" client:
        /// notably Google "Desktop app" clients, whose secret is explicitly documented as
        /// non-confidential but still mandatory. Leave empty for true public clients (e.g. Okta Native).
        /// </summary>
        [JsonProperty("clientSecret")] public string ClientSecret = string.Empty;

        [JsonProperty("scopes")] public List<string> Scopes = new List<string> { "openid", "profile", "email" };

        /// <summary>
        /// Extra query params appended to the authorization request, for provider-specific needs.
        /// Google requires <c>access_type=offline</c> (and typically <c>prompt=consent</c>) to return a
        /// refresh token. An explicit account-switch prompt overrides any <c>prompt</c> set here.
        /// </summary>
        [JsonProperty("extraAuthParams")] public Dictionary<string, string> ExtraAuthParams = new Dictionary<string, string>();

        [JsonProperty("redirect")] public RedirectConfig Redirect = new RedirectConfig();
        [JsonProperty("displayNameClaims")] public List<string> DisplayNameClaims = new List<string> { "name", "preferred_username", "email" };
        [JsonProperty("access")] public AccessConfig Access = new AccessConfig();
        [JsonProperty("enforcement")] public EnforcementConfig Enforcement = new EnforcementConfig();

        [Serializable]
        public sealed class RedirectConfig
        {
            /// <summary>Only "loopback" is supported this round (native app on the loopback interface).</summary>
            [JsonProperty("mode")] public string Mode = "loopback";
            /// <summary>Loopback host: "127.0.0.1" (default) or "localhost". Must match what the IdP allows.</summary>
            [JsonProperty("host")] public string Host = "127.0.0.1";
            /// <summary>Fixed loopback port, or 0 to pick a free ephemeral port each run (works with IdPs
            /// that allow dynamic loopback ports, e.g. Google Desktop app). Pin it if the IdP requires an
            /// exact registered redirect URI.</summary>
            [JsonProperty("port")] public int Port = 0;
            [JsonProperty("path")] public string Path = "/callback";
        }

        [Serializable]
        public sealed class AccessConfig
        {
            /// <summary>Any one match admits the user. Empty = no group restriction.</summary>
            [JsonProperty("allowedGroups")] public List<string> AllowedGroups = new List<string>();

            /// <summary>Every listed claim must match one of its allowed values. Empty = no restriction.</summary>
            [JsonProperty("allowedClaims")] public List<ClaimRule> AllowedClaims = new List<ClaimRule>();
        }

        [Serializable]
        public sealed class ClaimRule
        {
            [JsonProperty("claim")] public string Claim = string.Empty;
            [JsonProperty("values")] public List<string> Values = new List<string>();
        }

        [Serializable]
        public sealed class EnforcementConfig
        {
            /// <summary>Allow launching with a still-valid cached session when the IdP is unreachable.</summary>
            [JsonProperty("allowOfflineWithinTokenValidity")] public bool AllowOfflineWithinTokenValidity = true;
        }

        public bool HasGroupRestriction => Access?.AllowedGroups != null && Access.AllowedGroups.Count > 0;
        public bool HasClaimRestriction => Access?.AllowedClaims != null && Access.AllowedClaims.Count > 0;

        /// <summary>Structural validation of required fields. Does not contact the IdP.</summary>
        public bool TryValidate(out string error)
        {
            if (string.IsNullOrWhiteSpace(Issuer))
            {
                error = "OIDC config: 'issuer' is required.";
                return false;
            }
            if (!Uri.TryCreate(Issuer, UriKind.Absolute, out Uri issuerUri)
                || (issuerUri.Scheme != Uri.UriSchemeHttps && issuerUri.Scheme != Uri.UriSchemeHttp))
            {
                error = $"OIDC config: 'issuer' must be an absolute http(s) URL (got '{Issuer}').";
                return false;
            }
            if (string.IsNullOrWhiteSpace(ClientId))
            {
                error = "OIDC config: 'clientId' is required.";
                return false;
            }
            if (Scopes == null || Scopes.Count == 0 || !Scopes.Contains("openid"))
            {
                error = "OIDC config: 'scopes' must include 'openid'.";
                return false;
            }
            if (Redirect == null || !string.Equals(Redirect.Mode, "loopback", StringComparison.OrdinalIgnoreCase))
            {
                error = "OIDC config: only redirect.mode 'loopback' is supported.";
                return false;
            }
            error = null;
            return true;
        }

        // ── Loading ──────────────────────────────────────────────────────────

        public static string StreamingPath => Path.Combine(Application.streamingAssetsPath, FileName);
        public static string OverridePath => Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>
        /// Loads and validates the config. Returns null (and logs) if neither location has a
        /// readable, valid file. The persistentData copy overrides the streamingAssets copy.
        /// </summary>
        public static BasisOidcConfig Load()
        {
            BasisOidcConfig config = ReadFrom(OverridePath) ?? ReadFrom(StreamingPath);
            if (config == null)
            {
                BasisDebug.LogError(
                    $"[SSO] No OIDC config found. Provide '{FileName}' at '{StreamingPath}' " +
                    $"or '{OverridePath}'.");
                return null;
            }
            if (!config.TryValidate(out string error))
            {
                BasisDebug.LogError($"[SSO] {error}");
                return null;
            }
            return config;
        }

        private static BasisOidcConfig ReadFrom(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                BasisOidcConfig config = JsonConvert.DeserializeObject<BasisOidcConfig>(json);
                if (config != null)
                    BasisDebug.Log($"[SSO] Loaded OIDC config from '{path}'.");
                return config;
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"[SSO] Failed to read OIDC config from '{path}': {e.Message}");
                return null;
            }
        }
    }
}
