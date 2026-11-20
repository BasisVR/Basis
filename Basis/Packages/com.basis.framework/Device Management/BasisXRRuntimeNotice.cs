using Basis.BasisUI;
using System;

namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// Tells the user when a VR runtime did not come up the way they asked for it.
    /// Every give-up path in the XR boot chain used to end in a log line only, so from the
    /// user's side the headset simply never woke up and the client sat in Desktop with no
    /// explanation. Each report names what was asked for, what actually started, and why.
    /// </summary>
    public static class BasisXRRuntimeNotice
    {
        /// <summary>
        /// Human-readable name for a boot mode. Shared with the platform settings panel so the
        /// runtime is called the same thing in a failure popup and in the mode list.
        /// </summary>
        public static string ModeDisplayName(string mode)
        {
            if (string.Equals(mode, BasisConstants.OpenVRLoader, StringComparison.Ordinal))
            {
                return BasisLocalization.Get("settings.platform.mode.openvr");
            }
            if (string.Equals(mode, BasisConstants.OpenXRLoader, StringComparison.Ordinal))
            {
                return BasisLocalization.Get("settings.platform.mode.openxr");
            }
            if (string.Equals(mode, BasisConstants.WebXRLoader, StringComparison.Ordinal))
            {
                return BasisLocalization.Get("settings.platform.mode.webxr");
            }
            if (string.Equals(mode, BasisConstants.Desktop, StringComparison.Ordinal))
            {
                return BasisLocalization.Get("settings.platform.mode.desktop");
            }
            return mode;
        }

        /// <summary>
        /// The requested runtime could not start and the session landed somewhere else.
        /// </summary>
        /// <param name="requestedMode">The mode the user (or the boot default) asked for.</param>
        /// <param name="landedMode">The mode actually running now.</param>
        /// <param name="reason">Short sentence describing why the requested runtime refused.</param>
        public static void ReportFallback(string requestedMode, string landedMode, string reason)
        {
            BasisDebug.LogError($"XR: {requestedMode} could not start ({reason}) — continuing as {landedMode}", BasisDebug.LogTag.Device);

            Show(BasisLocalization.Get("settings.platform.vrFailed.title"),
                BasisLocalization.Get("settings.platform.vrFailed.body",
                    ModeDisplayName(requestedMode),
                    ModeDisplayName(landedMode),
                    reason));
        }

        /// <summary>
        /// The requested runtime could not start, but another VR runtime picked the session up.
        /// Worth its own report: the user asked for one headset stack and is now on the other,
        /// which changes which trackers and bindings they get.
        /// </summary>
        public static void ReportSubstituted(string requestedMode, string startedMode)
        {
            BasisDebug.LogWarning($"XR: {requestedMode} could not start — {startedMode} took over", BasisDebug.LogTag.Device);

            Show(BasisLocalization.Get("settings.platform.vrSubstituted.title"),
                BasisLocalization.Get("settings.platform.vrSubstituted.body",
                    ModeDisplayName(requestedMode),
                    ModeDisplayName(startedMode)));
        }

        /// <summary>
        /// A mode switch was refused before anything was torn down — currently only when the
        /// other VR runtime already owns the session. See
        /// <see cref="BasisDeviceManagement.CanEnterMode"/>.
        /// </summary>
        public static void ReportBlocked(string requestedMode, string reason)
        {
            BasisDebug.LogWarning($"XR: refused switch to {requestedMode} — {reason}", BasisDebug.LogTag.Device);

            Show(BasisLocalization.Get("settings.platform.vrBlocked.title"), reason);
        }

        /// <summary>
        /// Puts the notice in front of the user. A dialogue is preferred — this always follows a
        /// mode change the user can see — and the menu is opened for it when closed, since a
        /// closed menu is the normal state rather than a sign the UI is unavailable. Anything
        /// that stops the dialogue appearing (do-not-disturb, a dialogue already up, the menu
        /// failing to open) falls back to a notification-centre record so the report is never
        /// simply dropped.
        /// </summary>
        private static void Show(string title, string body)
        {
            try
            {
                if (!BasisNotificationCenter.RouteToNotifications)
                {
                    if (!BasisMainMenu.Instance)
                    {
                        BasisMainMenu.Open();
                    }

                    if (BasisMainMenu.Instance && !BasisMainMenu.Instance.Dialogue)
                    {
                        BasisMainMenu.Instance.OpenDialogue(
                            title,
                            body,
                            BasisLocalization.Get("ui.ok"),
                            value => { },
                            divertible: false,
                            BasisPanelSeverity.Caution);

                        if (BasisMainMenu.Instance.Dialogue)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"XR notice could not open a dialogue: {e}", BasisDebug.LogTag.Device);
            }

            BasisNotificationCenter.LogResolved(
                title,
                body,
                AddressableAssets.Sprites.Information,
                BasisNotificationStatus.Dismissed);
        }
    }
}
