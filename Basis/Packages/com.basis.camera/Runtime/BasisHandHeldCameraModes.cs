using Basis.Cinematics;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using CameraAnchorKind = BasisHandHeldCameraInteractable.CameraAnchorKind;
using CameraPinSpace = BasisHandHeldCameraInteractable.CameraPinSpace;
public partial class BasisHandHeldCamera
{
    public BasisCameraMode CameraMode { get; private set; } = BasisCameraMode.Photo;
    public BasisCameraMode ComparedMode { get; private set; } = BasisCameraMode.Photo;
    public bool MatchesCameraMode(BasisCameraMode mode) => CompareToMode(mode).Matches;
    public BasisCameraPresetDiff CompareToMode(BasisCameraMode mode) => BasisCameraModePresets.Diff(this, mode);
    public void ApplyCameraMode(BasisCameraMode mode)
    {
        if (!BasisCameraModePresets.TryGet(mode, out BasisCameraModePreset preset))
        {
            CameraMode = BasisCameraMode.Custom;
            return;
        }

        ApplyPlacement(preset.Pin, preset.BuildStack(Modifiers));

        useAutoLeveling = preset.AutoLevel;
        useVRHandheldSmoothing = preset.VrStabilisation;
        capture360Enabled = preset.Capture360;

        if (preset.DrivesSubject)
        {
            autoFocusFollowSubject = preset.AutoFocusSubject;
            subjectSettings.anchorToBody = preset.AnchorToBody;
        }

        SetBody(preset.Body, freshLoad: true);

        SetFieldOfView(preset.Fov);
        ApplyPresetOptics(preset);
        ApplyPresetLook(preset.Look);
        SyncPropUiAfterModeChange();

        CameraMode = mode;
        ComparedMode = mode;
    }
    public bool RefreshCameraMode()
    {
        BasisCameraMode resolved = ResolveCameraMode();

        if (resolved != BasisCameraMode.Custom) ComparedMode = resolved;
        if (resolved == CameraMode) return false;

        CameraMode = resolved;
        return true;
    }
    internal void ApplyPlacement(CameraPinSpace pin, BasisCameraModifierStack stack)
    {
        if (stack != null) ApplyModifierStack(stack);
        if (pin < CameraPinSpace.HandHeld || pin > CameraPinSpace.Attached) pin = CameraPinSpace.HandHeld;
        if (pin == CameraPinSpace.Attached && AnchorKind == CameraAnchorKind.None) pin = CameraPinSpace.WorldSpace;
        SetAnchorSpace(pin);
    }
    internal void RestoreCameraMode(BasisCameraMode mode)
    {
        if (BasisCameraModePresets.TryGet(mode, out BasisCameraModePreset preset))
        {
            PinSpace = preset.Pin;
            ComparedMode = mode;
        }

        CameraMode = mode;
        RefreshCameraMode();
    }
    private void ApplyPresetOptics(in BasisCameraModePreset preset)
    {
        DepthOfField depthOfField = MetaData?.depthOfField;
        if (depthOfField != null)
        {
            depthOfField.mode.overrideState = true;
            depthOfField.mode.value = (DepthOfFieldMode)Mathf.Clamp(preset.DoFStyle, 1, 2);
            depthOfField.active = preset.DoFEnabled;

            depthOfField.aperture.overrideState = true;
            depthOfField.aperture.value = preset.Aperture;
            depthOfField.focalLength.overrideState = true;
            depthOfField.focalLength.value = preset.FocalLength;

            BasisDOFInteractionHandler?.SetDoFState(preset.DoFEnabled);
        }

        MotionBlur motionBlur = MetaData?.motionBlur;
        if (motionBlur != null)
        {
            motionBlur.intensity.overrideState = true;
            motionBlur.intensity.value = preset.MotionBlur;
            motionBlur.active = preset.MotionBlur > 0f;
        }
    }
    private void ApplyPresetLook(in BasisCameraLook look)
    {
        if (!look.Active)
        {
            ResetFilmGrading();
            return;
        }

        SetCaptureTonemapping(look.Tonemapping);
        if (HandHeld != null && HandHeld.HHC != null) BasisCameraModePresets.WriteLook(HandHeld, look);
    }
    private void ResetFilmGrading()
    {
        if (HandHeld == null || HandHeld.HHC == null) return;

        BasisCameraLook shipped = BasisCameraModePresets.Shipped();
        BasisCameraModePresets.WriteLook(HandHeld, shipped);
        SetCaptureTonemapping(shipped.Tonemapping);
    }
    private void SyncPropUiAfterModeChange()
    {
        if (HandHeld == null || HandHeld.HHC == null) return;

        HandHeld.SyncPropControlsFromState();
        HandHeld.SetDepthMode(HandHeld.currentDepthMode);
    }
    private BasisCameraMode ResolveCameraMode()
    {
        if (CameraMode != BasisCameraMode.Custom && MatchesCameraMode(CameraMode)) return CameraMode;

        for (int Index = 0; Index < BasisCameraModes.Ordered.Length; Index++)
        {
            BasisCameraMode candidate = BasisCameraModes.Ordered[Index];
            if (candidate != BasisCameraMode.Custom && MatchesCameraMode(candidate)) return candidate;
        }

        return BasisCameraMode.Custom;
    }
#if UNITY_INCLUDE_TESTS
    public void RestoreCameraModeForTest(BasisCameraMode mode) => RestoreCameraMode(mode);
    public void ApplyPlacementForTest(int pinSpace) => ApplyPlacement((CameraPinSpace)pinSpace, null);
#endif
}
