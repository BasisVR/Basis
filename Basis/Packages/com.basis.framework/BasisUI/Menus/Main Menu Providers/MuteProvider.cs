#if !BASIS_DISABLE_MICROPHONE
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.BasisUI
{
    public class MuteProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new MuteProvider());
        }

        public override string Title => BasisLocalization.Get(BasisLocalMicrophoneDriver.isPaused ? "menu.provider.unmute" : "menu.provider.mute");
        public override string IconAddress => BasisLocalMicrophoneDriver.isPaused
            ? AddressableAssets.Sprites.MicrophoneMute
            : AddressableAssets.Sprites.Microphone;
        public override int Order => 0; // first — in front of Settings
        public override bool Hidden => false;

        public override void RunAction()
        {
            BasisLocalMicrophoneDriver.ToggleIsPaused();
        }

        public override void OnButtonCreated(PanelButton button)
        {
            // Unsubscribe first to prevent duplicate subscriptions across menu open/close cycles
            BasisLocalMicrophoneDriver.OnPausedAction -= OnMuteChanged;
            BasisLocalMicrophoneDriver.OnPausedAction += OnMuteChanged;
            BasisLocalMicrophoneIconDriver.OnColorChanged -= OnMicrophoneColorChanged;
            BasisLocalMicrophoneIconDriver.OnColorChanged += OnMicrophoneColorChanged;

            UpdateButtonVisuals(button, BasisLocalMicrophoneDriver.isPaused);

            // Half the normal hotbar button width — a compact mute button (like Exit).
            Vector2 size = button.rectTransform.sizeDelta;
            if (size.x > 0f) button.SetSize(new Vector2(size.x * 0.5f, size.y));
        }

        private void OnMuteChanged(bool isMuted)
        {
            if (BoundButton == null)
            {
                return;
            }

            UpdateButtonVisuals(BoundButton, isMuted);
        }

        private void OnMicrophoneColorChanged(Color color)
        {
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (BoundButton == null || BoundButton.IsReleased)
                {
                    return;
                }

                ApplyColor(BoundButton, BasisLocalMicrophoneDriver.isPaused);
            });
        }

        private static readonly Color MutedColor = new Color(1f, 0.3f, 0.3f, 1f);

        private void UpdateButtonVisuals(PanelButton button, bool isMuted)
        {
            string icon = isMuted
                ? AddressableAssets.Sprites.MicrophoneMute
                : AddressableAssets.Sprites.Microphone;

            button.SetIcon(icon);
            button.Descriptor.SetTitle(BasisLocalization.Get(isMuted ? "menu.provider.unmute" : "menu.provider.mute"));
            ApplyColor(button, isMuted);
        }

        private static void ApplyColor(PanelButton button, bool isMuted)
        {
            Color color = isMuted ? MutedColor : BasisLocalMicrophoneIconDriver.LastColor;
            button.Descriptor.IconImage.color = color;
            button.Descriptor.TitleLabel.color = color;
        }
    }
}
#endif
