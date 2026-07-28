using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

/// <summary>
/// Ballpark scale for an UNCALIBRATED VR session: tracks a robust standing eye height (and arm span) from the
/// live HMD/controllers so DeviceScale is plausible before the user calibrates. Active only while no explicit
/// calibration has happened (BasisHeightDriver.HasUserCalibratedHeight) and fully superseded once one does.
/// </summary>
public static class BasisAutoScaleEstimator
{
    const float MinEyeHeight = 1.10f;       // plausible standing eye-height band (metres)
    const float MaxEyeHeight = 2.10f;
    const float ReapplyThreshold = 0.03f;   // metres of drift from current before re-applying scale
    const float ReleaseRate = 0.03f;        // metres/sec the estimate eases down toward lower samples

    public static bool HasEstimate;
    public static float EstimatedEyeHeight = BasisHeightDriver.FallbackHeightInMeters;
    public static float EstimatedArmSpan = BasisHeightDriver.FallbackHeightInMeters;

    static float _maxArmSpan;

    public static void Reset()
    {
        HasEstimate = false;
        _maxArmSpan = 0f;
        EstimatedEyeHeight = BasisHeightDriver.FallbackHeightInMeters;
        EstimatedArmSpan = BasisHeightDriver.FallbackHeightInMeters;
    }

    public static void Tick(float deltaTime)
    {
        if (BasisHeightDriver.HasUserCalibratedHeight) return;
        if (!Basis.BasisUI.BasisSettingsDefaults.AutoScaleEstimateEnabled.RawValue) return;
        if (!BasisDeviceManagement.IsCurrentModeVR() || SMModuleSitStand.IsSteatedMode) return;

        var headInput = BasisLocalCameraDriver.Instance?.BasisLockToInput?.BasisInput;
        if (headInput == null) return;

        // Standing eye height: jump up to new highs immediately, ease down slowly, so a brief crouch holds the
        // standing value while a one-off reach/jump spike decays back out. Out-of-band samples are ignored.
        float sample = headInput.UnscaledDeviceCoord.position.y
            - BasisLocalPlayspaceMover.VerticalOffset
            - BasisHeightDriver.HeightModeGroundingOffset;
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

        EstimatedArmSpan = EstimateArmSpan(EstimatedEyeHeight);

        // Compare against the live value (not a private cache) so this also re-asserts after an avatar-load
        // instant capture nudged PlayerEyeHeight away from the estimate.
        if (Mathf.Abs(EstimatedEyeHeight - BasisHeightDriver.PlayerEyeHeight) > ReapplyThreshold)
        {
            BasisHeightDriver.PlayerEyeHeight = EstimatedEyeHeight;
            BasisHeightDriver.PlayerArmSpan = EstimatedArmSpan;
            BasisHeightDriver.ApplyScaleAndHeight();
        }
    }

    static float EstimateArmSpan(float eyeHeight)
    {
        // Stay within the same ±30% eye-to-arm band BasisLocalHeightCalculator.ValidateEyeToArm enforces.
        float lo = eyeHeight * 0.70f;
        float hi = eyeHeight * 1.30f;

        var dm = BasisDeviceManagement.Instance;
        if (dm != null
            && dm.FindDevice(out BasisInput left, BasisBoneTrackedRole.LeftHand)
            && dm.FindDevice(out BasisInput right, BasisBoneTrackedRole.RightHand))
        {
            Vector3 l = left.UnscaledDeviceCoord.position;
            Vector3 r = right.UnscaledDeviceCoord.position;
            float span = Vector3.Distance(new Vector3(l.x, 0f, l.z), new Vector3(r.x, 0f, r.z));
            _maxArmSpan = Mathf.Max(_maxArmSpan, span);
        }

        return _maxArmSpan > 0f ? Mathf.Clamp(_maxArmSpan, lo, hi) : eyeHeight;
    }
}
