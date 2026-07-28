using System;
using Basis.BTween;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Watches the instance population and, once per app run for each population tier, offers to
    /// switch <see cref="BasisPerformanceMode"/> on at the level that tier warrants. Pumped from
    /// the central tick; no MonoBehaviour.
    ///
    /// Each tier asks at most once per app lifetime — the static "asked" flags reset on restart,
    /// so the offer comes back the next time the app is launched into a crowded instance. Crossing
    /// a higher tier supersedes the lower ones (their flags are marked asked at the same time), so
    /// joining an already-huge instance shows a single prompt instead of stacking several.
    ///
    /// The dialogue lists the values the level will actually resolve to, read back from the mode
    /// itself, so the text can never drift from the effect. Nothing here is asked when the player
    /// has already turned the mode on to at least that level, or has it following the population
    /// automatically.
    /// </summary>
    public static class BasisHighPlayerCapPerformanceMode
    {
        // Population tiers in ascending order. A tier arms when the occupant count exceeds its
        // threshold. The count includes the local player, so "over 250 people" means 251 occupants.
        private static readonly int[] Thresholds = BasisPerformanceMode.PopulationThresholds;

        // Whether each tier's prompt has already been offered this app run.
        private static readonly bool[] _tierAsked = new bool[Thresholds.Length];

        // Cheap throttle so the player-count read isn't taken every single frame in a known-heavy
        // instance. ~ every 32 frames is plenty responsive for a one-off suggestion.
        private static int _frameGate;

        public static void Simulate()
        {
            // Highest tier already handled → nothing left to offer this app run. One bool check/frame.
            if (_tierAsked[_tierAsked.Length - 1])
            {
                return;
            }

            if ((++_frameGate & 31) != 0)
            {
                return;
            }

            if (!ShouldOffer())
            {
                return;
            }

            int count = BasisNetworkPlayer.GetPlayerCount();
            int tier = HighestUnaskedTier(count);
            if (tier < 0)
            {
                return;
            }

            MarkAskedThrough(tier);
            ShowPrompt(Thresholds[tier], LevelForTier(tier));
        }

        // The mode already covers this crowd, or the player asked it to track the population on its
        // own — either way there is nothing to offer.
        private static bool ShouldOffer()
        {
            if (!BasisSettingsDefaults.HighPlayerCapSuggestions.RawValue)
            {
                return false;
            }

            return !BasisSettingsDefaults.PerformanceModeAuto.RawValue;
        }

        private static BasisPerformanceLevel LevelForTier(int tier) => (BasisPerformanceLevel)(tier + 1);

        /// <summary>
        /// Offer the performance preset before a connection to a server whose probe reported a
        /// crowd. <paramref name="prospectiveOccupants"/> is the reported online count plus the
        /// joining local player, matching the occupant semantics of the post-join watcher.
        /// Returns true when a prompt was shown and the connection is deferred — every choice in
        /// the prompt (including closing it unanswered) then runs <paramref name="continueConnect"/>,
        /// so the click on Connect is never silently swallowed. Returns false when nothing needs
        /// asking and the caller should just connect.
        /// </summary>
        public static bool TryOfferBeforeConnect(int prospectiveOccupants, Action continueConnect)
        {
            if (continueConnect == null)
            {
                return false;
            }

            if (!ShouldOffer())
            {
                return false;
            }

            // Do-not-disturb → never block a connection on a popup; the post-join watcher will
            // park the same suggestion in the notification list instead.
            if (BasisNotificationCenter.RouteToNotifications)
            {
                return false;
            }

            int tier = HighestUnaskedTier(prospectiveOccupants);
            if (tier < 0)
            {
                return false;
            }

            MarkAskedThrough(tier);
            ShowPrompt(Thresholds[tier], LevelForTier(tier), forceShow: true, continueConnect: continueConnect);
            return true;
        }

        // Highest crossed tier that is still unasked; -1 when nothing (new) is crossed. A crossed
        // tier that was already asked returns -1 too — lower tiers were marked at the same time,
        // so there is nothing left to offer.
        private static int HighestUnaskedTier(int occupantCount)
        {
            for (int i = Thresholds.Length - 1; i >= 0; i--)
            {
                if (occupantCount <= Thresholds[i])
                {
                    continue;
                }
                if (_tierAsked[i])
                {
                    return -1;
                }
                return BasisPerformanceMode.ActiveLevel >= LevelForTier(i) ? -1 : i;
            }
            return -1;
        }

        // Mark this tier and every lower tier asked up front: ask once per app run, and never
        // fire a lighter prompt after a tighter one has been shown.
        private static void MarkAskedThrough(int tier)
        {
            for (int j = 0; j <= tier; j++)
            {
                _tierAsked[j] = true;
            }
        }

        private static void ShowPrompt(int threshold, BasisPerformanceLevel level, bool forceShow = false, Action continueConnect = null)
        {
            bool preConnect = continueConnect != null;
            BasisPerformanceMode.DescribeLevel(level,
                out float avatarRange, out float maxVisibleAvatars, out float poseLodBias,
                out float avatarMeshLodPercent, out float viewConeAngle);

            string title = BasisLocalization.Get("settings.highPlayerCap.prompt.title");
            string body = BasisLocalization.Get(
                preConnect ? "settings.highPlayerCap.prompt.bodyPreConnect" : "settings.highPlayerCap.prompt.body",
                threshold,
                avatarRange,
                maxVisibleAvatars,
                poseLodBias,
                avatarMeshLodPercent,
                viewConeAngle);

            string levelLine = BasisLocalization.Get("settings.highPlayerCap.prompt.levelLine",
                BasisPerformanceMode.DisplayName(level));

            // Do-not-disturb / admin panel open → park it in the notification list, recoverable from
            // the bell. forceShow bypasses this when re-opened from that list so it actually shows.
            if (!forceShow && BasisNotificationCenter.RouteToNotifications)
            {
                BasisNotificationCenter.AddPending(title, body, AddressableAssets.Sprites.Information,
                    reopen: () => ShowPrompt(threshold, level, true),
                    onDismiss: () => { });
                return;
            }

            // Force the menu open so an unsolicited prompt is actually seen (same as the eye-tracking
            // and direct-connection prompts).
            BasisMainMenu.Open();
            if (!BasisMainMenu.Instance)
            {
                continueConnect?.Invoke();
                return;
            }

            BasisMenuPanel.PanelData data = new BasisMenuPanel.PanelData
            {
                Title = title,
                PanelSize = new Vector2(950, 620),
                PanelPosition = new Vector3(0, -40, -5),
            };
            BasisMenuPanel panel = BasisMenuPanel.CreateNew(
                data, BasisMainMenu.Instance.MenuObjectInstance.PanelRoot, BasisMenuPanel.PanelStyles.Page);

            PanelTabPage tab = PanelTabPage.CreateVertical(panel.Descriptor.ContentParent);
            RectTransform root = tab.Descriptor.ContentParent;

            PanelElementDescriptor info = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, root);
            info.SetTitle(levelLine);
            info.SetDescription(body);

            bool answered = false;
            void Decide(bool enable)
            {
                if (answered) return;
                answered = true;
                if (enable)
                {
                    BasisPerformanceMode.SetLevel(level);
                }
                panel.ReleaseInstance();
                continueConnect?.Invoke();
            }

            void DisableFuturePrompts()
            {
                if (answered) return;
                answered = true;
                BasisSettingsDefaults.HighPlayerCapSuggestions.SetValue(false);
                panel.ReleaseInstance();
                ShowDisabledHint(continueConnect);
            }

            PanelTabGroup actionGroup = PanelTabGroup.CreateNew(root, LayoutDirection.HorizontalNoBackground);
            actionGroup.Descriptor.SetHeight(80);

            PanelButton enableButton = PanelButton.CreateNew(PanelButton.ButtonStyles.AcceptButton, actionGroup.TabButtonParent);
            enableButton.Descriptor.SetTitle(BasisLocalization.Get(preConnect
                ? "settings.highPlayerCap.prompt.enableConnect"
                : "settings.highPlayerCap.prompt.enable"));
            enableButton.OnClicked += () => Decide(true);

            PanelButton noButton = PanelButton.CreateNew(PanelButton.ButtonStyles.CancelButton, actionGroup.TabButtonParent);
            noButton.Descriptor.SetTitle(BasisLocalization.Get(preConnect
                ? "settings.highPlayerCap.prompt.connectWithout"
                : "settings.highPlayerCap.prompt.no"));
            noButton.OnClicked += () => Decide(false);

            PanelButton dontAskButton = PanelButton.CreateNew(PanelButton.ButtonStyles.StandardButton, actionGroup.TabButtonParent);
            dontAskButton.Descriptor.SetTitle(BasisLocalization.Get("settings.highPlayerCap.prompt.dontAskAgain"));
            dontAskButton.OnClicked += DisableFuturePrompts;

            // Closed without choosing (menu closed, tab switched): post-join → recover from the bell
            // (the tier is already marked asked, so the tick won't bring it back on its own);
            // pre-connect → the user asked to connect, so the connection still proceeds.
            panel.OnInstanceReleased += () =>
            {
                if (answered) return;
                answered = true;
                if (preConnect)
                {
                    continueConnect.Invoke();
                    return;
                }
                BasisNotificationCenter.AddPending(title, body, AddressableAssets.Sprites.Information,
                    reopen: () => ShowPrompt(threshold, level, true),
                    onDismiss: () => { });
            };

            panel.Descriptor.ForceRebuild();
            UIAnimations.PopIn(panel);
        }

        // Confirms the opt-out and points at the exact setting that turns the prompts back on;
        // in the pre-connect flow the pending connection resumes once the dialogue is closed.
        private static void ShowDisabledHint(Action continueConnect)
        {
            if (BasisMainMenu.Instance != null)
            {
                BasisMainMenu.Instance.OpenDialogue(
                    BasisLocalization.Get("settings.highPlayerCap.disabled.title"),
                    BasisLocalization.Get("settings.highPlayerCap.disabled.body"),
                    BasisLocalization.Get("ui.ok"),
                    _ => continueConnect?.Invoke());
            }
            else
            {
                continueConnect?.Invoke();
            }
        }
    }
}
