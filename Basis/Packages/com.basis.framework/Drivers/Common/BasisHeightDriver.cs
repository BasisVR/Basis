using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

/// <summary>
/// Utility for measuring, computing, persisting, and applying player/avatar height data.
/// </summary>
/// <remarks>
/// This driver derives eye height and arm span from devices when present, falls back to avatar/default metrics,
/// computes safe ratios for scaling, and persists a saved height. It also exposes helpers for custom-height overrides.
/// </remarks>
public static class BasisHeightDriver
{
    /// <summary>
    /// File name (with extension) used to persist the player's eye height via <see cref="BasisDataStore"/>.
    /// </summary>
    public static string FileNameAndExtension = "SavedHeight.BAS";

    /// <summary>
    /// Adjusts the player's eye height after allowing devices and systems to reset to native size, then notifies listeners next frame.
    /// </summary>
    /// <param name="localPlayer">The target <see cref="BasisLocalPlayer"/> whose height/ratios will be updated.</param>
    /// <param name="selectedHeightMode">The height computation mode that determines which ratios to apply.</param>
    /// <remarks>
    /// Establishes authoritative avatar metrics (eye height, arm span) first, then captures live player metrics.
    /// Ensures nonzero defaults, computes scale ratios safely, picks the active ratio set for <paramref name="selectedHeightMode"/>,
    /// and invokes <see cref="BasisLocalPlayer.OnPlayersHeightChangedNextFrame"/> via <see cref="BasisLocalPlayer.ExecuteNextFrame(System.Action)"/>.
    /// </remarks>
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
            localPlayer.CurrentHeight.AvatarEyeHeight = AvatarEyeHeight > 0f ? AvatarEyeHeight : BasisLocalHeight.DefaultAvatarEyeHeight;
        }
        else
        {
            BasisDebug.LogWarning("LocalAvatarDriver not available. Using default avatar eye height.", BasisDebug.LogTag.Avatar);
            localPlayer.CurrentHeight.AvatarEyeHeight = BasisLocalHeight.DefaultAvatarEyeHeight;
        }

        // hands/arm span for the AVATAR (TPose, scaled)
        var boneDriver = localPlayer.LocalBoneDriver;
        if (boneDriver != null && boneDriver.FindBone(out var leftHandBone, BasisBoneTrackedRole.LeftHand) && boneDriver.FindBone(out var rightHandBone, BasisBoneTrackedRole.RightHand))
        {
            localPlayer.CurrentHeight.AvatarArmSpan =
                Vector3.Distance(leftHandBone.TposeLocalScaled.position, rightHandBone.TposeLocalScaled.position);
            BasisDebug.Log($"Current Avatar Arm Span: {localPlayer.CurrentHeight.AvatarArmSpan}", BasisDebug.LogTag.Avatar);
        }
        else
        {
            BasisDebug.LogWarning("Could not resolve avatar hand bones; using default avatar arm span.", BasisDebug.LogTag.Avatar);
            localPlayer.CurrentHeight.AvatarArmSpan = BasisLocalHeight.DefaultAvatarArmSpan;
        }

        // ---- now capture player/device metrics; uses avatar defaults if devices are missing ----
        CapturePlayerHeight(localPlayer);

        // ---- validate & normalize heights ----
        if (localPlayer.CurrentHeight.PlayerEyeHeight <= 0f)
        {
            localPlayer.CurrentHeight.PlayerEyeHeight = BasisLocalHeight.DefaultPlayerEyeHeight;
            BasisDebug.LogWarning(
                $"Player eye height was invalid. Set to default: {BasisLocalHeight.DefaultPlayerEyeHeight}",
                BasisDebug.LogTag.Avatar);
        }

        if (localPlayer.CurrentHeight.AvatarEyeHeight <= 0f)
        {
            localPlayer.CurrentHeight.AvatarEyeHeight = BasisLocalHeight.DefaultAvatarEyeHeight;
            BasisDebug.LogWarning($"Avatar eye height was invalid. Set to default: {BasisLocalHeight.DefaultAvatarEyeHeight}", BasisDebug.LogTag.Avatar);
        }
        BasisLocalHeight.ComputeRatios();


        // choose which ratios to apply for the selected mode
        localPlayer.CurrentHeight.PickHeightMode(selectedHeightMode);

        BasisDebug.Log($"Final Player Eye Height (raw): {localPlayer.CurrentHeight.PlayerEyeHeight}, Avatar Eye Height (raw): {localPlayer.CurrentHeight.AvatarEyeHeight}", BasisDebug.LogTag.Avatar);

        // notify next frame
        localPlayer.ExecuteNextFrame(() =>
        {
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame?.Invoke();
        });
    }

    /// <summary>
    /// Captures live player eye height and arm span from connected input devices, using avatar/default fallbacks when necessary.
    /// </summary>
    /// <param name="localPlayer">The <see cref="BasisLocalPlayer"/> whose measurements will be populated.</param>
    /// <remarks>
    /// Eye height is read from <see cref="BasisLocalCameraDriver.Instance"/> lock-to-input if available, otherwise falls back to avatar eye height or default.
    /// Arm span uses left/right hand devices; if either hand is missing, the default arm span is used.
    /// </remarks>
    public static void CapturePlayerHeight(BasisLocalPlayer localPlayer)
    {
        if (SMModuleSitStand.IsSteatedMode)
        {
            BasisDebug.Log("Was Seated Mode taking standard size of 1.7m", BasisDebug.LogTag.Avatar);
            localPlayer.CurrentHeight.PlayerEyeHeight = BasisLocalHeight.DefaultPlayerEyeHeight;
        }
        else
        {
            var lockToInput = BasisLocalCameraDriver.Instance?.BasisLockToInput;
            if (lockToInput?.BasisInput != null)
            {
                lockToInput.BasisInput.PollData();
                localPlayer.CurrentHeight.PlayerEyeHeight = lockToInput.BasisInput.UnscaledDeviceCoord.position.y;
                BasisDebug.Log($"Player raw eye height from device: {localPlayer.CurrentHeight.PlayerEyeHeight}", BasisDebug.LogTag.Avatar);
            }
            else
            {
                // Prefer avatar eye height if it looks valid; otherwise fall back to default player height.
                float fallback = localPlayer.CurrentHeight.AvatarEyeHeight > 0f ? localPlayer.CurrentHeight.AvatarEyeHeight : BasisLocalHeight.DefaultPlayerEyeHeight;

                localPlayer.CurrentHeight.PlayerEyeHeight = fallback;

                BasisDebug.LogWarning("No attached input found for BasisLockToInput. Using fallback player eye height.", BasisDebug.LogTag.Avatar);
            }
        }
        // Player arm span (from *devices*) this is wrong. we need to use hand to upper arm length.
        if (BasisDeviceManagement.Instance.FindDevice(out BasisInput leftHand, BasisBoneTrackedRole.LeftHand) && BasisDeviceManagement.Instance.FindDevice(out BasisInput rightHand, BasisBoneTrackedRole.RightHand))
        {
            leftHand.PollData();
            rightHand.PollData();

            localPlayer.CurrentHeight.PlayerArmSpan = Vector3.Distance(leftHand.UnscaledDeviceCoord.position, rightHand.UnscaledDeviceCoord.position);

            BasisDebug.Log($"Current Player Arm Span: {localPlayer.CurrentHeight.PlayerArmSpan}", BasisDebug.LogTag.Avatar);
        }
        else
        {
            BasisDebug.LogWarning("Both hands were not discovered. Using default player arm span.", BasisDebug.LogTag.Avatar);
            localPlayer.CurrentHeight.PlayerArmSpan = BasisLocalHeight.DefaultPlayerArmSpan;
        }
    }

    /// <summary>
    /// Loads the persisted player eye height, or saves and returns the default if none is stored.
    /// </summary>
    /// <returns>
    /// The stored eye height if found; otherwise the default value from <see cref="BasisLocalPlayer.DefaultPlayerEyeHeight"/>.
    /// </returns>
    /// <remarks>
    /// On a cache miss, persists the default height to <see cref="FileNameAndExtension"/> and returns it.
    /// </remarks>
    public static float GetDefaultOrLoadPlayerHeight()
    {
        float defaultHeight = BasisLocalHeight.DefaultPlayerEyeHeight;

        if (BasisDataStore.LoadFloat(FileNameAndExtension, defaultHeight, out float foundHeight))
        {
            return foundHeight;
        }

        // FIX: 'foundHeight' is undefined on load failure; persist and return the default instead
        SaveHeight(defaultHeight);
        return defaultHeight;
    }

    /// <summary>
    /// Persists a specific eye height value to <see cref="FileNameAndExtension"/>.
    /// </summary>
    /// <param name="eyeHeight">The eye height to save (meters).</param>
    public static void SaveHeight(float eyeHeight)
    {
        BasisDataStore.SaveFloat(eyeHeight, FileNameAndExtension);
    }

    /// <summary>
    /// Applies a custom player eye height, persists it, recomputes ratios in <see cref="ChangeEyeHeightMode"/>,
    /// updates avatar scale, rescales TPose bone data, and notifies listeners next frame.
    /// </summary>
    /// <param name="customHeight">Desired eye height in meters. Must be greater than zero.</param>
    /// <remarks>
    /// Uses the recomputed unscaled avatar eye height as the baseline to compute the avatar height scale factor.
    /// Updates <see cref="BasisLocalAvatarDriver.ScaleAvatarModification"/> and adjusts TPose-scaled transforms on each bone control.
    /// </remarks>
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
