using System;
using Basis.BasisUI;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.Rendering
{
    public static class BasisDX12Notice
    {
        public static float QuietSeconds = 2f, AutoConnectWaitSeconds = 10f;

        private static bool _armed, _watching, _verdict, _answered;
        private static float _armedAt, _quiet;

        public static bool IsRunningOnDX12 => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;

        public static bool CanSwitchToDX11 =>
            BasisGraphicsApiSelection.IsSupported && BasisGraphicsApiSelection.TryParse(GraphicsDeviceType.Direct3D11.ToString(), out _);

        public static bool BootBusy
        {
            get
            {
                if (BasisUILoadingBar.HasDisplay || BasisConnectionService.ConnectInProgress) return true;
                if (BasisNetworkConnection.LocalPlayerIsConnected || BasisConnectionService.AutoConnectAttempted) return false;
                return Time.unscaledTime - _armedAt < AutoConnectWaitSeconds;
            }
        }

        public static void ShowOnce()
        {
            if (_armed || !IsRunningOnDX12 || !BasisSettingsDefaults.Dx12Warning.RawValue) return;
            _armed = true;
            _armedAt = Time.unscaledTime;
            BasisDebug.LogWarning("Rendering with DirectX 12. Unity's DirectX 12 backend is unstable; crashes on it are Unity engine bugs. Switch to DirectX 11 if they happen.", BasisDebug.LogTag.System);
            Watch();
        }

        public static void SwitchToDX11()
        {
            BasisSettingsDefaults.GraphicsApi.SetValue(GraphicsDeviceType.Direct3D11.ToString());
            BasisAppRelaunch.RebootAndReconnect();
        }

        public static void Show(bool force = false)
        {
            if (!force && BasisNotificationCenter.RouteToNotifications)
            {
                Park();
                return;
            }

            try
            {
                if (!BasisMainMenu.Instance) BasisMainMenu.Open();

                BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
                if (menu)
                {
                    if (menu.Dialogue)
                    {
                        menu.Dialogue.OnInstanceReleased += Watch;
                        return;
                    }

                    OpenDialogue(menu);
                    if (menu.Dialogue) return;
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"DirectX 12 notice could not open a dialogue: {e}", BasisDebug.LogTag.System);
            }

            Park();
        }

        private static void OpenDialogue(BasisMenuBase<BasisMainMenu> menu)
        {
            string title = BasisLocalization.Get("settings.graphics.renderer.dx12Warning.title");
            string body = BasisLocalization.Get("settings.graphics.renderer.dx12Warning.body");
            _answered = false;

            if (CanSwitchToDX11)
            {
                menu.OpenDialogue(
                    title,
                    body,
                    BasisLocalization.Get("settings.graphics.renderer.dx12Warning.switch"),
                    BasisLocalization.Get("settings.graphics.renderer.dx12Warning.keep"),
                    accepted => { _answered = true; if (accepted) SwitchToDX11(); },
                    false,
                    BasisPanelSeverity.Caution);
            }
            else
            {
                menu.OpenDialogue(
                    title,
                    body,
                    BasisLocalization.Get("ui.ok"),
                    _ => _answered = true,
                    false,
                    BasisPanelSeverity.Caution);
            }

            BasisMenuDialoguePanel dialogue = menu.Dialogue;
            if (!dialogue) return;

            dialogue.CaptureOnClose = false;
            dialogue.EnableAlternate(
                BasisLocalization.Get("settings.graphics.renderer.dx12Warning.dontShowAgain"),
                () => { _answered = true; BasisSettingsDefaults.Dx12Warning.SetValue(false); });
            dialogue.OnInstanceReleased += OnDialogueReleased;
        }

        private static void OnDialogueReleased()
        {
            if (_answered) return;
            _verdict = true;
            Watch();
        }

        private static void Park()
        {
            BasisNotificationCenter.AddPending(
                BasisLocalization.Get("settings.graphics.renderer.dx12Warning.title"),
                BasisLocalization.Get("settings.graphics.renderer.dx12Warning.body"),
                AddressableAssets.Sprites.Information,
                () => Show(true),
                () => { });
        }

        private static void Watch()
        {
            _quiet = 0f;
            if (_watching) return;
            _watching = true;
            BasisFrameClock.OnTick += Tick;
            BasisFrameClock.AddRequest();
        }

        private static void Unwatch()
        {
            if (!_watching) return;
            _watching = false;
            BasisFrameClock.OnTick -= Tick;
            BasisFrameClock.RemoveRequest();
        }

        private static void Tick()
        {
            bool busy = BootBusy;

            if (_verdict)
            {
                _verdict = false;
                if (!busy)
                {
                    Unwatch();
                    Park();
                    return;
                }
            }

            if (busy)
            {
                _quiet = 0f;
                return;
            }

            _quiet += Time.unscaledDeltaTime;
            if (_quiet < QuietSeconds) return;

            Unwatch();
            Show();
        }
    }
}
