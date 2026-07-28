using System;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Integration.Sso
{
    /// <summary>
    /// The SSO "brain": loads config, drives silent-renew / interactive sign-in / sign-out, applies
    /// the org access rules, and binds identity + display name. UI (the login screen, the sign-out
    /// button) and the launch gate call into this and observe <see cref="StateChanged"/>; all the
    /// orchestration lives here so those surfaces stay thin.
    /// </summary>
    public static class BasisSsoAuthController
    {
        private static BasisOidcConfig _config;
        private static BasisOidcLoginService _service;

        public static BasisSsoSession Current { get; private set; }
        public static bool IsSignedIn => Current != null;

        /// <summary>True once config has loaded and validated. When false, SSO cannot proceed.</summary>
        public static bool IsConfigured => _config != null;

        public static string ActiveDisplayName =>
            IsSignedIn ? BasisSsoIdentityBinding.ResolveDisplayNameFromClaims(_config, Current) : null;

        /// <summary>Raised after any sign-in/sign-out/renew so the gate and settings UI can refresh.</summary>
        public static event Action StateChanged;

        /// <summary>
        /// One-shot OIDC <c>prompt</c> consumed by the next <see cref="SignInAsync"/>. Setting this
        /// to "login" before <see cref="SignOut"/> makes the gate's re-login force the account
        /// chooser — how the settings "Switch account" action works without a second parallel flow.
        /// </summary>
        public static string PendingPrompt;

        /// <summary>Loads and validates config. Safe to call repeatedly; returns cached result.</summary>
        public static bool EnsureConfigLoaded()
        {
            if (_config != null) return true;
            _config = BasisOidcConfig.Load();
            if (_config == null) return false;
            _service = new BasisOidcLoginService(_config);
            return true;
        }

        /// <summary>
        /// Startup path: reuse a stored session (silent renew / offline). Does not launch the
        /// browser. Returns the outcome so the gate can decide whether to show the login screen.
        /// </summary>
        public static async Task<SsoAuthResult> InitializeAsync(CancellationToken ct = default)
        {
            if (!EnsureConfigLoaded())
                return SsoAuthResult.Fail("SSO is not configured.");

            BasisSsoSession stored = BasisSsoSessionStore.Load();
            if (stored == null) return SsoAuthResult.Fail("No stored session.");

            SsoAuthResult result = await _service.TrySilentAsync(stored, ct);
            return await FinalizeAsync(result, persist: true);
        }

        /// <summary>Interactive browser sign-in.</summary>
        public static async Task<SsoAuthResult> SignInAsync(CancellationToken ct = default, string prompt = null)
        {
            if (!EnsureConfigLoaded())
                return SsoAuthResult.Fail("SSO is not configured.");

            string effectivePrompt = prompt ?? PendingPrompt;
            PendingPrompt = null;
            SsoAuthResult result = await _service.SignInInteractiveAsync(ct, effectivePrompt);
            return await FinalizeAsync(result, persist: true);
        }

        /// <summary>Sign out: drop the local session and identity binding. UI returns to the login gate.</summary>
        public static void SignOut()
        {
            // Remember the (possibly edited) display name for this account before tearing down.
            BasisSsoIdentityBinding.CaptureGlobalNameForActive();
            Current = null;
            BasisSsoSessionStore.Clear();
            BasisSsoIdentityBinding.Unbind();
            StateChanged?.Invoke();
        }

        /// <summary>Sign out then immediately prompt for a different account.</summary>
        public static async Task<SsoAuthResult> SwitchAccountAsync(CancellationToken ct = default)
        {
            SignOut();
            // prompt=login forces the IdP to re-authenticate rather than silently reusing its session.
            return await SignInAsync(ct, prompt: "login");
        }

        public static async Task<string> GetEndSessionEndpointAsync(CancellationToken ct = default)
        {
            if (!EnsureConfigLoaded() || _service == null) return null;
            return await _service.GetEndSessionEndpointAsync(ct);
        }

        // ── shared post-processing ─────────────────────────────────────────

        private static Task<SsoAuthResult> FinalizeAsync(SsoAuthResult result, bool persist)
        {
            if (result == null || !result.Success || result.Session == null)
                return Task.FromResult(result ?? SsoAuthResult.Fail("Unknown sign-in failure."));

            SsoAccessDecision decision = BasisSsoAccessControl.Evaluate(_config, result.Session);
            if (!decision.Allowed)
            {
                // Never persist or activate a denied session.
                BasisSsoSessionStore.Clear();
                BasisSsoIdentityBinding.Unbind();
                Current = null;
                StateChanged?.Invoke();
                return Task.FromResult(SsoAuthResult.Deny(decision.Reason));
            }

            Current = result.Session;
            if (persist) BasisSsoSessionStore.Save(result.Session);
            BasisSsoIdentityBinding.Bind(_config, result.Session);
            StateChanged?.Invoke();
            return Task.FromResult(SsoAuthResult.Ok(result.Session));
        }
    }
}
