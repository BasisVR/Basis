using Basis.Scripts.Common;
using BasisNetworkClient;
using UnityEngine;

namespace Basis.Integration.Sso
{
    /// <summary>
    /// Ties a signed-in SSO user to the client's network identity and display name.
    ///
    /// Identity: the local DID keypair is kept (server-side DID challenge/response is unchanged),
    /// but namespaced per OIDC <c>sub</c> via <see cref="BasisDIDAuthIdentityClient.IdentityNamespace"/>
    /// so distinct users on one machine keep distinct DIDs.
    ///
    /// Display name: seeded from the configured claims on first sign-in for a subject, then stored
    /// per-subject so an account switch restores that account's (possibly user-edited) name. The
    /// active name is mirrored into the existing global username file the rest of the app reads.
    /// </summary>
    public static class BasisSsoIdentityBinding
    {
        // Mirrors Basis.Scripts.Networking.BasisConnectionService.UsernameFileName. Kept as a
        // local constant so this package doesn't take a dependency on the whole framework assembly.
        private const string GlobalUsernameFile = "CachedUserName.BAS";
        private const string PerSubjectNamePrefsPrefix = "SsoDisplayName::";

        public static string ActiveSubject { get; private set; }

        /// <summary>
        /// Binds the DID namespace to this session's subject, ensures a DID exists, and resolves
        /// the active display name (per-subject store first, then claims) into the global username.
        /// </summary>
        public static void Bind(BasisOidcConfig config, BasisSsoSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.Sub)) return;

            ActiveSubject = session.Sub;
            BasisDIDAuthIdentityClient.IdentityNamespace = session.Sub;
            // Force the keypair for this namespace to be created/loaded now.
            BasisDIDAuthIdentityClient.GetOrSaveDID();

            string stored = PlayerPrefs.GetString(PerSubjectNamePrefsPrefix + session.Sub, string.Empty);
            string displayName = !string.IsNullOrWhiteSpace(stored)
                ? stored
                : ResolveDisplayNameFromClaims(config, session);

            SaveActiveDisplayName(displayName);
        }

        /// <summary>
        /// Copies the current global username back into the active subject's per-account store, so a
        /// name the user edited in-app is remembered for that account across a switch. Call before sign-out.
        /// </summary>
        public static void CaptureGlobalNameForActive()
        {
            if (string.IsNullOrEmpty(ActiveSubject)) return;
            string current = BasisDataStore.LoadString(GlobalUsernameFile, string.Empty);
            if (!string.IsNullOrWhiteSpace(current))
            {
                PlayerPrefs.SetString(PerSubjectNamePrefsPrefix + ActiveSubject, current.Trim());
                PlayerPrefs.Save();
            }
        }

        /// <summary>Clears the binding on sign-out (returns the DID namespace to the default).</summary>
        public static void Unbind()
        {
            ActiveSubject = null;
            BasisDIDAuthIdentityClient.IdentityNamespace = null;
        }

        /// <summary>
        /// Persists a display name for the currently bound subject and mirrors it into the global
        /// username file. Call this when the user edits their name so the edit sticks per-account.
        /// </summary>
        public static void SaveActiveDisplayName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return;
            displayName = displayName.Trim();
            if (!string.IsNullOrEmpty(ActiveSubject))
            {
                PlayerPrefs.SetString(PerSubjectNamePrefsPrefix + ActiveSubject, displayName);
                PlayerPrefs.Save();
            }
            BasisDataStore.SaveString(displayName, GlobalUsernameFile);
        }

        public static string ResolveDisplayNameFromClaims(BasisOidcConfig config, BasisSsoSession session)
        {
            if (config?.DisplayNameClaims != null)
            {
                foreach (string claim in config.DisplayNameClaims)
                {
                    string value = session.GetFirstClaim(claim);
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
            }
            return string.IsNullOrEmpty(session.Sub) ? "User" : session.Sub;
        }
    }
}
