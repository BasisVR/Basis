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
    /// Waits one frame (via ExecuteNextFrame) before notifying listeners.
    /// </summary>
    public static void ChangeEyeHeightMode(BasisLocalPlayer localPlayer, BasisSelectedHeightMode selectedHeightMode)
    {
        if (localPlayer == null)
        {
            BasisDebug.LogError("BasisPlayer is null. Cannot set player's eye height.");
            return;
        }

        // basic avatar info
        if (localPlayer.BasisAvatar != null)
        {
            localPlayer.CurrentHeight.AvatarName = localPlayer.BasisAvatar.name;
        }

        // ---- establish authoritative avatar metrics first (so fallbacks work) ----
        var avatarDriver = localPlayer.LocalAvatarDriver;
        if (avatarDriver != null)
        {
            float AvatarEyeHeight = avatarDriver.ActiveAvatarEyeHeight();
            //this is wrong
            localPlayer.CurrentHeight.AvatarEyeHeight = AvatarEyeHeight > 0f ? AvatarEyeHeight : BasisLocalPlayer.DefaultAvatarEyeHeight;
        }
        else
        {
            BasisDebug.LogWarning("LocalAvatarDriver not available. Using default avatar eye height.", BasisDebug.LogTag.Avatar);
            localPlayer.CurrentHeight.AvatarEyeHeight = BasisLocalPlayer.DefaultAvatarEyeHeight;
        }

        // hands/arm span for the AVATAR (TPose, scaled)
        var boneDriver = localPlayer.LocalBoneDriver;
        if (boneDriver != null &&
            boneDriver.FindBone(out var leftHandBone, BasisBoneTrackedRole.LeftHand) &&
            boneDriver.FindBone(out var rightHandBone, BasisBoneTrackedRole.RightHand))
        {
            localPlayer.CurrentHeight.AvatarArmSpan =
                Vector3.Distance(leftHandBone.TposeLocalScaled.position, rightHandBone.TposeLocalScaled.position);
            BasisDebug.Log($"Current Avatar Arm Span: {localPlayer.CurrentHeight.AvatarArmSpan}", BasisDebug.LogTag.Avatar);
        }
        else
        {
            BasisDebug.LogWarning("Could not resolve avatar hand bones; using default avatar arm span.", BasisDebug.LogTag.Avatar);
            localPlayer.CurrentHeight.AvatarArmSpan = BasisLocalPlayer.DefaultAvatarArmSpan;
        }

        // ---- now capture player/device metrics; uses avatar defaults if devices are missing ----
        CapturePlayerHeight(localPlayer);

        // ---- validate & normalize heights ----
        if (localPlayer.CurrentHeight.PlayerEyeHeight <= 0f)
        {
            localPlayer.CurrentHeight.PlayerEyeHeight = BasisLocalPlayer.DefaultPlayerEyeHeight;
            BasisDebug.LogWarning(
                $"Player eye height was invalid. Set to default: {BasisLocalPlayer.DefaultPlayerEyeHeight}",
                BasisDebug.LogTag.Avatar);
        }

        if (localPlayer.CurrentHeight.AvatarEyeHeight <= 0f)
        {
            localPlayer.CurrentHeight.AvatarEyeHeight = BasisLocalPlayer.DefaultAvatarEyeHeight;
            BasisDebug.LogWarning(
                $"Avatar eye height was invalid. Set to default: {BasisLocalPlayer.DefaultAvatarEyeHeight}",
                BasisDebug.LogTag.Avatar);
        }

        // ---- compute ratios safely ----
        localPlayer.CurrentHeight.EyeRatioAvatarToAvatarDefaultScale =
            localPlayer.CurrentHeight.AvatarEyeHeight / Mathf.Max(0.0001f, BasisLocalPlayer.DefaultAvatarEyeHeight);

        localPlayer.CurrentHeight.EyeRatioPlayerToDefaultScale =
            localPlayer.CurrentHeight.PlayerEyeHeight / Mathf.Max(0.0001f, BasisLocalPlayer.DefaultPlayerEyeHeight);

        localPlayer.CurrentHeight.ArmRatioAvatarToAvatarDefaultScale =
            localPlayer.CurrentHeight.AvatarArmSpan / Mathf.Max(0.0001f, BasisLocalPlayer.DefaultAvatarArmSpan);

        localPlayer.CurrentHeight.ArmRatioPlayerToDefaultScale =
            localPlayer.CurrentHeight.PlayerArmSpan / Mathf.Max(0.0001f, BasisLocalPlayer.DefaultPlayerArmSpan);

        // choose which ratios to apply for the selected mode
        localPlayer.CurrentHeight.PickRatio(selectedHeightMode);

        BasisDebug.Log(
            $"Final Player Eye Height (raw): {localPlayer.CurrentHeight.PlayerEyeHeight}, " +
            $"Avatar Eye Height (raw): {localPlayer.CurrentHeight.AvatarEyeHeight}",
            BasisDebug.LogTag.Avatar);

        // notify next frame
        localPlayer.ExecuteNextFrame(() =>
        {
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame?.Invoke();
        });
    }

    /// <summary>
    /// Captures player eye height and arm span from live devices.
    /// Fallbacks to avatar/default metrics as needed.
    /// </summary>
    public static void CapturePlayerHeight(BasisLocalPlayer localPlayer)
    {
        var lockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
        if (lockToInput?.BasisInput != null)
        {
          lockToInput.BasisInput.PollData();
            localPlayer.CurrentHeight.PlayerEyeHeight = lockToInput.BasisInput.UnscaledDeviceCoord.position.y;
            BasisDebug.Log( $"Player raw eye height from device: {localPlayer.CurrentHeight.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
        }
        else
        {
            // Prefer avatar eye height if it looks valid; otherwise fall back to default player height.
            float fallback = localPlayer.CurrentHeight.AvatarEyeHeight > 0f
                ? localPlayer.CurrentHeight.AvatarEyeHeight
                : BasisLocalPlayer.DefaultPlayerEyeHeight;

            localPlayer.CurrentHeight.PlayerEyeHeight = fallback;

            BasisDebug.LogWarning(
                "No attached input found for BasisLockToInput. Using fallback player eye height.",
                BasisDebug.LogTag.Avatar);
        }

        // Player arm span (from *devices*) this is wrong. we need to use hand to upper arm length.
        if (BasisDeviceManagement.Instance.FindDevice(out BasisInput leftHand, BasisBoneTrackedRole.LeftHand) &&
            BasisDeviceManagement.Instance.FindDevice(out BasisInput rightHand, BasisBoneTrackedRole.RightHand))
        {

            leftHand.PollData();
            rightHand.PollData();

            localPlayer.CurrentHeight.PlayerArmSpan =
                Vector3.Distance(leftHand.UnscaledDeviceCoord.position, rightHand.UnscaledDeviceCoord.position);

            BasisDebug.Log($"Current Player Arm Span: {localPlayer.CurrentHeight.PlayerArmSpan}", BasisDebug.LogTag.Avatar);
        }
        else
        {
            BasisDebug.LogWarning("Both hands were not discovered. Using default player arm span.", BasisDebug.LogTag.Avatar);
            localPlayer.CurrentHeight.PlayerArmSpan = BasisLocalPlayer.DefaultPlayerArmSpan;
        }
    }

    /// <summary>
    /// Load saved player eye height; on miss, save and return default.
    /// </summary>
    public static float GetDefaultOrLoadPlayerHeight()
    {
        float defaultHeight = BasisLocalPlayer.DefaultPlayerEyeHeight;

        if (BasisDataStore.LoadFloat(FileNameAndExtension, defaultHeight, out float foundHeight))
        {
            return foundHeight;
        }

        // FIX: 'foundHeight' is undefined on load failure; persist and return the default instead
        SaveHeight(defaultHeight);
        return defaultHeight;
    }

    /// <summary>
    /// Saves the current player's eye height if available; otherwise saves the default.
    /// </summary>
    public static void SaveHeight()
    {
        float heightToSave =
            BasisLocalPlayer.Instance != null
                ? Mathf.Max(0f, BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight)
                : BasisLocalPlayer.DefaultPlayerEyeHeight;

        SaveHeight(heightToSave);
    }

    public static void SaveHeight(float eyeHeight)
    {
        BasisDataStore.SaveFloat(eyeHeight, FileNameAndExtension);
    }

    /// <summary>
    /// Manually set and save a custom player eye height, update avatar scale, and resync bones.
    /// </summary>
    public static void SetCustomPlayerHeight(float customHeight)
    {
        if (customHeight <= 0f)
        {
            BasisDebug.LogError("Invalid custom height. Must be greater than zero.");
            return;
        }

        var player = BasisLocalPlayer.Instance;
        if (player == null)
        {
            BasisDebug.LogError("No local player instance.");
            return;
        }

        var avatarDriver = player.LocalAvatarDriver;
        var boneDriver = player.LocalBoneDriver;

        if (avatarDriver == null || boneDriver == null)
        {
            BasisDebug.LogError("Avatar or Bone driver missing; cannot apply custom height.");
            return;
        }

        BasisDebug.Log($"Setting custom player eye height: {customHeight}", BasisDebug.LogTag.Avatar);

        // update height fields
        player.CurrentHeight.CustomAvatarEyeHeight = customHeight;
        player.CurrentHeight.CustomPlayerEyeHeight = customHeight;

        // persist and recompute overall mode/ratios
        SaveHeight(customHeight);
        ChangeEyeHeightMode(player, BasisSelectedHeightMode.Custom);

        // use the (now) known unscaled avatar eye height as baseline
        float baselineAvatarEyeHeight = player.CurrentHeight.AvatarEyeHeight;
        if (baselineAvatarEyeHeight <= 0f)
        {
            BasisDebug.LogError("Invalid baseline avatar eye height after recalculation. Cannot compute scale.");
            return;
        }

        // compute and apply scale
        float heightScaleFactor = customHeight / baselineAvatarEyeHeight;
        avatarDriver.ScaleAvatarModification.SetAvatarheightOverride(heightScaleFactor);

        // rescale bone-space TPose transforms
        int count = boneDriver.ControlsLength;
        for (int i = 0; i < count; i++)
        {
            BasisLocalBoneControl c = boneDriver.Controls[i];
            c.TposeLocalScaled.position = c.TposeLocal.position * heightScaleFactor;
            c.TposeLocalScaled.rotation = c.TposeLocal.rotation;
            c.ScaledOffset = c.Offset * heightScaleFactor;
        }

        player.ExecuteNextFrame(() =>
        {
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame?.Invoke();
        });
    }
}
