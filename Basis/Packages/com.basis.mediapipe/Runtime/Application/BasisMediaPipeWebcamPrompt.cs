using System;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Settings;
using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>
    /// Asks once per webcam, in Desktop mode, whether to turn on webcam tracking, the same way an eye-tracking
    /// headset asks about gaze foveation. At boot it is a startup prompt, so the server list opens after the
    /// answer; switching to Desktop later asks from the tick. Enable and Don't Remind Me Again are remembered
    /// per camera; No and closing the prompt ask again the next time Desktop mode starts.
    /// </summary>
    public partial class BasisMediaPipeManagement
    {
        private static bool _webcamPromptPending;
        private static bool _webcamPromptOpen;
        private static bool _webcamAskedThisDesktopStint;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterWebcamStartupPrompt() => BasisStartupPrompts.Register(ShowWebcamStartupPrompt);

        private static bool ShowWebcamStartupPrompt(Action onClosed)
        {
            _webcamPromptPending = false;
            return TryShowWebcamPrompt(onClosed);
        }

        private static void QueueWebcamPrompt() => _webcamPromptPending = true;

        private static void EndWebcamPromptDesktopStint()
        {
            _webcamPromptPending = false;
            _webcamAskedThisDesktopStint = false;
        }

        private void TickWebcamPrompt()
        {
            if (!BasisStartupPrompts.Finished || _webcamPromptOpen || BasisDeviceManagement.StaticCurrentMode != BasisConstants.Desktop)
            {
                return;
            }
            if (BasisLocalPlayer.Instance == null || BasisLocalPlayer.Instance.BasisAvatar == null)
            {
                return;
            }
            _webcamPromptPending = false;
            TryShowWebcamPrompt(null);
        }

        private static bool TryShowWebcamPrompt(Action onClosed)
        {
            if (_webcamPromptOpen || _webcamAskedThisDesktopStint || BasisSafeBoot.SafeMode || BasisDeviceManagement.StaticCurrentMode != BasisConstants.Desktop)
            {
                return false;
            }
            if (BasisMediaPipeSettings.Enable.RawValue || !BasisMediaPipeSettings.AutoPrompt.RawValue || !BasisMediaPipeBackendRegistry.HasBackend)
            {
                return false;
            }

            WebCamDevice[] devices = BasisMediaPipeCamera.EnumerateDevices();
            if (devices == null || devices.Length == 0)
            {
                return false;
            }
            string cameraName = BasisMediaPipeCamera.ResolveDeviceName(devices, BasisMediaPipeSettings.Camera.RawValue);
            if (string.IsNullOrEmpty(cameraName) || BasisSettingsSystem.LoadBool(DecidedKey(cameraName), false))
            {
                return false;
            }

            _webcamPromptOpen = true;
            _webcamAskedThisDesktopStint = true;
            string body = BasisLocalization.Get("settings.mediapipe.prompt.detected", cameraName) + "\n\n" + BasisLocalization.Get("settings.mediapipe.prompt.body");
            BasisFeatureDetectedPrompt.Show(BasisLocalization.Get("settings.mediapipe.prompt.title"), body, "BasisMediaPipeWebcamPrompt",
                onDecided: choice =>
                {
                    _webcamPromptOpen = false;
                    if (choice == BasisFeatureDetectedPrompt.Choice.No)
                    {
                        return;
                    }
                    BasisSettingsSystem.SaveBool(DecidedKey(cameraName), true);
                    if (choice == BasisFeatureDetectedPrompt.Choice.Enable)
                    {
                        BasisMediaPipeSettings.Enable.SetValue(true);
                        if (Instance != null && BasisDeviceManagement.StaticCurrentMode == BasisConstants.Desktop)
                        {
                            Instance.SetEnabled(true);
                        }
                    }
                },
                onDismissed: () => _webcamPromptOpen = false,
                onClosed: onClosed);
            return true;
        }

        private static string DecidedKey(string cameraName) => $"mediapipe.prompt.decided.{cameraName}";
    }
}
