using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using UnityEngine;
[AutoStaticsCleanup]
public static partial class BasisAutoScaleEstimator
{
    const float MinEyeHeight = 1.10f;
    const float MaxEyeHeight = 2.10f;
    const float ReapplyThreshold = 0.03f;
    const float ReleaseRate = 0.03f;
    public static bool HasEstimate;
    public static float EstimatedEyeHeight = BasisHeightDriver.FallbackHeightInMeters;
    public static void Reset()
    {
        HasEstimate = false;
        EstimatedEyeHeight = BasisHeightDriver.FallbackHeightInMeters;
    }
    public static void Tick(float deltaTime)
    {
        if (BasisHeightDriver.ObservingBodyEvidence) return;
        if (BasisHeightDriver.HasUserCalibratedHeight) return;
        if (!Basis.BasisUI.BasisSettingsDefaults.AutoScaleEstimateEnabled.RawValue) return;
        if (!BasisDeviceManagement.IsCurrentModeVR() || SMModuleSitStand.IsSteatedMode) return;

        var headInput = BasisLocalCameraDriver.Instance?.BasisLockToInput?.BasisInput;
        if (headInput == null) return;

        float sample = headInput.UnscaledDeviceCoord.position.y - BasisLocalPlayspaceMover.VerticalOffset - BasisHeightDriver.HeightModeGroundingOffset;
        if (sample >= MinEyeHeight && sample <= MaxEyeHeight)
        {
            if (!HasEstimate || sample > EstimatedEyeHeight)
            {
                EstimatedEyeHeight = sample;
            }
            else
            {
                EstimatedEyeHeight = Mathf.MoveTowards(EstimatedEyeHeight, sample, ReleaseRate * deltaTime);
            }
            HasEstimate = true;
        }

        if (!HasEstimate) return;

        // The arm span follows from the eye height inside ApplyScaleAndHeight.
        if (Mathf.Abs(EstimatedEyeHeight - BasisHeightDriver.PlayerEyeHeight) > ReapplyThreshold)
        {
            BasisHeightDriver.PlayerEyeHeight = EstimatedEyeHeight;
            BasisHeightDriver.ApplyScaleAndHeight();
        }
    }
}
[AutoStaticsCleanup]
public static partial class BasisCalibrationRefitGate
{
    static readonly HashSet<BasisInteractableObject> sheld = new();
    public static void MarkInteracting(BasisInteractableObject interactable)
    {
        if (interactable != null)
        {
            sheld.Add(interactable);
        }
    }
    public static void MarkReleased(BasisInteractableObject interactable)
    {
        if (interactable != null)
        {
            sheld.Remove(interactable);
        }
    }
    static readonly List<BasisInteractableObject> stale = new();
    public static bool IsHoldingSomething()
    {
        if (sheld.Count == 0)
        {
            return false;
        }

        stale.Clear();
        bool holding = false;
        foreach (BasisInteractableObject held in sheld)
        {
            if (held == null || !held.Inputs.AnyInteracting(false))
            {
                stale.Add(held);
                continue;
            }
            holding = true;
        }
        for (int Index = 0; Index < stale.Count; Index++)
        {
            sheld.Remove(stale[Index]);
        }
        stale.Clear();
        return holding;
    }
    public static bool ShouldHoldRefit(out string reason)
    {
        reason = null;

        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        if (player == null)
        {
            reason = "no local player";
            return true;
        }

        if (player.LocalSeatDriver != null && player.LocalSeatDriver.IsSeated)
        {
            reason = "seated";
            return true;
        }

        if (IsHoldingSomething())
        {
            reason = "holding something";
            return true;
        }

        if (!string.IsNullOrEmpty(Basis.BasisUI.BasisMainMenu.ActiveMenuTitle))
        {
            reason = "menu open";
            return true;
        }

        return false;
    }
}
