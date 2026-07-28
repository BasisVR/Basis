using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Basis.Integration.Sso
{
    public sealed class SsoAuthResult
    {
        public bool Success;
        public bool Cancelled;
        /// <summary>True when authentication succeeded but the org access rules rejected the user.</summary>
        public bool AccessDenied;
        public string Error;
        public BasisSsoSession Session;

        public static SsoAuthResult Ok(BasisSsoSession s) => new SsoAuthResult { Success = true, Session = s };
        public static SsoAuthResult Fail(string error) => new SsoAuthResult { Success = false, Error = error };
        public static SsoAuthResult Deny(string reason) => new SsoAuthResult { Success = false, AccessDenied = true, Error = reason };
        public static SsoAuthResult Canceled() => new SsoAuthResult { Success = false, Cancelled = true, Error = "Sign-in cancelled." };
    }

    /// <summary>
    /// Drives the OIDC Authorization Code + PKCE flow for a native app: system browser +
    /// 127.0.0.1 loopback redirect. Also handles silent renewal from a stored session. Produces a
    /// validated <see cref="BasisSsoSession"/>; persistence and access control are the caller's
    /// job (see <c>BasisSsoGate</c>). Continuations intentionally stay on Unity's main thread so
    /// <see cref="Application.OpenURL"/> is called safely.
    /// </summary>
    public sealed class BasisOidcLoginService
    {
        private readonly BasisOidcConfig _config;
        private readonly ISsoTokenValidator _validator;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private OidcDiscovery _discovery;

        public BasisOidcLoginService(BasisOidcConfig config, ISsoTokenValidator validator = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _validator = validator ?? new BasisRsaJwksTokenValidator();
        }

        // ── Discovery ──────────────────────────────────────────────────────

        private sealed class OidcDiscovery
        {
            public string AuthorizationEndpoint;
            public string TokenEndpoint;
            public string JwksUri;
            public string UserInfoEndpoint;
            public string EndSessionEndpoint;
        }

        private async Task<OidcDiscovery> GetDiscoveryAsync(CancellationToken ct)
        {
            if (_discovery != null) return _discovery;
            string url = _config.Issuer.TrimEnd('/') + "/.well-known/openid-configuration";
            using HttpResponseMessage resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            JObject doc = JObject.Parse(await resp.Content.ReadAsStringAsync());
            _discovery = new OidcDiscovery
            {
                AuthorizationEndpoint = (string)doc["authorization_endpoint"],
                TokenEndpoint = (string)doc["token_endpoint"],
                JwksUri = (string)doc["jwks_uri"],
                UserInfoEndpoint = (string)doc["userinfo_endpoint"],
                EndSessionEndpoint = (string)doc["end_session_endpoint"],
            };
            if (string.IsNullOrEmpty(_discovery.AuthorizationEndpoint) || string.IsNullOrEmpty(_discovery.TokenEndpoint))
                throw new Exception("OIDC discovery document is missing authorization/token endpoints.");
            return _discovery;
        }

        public async Task<string> GetEndSessionEndpointAsync(CancellationToken ct)
        {
            try { return (await GetDiscoveryAsync(ct)).EndSessionEndpoint; }
            catch { return null; }
        }

        // ── Interactive sign-in ────────────────────────────────────────────

        /// <param name="prompt">Optional OIDC <c>prompt</c> (e.g. "login" / "select_account" to force account choice).</param>
        public async Task<SsoAuthResult> SignInInteractiveAsync(CancellationToken ct, string prompt = null)
        {
            OidcDiscovery disco;
            try { disco = await GetDiscoveryAsync(ct); }
            catch (OperationCanceledException) { return SsoAuthResult.Canceled(); }
            catch (Exception e) { return SsoAuthResult.Fail($"Could not reach the identity provider: {e.Message}"); }

            string codeVerifier = BasisSsoUtil.RandomUrlToken(32);
            string codeChallenge = BasisSsoUtil.Sha256Challenge(codeVerifier);
            string state = BasisSsoUtil.RandomUrlToken(16);
            string nonce = BasisSsoUtil.RandomUrlToken(16);

            HttpListener listener = null;
            try
            {
                string host = string.IsNullOrEmpty(_config.Redirect.Host) ? "127.0.0.1" : _config.Redirect.Host;
                int port = _config.Redirect.Port > 0 ? _config.Redirect.Port : GetFreeLoopbackPort();
                string redirectUri = $"http://{host}:{port}{NormalizePath(_config.Redirect.Path)}";
                listener = new HttpListener();
                listener.Prefixes.Add($"http://{host}:{port}/");
                listener.Start();

                string authUrl = BuildAuthorizeUrl(disco.AuthorizationEndpoint, redirectUri, codeChallenge, state, nonce, prompt);
                Application.OpenURL(authUrl);

                (string code, string returnedState, string error) = await WaitForCallbackAsync(listener, ct);
                if (!string.IsNullOrEmpty(error)) return SsoAuthResult.Fail($"Identity provider returned an error: {error}");
                if (string.IsNullOrEmpty(code)) return SsoAuthResult.Canceled();
                if (!string.Equals(returnedState, state, StringComparison.Ordinal))
                    return SsoAuthResult.Fail("State mismatch — possible CSRF; sign-in aborted.");

                JObject tokenResponse = await ExchangeCodeAsync(disco.TokenEndpoint, code, redirectUri, codeVerifier, ct);
                return await BuildSessionFromTokenResponseAsync(disco, tokenResponse, nonce, ct);
            }
            catch (OperationCanceledException) { return SsoAuthResult.Canceled(); }
            catch (Exception e) { return SsoAuthResult.Fail($"Sign-in failed: {e.Message}"); }
            finally
            {
                try { listener?.Stop(); listener?.Close(); } catch { /* ignore */ }
            }
        }

        // ── Silent renewal / offline ───────────────────────────────────────

        /// <summary>
        /// Reuses a stored session. Fast-paths a still-valid access token (also the offline case),
        /// otherwise refreshes via the refresh token. Returns failure when re-authentication is required.
        /// </summary>
        public async Task<SsoAuthResult> TrySilentAsync(BasisSsoSession existing, CancellationToken ct)
        {
            if (existing == null) return SsoAuthResult.Fail("No stored session.");
            if (!string.Equals(existing.Issuer, _config.Issuer, StringComparison.Ordinal))
                return SsoAuthResult.Fail("Stored session was issued by a different provider.");

            // Cached access token still valid → no network needed (covers offline launch).
            if (existing.AccessTokenValid) return SsoAuthResult.Ok(existing);

            if (!existing.RefreshTokenValid) return SsoAuthResult.Fail("Session expired; sign-in required.");

            try
            {
                OidcDiscovery disco = await GetDiscoveryAsync(ct);
                JObject tokenResponse = await RefreshAsync(disco.TokenEndpoint, existing.RefreshToken, ct);
                // Providers may omit id_token/refresh_token on refresh; carry the previous ones forward.
                return await BuildSessionFromTokenResponseAsync(disco, tokenResponse, null, ct, existing);
            }
            catch (OperationCanceledException) { return SsoAuthResult.Canceled(); }
            catch (Exception e)
            {
                // Offline but access token already expired: cannot safely proceed.
                return SsoAuthResult.Fail($"Could not renew session: {e.Message}");
            }
        }

        // ── HTTP steps ─────────────────────────────────────────────────────

        private async Task<JObject> ExchangeCodeAsync(string tokenEndpoint, string code, string redirectUri, string codeVerifier, CancellationToken ct)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = _config.ClientId,
                ["code_verifier"] = codeVerifier,
            };
            if (!string.IsNullOrEmpty(_config.ClientSecret))
                form["client_secret"] = _config.ClientSecret;
            return await PostFormAsync(tokenEndpoint, form, ct);
        }

        private async Task<JObject> RefreshAsync(string tokenEndpoint, string refreshToken, CancellationToken ct)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = _config.ClientId,
            };
            if (!string.IsNullOrEmpty(_config.ClientSecret))
                form["client_secret"] = _config.ClientSecret;
            return await PostFormAsync(tokenEndpoint, form, ct);
        }

        private static async Task<JObject> PostFormAsync(string endpoint, Dictionary<string, string> form, CancellationToken ct)
        {
            using var content = new FormUrlEncodedContent(form);
            using HttpResponseMessage resp = await Http.PostAsync(endpoint, content, ct);
            string body = await resp.Content.ReadAsStringAsync();
            JObject json;
            try { json = JObject.Parse(body); }
            catch { throw new Exception($"Token endpoint returned non-JSON ({(int)resp.StatusCode})."); }
            if (!resp.IsSuccessStatusCode)
            {
                string err = (string)json["error"] ?? resp.StatusCode.ToString();
                string desc = (string)json["error_description"];
                throw new Exception(string.IsNullOrEmpty(desc) ? err : $"{err}: {desc}");
            }
            return json;
        }

        private async Task<SsoAuthResult> BuildSessionFromTokenResponseAsync(
            OidcDiscovery disco, JObject tokenResponse, string expectedNonce, CancellationToken ct, BasisSsoSession fallback = null)
        {
            string idToken = (string)tokenResponse["id_token"] ?? fallback?.IdToken;
            string accessToken = (string)tokenResponse["access_token"] ?? fallback?.AccessToken;
            string refreshToken = (string)tokenResponse["refresh_token"] ?? fallback?.RefreshToken;

            if (string.IsNullOrEmpty(idToken))
                return SsoAuthResult.Fail("Token response contained no id_token.");

            var validationParams = new SsoTokenValidationParameters
            {
                Issuer = _config.Issuer,
                Audience = _config.ClientId,
                JwksUri = disco.JwksUri,
                ExpectedNonce = expectedNonce,
            };
            SsoTokenValidationResult validation = await _validator.ValidateIdTokenAsync(idToken, validationParams, ct);
            if (!validation.Valid)
                return SsoAuthResult.Fail($"id_token rejected: {validation.Error}");

            var claims = validation.Claims ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            // Merge UserInfo (groups/profile often live only there), guarding the sub matches.
            if (!string.IsNullOrEmpty(disco.UserInfoEndpoint) && !string.IsNullOrEmpty(accessToken))
            {
                try
                {
                    JObject userInfo = await FetchUserInfoAsync(disco.UserInfoEndpoint, accessToken, ct);
                    MergeUserInfo(claims, userInfo, validation.Subject);
                }
                catch (Exception e)
                {
                    BasisDebug.LogWarning($"[SSO] UserInfo fetch failed (continuing with id_token claims): {e.Message}");
                }
            }

            int accessExpiresIn = tokenResponse["expires_in"] != null ? tokenResponse["expires_in"].Value<int>() : 3600;
            DateTime accessExpiry = DateTime.UtcNow.AddSeconds(accessExpiresIn);

            DateTime? refreshExpiry = null;
            if (tokenResponse["refresh_expires_in"] != null)
                refreshExpiry = DateTime.UtcNow.AddSeconds(tokenResponse["refresh_expires_in"].Value<int>());
            else
                refreshExpiry = fallback?.RefreshTokenExpiresAtUtc;

            var session = new BasisSsoSession
            {
                Issuer = _config.Issuer,
                Sub = validation.Subject,
                IdToken = idToken,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                AccessTokenExpiresAtUtc = accessExpiry,
                RefreshTokenExpiresAtUtc = refreshExpiry,
                Claims = claims,
            };
            return SsoAuthResult.Ok(session);
        }

        private static async Task<JObject> FetchUserInfoAsync(string endpoint, string accessToken, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage resp = await Http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return JObject.Parse(await resp.Content.ReadAsStringAsync());
        }

        private static void MergeUserInfo(Dictionary<string, List<string>> claims, JObject userInfo, string expectedSub)
        {
            if (userInfo == null) return;
            string uiSub = (string)userInfo["sub"];
            if (!string.IsNullOrEmpty(uiSub) && !string.IsNullOrEmpty(expectedSub)
                && !string.Equals(uiSub, expectedSub, StringComparison.Ordinal))
            {
                BasisDebug.LogWarning("[SSO] UserInfo sub does not match id_token sub; ignoring UserInfo.");
                return;
            }
            Dictionary<string, List<string>> uiClaims = BasisRsaJwksTokenValidator.NormalizeClaims(userInfo);
            foreach (var kv in uiClaims)
            {
                if (!claims.ContainsKey(kv.Key)) claims[kv.Key] = kv.Value;
            }
        }

        // ── Loopback callback ──────────────────────────────────────────────

        private async Task<(string code, string state, string error)> WaitForCallbackAsync(HttpListener listener, CancellationToken ct)
        {
            using (ct.Register(() => { try { listener.Stop(); } catch { } }))
            {
                while (true)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch { ct.ThrowIfCancellationRequested(); throw; }

                    string path = context.Request.Url.AbsolutePath;
                    if (!string.Equals(path, NormalizePath(_config.Redirect.Path), StringComparison.OrdinalIgnoreCase))
                    {
                        WriteResponse(context, 404, "Not found.");
                        continue;
                    }

                    System.Collections.Specialized.NameValueCollection q = context.Request.QueryString;
                    string error = q["error"];
                    string code = q["code"];
                    string state = q["state"];
                    WriteResponse(context, 200,
                        "<html><body style='font-family:sans-serif;text-align:center;padding-top:3em'>" +
                        "<h2>Sign-in complete</h2><p>You can close this window and return to BasisVR.</p>" +
                        "</body></html>");
                    return (code, state, error);
                }
            }
        }

        private static void WriteResponse(HttpListenerContext context, int status, string html)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(html);
                context.Response.StatusCode = status;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = buffer.Length;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
            }
            catch { /* client may have navigated away */ }
        }

        // ── URL building ───────────────────────────────────────────────────

        private string BuildAuthorizeUrl(string authEndpoint, string redirectUri, string codeChallenge, string state, string nonce, string prompt)
        {
            // Build via a dictionary so provider-specific extras can't produce duplicate params and
            // an explicit switch-account prompt cleanly overrides any configured one.
            var p = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = _config.ClientId,
                ["redirect_uri"] = redirectUri,
                ["scope"] = string.Join(" ", _config.Scopes),
                ["state"] = state,
                ["nonce"] = nonce,
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256",
            };
            if (_config.ExtraAuthParams != null)
            {
                foreach (var kv in _config.ExtraAuthParams)
                    if (!string.IsNullOrEmpty(kv.Key)) p[kv.Key] = kv.Value ?? string.Empty;
            }
            if (!string.IsNullOrEmpty(prompt)) p["prompt"] = prompt;

            var sb = new StringBuilder(authEndpoint);
            sb.Append(authEndpoint.Contains("?") ? "&" : "?");
            bool first = true;
            foreach (var kv in p)
            {
                if (!first) sb.Append('&');
                first = false;
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
            }
            return sb.ToString();
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/callback";
            return path.StartsWith("/") ? path : "/" + path;
        }

        private static int GetFreeLoopbackPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
