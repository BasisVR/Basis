using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Rendering;
using Basis.Scripts.Settings;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Device_Management.EyeTracking
{
    /// <summary>
    /// Drives gaze-foveated VRS automatically from eye-tracking presence:
    /// - When an eye-tracking headset is first detected, asks once (per headset) to enable it.
    /// - Remembers the answer per headset and applies it silently thereafter.
    /// - Turns VR VRS off when the eye-tracking headset goes away (doffed / switched to desktop /
    ///   swapped to a non-eye-tracking headset).
    ///
    /// It only manages VR VRS in response to eye tracking it turned on — it never forces a user's
    /// manual VR/desktop VRS choice off on its own. Pumped from the central tick; no MonoBehaviour.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisGazeFoveationAutoDriver
    {
        private static bool _wasPresent;
        private static string _currentDeviceId;
        private static bool _promptOpen;

        public static void Simulate()
        {
            if (!BasisVariableRateShadingFeature.IsSupported)
            {
                return;
            }

            if (!BasisSettingsDefaults.EyeFoveationAutoManage.RawValue)
            {
                return;
            }

            bool present = BasisEyeTrackingManager.HmdEyeTrackingPresent;

            if (!present)
            {
                if (_wasPresent)
                {
                    SetVrVrs(false);
                    _wasPresent = false;
                    _currentDeviceId = null;
                }
                return;
            }

            if (_wasPresent)
            {
                return;
            }

            string deviceId = GetHmdDeviceId();
            if (string.IsNullOrEmpty(deviceId))
            {
                return;
            }

            _wasPresent = true;
            _currentDeviceId = deviceId;
            EvaluateForDevice(deviceId);
        }

        private static void EvaluateForDevice(string deviceId)
        {
            if (BasisSettingsSystem.LoadBool(DecidedKey(deviceId), false))
            {
                SetVrVrs(BasisSettingsSystem.LoadBool(EnabledKey(deviceId), false));
                return;
            }

            if (_promptOpen)
            {
                return;
            }
            _promptOpen = true;

            BasisGazeFoveationPrompt.Show(
                onDecided: enabled =>
                {
                    _promptOpen = false;
                    BasisSettingsSystem.SaveBool(DecidedKey(deviceId), true);
                    BasisSettingsSystem.SaveBool(EnabledKey(deviceId), enabled);
                    SetVrVrs(enabled && BasisEyeTrackingManager.HmdEyeTrackingPresent);
                },
                onDismissed: () =>
                {
                    // Left undecided — re-ask the next time the headset is detected.
                    _promptOpen = false;
                });
        }

        private static void SetVrVrs(bool on)
        {
            if (BasisSettingsDefaults.DevVariableRateShading.RawValue != on)
            {
                BasisSettingsDefaults.DevVariableRateShading.SetValue(on);
            }
        }

        private static string GetHmdDeviceId()
        {
            BasisDeviceManagement instance = BasisDeviceManagement.Instance;
            if (instance == null)
            {
                return null;
            }
            if (instance.FindDevice(out BasisInput head, BasisBoneTrackedRole.CenterEye))
            {
                string id = head.CommonDeviceIdentifier;
                if (string.IsNullOrEmpty(id))
                {
                    id = head.UniqueDeviceIdentifier;
                }
                return id;
            }
            return null;
        }

        private static string DecidedKey(string deviceId) => $"gazefov.decided.{deviceId}";
        private static string EnabledKey(string deviceId) => $"gazefov.enabled.{deviceId}";
    }
}
