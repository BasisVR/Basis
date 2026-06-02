using Basis.Scripts.BasisSdk.Players;
using HVR.Basis.Comms.OSC;
using UnityEngine;

namespace HVR.Basis.Comms
{
    public static class BasisOscAvatarScaling
    {
        private static bool _initialized;

        public const string EyeHeightAddress = "/avatar/eyeheight";
        public const string EyeHeightMinAddress = "/avatar/eyeheightmin";
        public const string EyeHeightMaxAddress = "/avatar/eyeheightmax";
        public const string EyeHeightScalingAllowedAddress = "/avatar/eyeheightscalingallowed";

        // OSC accepts extreme float inputs; OscMaximumEyeHeightMeters is a sanity ceiling, separate from supported/user-selectable limits.
        public const float OscMaximumEyeHeightMeters = 10000f;
        public const float SupportedMinimumEyeHeightMeters = 0.1f;
        public const float SupportedMaximumEyeHeightMeters = 100f;
        public const float UserSelectableMinimumEyeHeightMeters = 0.1f;
        public const float UserSelectableMaximumEyeHeightMeters = 5f;

        public static bool EyeHeightScalingAllowed => SMModuleCalibration.ApplyCustomScale;

        public static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            BasisOscService.MessageReceived -= OnOscMessageReceived;
            BasisOscService.MessageReceived += OnOscMessageReceived;
            BasisLocalPlayer.OnLocalAvatarChanged -= OnLocalAvatarChanged;
            BasisLocalPlayer.OnLocalAvatarChanged += OnLocalAvatarChanged;
            OnLocalAvatarChanged();
        }

        public static bool IsAvatarScalingAddress(string path)
        {
            return path == EyeHeightAddress ||
                   path == EyeHeightMinAddress ||
                   path == EyeHeightMaxAddress ||
                   path == EyeHeightScalingAllowedAddress;
        }

        public static bool TryHandleWrite(OscMessage message)
        {
            if (message == null || !IsAvatarScalingAddress(message.Path))
            {
                return false;
            }

            if (message.Path != EyeHeightAddress)
            {
                return true;
            }

            OscData[] arguments = message.Arguments;
            if (arguments == null || arguments.Length == 0 || arguments[0].Kind != OscDataKind.Float32)
            {
                return true;
            }

            ApplyEyeHeight(arguments[0].FloatValue);
            return true;
        }

        public static void ClearRuntimeOverride()
        {
            BasisHeightDriver.ClearRuntimeOscEyeHeightOverride();
        }

        public static void ApplyEyeHeight(float requestedEyeHeightMeters)
        {
            if (!EyeHeightScalingAllowed)
            {
                BasisDebug.LogWarning("Ignoring OSC avatar eye height because Custom Scale is disabled.", BasisDebug.LogTag.LocalNetwork);
                PublishCurrentState();
                return;
            }

            if (float.IsNaN(requestedEyeHeightMeters) || float.IsInfinity(requestedEyeHeightMeters) || requestedEyeHeightMeters <= 0f)
            {
                BasisDebug.LogWarning($"Ignoring invalid OSC avatar eye height {requestedEyeHeightMeters}.", BasisDebug.LogTag.LocalNetwork);
                PublishCurrentState();
                return;
            }
            BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
            if (localPlayer == null || localPlayer.LocalAvatarDriver == null || localPlayer.LocalBoneDriver == null)
            {
                BasisDebug.LogWarning("Ignoring OSC avatar eye height because the local avatar is not ready.", BasisDebug.LogTag.LocalNetwork);
                return;
            }

            float eyeHeightMeters = Mathf.Min(requestedEyeHeightMeters, OscMaximumEyeHeightMeters);
            if (!Mathf.Approximately(eyeHeightMeters, requestedEyeHeightMeters))
            {
                BasisDebug.LogWarning($"Clamped OSC avatar eye height from {requestedEyeHeightMeters}m to {eyeHeightMeters}m.", BasisDebug.LogTag.LocalNetwork);
            }

            if (eyeHeightMeters < SupportedMinimumEyeHeightMeters || eyeHeightMeters > SupportedMaximumEyeHeightMeters)
            {
                BasisDebug.LogWarning(
                    $"OSC avatar eye height {eyeHeightMeters}m is outside the supported {SupportedMinimumEyeHeightMeters}m..{SupportedMaximumEyeHeightMeters}m range.",
                    BasisDebug.LogTag.LocalNetwork);
            }

            BasisHeightDriver.ApplyRuntimeOscEyeHeightOverride(eyeHeightMeters);
            PublishCurrentState();
        }

        public static void PublishCurrentState()
        {
            BasisOscService.PublishValue(EyeHeightAddress, OscData.Float32(GetCurrentEyeHeightMeters()));
            BasisOscService.PublishValue(EyeHeightMinAddress, OscData.Float32(UserSelectableMinimumEyeHeightMeters));
            BasisOscService.PublishValue(EyeHeightMaxAddress, OscData.Float32(UserSelectableMaximumEyeHeightMeters));
            BasisOscService.PublishValue(EyeHeightScalingAllowedAddress, OscData.Boolean(EyeHeightScalingAllowed));
        }

        private static float GetCurrentEyeHeightMeters()
        {
            float selected = BasisHeightDriver.SelectedScaledAvatarHeight;
            if (!float.IsNaN(selected) && !float.IsInfinity(selected) && selected > 0f)
            {
                return selected;
            }

            float fallback = BasisHeightDriver.AvatarEyeHeight * BasisHeightDriver.AppliedUpScale;
            if (!float.IsNaN(fallback) && !float.IsInfinity(fallback) && fallback > 0f)
            {
                return fallback;
            }

            return BasisHeightDriver.FallbackHeightInMeters;
        }

        private static void OnLocalAvatarChanged()
        {
            ClearRuntimeOverride();
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnPlayerHeightChanged;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnPlayerHeightChanged;
            PublishCurrentState();
        }

        private static void OnPlayerHeightChanged(BasisHeightDriver.HeightModeChange mode)
        {
            PublishCurrentState();
        }

        private static void OnOscMessageReceived(OscMessage message)
        {
            TryHandleWrite(message);
        }
    }
}
