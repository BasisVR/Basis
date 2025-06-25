using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
public static class BasisHeightDriver
{
    public static string FileNameAndExtension = "SavedHeight.BAS";
    /// <summary>
    /// Adjusts the player's eye height after allowing all devices and systems to reset to their native size. 
    /// This method waits for 4 frames (including asynchronous frames) to ensure the final positions are updated.
    /// </summary>
    public static void ChangeEyeHeightMode(BasisLocalPlayer LocalPlayer, BasisSelectedHeightMode SelectedHeightMode)
    {
        if (LocalPlayer == null)
        {
            BasisDebug.LogError("BasisPlayer is null. Cannot set player's eye height.");
            return;
        }
        LocalPlayer.CurrentHeight.CopyTo(LocalPlayer.LastHeight);
        LocalPlayer.CurrentHeight.AvatarName = LocalPlayer.BasisAvatar.name;
        // Retrieve the player's eye height from the input device
        CapturePlayerHeight();
        // Retrieve the active avatar's eye height
        LocalPlayer.CurrentHeight.AvatarEyeHeight = LocalPlayer.LocalAvatarDriver?.ActiveAvatarEyeHeight() ?? BasisLocalPlayer.FallbackSize;
        BasisDebug.Log($"Avatar height: {LocalPlayer.CurrentHeight.SelectedAvatarHeight}, Player eye height: {LocalPlayer.CurrentHeight.SelectedPlayerHeight}", BasisDebug.LogTag.Avatar);

        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisBoneControl LeftHand, BasisBoneTrackedRole.LeftHand) && BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisBoneControl RightHand, BasisBoneTrackedRole.RightHand))
        {
            BasisLocalPlayer.Instance.CurrentHeight.AvatarArmSpan = Vector3.Distance(LeftHand.TposeLocalScaled.position, RightHand.TposeLocalScaled.position);
            BasisDebug.Log("Current Avatar Arm Span is " + BasisLocalPlayer.Instance.CurrentHeight.AvatarArmSpan);
        }

        // Handle potential issues with height data
        if (LocalPlayer.CurrentHeight.PlayerEyeHeight <= 0 || LocalPlayer.CurrentHeight.AvatarEyeHeight <= 0)
        {
            if (LocalPlayer.CurrentHeight.PlayerEyeHeight <= 0)
            {
                LocalPlayer.CurrentHeight.PlayerEyeHeight = BasisLocalPlayer.DefaultPlayerEyeHeight; // Set a default eye height if invalid
                Debug.LogWarning($"Player eye height was invalid. Set to default: {BasisLocalPlayer.FallbackSize}");
            }

            BasisDebug.LogError("Invalid height data. Scaling ratios set to defaults.");
        }
        // Calculate other scaling ratios
        LocalPlayer.CurrentHeight.EyeRatioAvatarToAvatarDefaultScale = LocalPlayer.CurrentHeight.AvatarEyeHeight / BasisLocalPlayer.DefaultAvatarEyeHeight;
        LocalPlayer.CurrentHeight.EyeRatioPlayerToDefaultScale = LocalPlayer.CurrentHeight.PlayerEyeHeight / BasisLocalPlayer.DefaultPlayerEyeHeight;

        LocalPlayer.CurrentHeight.ArmRatioAvatarToAvatarDefaultScale = LocalPlayer.CurrentHeight.PlayerArmSpan / BasisLocalPlayer.DefaultAvatarArmSpan;
        LocalPlayer.CurrentHeight.ArmRatioPlayerToDefaultScale = LocalPlayer.CurrentHeight.AvatarArmSpan / BasisLocalPlayer.DefaultPlayerArmSpan;


        // Notify listeners that height recalculation is complete
        BasisDebug.Log($"Final Player Eye Height: {LocalPlayer.CurrentHeight.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
        LocalPlayer.CurrentHeight.PickRatio(SelectedHeightMode);
        BasisLocalPlayer.Instance.ExecuteNextFrame((BasisLocalPlayer.NextFrameAction)(() =>
        {
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame?.Invoke();
        }));
    }
    public static void CapturePlayerHeight()
    {
        Basis.Scripts.TransformBinders.BasisLockToInput basisLockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
        if (basisLockToInput?.BasisInput != null)
        {
            BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight = basisLockToInput.BasisInput.UnscaledDeviceCoord.position.y;
            BasisDebug.Log($"Player's raw eye height recalculated: {BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
        }
        else
        {
            BasisDebug.LogWarning("No attached input found for BasisLockToInput. Using the avatars height.", BasisDebug.LogTag.Avatar);
            BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight = BasisLocalPlayer.Instance.CurrentHeight.AvatarEyeHeight; // Set a reasonable default
        }
        if (BasisDeviceManagement.Instance.FindDevice(out BasisInput LeftHand, BasisBoneTrackedRole.LeftHand) && BasisDeviceManagement.Instance.FindDevice(out BasisInput RightHand, BasisBoneTrackedRole.RightHand))
        {
            BasisLocalPlayer.Instance.CurrentHeight.PlayerArmSpan = Vector3.Distance(LeftHand.UnscaledDeviceCoord.position, RightHand.UnscaledDeviceCoord.position);
            BasisDebug.Log("Current Arm Span is " + BasisLocalPlayer.Instance.CurrentHeight.PlayerArmSpan);
        }
        else
        {
            BasisDebug.Log("Both hands where not discovered.");
        }
    }

    public static float GetDefaultOrLoadPlayerHeight()
    {
        float DefaultHeight = BasisLocalPlayer.DefaultPlayerEyeHeight;
        if (BasisDataStore.LoadFloat(FileNameAndExtension, DefaultHeight, out float FoundHeight))
        {
            return FoundHeight;
        }
        else
        {
            SaveHeight(FoundHeight);
            return FoundHeight;
        }
    }
    public static void SaveHeight()
    {
        float DefaultHeight = BasisLocalPlayer.DefaultPlayerEyeHeight;
        SaveHeight(DefaultHeight);
    }
    public static void SaveHeight(float EyeHeight)
    {
        BasisDataStore.SaveFloat(EyeHeight, FileNameAndExtension);
    }
    /// <summary>
    /// Manually set and save a custom player height.
    /// </summary>
    /// <param name="customHeight">The custom eye height to set for the player.</param>
    public static void SetCustomPlayerHeight(float customHeight)
    {
        // Validate input
        if (customHeight <= 0f)
        {
            BasisDebug.LogError("Invalid custom height. Must be greater than zero.");
            return;
        }

        BasisDebug.Log($"Setting custom player eye height: {customHeight}", BasisDebug.LogTag.Avatar);

        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        BasisLocalAvatarDriver localAvatarDriver = player.LocalAvatarDriver;
        BasisLocalBoneDriver driver = player.LocalBoneDriver;

        // Update height values
        player.CurrentHeight.CustomAvatarEyeHeight = customHeight;
        player.CurrentHeight.CustomPlayerEyeHeight = customHeight;

        SaveHeight(customHeight);
        ChangeEyeHeightMode(player, BasisSelectedHeightMode.Custom);

        // Get avatar’s calibration-time scale and eye height
        Vector3 defaultScale = localAvatarDriver.ScaleAvatarModification.DuringCalibrationScale;

        float defaultEyeHeight = player.CurrentHeight.AvatarEyeHeight;

        if (defaultEyeHeight <= 0f)
        {
            BasisDebug.LogError("Invalid calibration eye height. Cannot compute scale.");
            return;
        }

        // Compute the scale factor relative to unscaled height
        float heightScaleFactor = customHeight / defaultEyeHeight;

        // Apply avatar scale
        localAvatarDriver.ScaleAvatarModification.SetAvatarheightOverride(heightScaleFactor);

        // Recalculate bone transforms
        int lengthCount = driver.ControlsLength;
        for (int i = 0; i < lengthCount; i++)
        {
            BasisBoneControl control = driver.Controls[i];
            control.TposeLocalScaled.position = heightScaleFactor * control.TposeLocal.position;
            control.TposeLocalScaled.rotation = control.TposeLocal.rotation;
            control.ScaledOffset = heightScaleFactor * control.Offset;
        }

        localAvatarDriver.CalculateMaxExtended();
        BasisLocalPlayer.Instance.ExecuteNextFrame((BasisLocalPlayer.NextFrameAction)(() =>
        {
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame?.Invoke();
        }));
    }
}
