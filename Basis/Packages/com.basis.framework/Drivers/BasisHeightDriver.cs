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
    public static void SetPlayersEyeHeight(BasisLocalPlayer LocalPlayer, BasisSelectedHeightMode SelectedHeightMode)
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
        LocalPlayer.OnPlayersHeightChanged?.Invoke();
    }
    public static void CapturePlayerHeight()
    {
        Basis.Scripts.TransformBinders.BasisLockToInput basisLockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
        if (basisLockToInput?.AttachedInput != null)
        {
            BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight = basisLockToInput.AttachedInput.LocalRawPosition.y;
            BasisDebug.Log($"Player's raw eye height recalculated: {BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
        }
        else
        {
            BasisDebug.LogWarning("No attached input found for BasisLockToInput. Using the avatars height.", BasisDebug.LogTag.Avatar);
            BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight = BasisLocalPlayer.Instance.CurrentHeight.AvatarEyeHeight; // Set a reasonable default
        }
        if (BasisDeviceManagement.Instance.FindDevice(out BasisInput LeftHand, BasisBoneTrackedRole.LeftHand) && BasisDeviceManagement.Instance.FindDevice(out BasisInput RightHand, BasisBoneTrackedRole.RightHand))
        {
            BasisLocalPlayer.Instance.CurrentHeight.PlayerArmSpan = Vector3.Distance(LeftHand.LocalRawPosition, RightHand.LocalRawPosition);
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
        // Set the custom height in the driver
        if (customHeight <= 0)
        {
            BasisDebug.LogError("Invalid custom height. Must be greater than zero.");
            return;
        }

        BasisDebug.Log($"Setting custom player eye height: {customHeight}", BasisDebug.LogTag.Avatar);

        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        BasisLocalAvatarDriver LocalAvatarDriver = player.LocalAvatarDriver;
        BasisLocalBoneDriver Driver = player.LocalBoneDriver;

        player.CurrentHeight.CustomAvatarEyeHeight = customHeight;
        player.CurrentHeight.CustomPlayerEyeHeight = customHeight;

        SaveHeight(customHeight);
        SetPlayersEyeHeight(player, BasisSelectedHeightMode.Custom);
        // Get the avatar's default scale
        Vector3 defaultScale = LocalAvatarDriver.AvatarDefaultScale;

        // Compute the scale multiplier based on the desired height vs actual height
        float heightScaleFactor = customHeight / player.CurrentHeight.AvatarEyeHeight;

        // Scale uniformly based on default scale
        Vector3 newScale = defaultScale * heightScaleFactor;

        // Apply the new scale to the avatar
        LocalAvatarDriver.SetAvatarheightOverride(newScale);
        Vector3 CurrentScaleValidated = LocalAvatarDriver.CurrentScaleValidated;
        int LengthCount = Driver.ControlsLength;
        for (int Index = 0; Index < LengthCount; Index++)
        {
            BasisBoneControl Control = Driver.Controls[Index];
            Control.TposeLocalScaled.position = CurrentScaleValidated * Control.TposeLocal.position;
            Control.TposeLocalScaled.rotation = Control.TposeLocal.rotation;
            Control.ScaledOffset = CurrentScaleValidated * Control.Offset;
        }
        LocalAvatarDriver.CalculateMaxExtended();
        player.OnPlayersHeightChanged?.Invoke();
    }
}
