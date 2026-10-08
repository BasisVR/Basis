using Unity.Scripting.LifecycleManagement;
using System;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Warns the local player when the avatar they are wearing is a file only this machine has.
    ///
    /// <para>An avatar is broadcast as a bare URL that every remote then resolves for itself. A
    /// <c>file://</c> URL names nothing outside this machine, so each remote's load throws and
    /// latches <c>HasFailedAvatarLoadGlobally</c> — they are left on the loading dummy for the rest
    /// of the session and never retry. None of that comes back over the wire, so the machine that
    /// chose the avatar is the only place it can be explained.</para>
    ///
    /// <para>Built-in avatars load by Addressable key out of each client's own build instead of by
    /// URL, so they carry across fine even though they are equally "not hosted anywhere" — both
    /// ends already ship the asset. That is why only <see cref="BasisLoadMode.Download"/> is
    /// considered here.</para>
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisLocalAvatarNetworkNotice
    {
        /// <summary>
        /// Avatar address the player has already been warned about, so re-equipping the same avatar
        /// does not nag. Scoped to one connection by <see cref="Reset"/>.
        /// </summary>
        private static string WarnedForUrl;

        /// <summary>
        /// Forgets what has already been warned about, so the next server gets its own warning
        /// rather than inheriting silence from the last one. Called as the connection is torn down.
        /// </summary>
        public static void Reset()
        {
            WarnedForUrl = null;
        }

        /// <summary>
        /// True when this avatar loads here but nowhere else: a downloaded bundle whose address is
        /// a local file path rather than something a remote can fetch.
        /// </summary>
        public static bool IsLocalOnly(byte LoadMode, BasisLoadableBundle Bundle)
        {
            if (LoadMode != (byte)BasisLoadMode.Download)
            {
                return false;
            }
            if (Bundle == null || Bundle.BasisRemoteBundleEncrypted == null)
            {
                return false;
            }
            return BasisIOManagement.IsLocalBeeUrl(Bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
        }

        /// <summary>
        /// Shows the warning when the avatar currently applied to the local player is one no other
        /// client can load. Safe to call whenever the avatar or the connection changes — it stays
        /// quiet while offline, when the avatar is reachable, and when this one was already flagged.
        /// </summary>
        public static void NotifyIfLocalOnly()
        {
            BasisLocalPlayer Player = BasisLocalPlayer.Instance;
            if (Player == null)
            {
                return;
            }

            // Alone, a local-only avatar is nobody else's problem.
            if (!BasisNetworkConnection.LocalPlayerIsConnected)
            {
                return;
            }

            // Read what is actually worn rather than what was asked for: a load that failed leaves
            // the player on the loading dummy, which is not the avatar to warn about.
            if (!IsLocalOnly(Player.AvatarLoadMode, Player.AvatarMetaData))
            {
                return;
            }

            string Url = Player.AvatarMetaData.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
            if (WarnedForUrl == Url)
            {
                return;
            }
            WarnedForUrl = Url;

            BasisDebug.LogWarning($"Local avatar {Url} is worn while connected; remote players cannot load it.");

            string Title = BasisLocalization.Get("network.avatarLocalOnly.title");
            string Body = BasisLocalization.Get("network.avatarLocalOnly.body");
            string Accept = BasisLocalization.Get("ui.ok");

            // Unsolicited, so under do-not-disturb this belongs in the notification bell instead of
            // interrupting — CreateNew makes that call itself. Only the branch that actually draws a
            // panel needs a menu, and a null Instance just means the menu is currently closed.
            if (!BasisNotificationCenter.RouteToNotifications && !BasisMainMenu.Instance)
            {
                BasisMainMenu.Open();
            }

            BasisMenuDialoguePanel.CreateNew(Title, Body, Accept, (Action<bool>)null, true, BasisPanelSeverity.Caution);
        }
    }
}
