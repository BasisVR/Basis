#if !BASIS_DISABLE_MICROPHONE
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Transmitters;
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

        public override string Title => BasisLocalMicrophoneDriver.isPaused ? "Unmute" : "Mute";
        public override string IconAddress => BasisLocalMicrophoneDriver.isPaused
            ? AddressableAssets.Sprites.MicrophoneMute
            : AddressableAssets.Sprites.Microphone;
        public override int Order => 1;
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
            BasisNetworkModeration.OnShoutModeChanged -= OnShoutModeChanged;
            BasisNetworkModeration.OnShoutModeChanged += OnShoutModeChanged;

            UpdateButtonVisuals(button, BasisLocalMicrophoneDriver.isPaused);
        }

        private void OnMuteChanged(bool isMuted)
        {
            if (BoundButton == null)
            {
                return;
            }

            UpdateButtonVisuals(BoundButton, isMuted);
        }

        private void OnShoutModeChanged(ushort playerId, bool enabled)
        {
            if (BoundButton == null)
                return;
            if (BasisNetworkPlayer.LocalPlayer == null || playerId != BasisNetworkPlayer.LocalPlayer.playerId)
                return;

            UpdateButtonVisuals(BoundButton, BasisLocalMicrophoneDriver.isPaused);
        }

        private static readonly Color MutedColor = new Color(1f, 0.3f, 0.3f, 1f);
        private static readonly Color ShoutColor = Color.yellow;

        private void UpdateButtonVisuals(PanelButton button, bool isMuted)
        {
            string icon = isMuted
                ? AddressableAssets.Sprites.MicrophoneMute
                : AddressableAssets.Sprites.Microphone;

            button.SetIcon(icon);
            button.Descriptor.SetTitle(isMuted ? "Unmute" : "Mute");
            Color color = isMuted ? MutedColor : BasisAudioTransmission.IsInShoutMode ? ShoutColor : Color.white;
            button.Descriptor.IconImage.color = color;
            button.Descriptor.TitleLabel.color = color;
        }
    }
}
#endif
