#if !BASIS_DISABLE_MICROPHONE
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Settings;
using UnityEngine;

namespace Basis.BasisUI
{
    public class MicModeProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        static MicModeProvider _instance;
        static bool _added;

        [RuntimeInitializeOnLoadMethod]
        public static void Init()
        {
            _instance = new MicModeProvider();
            BasisTalkModeManager.OnLocalTalkModeChanged -= Refresh;
            BasisTalkModeManager.OnLocalTalkModeChanged += Refresh;
            BasisSettingsSystem.OnSettingsFinishedChanges -= Refresh;
            BasisSettingsSystem.OnSettingsFinishedChanges += Refresh;
            Refresh();
        }

        static void Refresh()
        {
            BasisDeviceManagement.EnqueueOnMainThread(RefreshMainThread);
        }

        static void RefreshMainThread()
        {
            bool show = BasisTalkModeManager.ShouldShowModeButton();
            if (show && !_added)
            {
                _added = true;
                BasisMenuBase<BasisMainMenu>.AddProvider(_instance);
            }
            else if (!show && _added)
            {
                _added = false;
                BasisMenuBase<BasisMainMenu>.RemoveProvider(_instance);
            }
            else if (show && _added && _instance.BoundButton != null)
            {
                _instance.UpdateButtonVisuals(_instance.BoundButton);
            }
        }

        public override string Title => TitleText(BasisTalkModeManager.CurrentMode);
        public override string IconAddress => AddressableAssets.Sprites.People;
        public override int Order => 5; // right after the mute button (Order 0)
        public override bool Hidden => false;

        public override void RunAction()
        {
            BasisTalkModeManager.CycleMode();
        }

        public override void OnButtonCreated(PanelButton button)
        {
            UpdateButtonVisuals(button);

            Vector2 size = button.rectTransform.sizeDelta;
            if (size.x > 0f) button.SetSize(new Vector2(size.x * 0.5f, size.y));
        }

        private static readonly Color NormalColor = Color.white;
        private static readonly Color PrivateColor = new Color(0.6078432f, 0.1882353f, 1f, 1f);
        private static readonly Color DirectColor = new Color(0.12156863f, 0.7490196f, 0.3529412f, 1f);
        private static readonly Color ThisPersonColor = new Color(1f, 0.3098039f, 0.627451f, 1f);
        private static readonly Color AnnounceColor = new Color(1f, 0.5490196f, 0f, 1f);
        private static readonly Color ShoutColor = new Color(1f, 0.8117647f, 0.1607843f, 1f);
        private static readonly Color NoOneColor = new Color(0.3921569f, 0.5882353f, 0.7843137f, 1f);

        private void UpdateButtonVisuals(PanelButton button)
        {
            BasisTalkMode mode = BasisTalkModeManager.CurrentMode;
            button.SetIcon(IconAddress);
            button.Descriptor.SetTitle(TitleText(mode));
            Color color = ColorFor(mode);
            button.Descriptor.IconImage.color = color;
            button.Descriptor.TitleLabel.color = color;
        }

        private static Color ColorFor(BasisTalkMode mode)
        {
            switch (mode)
            {
                case BasisTalkMode.Private: return PrivateColor;
                case BasisTalkMode.Direct: return DirectColor;
                case BasisTalkMode.ThisPerson: return ThisPersonColor;
                case BasisTalkMode.Announce: return AnnounceColor;
                case BasisTalkMode.Shout: return ShoutColor;
                case BasisTalkMode.NoOne: return NoOneColor;
                default: return NormalColor;
            }
        }

        private static string TitleKey(BasisTalkMode mode)
        {
            switch (mode)
            {
                case BasisTalkMode.Private: return "menu.provider.micmode.private";
                case BasisTalkMode.Direct: return "menu.provider.micmode.direct";
                case BasisTalkMode.ThisPerson: return "menu.provider.micmode.thisperson";
                case BasisTalkMode.Announce: return "menu.provider.micmode.announce";
                case BasisTalkMode.Shout: return "menu.provider.micmode.shout";
                case BasisTalkMode.NoOne: return "menu.provider.micmode.noone";
                default: return "menu.provider.micmode.normal";
            }
        }

        private const int MaxNameLength = 14;

        // For 1-on-1, label the button with the target's display name (rich text stripped,
        // truncated) instead of the generic "1-on-1" so it's clear who you're talking to.
        private static string TitleText(BasisTalkMode mode)
        {
            if (mode == BasisTalkMode.ThisPerson
                && BasisTalkModeManager.TryGetThisPersonTarget(out ushort targetId)
                && BasisNetworkPlayers.Players.TryGetValue(targetId, out var np)
                && np != null)
            {
                string name = np.SafeDisplayName;
                if (!string.IsNullOrEmpty(name)) return Truncate(name, MaxNameLength);
            }
            return BasisLocalization.Get(TitleKey(mode));
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            if (max <= 3) return value.Substring(0, max);
            return value.Substring(0, max - 3).TrimEnd() + "...";
        }
    }
}
#endif
