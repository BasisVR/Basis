using System;
using System.Collections;
using System.Threading;
using Basis.BasisUI;
using Basis.Integration.Sso;
using Basis.Scripts.Networking;
using UnityEngine;

namespace Basis.Integration.Sso.FrameworkGate
{
    /// <summary>
    /// The launch gate. When an OIDC config is present it blocks the normal flow (auto-connect and
    /// manual connects) until the user has signed in via <see cref="BasisSsoAuthController"/>. With
    /// no config it disables itself so non-SSO / dev builds are unaffected. Login is presented
    /// through the existing <see cref="BasisMainMenu"/> dialogue system so it renders and takes
    /// input on both desktop and PCVR. This is a client-side gate only — the server/DID protocol is
    /// unchanged (see docs/sso-spec.md §7).
    /// </summary>
    public static class BasisSsoGate
    {
        internal const string BlockedReason = "Please sign in before connecting.";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!BasisSsoAuthController.EnsureConfigLoaded())
            {
                BasisDebug.Log("[SSO] No OIDC config found; launch gate disabled.");
                return;
            }

            // Block startup auto-connect and every manual/CLI connect until sign-in completes.
            BasisConnectionService.AutoConnectAttempted = true;
            BasisConnectionService.ConnectionBlockedReason = () =>
                (BasisSsoAuthController.IsConfigured && !BasisSsoAuthController.IsSignedIn) ? BlockedReason : null;

            BasisSsoAccountTab.Register();
            BasisSsoGateRunner.Begin();
        }

        /// <summary>Called once sign-in succeeds: let connections through again.</summary>
        internal static void OnSignedIn()
        {
            // Allow the Servers panel's auto-connect to run on next open now that we're signed in.
            BasisConnectionService.AutoConnectAttempted = false;
        }

        /// <summary>Called on sign-out: re-arm the block so nothing connects until sign-in again.</summary>
        internal static void OnSignedOut()
        {
            BasisConnectionService.AutoConnectAttempted = true;
        }
    }

    /// <summary>
    /// Persistent driver for the gate: runs silent renew, then presents the login dialogs, and
    /// re-engages whenever the user signs out. Kept as a MonoBehaviour so it has a Unity context to
    /// await on and can wait for the menu system to become available at startup.
    /// </summary>
    public sealed class BasisSsoGateRunner : MonoBehaviour
    {
        private static BasisSsoGateRunner _instance;

        private CancellationTokenSource _cts;
        private bool _flowActive;

        internal static void Begin()
        {
            if (_instance != null) return;
            GameObject go = new GameObject("BasisSsoGate");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<BasisSsoGateRunner>();
        }

        private void Start()
        {
            BasisSsoAuthController.StateChanged += OnStateChanged;
            StartFlow();
        }

        private void OnDestroy()
        {
            BasisSsoAuthController.StateChanged -= OnStateChanged;
            _cts?.Cancel();
            _cts?.Dispose();
        }

        private void OnStateChanged()
        {
            // If the user signed out (and SSO is still required), gate again.
            if (BasisSsoAuthController.IsConfigured && !BasisSsoAuthController.IsSignedIn && !_flowActive)
            {
                BasisSsoGate.OnSignedOut();
                StartFlow();
            }
        }

        private void StartFlow()
        {
            if (_flowActive) return;
            _flowActive = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            RunGateAsync();
        }

        private async void RunGateAsync()
        {
            try
            {
                // 1) Try to reuse a stored session (silent renew / offline).
                SsoAuthResult silent = await BasisSsoAuthController.InitializeAsync(_cts.Token);
                if (silent.Success)
                {
                    Succeed();
                    return;
                }

                // 2) Need interactive sign-in — present the prompt through the menu.
                await EnsureMenuReadyAsync();
                PresentSignInPrompt();
            }
            catch (OperationCanceledException) { /* re-gate cancelled */ }
            catch (Exception e)
            {
                BasisDebug.LogError($"[SSO] Gate failure: {e}");
                await EnsureMenuReadyAsync();
                ShowDialog("Sign-in error", e.Message, "Retry", "Quit", ok =>
                {
                    if (ok) StartFlow(); else Quit();
                });
            }
        }

        // ── Dialog steps ───────────────────────────────────────────────────

        private void PresentSignInPrompt()
        {
            ShowDialog(
                "Sign in required",
                "This BasisVR client requires you to sign in with your organization account to continue.",
                "Sign in",
                "Quit",
                accepted =>
                {
                    if (accepted) BeginInteractiveSignIn();
                    else Quit();
                });
        }

        private async void BeginInteractiveSignIn()
        {
            ShowDialog(
                "Signing in…",
                "Complete the sign-in in your web browser, then return here.",
                "Cancel",
                _ => _cts?.Cancel());

            SsoAuthResult result;
            try
            {
                result = await BasisSsoAuthController.SignInAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                ResetCancellation();
                PresentSignInPrompt();
                return;
            }

            ReleaseDialogue();

            if (result.Success)
            {
                Succeed();
            }
            else if (result.AccessDenied)
            {
                ShowDialog("Access denied", result.Error ?? "You are not permitted to use this client.",
                    "Try another account", "Quit", tryAgain =>
                    {
                        if (tryAgain) { ResetCancellation(); BeginInteractiveSignIn(); }
                        else Quit();
                    });
            }
            else if (result.Cancelled)
            {
                ResetCancellation();
                PresentSignInPrompt();
            }
            else
            {
                ShowDialog("Sign-in failed", result.Error ?? "Unknown error.", "Retry", "Quit", retry =>
                {
                    if (retry) { ResetCancellation(); BeginInteractiveSignIn(); }
                    else Quit();
                });
            }
        }

        private void Succeed()
        {
            ReleaseDialogue();
            BasisSsoGate.OnSignedIn();
            _flowActive = false;
            BasisDebug.Log($"[SSO] Signed in as '{BasisSsoAuthController.ActiveDisplayName}'.");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private void ResetCancellation()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }

        private IEnumerator EnsureMenuReadyCoroutine(Action done)
        {
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                bool ready = false;
                try
                {
                    if (!BasisMainMenu.Instance) BasisMainMenu.Open();
                    ready = BasisMainMenu.Instance;
                }
                catch (Exception e)
                {
                    BasisDebug.LogWarning($"[SSO] Menu not ready yet: {e.Message}");
                }
                if (ready) break;
                yield return null;
            }
            done?.Invoke();
        }

        private System.Threading.Tasks.Task EnsureMenuReadyAsync()
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            StartCoroutine(EnsureMenuReadyCoroutine(() => tcs.TrySetResult(true)));
            return tcs.Task;
        }

        private static void ShowDialog(string title, string desc, string accept, string deny, Action<bool> cb)
        {
            if (!EnsureMenuInstance()) return;
            ReleaseDialogue();
            BasisMainMenu.Instance.OpenDialogue(title, desc, accept, deny, cb);
        }

        private static void ShowDialog(string title, string desc, string accept, Action<bool> cb)
        {
            if (!EnsureMenuInstance()) return;
            ReleaseDialogue();
            BasisMainMenu.Instance.OpenDialogue(title, desc, accept, cb);
        }

        private static bool EnsureMenuInstance()
        {
            try
            {
                if (!BasisMainMenu.Instance) BasisMainMenu.Open();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[SSO] Could not open the menu to show the sign-in dialog: {e.Message}");
            }
            return BasisMainMenu.Instance;
        }

        private static void ReleaseDialogue()
        {
            if (BasisMainMenu.Instance && BasisMainMenu.Instance.Dialogue)
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();
        }

        private void Quit()
        {
            BasisDebug.Log("[SSO] User chose to quit at the sign-in gate.");
            Application.Quit();
            // In the editor Application.Quit is a no-op; leave the gate armed.
        }
    }
}
