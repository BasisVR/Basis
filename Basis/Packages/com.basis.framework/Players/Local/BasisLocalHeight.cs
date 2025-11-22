using System.Collections.Generic;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Players
{
    /// <summary>
    /// Specifies the measurement method used for defining player height.
    /// </summary>
    public enum BasisHeightMeasurement
    {
        /// <summary>
        /// Height is specified as a scale multiplier relative to the avatar's default height.
        /// </summary>
        ScaleMultiplier,
        /// <summary>
        /// Height is specified as the arm span in meters.
        /// </summary>
        ArmSpanMeters,
        /// <summary>
        /// Height is specified as the eye height in meters.
        /// </summary>
        EyeHeightMeters,
    }

    /// <summary>
    /// Utility for measuring and applying player/avatar height data locally.
    /// </summary>
    public class BasisLocalHeight
    {
        /// <summary>
        /// Fired on the frame after the player height is changed.
        /// </summary>
        public static System.Action OnChangedNextFrame;
        /// <summary>
        /// The per-avatar scale factor for the player's height relative to that avatar's default size.
        /// </summary>
        public static Dictionary<string, float> AvatarScales = new Dictionary<string, float>();
        private static readonly string AvatarScalesFileName = "avatar_scales.json";

        /// <summary>
        /// Fallback height (meters) used when no measurement is available.
        /// not the total height but the eye height
        /// </summary>
        public const float FallbackEyeHeightMeters = 1.61f;
        public const float FallbackArmSpanMeters = 1.61f;

        /// <summary>
        /// The avatar's arm span in meters as measured from the avatar 3D model.
        /// </summary>
        public float AvatarArmSpanMeters { get; private set; } = FallbackArmSpanMeters;
        /// <summary>
        /// The avatar's eye height in meters as measured from the avatar 3D model.
        /// </summary>
        public float AvatarEyeHeightMeters { get; private set; } = FallbackEyeHeightMeters;
        /// <summary>
        /// The avatar's scale factor relative to the fallback size as measured from the avatar 3D model.
        /// </summary>
        public float AvatarScaleVsFallback { get; private set; } = 1.0f;
        /// <summary>
        /// The player's in-game arm span in meters as set in the settings menu.
        /// </summary>
        public float PlayerArmSpanMeters { get; private set; } = FallbackArmSpanMeters;
        /// <summary>
        /// The player's in-game eye height in meters as set in the settings menu.
        /// </summary>
        public float PlayerEyeHeightMeters { get; private set; } = FallbackEyeHeightMeters;
        /// <summary>
        /// The player's in-game scale factor relative to the avatar's size.
        /// </summary>
        public float PlayerScaleVsAvatar { get; private set; } = 1.0f;
        /// <summary>
        /// The player's in-game scale factor relative to the fallback size.
        /// </summary>
        public float PlayerScaleVsFallback { get; private set; } = 1.0f;
        /// <summary>
        /// The real life user's physical arm span in meters as measured during calibration of VR devices.
        /// </summary>
        public float RealUserArmSpanMeters { get; private set; } = FallbackArmSpanMeters;
        /// <summary>
        /// The real life user's physical eye height in meters as measured during calibration of VR devices.
        /// </summary>
        public float RealUserEyeHeightMeters { get; private set; } = FallbackEyeHeightMeters;

        public static void LoadSavedScales()
        {
            string path = Application.persistentDataPath + "/" + AvatarScalesFileName;
            AvatarScales = BasisDataStore.LoadJson<Dictionary<string, float>>(path, AvatarScales);
        }

        public void SetupForAvatar()
        {
            if (BasisLocalPlayer.Instance.BasisAvatar != null)
            {
                AvatarEyeHeightMeters = BasisLocalPlayer.Instance.BasisAvatar.AvatarEyePosition.x;
                if (AvatarEyeHeightMeters < 1e-6f)
                {
                    AvatarEyeHeightMeters = FallbackEyeHeightMeters;
                }
            }
            else
            {
                AvatarEyeHeightMeters = FallbackEyeHeightMeters;
            }
            var boneDriver = BasisLocalPlayer.Instance.LocalBoneDriver;
            if (boneDriver != null && boneDriver.FindBone(out var leftHandBone, TransformBinders.BoneControl.BasisBoneTrackedRole.LeftHand) && boneDriver.FindBone(out var rightHandBone, TransformBinders.BoneControl.BasisBoneTrackedRole.RightHand))
            {
                AvatarArmSpanMeters = Vector3.Distance(leftHandBone.TposeLocalScaled.position, rightHandBone.TposeLocalScaled.position);
            }
            else
            {
                AvatarArmSpanMeters = FallbackArmSpanMeters;
            }
            string currentAvatarId = BasisLocalPlayer.Instance.AvatarMetaData.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
            if (AvatarScales.TryGetValue(currentAvatarId, out float savedScale))
            {
                PlayerScaleVsAvatar = savedScale;
            }
            else
            {
                PlayerScaleVsAvatar = 1.0f;
            }
            PlayerArmSpanMeters = AvatarArmSpanMeters * PlayerScaleVsAvatar;
            PlayerEyeHeightMeters = AvatarEyeHeightMeters * PlayerScaleVsAvatar;
            AvatarScaleVsFallback = AvatarEyeHeightMeters / FallbackEyeHeightMeters;
            PlayerScaleVsFallback = PlayerEyeHeightMeters / FallbackEyeHeightMeters;
            _playerScaleChanged();
        }

        public void SetPlayerSize(BasisHeightMeasurement measurement, float sizeValue)
        {
            switch (measurement)
            {
                case BasisHeightMeasurement.ScaleMultiplier:
                    PlayerScaleVsAvatar = sizeValue;
                    break;
                case BasisHeightMeasurement.ArmSpanMeters:
                    PlayerScaleVsAvatar = sizeValue / AvatarArmSpanMeters;
                    break;
                case BasisHeightMeasurement.EyeHeightMeters:
                    PlayerScaleVsAvatar = sizeValue / AvatarEyeHeightMeters;
                    break;
            }
            PlayerEyeHeightMeters = AvatarEyeHeightMeters * PlayerScaleVsAvatar;
            if (PlayerEyeHeightMeters < 1e-4f)
            {
                BasisDebug.LogError("Player eye height was set too small, resetting to the fallback height.", BasisDebug.LogTag.Avatar);
                PlayerEyeHeightMeters = FallbackEyeHeightMeters;
                PlayerScaleVsAvatar = PlayerEyeHeightMeters / AvatarEyeHeightMeters;
            }
            PlayerArmSpanMeters = AvatarArmSpanMeters * PlayerScaleVsAvatar;
            PlayerScaleVsFallback = PlayerEyeHeightMeters / FallbackEyeHeightMeters;
            _playerScaleChanged();
        }

        private void _playerScaleChanged()
        {
            BasisLocalPlayer.Instance.LocalAvatarDriver.ScaleAvatarModification.SetAvatarScale(PlayerScaleVsAvatar);
            // Save the per-avatar scale.
            string currentAvatarId = BasisLocalPlayer.Instance.AvatarMetaData.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
            if (Mathf.Approximately(PlayerScaleVsAvatar, 1.0f))
            {
                AvatarScales.Remove(currentAvatarId);
            }
            else
            {
                AvatarScales[currentAvatarId] = PlayerScaleVsAvatar;
            }
            string path = Application.persistentDataPath + "/" + AvatarScalesFileName;
            BasisDataStore.SaveJson(path, AvatarScales);
            // Rescale bone-space T-pose transforms.
            int count = BasisLocalPlayer.Instance.LocalBoneDriver.ControlsLength;
            for (int i = 0; i < count; i++)
            {
                BasisLocalBoneControl c = BasisLocalPlayer.Instance.LocalBoneDriver.Controls[i];
                c.TposeLocalScaled.position = c.TposeLocal.position * PlayerScaleVsAvatar;
                c.TposeLocalScaled.rotation = c.TposeLocal.rotation;
                c.ScaledOffset = c.Offset * PlayerScaleVsAvatar;
            }
            BasisLocalPlayer.Instance.ExecuteNextFrame(() =>
            {
                OnChangedNextFrame?.Invoke();
            });
        }

        /// <summary>
        /// Captures live player eye height and arm span from connected input devices, using avatar/default fallbacks when necessary.
        /// </summary>
        /// <remarks>
        /// Eye height is read from <see cref="BasisLocalCameraDriver.Instance"/> lock-to-input if available, otherwise falls back to avatar eye height or default.
        /// Arm span uses left/right hand devices; if either hand is missing, the default arm span is used.
        /// </remarks>
        public void CaptureRealUserSizes()
        {
            if (SMModuleSitStand.IsSteatedMode)
            {
                BasisDebug.Log("Was Seated Mode taking standard size of 1.7m", BasisDebug.LogTag.Avatar);
                RealUserEyeHeightMeters = FallbackEyeHeightMeters;
            }
            else
            {
                var lockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
                if (lockToInput?.BasisInput != null)
                {
                    lockToInput.BasisInput.PollData();
                    RealUserEyeHeightMeters = lockToInput.BasisInput.UnscaledDeviceCoord.position.y;
                    BasisDebug.Log($"Player raw eye height from device: {RealUserEyeHeightMeters}", BasisDebug.LogTag.Avatar);
                }
                else
                {
                    RealUserEyeHeightMeters = FallbackEyeHeightMeters;
                    BasisDebug.LogWarning("No attached input found for BasisLockToInput. Using fallback player eye height.", BasisDebug.LogTag.Avatar);
                }
            }
            // Player arm span (from *devices*) this is wrong. we need to use hand to upper arm length.
            if (BasisDeviceManagement.Instance.FindDevice(out BasisInput leftHand, BasisBoneTrackedRole.LeftHand) && BasisDeviceManagement.Instance.FindDevice(out BasisInput rightHand, BasisBoneTrackedRole.RightHand))
            {
                leftHand.PollData();
                rightHand.PollData();
                RealUserArmSpanMeters = Vector3.Distance(leftHand.UnscaledDeviceCoord.position, rightHand.UnscaledDeviceCoord.position);
                BasisDebug.Log($"Current Player Arm Span: {RealUserArmSpanMeters}", BasisDebug.LogTag.Avatar);
            }
            else
            {
                BasisDebug.LogWarning("Both hands were not discovered. Using default player arm span.", BasisDebug.LogTag.Avatar);
                RealUserArmSpanMeters = FallbackArmSpanMeters;
            }
        }
    }
}
