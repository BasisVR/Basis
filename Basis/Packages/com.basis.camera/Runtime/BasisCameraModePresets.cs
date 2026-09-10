using Basis.Cinematics;
using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using CameraPinSpace = BasisHandHeldCameraInteractable.CameraPinSpace;
internal readonly struct BasisCameraLook
{
    public readonly bool Active, VignetteRounded;
    public readonly int GrainType, Tonemapping;
    public readonly float FilmGrain, GrainResponse, Vignette, VignetteSmoothness, ChromaticAberration, WhiteBalanceTemperature;
    public readonly float WhiteBalanceTint, Contrast, Saturation, Lift, SplitBalance, BloomIntensity, BloomThreshold;
    public readonly float LensDistortion, LensDistortionScale;
    public readonly Color VignetteColour, SplitShadows, SplitHighlights, BloomTint;
    public BasisCameraLook(float filmGrain, int grainType, float grainResponse, float vignette, float vignetteSmoothness, Color vignetteColour, bool vignetteRounded, float chromaticAberration, float whiteBalanceTemperature, float whiteBalanceTint, float contrast, float saturation, float lift, Color splitShadows, Color splitHighlights, float splitBalance, float bloomIntensity, float bloomThreshold, Color bloomTint, float lensDistortion, float lensDistortionScale, int tonemapping)
    {
        Active = true;
        FilmGrain = filmGrain;
        GrainType = grainType;
        GrainResponse = grainResponse;
        Vignette = vignette;
        VignetteSmoothness = vignetteSmoothness;
        VignetteColour = vignetteColour;
        VignetteRounded = vignetteRounded;
        ChromaticAberration = chromaticAberration;
        WhiteBalanceTemperature = whiteBalanceTemperature;
        WhiteBalanceTint = whiteBalanceTint;
        Contrast = contrast;
        Saturation = saturation;
        Lift = lift;
        SplitShadows = splitShadows;
        SplitHighlights = splitHighlights;
        SplitBalance = splitBalance;
        BloomIntensity = bloomIntensity;
        BloomThreshold = bloomThreshold;
        BloomTint = bloomTint;
        LensDistortion = lensDistortion;
        LensDistortionScale = lensDistortionScale;
        Tonemapping = tonemapping;
    }
}
internal readonly struct BasisCameraModePreset
{
    public readonly CameraPinSpace Pin;
    public readonly BasisCameraPositionModifier Position;
    public readonly BasisCameraRotationModifier Rotation;
    public readonly BasisCameraEffectModifier[] Effects;
    public readonly bool AutoLevel, VrStabilisation, Capture360, AutoFocusSubject, AnchorToBody, DoFEnabled;
    public readonly int DoFStyle;
    public readonly float Fov, Aperture, FocalLength, MotionBlur;
    public readonly Vector3 FollowOffset;
    public readonly BasisCameraBodyKind Body;
    public readonly BasisCameraLook Look;
    public BasisCameraModePreset(CameraPinSpace pin, BasisCameraPositionModifier position, BasisCameraRotationModifier rotation, BasisCameraEffectModifier[] effects, bool autoLevel, bool vrStabilisation, bool capture360, bool autoFocusSubject, bool anchorToBody, Vector3 followOffset, float fov, bool dofEnabled, int dofStyle, float aperture, float focalLength, float motionBlur, BasisCameraBodyKind body = BasisCameraBodyKind.Digital, BasisCameraLook look = default)
    {
        Pin = pin;
        Position = position;
        Rotation = rotation;
        Effects = effects;
        AutoLevel = autoLevel;
        VrStabilisation = vrStabilisation;
        Capture360 = capture360;
        AutoFocusSubject = autoFocusSubject;
        AnchorToBody = anchorToBody;
        FollowOffset = followOffset;
        Fov = fov;
        DoFEnabled = dofEnabled;
        DoFStyle = dofStyle;
        Aperture = aperture;
        FocalLength = focalLength;
        MotionBlur = motionBlur;
        Body = body;
        Look = look;
    }
    public bool DrivesSubject => BasisCameraModifiers.NeedsSubject(Position) || BasisCameraModifiers.NeedsSubject(Rotation);
    public BasisCameraModifierStack BuildStack(BasisCameraModifierStack current)
    {
        BasisCameraModifierStack stack = current != null ? current.Clone() : new BasisCameraModifierStack();

        stack.positionModifier = Position;
        stack.rotationModifier = Rotation;

        bool needsSubject = DrivesSubject;
        for (int Index = 0; !needsSubject && Effects != null && Index < Effects.Length; Index++) needsSubject = BasisCameraModifiers.NeedsSubject(Effects[Index]);
        if (needsSubject && !stack.ResolvesSubject) stack.subject.modifier = BasisCameraSubjectModifier.FollowPlayer;

        stack.ClearEffects();
        for (int Index = 0; Effects != null && Index < Effects.Length; Index++) stack.AddEffect(Effects[Index]);

        if (DrivesSubject)
        {
            stack.follow.positionOffset = FollowOffset;
            stack.framing.directionOffset = FollowOffset;
        }
        return stack;
    }
    public bool EffectsMatch(BasisCameraModifierStack stack)
    {
        int wanted = Effects?.Length ?? 0;
        if (stack == null || stack.EffectCount != wanted) return false;

        for (int Index = 0; Index < wanted; Index++)
        {
            if (!stack.HasEffect(Effects[Index])) return false;
        }
        return true;
    }
}
internal static class BasisCameraModePresets
{
    private const int FineGrain = (int)FilmGrainLookup.Medium3, MediumGrain = (int)FilmGrainLookup.Medium1, LargeGrain = (int)FilmGrainLookup.Large01;
    private const int CoarseGrain = (int)FilmGrainLookup.Large02, NeutralTonemapping = (int)TonemappingMode.Neutral;
    private const float FovTolerance = 0.5f, ApertureTolerance = 0.02f, FocalLengthTolerance = 0.5f, MotionBlurTolerance = 0.005f;
    private const float OffsetTolerance = 0.01f, LookTolerance = 0.1f, ColourTolerance = 1f / 255f;
    private static readonly BasisCameraEffectModifier[] NoEffects = Array.Empty<BasisCameraEffectModifier>();
    private static readonly BasisCameraEffectModifier[] CinematicEffects = { BasisCameraEffectModifier.LookAhead, BasisCameraEffectModifier.Shake };
    private static readonly Vector3 DefaultFollowOffset = new Vector3(0.5f, 0f, 1.4f);
    private static readonly BasisCameraModePreset Photo = new BasisCameraModePreset(pin: CameraPinSpace.HandHeld, position: BasisCameraPositionModifier.FreeFly, rotation: BasisCameraRotationModifier.Hold, effects: NoEffects, autoLevel: false, vrStabilisation: false, capture360: false, autoFocusSubject: false, anchorToBody: true, followOffset: DefaultFollowOffset, fov: 40f, dofEnabled: false, dofStyle: 2, aperture: 2.8f, focalLength: 50f, motionBlur: 0f);
    private static readonly BasisCameraModePreset FlyingPuck = new BasisCameraModePreset(pin: CameraPinSpace.WorldSpace, position: BasisCameraPositionModifier.FreeFly, rotation: BasisCameraRotationModifier.Hold, effects: NoEffects, autoLevel: true, vrStabilisation: true, capture360: false, autoFocusSubject: false, anchorToBody: true, followOffset: DefaultFollowOffset, fov: 55f, dofEnabled: false, dofStyle: 2, aperture: 5.6f, focalLength: 35f, motionBlur: 0f);
    private static readonly BasisCameraModePreset FollowMe = new BasisCameraModePreset(pin: CameraPinSpace.WorldSpace, position: BasisCameraPositionModifier.FollowSubject, rotation: BasisCameraRotationModifier.LookAtSubject, effects: NoEffects, autoLevel: false, vrStabilisation: false, capture360: false, autoFocusSubject: true, anchorToBody: true, followOffset: DefaultFollowOffset, fov: 45f, dofEnabled: true, dofStyle: 2, aperture: 2.8f, focalLength: 50f, motionBlur: 0f);
    private static readonly BasisCameraModePreset Cinematic = new BasisCameraModePreset(pin: CameraPinSpace.WorldSpace, position: BasisCameraPositionModifier.FollowSubject, rotation: BasisCameraRotationModifier.Compose, effects: CinematicEffects, autoLevel: false, vrStabilisation: false, capture360: false, autoFocusSubject: false, anchorToBody: true, followOffset: new Vector3(1.2f, 0.4f, 3f), fov: 35f, dofEnabled: true, dofStyle: 2, aperture: 2.0f, focalLength: 85f, motionBlur: 0.35f);
    private static readonly BasisCameraModePreset Disposable = new BasisCameraModePreset(pin: CameraPinSpace.HandHeld, position: BasisCameraPositionModifier.FreeFly, rotation: BasisCameraRotationModifier.Hold, effects: NoEffects, autoLevel: false, vrStabilisation: false, capture360: false, autoFocusSubject: false, anchorToBody: true, followOffset: DefaultFollowOffset, fov: 45f, dofEnabled: false, dofStyle: 2, aperture: 8f, focalLength: 32f, motionBlur: 0f, body: BasisCameraBodyKind.Disposable, look: new BasisCameraLook(filmGrain: 0.55f, grainType: LargeGrain, grainResponse: 0.85f, vignette: 0.45f, vignetteSmoothness: 0.32f, vignetteColour: new Color(0.09f, 0.05f, 0.03f), vignetteRounded: true, chromaticAberration: 0.32f, whiteBalanceTemperature: 20f, whiteBalanceTint: -5f, contrast: 14f, saturation: 16f, lift: 0.055f, splitShadows: new Color(0.40f, 0.53f, 0.58f), splitHighlights: new Color(0.60f, 0.53f, 0.40f), splitBalance: 12f, bloomIntensity: 0.75f, bloomThreshold: 0.85f, bloomTint: new Color(1.00f, 0.42f, 0.26f), lensDistortion: 0.06f, lensDistortionScale: 1f, tonemapping: NeutralTonemapping));
    private static readonly BasisCameraModePreset Instant = new BasisCameraModePreset(pin: CameraPinSpace.HandHeld, position: BasisCameraPositionModifier.FreeFly, rotation: BasisCameraRotationModifier.Hold, effects: NoEffects, autoLevel: false, vrStabilisation: false, capture360: false, autoFocusSubject: false, anchorToBody: true, followOffset: DefaultFollowOffset, fov: 42f, dofEnabled: false, dofStyle: 2, aperture: 11f, focalLength: 40f, motionBlur: 0f, body: BasisCameraBodyKind.Instant, look: new BasisCameraLook(filmGrain: 0.22f, grainType: MediumGrain, grainResponse: 0.7f, vignette: 0.5f, vignetteSmoothness: 0.55f, vignetteColour: new Color(0.10f, 0.09f, 0.11f), vignetteRounded: true, chromaticAberration: 0.12f, whiteBalanceTemperature: 12f, whiteBalanceTint: 9f, contrast: -24f, saturation: -14f, lift: 0.115f, splitShadows: new Color(0.42f, 0.55f, 0.48f), splitHighlights: new Color(0.62f, 0.50f, 0.52f), splitBalance: -10f, bloomIntensity: 1.25f, bloomThreshold: 0.62f, bloomTint: new Color(1.00f, 0.88f, 0.80f), lensDistortion: 0.02f, lensDistortionScale: 1f, tonemapping: NeutralTonemapping));
    private static readonly BasisCameraModePreset Camcorder = new BasisCameraModePreset(pin: CameraPinSpace.HandHeld, position: BasisCameraPositionModifier.FreeFly, rotation: BasisCameraRotationModifier.Hold, effects: NoEffects, autoLevel: false, vrStabilisation: false, capture360: false, autoFocusSubject: false, anchorToBody: true, followOffset: DefaultFollowOffset, fov: 52f, dofEnabled: false, dofStyle: 2, aperture: 8f, focalLength: 28f, motionBlur: 0.5f, body: BasisCameraBodyKind.Camcorder, look: new BasisCameraLook(filmGrain: 0.34f, grainType: FineGrain, grainResponse: 0.35f, vignette: 0.26f, vignetteSmoothness: 0.45f, vignetteColour: new Color(0.05f, 0.06f, 0.08f), vignetteRounded: false, chromaticAberration: 0.6f, whiteBalanceTemperature: -8f, whiteBalanceTint: 4f, contrast: -6f, saturation: -22f, lift: 0.07f, splitShadows: new Color(0.45f, 0.50f, 0.58f), splitHighlights: new Color(0.56f, 0.54f, 0.47f), splitBalance: 0f, bloomIntensity: 0.9f, bloomThreshold: 0.55f, bloomTint: new Color(0.86f, 0.92f, 1.00f), lensDistortion: 0.04f, lensDistortionScale: 1f, tonemapping: NeutralTonemapping));
    private static readonly BasisCameraModePreset Security = new BasisCameraModePreset(pin: CameraPinSpace.HandHeld, position: BasisCameraPositionModifier.FreeFly, rotation: BasisCameraRotationModifier.Hold, effects: NoEffects, autoLevel: true, vrStabilisation: true, capture360: false, autoFocusSubject: false, anchorToBody: true, followOffset: DefaultFollowOffset, fov: 82f, dofEnabled: false, dofStyle: 2, aperture: 11f, focalLength: 18f, motionBlur: 0.15f, body: BasisCameraBodyKind.Security, look: new BasisCameraLook(filmGrain: 0.48f, grainType: CoarseGrain, grainResponse: 0.25f, vignette: 0.55f, vignetteSmoothness: 0.3f, vignetteColour: new Color(0.04f, 0.05f, 0.05f), vignetteRounded: true, chromaticAberration: 0.04f, whiteBalanceTemperature: -4f, whiteBalanceTint: 0f, contrast: 22f, saturation: -82f, lift: 0.04f, splitShadows: new Color(0.47f, 0.50f, 0.54f), splitHighlights: new Color(0.53f, 0.52f, 0.48f), splitBalance: 0f, bloomIntensity: 0.35f, bloomThreshold: 0.95f, bloomTint: new Color(0.90f, 0.95f, 1.00f), lensDistortion: 0.3f, lensDistortionScale: 1f, tonemapping: NeutralTonemapping));
    public static bool TryGet(BasisCameraMode mode, out BasisCameraModePreset preset)
    {
        switch (mode)
        {
            case BasisCameraMode.Photo: preset = Photo; return true;
            case BasisCameraMode.FlyingPuck: preset = FlyingPuck; return true;
            case BasisCameraMode.FollowMe: preset = FollowMe; return true;
            case BasisCameraMode.Cinematic: preset = Cinematic; return true;
            case BasisCameraMode.Disposable: preset = Disposable; return true;
            case BasisCameraMode.Instant: preset = Instant; return true;
            case BasisCameraMode.Camcorder: preset = Camcorder; return true;
            case BasisCameraMode.Security: preset = Security; return true;
            default: preset = default; return false;
        }
    }
    public static BasisCameraLook Shipped()
    {
        BasisHandHeldCameraUI.CameraSettings shipped = new BasisHandHeldCameraUI.CameraSettings();
        return new BasisCameraLook(shipped.filmGrain, shipped.filmGrainType, shipped.filmGrainResponse, shipped.vignette, shipped.vignetteSmoothness, shipped.vignetteColour, shipped.vignetteRounded, shipped.chromaticAberration, shipped.whiteBalanceTemperature, shipped.whiteBalanceTint, shipped.contrast, shipped.saturation, shipped.filmLift, shipped.splitToningShadows, shipped.splitToningHighlights, shipped.splitToningBalance, shipped.bloomIntensity, shipped.bloomThreshold, shipped.bloomTint, shipped.lensDistortion, shipped.lensDistortionScale, shipped.captureTonemapping);
    }
    public static void WriteLook(BasisHandHeldCameraUI ui, in BasisCameraLook look)
    {
        ui.ChangeVignetteSmoothness(look.VignetteSmoothness);
        ui.ChangeVignetteColour(look.VignetteColour);
        ui.ChangeVignetteRounded(look.VignetteRounded);
        ui.ChangeVignette(look.Vignette);
        ui.ChangeFilmGrainType(look.GrainType);
        ui.ChangeFilmGrainResponse(look.GrainResponse);
        ui.ChangeFilmGrain(look.FilmGrain);
        ui.ChangeChromaticAberration(look.ChromaticAberration);
        ui.ChangeWhiteBalanceTemperature(look.WhiteBalanceTemperature);
        ui.ChangeWhiteBalanceTint(look.WhiteBalanceTint);
        ui.ChangeContrast(look.Contrast);
        ui.ChangeSaturation(look.Saturation);
        ui.ChangeSplitToning(look.SplitShadows, look.SplitHighlights);
        ui.ChangeSplitToningBalance(look.SplitBalance);
        ui.ChangeFilmLift(look.Lift);
        ui.ChangeLensDistortionScale(look.LensDistortionScale);
        ui.ChangeLensDistortion(look.LensDistortion);
        ui.ChangeBloomIntensity(look.BloomIntensity);
        ui.ChangeBloomThreshold(look.BloomThreshold);
        ui.ChangeBloomTint(look.BloomTint);
    }
    public static BasisCameraPresetDiff Diff(BasisHandHeldCamera camera, BasisCameraMode mode)
    {
        if (!TryGet(mode, out BasisCameraModePreset preset)) return default;

        ulong fields = 0;
        BasisCameraModifierStack modifiers = camera.Modifiers;
        if (camera.Body != preset.Body) fields |= Bit(BasisCameraPresetField.Body);
        if (modifiers.positionModifier != preset.Position) fields |= Bit(BasisCameraPresetField.PositionModifier);
        if (modifiers.rotationModifier != preset.Rotation) fields |= Bit(BasisCameraPresetField.RotationModifier);
        if (!preset.EffectsMatch(modifiers)) fields |= Bit(BasisCameraPresetField.Effects);
        if (camera.useAutoLeveling != preset.AutoLevel) fields |= Bit(BasisCameraPresetField.AutoLevel);
        if (camera.useVRHandheldSmoothing != preset.VrStabilisation) fields |= Bit(BasisCameraPresetField.VrStabilisation);
        if (camera.capture360Enabled != preset.Capture360) fields |= Bit(BasisCameraPresetField.Capture360);

        if (preset.DrivesSubject)
        {
            if (camera.autoFocusFollowSubject != preset.AutoFocusSubject) fields |= Bit(BasisCameraPresetField.AutoFocusSubject);
            if (camera.subjectSettings.anchorToBody != preset.AnchorToBody) fields |= Bit(BasisCameraPresetField.AnchorToBody);
            if (Vector3.Distance(modifiers.follow.positionOffset, preset.FollowOffset) > OffsetTolerance) fields |= Bit(BasisCameraPresetField.FollowOffset);
        }

        if (camera.captureCamera != null && Mathf.Abs(camera.captureCamera.fieldOfView - preset.Fov) > FovTolerance) fields |= Bit(BasisCameraPresetField.FieldOfView);

        DepthOfField depthOfField = camera.MetaData?.depthOfField;
        if (depthOfField != null)
        {
            if (depthOfField.active != preset.DoFEnabled) fields |= Bit(BasisCameraPresetField.DepthOfField);

            if (preset.DoFEnabled)
            {
                if ((int)depthOfField.mode.value != preset.DoFStyle) fields |= Bit(BasisCameraPresetField.DepthStyle);
                if (Mathf.Abs(depthOfField.aperture.value - preset.Aperture) > ApertureTolerance) fields |= Bit(BasisCameraPresetField.DepthAperture);
                if (Mathf.Abs(depthOfField.focalLength.value - preset.FocalLength) > FocalLengthTolerance) fields |= Bit(BasisCameraPresetField.FocalLength);
            }
        }

        MotionBlur motionBlur = camera.MetaData?.motionBlur;
        if (motionBlur != null && Mathf.Abs((motionBlur.active ? motionBlur.intensity.value : 0f) - preset.MotionBlur) > MotionBlurTolerance) fields |= Bit(BasisCameraPresetField.MotionBlur);

        CompareLook(camera.MetaData, camera.CaptureTonemapping, preset.Look, ref fields);
        return new BasisCameraPresetDiff(mode, fields);
    }
    private static void CompareLook(BasisHandHeldCameraMetaData meta, TonemappingMode captureTonemapping, in BasisCameraLook look, ref ulong fields)
    {
        if (!look.Active) return;

        FilmGrain grain = meta?.filmGrain;
        if (grain != null)
        {
            if (!NearLook(Strength(grain.active, grain.intensity.value), look.FilmGrain)) fields |= Bit(BasisCameraPresetField.FilmGrain);

            if (look.FilmGrain > 0f)
            {
                if ((int)grain.type.value != look.GrainType) fields |= Bit(BasisCameraPresetField.GrainType);
                if (!NearLook(grain.response.value, look.GrainResponse)) fields |= Bit(BasisCameraPresetField.GrainResponse);
            }
        }

        Vignette vignette = meta?.vignette;
        if (vignette != null)
        {
            if (!NearLook(Strength(vignette.active, vignette.intensity.value), look.Vignette)) fields |= Bit(BasisCameraPresetField.Vignette);

            if (look.Vignette > 0f)
            {
                if (!NearLook(vignette.smoothness.value, look.VignetteSmoothness)) fields |= Bit(BasisCameraPresetField.VignetteSmoothness);
                if (!NearColour(vignette.color.value, look.VignetteColour)) fields |= Bit(BasisCameraPresetField.VignetteColour);
                if (vignette.rounded.value != look.VignetteRounded) fields |= Bit(BasisCameraPresetField.VignetteRounded);
            }
        }

        ChromaticAberration chromatic = meta?.chromaticAberration;
        if (chromatic != null && !NearLook(Strength(chromatic.active, chromatic.intensity.value), look.ChromaticAberration)) fields |= Bit(BasisCameraPresetField.ChromaticAberration);

        WhiteBalance whiteBalance = meta?.whiteBalance;
        if (whiteBalance != null)
        {
            if (!NearLook(Strength(whiteBalance.active, whiteBalance.temperature.value), look.WhiteBalanceTemperature)) fields |= Bit(BasisCameraPresetField.WhiteBalanceTemperature);
            if (!NearLook(Strength(whiteBalance.active, whiteBalance.tint.value), look.WhiteBalanceTint)) fields |= Bit(BasisCameraPresetField.WhiteBalanceTint);
        }

        ColorAdjustments colour = meta?.colorAdjustments;
        if (colour != null)
        {
            if (!NearLook(colour.contrast.value, look.Contrast)) fields |= Bit(BasisCameraPresetField.Contrast);
            if (!NearLook(colour.saturation.value, look.Saturation)) fields |= Bit(BasisCameraPresetField.Saturation);
        }

        Bloom bloom = meta?.bloom;
        if (bloom != null)
        {
            if (!NearLook(bloom.intensity.value, look.BloomIntensity)) fields |= Bit(BasisCameraPresetField.BloomIntensity);
            if (!NearLook(bloom.threshold.value, look.BloomThreshold)) fields |= Bit(BasisCameraPresetField.BloomThreshold);
            if (look.BloomIntensity > 0f && !NearColour(bloom.tint.value, look.BloomTint)) fields |= Bit(BasisCameraPresetField.BloomTint);
        }

        SplitToning splitToning = meta?.splitToning;
        if (splitToning != null)
        {
            if (!NearColour(splitToning.shadows.value, look.SplitShadows)) fields |= Bit(BasisCameraPresetField.SplitShadows);
            if (!NearColour(splitToning.highlights.value, look.SplitHighlights)) fields |= Bit(BasisCameraPresetField.SplitHighlights);
            if (!NearLook(splitToning.balance.value, look.SplitBalance)) fields |= Bit(BasisCameraPresetField.SplitBalance);
        }

        LiftGammaGain lift = meta?.liftGammaGain;
        if (lift != null && !NearLook(lift.lift.value.w, look.Lift)) fields |= Bit(BasisCameraPresetField.FilmLift);

        LensDistortion distortion = meta?.lensDistortion;
        if (distortion != null)
        {
            if (!NearLook(Strength(distortion.active, distortion.intensity.value), look.LensDistortion)) fields |= Bit(BasisCameraPresetField.LensDistortion);
            if (look.LensDistortion != 0f && !NearLook(distortion.scale.value, look.LensDistortionScale)) fields |= Bit(BasisCameraPresetField.LensDistortionScale);
        }

        if ((int)captureTonemapping != look.Tonemapping) fields |= Bit(BasisCameraPresetField.Tonemapping);
    }
    private static ulong Bit(BasisCameraPresetField field) => BasisCameraPresetDiff.Bit(field);
    private static float Strength(bool active, float value) => active ? value : 0f;
    private static bool NearLook(float live, float wanted) => Mathf.Abs(live - wanted) <= LookTolerance;
    private static bool NearColour(Color live, Color wanted) => Mathf.Abs(live.r - wanted.r) <= ColourTolerance && Mathf.Abs(live.g - wanted.g) <= ColourTolerance && Mathf.Abs(live.b - wanted.b) <= ColourTolerance;
}
