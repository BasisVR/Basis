using System.Text;
using Basis;
using Basis.BasisUI;
using UnityEngine;
using CameraSettings = BasisHandHeldCameraUI.CameraSettings;

/// <summary>
/// Writes a settings file out as something you can read.
///
/// <para>The panel spreads these values over seven tabs and sixteen collapsible sections, which is
/// the right shape for <em>changing</em> one and the wrong shape for answering "what is this camera
/// actually set to" — or the question a saved mode raises, "what am I about to get back". This is
/// the whole file on one page, in the panel's own words, using the same localized labels the
/// controls carry so the readout and the control that sets a value never disagree about its name.
/// </para>
///
/// <para>Values that reach the camera as an index into a preset table are shown as the preset —
/// "f/2.8", not "1". The tables live on <see cref="BasisHandHeldCameraMetaData"/>, which is a plain
/// serializable class with readonly defaults, so passing null is only a fallback for a camera that
/// has not built one yet rather than the normal case.</para>
///
/// <para>Rows carrying a value the camera's mode did not put there are written in blue. The mode
/// dropdown can only say "Custom" — it is one bit, and by the time it says that the one thing worth
/// knowing is <em>which</em> of fifty values moved. That is the question this page exists for, and
/// the colour is the answer to it.</para>
/// </summary>
public static class BasisCameraSettingsReadout
{
    private static readonly StringBuilder Builder = new StringBuilder(2048);

    /// <summary>
    /// The changed-row colour: the panel's accent, lightened until it reads as body text on the
    /// card's own background rather than as a pressed control.
    /// </summary>
    private const string ChangedOpen = "<color=#5FA8FF>";

    private const string ChangedClose = "</color>";

    /// <summary>
    /// What the rows are being measured against for this pass. Held beside the builder rather than
    /// threaded through every row, which the builder itself already is.
    /// </summary>
    private static BasisCameraPresetDiff Changes;

    /// <summary>
    /// The whole file as text. <paramref name="pinSpace"/> is passed alongside because it is
    /// placement rather than
    /// settings — <see cref="CameraSettings"/> deliberately does not carry them — and leaving them
    /// out would make the readout silent about the two things that decide where the camera goes.
    ///
    /// <para><paramref name="changes"/> is what the camera's mode wanted, from
    /// <c>BasisHandHeldCamera.CompareToMode</c>. Left out, nothing is coloured and the page reads
    /// as it always has.</para>
    /// </summary>
    public static string Build(
        CameraSettings settings,
        int pinSpace,
        BasisHandHeldCameraMetaData metaData,
        BasisCameraPresetDiff changes = default)
    {
        if (settings == null) return string.Empty;

        Builder.Clear();
        Changes = changes;

        // Named, because the dropdown above is showing Custom by now and cannot say it: the colour
        // is only meaningful once the reader knows which mode the rows are being held against.
        if (changes.HasChanges)
        {
            Builder.Append(ChangedOpen)
                .Append(BasisLocalization.Get(
                    "camera.readout.changedFrom",
                    BasisLocalization.Get(BasisCameraModes.Get(changes.Mode).TitleKey)))
                .Append(ChangedClose)
                .Append('\n');
        }

        Section("camera.placement");
        Row("camera.anchor", PinSpaceLabel(pinSpace));
        Row("camera.anchorFollowsBody", OnOff(settings.anchorFollowsBody));

        Section("camera.lens");
        Row("camera.fieldOfView", Number(settings.fov), BasisCameraPresetField.FieldOfView);
        Row("camera.sensorSize", $"{Number(settings.sensorSizeX)} x {Number(settings.sensorSizeY)} mm");
        Row("camera.aperture", Preset(metaData?.apertures, settings.apertureIndex));
        Row("camera.shutterSpeed", Preset(metaData?.shutterSpeeds, settings.shutterSpeedIndex));
        Row("camera.iso", Preset(metaData?.isoValues, settings.isoIndex));

        Section("camera.depthOfField");
        Row("camera.depthOfField", OnOff(settings.depthIsActive), BasisCameraPresetField.DepthOfField);
        Row("camera.mode", DepthModeLabel(settings.dofMode), BasisCameraPresetField.DepthStyle);
        Row("camera.aperture", "f/" + Number(settings.depthAperture), BasisCameraPresetField.DepthAperture);
        Row("camera.focalLength", Number(settings.dofFocalLength) + " mm", BasisCameraPresetField.FocalLength);
        Row("camera.bokehBlades", settings.dofBladeCount.ToString());
        Row("camera.focusMode", BasisLocalization.Get(settings.useManualFocus ? "camera.focusManual" : "camera.focusAuto"));
        Row("camera.focusDistance", Number(settings.depthFocusDistance));

        Section("camera.body");
        Row("camera.body.kind", BasisLocalization.Get(
            BasisCameraBodies.TitleKey(BasisCameraBodies.Sanitize(settings.cameraBody))),
            BasisCameraPresetField.Body);

        BasisCameraBodyTraits body = BasisCameraBodies.Get(BasisCameraBodies.Sanitize(settings.cameraBody));
        if (body.HasFlash) Row("camera.body.flash", OnOff(settings.flashEnabled));

        Section("camera.exposureColour");
        Row("camera.exposure", ExposureLabel(settings.exposureIndex));
        Row("camera.exposureOnCamera", OnOff(settings.showExposureOnCamera));
        Row("camera.contrast", Number(settings.contrast), BasisCameraPresetField.Contrast);
        Row("camera.saturation", Number(settings.saturation), BasisCameraPresetField.Saturation);
        Row("camera.hueShift", Number(settings.hueShift));
        Row("camera.whiteBalanceTemp", Number(settings.whiteBalanceTemperature), BasisCameraPresetField.WhiteBalanceTemperature);
        Row("camera.whiteBalanceTint", Number(settings.whiteBalanceTint), BasisCameraPresetField.WhiteBalanceTint);
        Row("camera.splitToning.shadows", Swatch(settings.splitToningShadows), BasisCameraPresetField.SplitShadows);
        Row("camera.splitToning.highlights", Swatch(settings.splitToningHighlights), BasisCameraPresetField.SplitHighlights);
        Row("camera.splitToning.balance", Number(settings.splitToningBalance), BasisCameraPresetField.SplitBalance);
        Row("camera.filmLift", Number(settings.filmLift), BasisCameraPresetField.FilmLift);
        Row("camera.tonemapping", TonemappingLabel(settings.captureTonemapping), BasisCameraPresetField.Tonemapping);

        Section("camera.effects");
        Row("camera.bloomIntensity", Number(settings.bloomIntensity), BasisCameraPresetField.BloomIntensity);
        Row("camera.bloomThreshold", Number(settings.bloomThreshold), BasisCameraPresetField.BloomThreshold);
        Row("camera.bloomScatter", Number(settings.bloomScatter));
        Row("camera.vignette", Number(settings.vignette), BasisCameraPresetField.Vignette);
        Row("camera.vignetteSmoothness", Number(settings.vignetteSmoothness), BasisCameraPresetField.VignetteSmoothness);
        Row("camera.chromaticAberration", Number(settings.chromaticAberration), BasisCameraPresetField.ChromaticAberration);
        Row("camera.filmGrain", Number(settings.filmGrain), BasisCameraPresetField.FilmGrain);
        Row("camera.filmGrain.type", FilmGrainTypeLabel(settings.filmGrainType), BasisCameraPresetField.GrainType);
        Row("camera.filmGrain.response", Number(settings.filmGrainResponse), BasisCameraPresetField.GrainResponse);
        Row("camera.bloomTint", Swatch(settings.bloomTint), BasisCameraPresetField.BloomTint);
        Row("camera.vignetteColour", Swatch(settings.vignetteColour), BasisCameraPresetField.VignetteColour);
        Row("camera.vignetteRounded", OnOff(settings.vignetteRounded), BasisCameraPresetField.VignetteRounded);
        Row("camera.lensDistortion", Number(settings.lensDistortion), BasisCameraPresetField.LensDistortion);
        Row("camera.lensDistortionScale", Number(settings.lensDistortionScale), BasisCameraPresetField.LensDistortionScale);
        Row("camera.panini", Number(settings.paniniDistance));
        Row("camera.paniniCrop", Number(settings.paniniCropToFit));
        Row("camera.motionBlur", Number(settings.motionBlurIntensity), BasisCameraPresetField.MotionBlur);
        Row("camera.motionBlurClamp", Number(settings.motionBlurClamp));
        Row("camera.motionBlurQuality", MotionBlurQualityLabel(settings.motionBlurQuality));
        Row("camera.motionBlurMode", MotionBlurModeLabel(settings.motionBlurMode));
        Row("settings.graphics.fog.override", OnOff(settings.overrideVolumetricFog));
        if (settings.overrideVolumetricFog)
            Row("settings.graphics.fog.density", Number(settings.VolumetricFogVolumedensity));

        Section("camera.section.globalIllumination");
        Row("camera.gi.override", OnOff(settings.overrideGlobalIllumination));
        if (settings.overrideGlobalIllumination)
        {
            Row("settings.graphics.gi.mode", GiModeLabel(settings.giMode));
            Row("settings.graphics.gi.layers", GiLayersLabel(settings.giLayers));
            Row("settings.graphics.gi.skinned", GiSkinnedMeshesLabel(settings.giSkinnedMeshes));
            Row("settings.graphics.gi.quality", GiQualityLabel(settings.giQuality));
            Row("settings.graphics.gi.fallback", GiFallbackLabel(settings.giFallback));
            Row("settings.graphics.gi.ignoreBakedEmission", OnOff(settings.giIgnoreBakedEmission));
            Row("settings.graphics.gi.intensity", Number(settings.giIntensity));
            Row("settings.graphics.gi.saturation", Number(settings.giSaturation));
            Row("settings.graphics.gi.obscurance", Number(settings.giObscurance));
            Row("settings.graphics.gi.rayLength", Number(settings.giRayLength));
            Row("settings.graphics.gi.smoothing", Number(settings.giSmoothing));
            Row("settings.graphics.gi.wideBlur", OnOff(settings.giWideBlur));
            Row("settings.graphics.gi.rayReuse", OnOff(settings.giRayReuse));
            Row("settings.graphics.gi.emitters", OnOff(settings.giEmitters));
            Row("settings.graphics.gi.emitterIntensity", Number(settings.giEmitterIntensity));
            Row("settings.graphics.gi.specular", OnOff(settings.giSpecular));
            Row("settings.graphics.gi.obscuranceRadius", Number(settings.giObscuranceRadius));
            Row("settings.graphics.gi.fadeDistance", Number(settings.giFadeDistance));
            Row("settings.graphics.gi.normalBias", Number(settings.giNormalBias));
            Row("settings.graphics.gi.distanceBias", Number(settings.giDistanceBias));
            Row("settings.graphics.gi.bounceThreshold", Number(settings.giBounceThreshold));
            Row("settings.graphics.gi.fireflyClamp", Number(settings.giFireflyClamp));
            Row("settings.graphics.gi.reflectionProbes", OnOff(settings.giReflectionProbes));
            Row("camera.gi.mirrors", OnOff(settings.giMirrors));
        }

        Section("camera.section.rtao");
        Row("camera.rtao.override", OnOff(settings.overrideRTAO));
        if (settings.overrideRTAO)
        {
            Row("settings.graphics.rtao.mode", RtaoModeLabel(settings.rtaoMode));
            Row("settings.graphics.rtao.intensity", Number(settings.rtaoIntensity));
            Row("settings.graphics.rtao.radius", Number(settings.rtaoRadius));
            Row("settings.graphics.rtao.apply", RtaoApplyModeLabel(settings.rtaoApplyMode));
            Row("settings.graphics.rtao.denoise", RtaoDenoiseLabel(settings.rtaoDenoisePasses));
            Row("settings.graphics.rtao.directStrength", Number(settings.rtaoDirectStrength));
            Row("settings.graphics.rtao.layers", RtaoLayersLabel(settings.rtaoLayers));
            Row("settings.graphics.rtao.skinned", RtaoSkinnedMeshesLabel(settings.rtaoSkinnedMeshes));
            Row("settings.graphics.rtao.normalBias", Number(settings.rtaoNormalBias));
            Row("settings.graphics.rtao.distanceBias", Number(settings.rtaoDistanceBias));
            Row("settings.graphics.rtao.falloff", Number(settings.rtaoFalloff));
            Row("settings.graphics.rtao.power", Number(settings.rtaoPower));
            Row("settings.graphics.rtao.fadeStart", Number(settings.rtaoFadeStart));
            Row("settings.graphics.rtao.fadeEnd", Number(settings.rtaoFadeEnd));
            Row("settings.graphics.rtao.specularRelief", Number(settings.rtaoSpecularRelief));
        }

        Section("camera.output");
        Row("camera.photoResolution", ResolutionLabel(metaData, settings.resolutionIndex));
        Row("camera.photoFormat", Preset(metaData?.formats, settings.formatIndex));
        Row("camera.msaa", settings.msaaSamples + "x");
        Row("camera.n360Capture", OnOff(settings.capture360), BasisCameraPresetField.Capture360);
        Row("camera.printPhoto", OnOff(settings.printPhoto));
        Row("camera.flySpeed", Number(settings.flySpeed) + " m/s");
        Row("camera.flyClimbSpeed", Number(settings.flyClimbSpeed) + " m/s");
        Row("camera.flyFastMultiplier", Number(settings.flyFastMultiplier) + "x");
        Row("camera.flyTurnSpeed", Number(settings.flyTurnSpeed) + " °/s");
        Row("camera.flyMouseSensitivity", Number(settings.flyMouseSensitivity));
        Row("camera.flyMomentum", OnOff(settings.flyMomentum));
        Row("camera.flyOnMenu", OnOff(settings.showFlyOnMainMenu));
        Row("camera.autoLevel", OnOff(settings.useAutoLeveling), BasisCameraPresetField.AutoLevel);
        Row("camera.vrStabilization", OnOff(settings.useVRHandheldSmoothing), BasisCameraPresetField.VrStabilisation);
        Row("camera.vrStabilization.position", Number(settings.vrStabilizationPositionDamping) + " s");
        Row("camera.vrStabilization.yaw", Number(settings.vrStabilizationYawDamping) + " s");
        Row("camera.vrStabilization.pitch", Number(settings.vrStabilizationPitchDamping) + " s");
        Row("camera.vrStabilization.roll", Number(settings.vrStabilizationRollDamping) + " s");
        Row("camera.zoomStabilization", OnOff(settings.zoomStabilization));
        Row("camera.zoomStabilization.response", Number(settings.zoomStabilizationResponse));
        Row("camera.zoomStabilization.min", Number(settings.zoomStabilizationMinScale) + "x");
        Row("camera.zoomStabilization.max", Number(settings.zoomStabilizationMaxScale) + "x");
        Row("camera.smoothDrag", OnOff(settings.useSmoothDrag));
        Row("camera.smoothDrag.position", Number(settings.smoothDragPositionDamping) + " s");
        Row("camera.smoothDrag.rotation", Number(settings.smoothDragRotationDamping) + " s");
        Row("camera.smoothDrag.leash", Number(settings.smoothDragMaxDistance) + " m");
        Row("camera.resize", OnOff(settings.resizeWithGesture));
        Row("camera.streamPreset", BasisCameraStreamPresets.Label(BasisCameraStreamPresets.KeyFor((BasisVideoTransport)settings.streamTransport, settings.streamWidth, settings.streamHeight, settings.streamFrameRate, settings.streamQuality)));
        Row("camera.transport", BasisHandHeldCamera.GetVideoTransportName((BasisVideoTransport)settings.streamTransport));
        Row("camera.streamResolution", settings.streamWidth + " x " + settings.streamHeight);
        Row("camera.streamFrameRate", Number(settings.streamFrameRate) + " Hz");
        Row("camera.streamQuality", settings.streamQuality.ToString());
        Row("camera.streamPort", settings.streamPort.ToString());

        // The one value on the page somebody typed. Everything else here is a number or a label the
        // panel wrote itself, and the card parses tags now — so a sender name with a bracket in it
        // would eat the rest of the readout as markup.
        Row("camera.senderName", "<noparse>" + settings.streamSenderName + "</noparse>");

        Section("camera.gif");
        Row("camera.gif.length", Number(settings.gifDurationSeconds) + " s");
        Row("camera.gif.frameRate", settings.gifFrameRate.ToString());
        Row("camera.gif.size", settings.gifWidth + " px");
        Row("camera.gif.loop", OnOff(settings.gifLoop));
        Row("camera.gif.dither", OnOff(settings.gifDither));

        Section("camera.video");
        Row("camera.video.timeLimit", OnOff(settings.videoTimeLimit));
        Row("camera.video.autoNewClip", OnOff(settings.videoContinuousClips));
        Row("camera.video.length", Number(settings.videoDurationSeconds) + " s");
        Row("camera.video.frameRate", settings.videoFrameRate.ToString());
        Row("camera.video.size", settings.videoWidth + " px");
        Row("camera.video.quality", settings.videoQuality.ToString());

        Section("camera.photogrammetry");
        Row("camera.photogrammetry.distance", Number(settings.photogrammetryDistanceMeters) + " m");
        Row("camera.photogrammetry.angle", Number(settings.photogrammetryAngleDegrees) + " °");
        Row("camera.photogrammetry.resolution", settings.photogrammetryWidth + " px");

        Basis.Cinematics.BasisCameraModifierStack stack =
            settings.modifiers ?? new Basis.Cinematics.BasisCameraModifierStack();

        Section("camera.subject");
        Row("camera.modifier.subject",
            BasisLocalization.Get(Basis.Cinematics.BasisCameraModifiers.NameKey(stack.subject.modifier)));
        Row("camera.followPlayspace", OnOff(stack.subject.anchorToBody), BasisCameraPresetField.AnchorToBody);
        Row("camera.aimPoint", BasisLocalization.Get(Basis.Cinematics.BasisCameraModifiers.NameKey(stack.subject.aimPoint)));
        Row("camera.lookAtHeightY", Number(stack.subject.aimHeightOffset));
        Row("camera.subjectRadius", Number(stack.subject.framingRadius));
        Row("camera.groupIncludesMe", OnOff(stack.subject.groupIncludesLocal));
        Row("camera.fixedPoint", Vector(stack.subject.fixedPoint));

        Section("camera.modifiers");
        Row("camera.modifier.position",
            BasisLocalization.Get(Basis.Cinematics.BasisCameraModifiers.NameKey(stack.positionModifier)),
            BasisCameraPresetField.PositionModifier);
        Row("camera.modifier.rotation",
            BasisLocalization.Get(Basis.Cinematics.BasisCameraModifiers.NameKey(stack.rotationModifier)),
            BasisCameraPresetField.RotationModifier);
        Row("camera.modifier.effects", EffectList(stack), BasisCameraPresetField.Effects);
        Row("camera.followOffset", Vector(stack.follow.positionOffset), BasisCameraPresetField.FollowOffset);
        Row("camera.lateralTrackingX", Number(stack.follow.lateralTracking));
        Row("camera.followRotationOffset", Vector(stack.lookAt.rotationOffset));
        Row("camera.autoFocusSubject", OnOff(settings.autoFocusFollowSubject), BasisCameraPresetField.AutoFocusSubject);
        Row("camera.detachedMarker", DetachedMarkerLabel(settings.detachedMarker));
        Row("camera.detachedMarker.size", Number(settings.detachedMarkerScale * 100f) + "%");
        Row("camera.puckPreview", OnOff(settings.puckLookAtPreview));
        Row("camera.rollControl", OnOff(settings.cameraRoll));

        Section("camera.background");
        Row("camera.backgroundMode", BackgroundModeLabel(settings.backgroundMode));
        Row("camera.backgroundKeepWorld", OnOff(settings.backgroundKeepsWorld));
        Row("camera.backgroundRed", "#" + ColorUtility.ToHtmlStringRGB(settings.backgroundCustomColor));

        // A count rather than a list: the shots are a track someone authored, and naming each one
        // here would bury every setting above it under a mode with a dozen of them.

        return Builder.ToString();
    }

    private static void Section(string key)
    {
        if (Builder.Length > 0) Builder.Append('\n');
        Builder.Append(BasisLocalization.Get(key)).Append('\n');
    }

    // Two spaces rather than a tab: TextMeshPro's tab stops are set by the style asset, so an
    // indent made of tabs is a different width in every panel it is read in.
    private static void Row(string key, string value) =>
        Builder.Append("  ").Append(BasisLocalization.Get(key)).Append(": ").Append(value).Append('\n');

    /// <summary>
    /// A row for a value the camera's mode owns, coloured where the camera is no longer holding it.
    ///
    /// <para>The label is coloured along with the value. A blue number alone reads as a number
    /// worth noticing; the whole row reading blue is what makes a page of fifty scannable at a
    /// glance, which is the only way this is quicker than reopening the tab it lives on.</para>
    /// </summary>
    private static void Row(string key, string value, BasisCameraPresetField field)
    {
        if (!Changes.Differs(field))
        {
            Row(key, value);
            return;
        }

        Builder.Append("  ").Append(ChangedOpen).Append(BasisLocalization.Get(key)).Append(": ")
            .Append(value).Append(ChangedClose).Append('\n');
    }

    /// <summary>Trailing zeros are noise on a readout — 40 and 2.8 rather than 40.00 and 2.80.</summary>
    private static string Number(float value) => value.ToString("0.###");

    /// <summary>A colour as the hex the panel's own colour rows show, which is what can be typed back in.</summary>
    private static string Swatch(Color value) => "#" + ColorUtility.ToHtmlStringRGB(value);

    /// <summary>
    /// The grain texture by name. Written out rather than looked up in the language table: these are
    /// URP's own identifiers and a translated "Large01" would name nothing.
    /// </summary>
    private static string FilmGrainTypeLabel(int type)
    {
        int highest = (int)UnityEngine.Rendering.Universal.FilmGrainLookup.Large02;
        return type >= 0 && type <= highest
            ? ((UnityEngine.Rendering.Universal.FilmGrainLookup)type).ToString()
            : type.ToString();
    }

    private static string Vector(Vector3 value) =>
        $"{Number(value.x)}, {Number(value.y)}, {Number(value.z)}";

    private static string OnOff(bool value) =>
        BasisLocalization.Get(value ? "ui.option.on" : "ui.option.off");

    /// <summary>An index with no table behind it is still worth showing — as an index, honestly.</summary>
    private static string Preset(string[] table, int index) =>
        table != null && index >= 0 && index < table.Length ? table[index] : index.ToString();

    private static string ResolutionLabel(BasisHandHeldCameraMetaData metaData, int index)
    {
        if (metaData == null || index < 0 || index >= metaData.resolutions.Length) return index.ToString();

        var resolution = metaData.resolutions[index];
        return $"{resolution.width} x {resolution.height}";
    }

    private static string ExposureLabel(int index)
    {
        // The stop table is the UI's, and it is what the index means. Showing the stop rather than
        // the index is the difference between "+1" and "8".
        float stop = BasisHandHeldCameraUI.ExposureStopAt(index);
        return (stop > 0f ? "+" : string.Empty) + Number(stop);
    }

    /// <summary>The fitted effects by name, or a dash when the stack carries none.</summary>
    private static string EffectList(Basis.Cinematics.BasisCameraModifierStack stack)
    {
        if (stack == null || stack.EffectCount == 0)
        {
            return "-";
        }

        System.Text.StringBuilder builder = new System.Text.StringBuilder();
        for (int Index = 0; Index < stack.EffectCount; Index++)
        {
            if (!stack.TryGetEffectAt(Index, out Basis.Cinematics.BasisCameraEffectModifier effect)) continue;
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(BasisLocalization.Get(Basis.Cinematics.BasisCameraModifiers.NameKey(effect)));
        }
        return builder.Length > 0 ? builder.ToString() : "-";
    }

    private static string PinSpaceLabel(int pinSpace)
    {
        switch (pinSpace)
        {
            case (int)BasisHandHeldCameraInteractable.CameraPinSpace.HandHeld:
                return BasisLocalization.Get("camera.anchor.hand");
            case (int)BasisHandHeldCameraInteractable.CameraPinSpace.PlaySpace:
                return BasisLocalization.Get("camera.anchor.playspace");
            case (int)BasisHandHeldCameraInteractable.CameraPinSpace.Attached:
                return BasisLocalization.Get("camera.anchor.attached");
            default:
                return BasisLocalization.Get("camera.anchor.world");
        }
    }

    private static string DepthModeLabel(int dofMode) =>
        BasisLocalization.Get(dofMode == 1 ? "camera.mode.gaussian" : "camera.mode.bokeh");

    private static string MotionBlurQualityLabel(int quality)
    {
        switch (quality)
        {
            case 0: return BasisLocalization.Get("camera.motionBlurQuality.low");
            case 2: return BasisLocalization.Get("camera.motionBlurQuality.high");
            default: return BasisLocalization.Get("camera.motionBlurQuality.medium");
        }
    }

    private static string MotionBlurModeLabel(int mode) =>
        BasisLocalization.Get(mode == 1 ? "camera.motionBlurMode.cameraAndObjects" : "camera.motionBlurMode.cameraOnly");

    private static string TonemappingLabel(int mode)
    {
        switch (mode)
        {
            case 0: return BasisLocalization.Get("camera.tonemapping.none");
            case 1: return BasisLocalization.Get("camera.tonemapping.neutral");
            default: return BasisLocalization.Get("camera.tonemapping.aces");
        }
    }

    private static string DetachedMarkerLabel(int marker)
    {
        switch ((BasisCameraDetachedMarker)marker)
        {
            case BasisCameraDetachedMarker.Off: return BasisLocalization.Get("ui.option.off");
            case BasisCameraDetachedMarker.Gizmo: return BasisLocalization.Get("camera.detachedMarker.wireframe");
            default: return BasisLocalization.Get("camera.detachedMarker.puck");
        }
    }

    private static string GiModeLabel(int mode) =>
        BasisLocalization.Get(mode == 1 ? "settings.graphics.gi.mode.rayTraced" : "settings.graphics.gi.mode.screenSpace");

    private static string GiLayersLabel(int layers)
    {
        switch (layers)
        {
            case 0: return BasisLocalization.Get("settings.graphics.gi.layers.avatars");
            case 1: return BasisLocalization.Get("settings.graphics.gi.layers.world");
            default: return BasisLocalization.Get("settings.graphics.gi.layers.worldAndAvatars");
        }
    }

    private static string GiSkinnedMeshesLabel(int mode) =>
        BasisLocalization.Get(mode == 0 ? "settings.graphics.gi.skinned.off" : "settings.graphics.gi.skinned.proxy");

    private static string GiQualityLabel(int quality)
    {
        switch (quality)
        {
            case 0: return BasisLocalization.Get("settings.graphics.quality.low");
            case 2: return BasisLocalization.Get("settings.graphics.quality.high");
            case 3: return BasisLocalization.Get("settings.graphics.quality.ultra");
            default: return BasisLocalization.Get("settings.graphics.quality.medium");
        }
    }

    private static string GiFallbackLabel(int fallback)
    {
        switch (fallback)
        {
            case 0: return BasisLocalization.Get("settings.graphics.gi.fallback.none");
            case 1: return BasisLocalization.Get("settings.graphics.gi.fallback.sky");
            default: return BasisLocalization.Get("settings.graphics.gi.fallback.probe");
        }
    }

    private static string RtaoModeLabel(int mode) =>
        BasisLocalization.Get(mode == 1 ? "settings.graphics.rtao.mode.rayTraced" : "settings.graphics.rtao.mode.screenSpace");

    private static string RtaoApplyModeLabel(int mode) =>
        BasisLocalization.Get(mode == 1 ? "settings.graphics.rtao.apply.finalImage" : "settings.graphics.rtao.apply.lighting");

    private static string RtaoDenoiseLabel(int passes)
    {
        switch (passes)
        {
            case 0: return BasisLocalization.Get("ui.option.off");
            case 1: return BasisLocalization.Get("settings.graphics.rtao.denoise.standard");
            case 3: return BasisLocalization.Get("settings.graphics.rtao.denoise.maximum");
            default: return BasisLocalization.Get("settings.graphics.rtao.denoise.high");
        }
    }

    private static string RtaoLayersLabel(int layers)
    {
        switch (layers)
        {
            case 1: return BasisLocalization.Get("settings.graphics.rtao.layers.world");
            case 2: return BasisLocalization.Get("settings.graphics.rtao.layers.worldAndAvatars");
            default: return BasisLocalization.Get("settings.graphics.rtao.layers.avatars");
        }
    }

    private static string RtaoSkinnedMeshesLabel(int mode) =>
        BasisLocalization.Get(mode == 1 ? "settings.graphics.rtao.skinned.proxy" : "settings.graphics.rtao.skinned.off");

    private static string BackgroundModeLabel(int mode)
    {
        switch ((BasisCameraBackgroundMode)mode)
        {
            case BasisCameraBackgroundMode.GreenScreen: return BasisLocalization.Get("camera.background.greenScreen");
            case BasisCameraBackgroundMode.BlueScreen: return BasisLocalization.Get("camera.background.blueScreen");
            case BasisCameraBackgroundMode.Black: return BasisLocalization.Get("camera.background.black");
            case BasisCameraBackgroundMode.White: return BasisLocalization.Get("camera.background.white");
            case BasisCameraBackgroundMode.Magenta: return BasisLocalization.Get("camera.background.magenta");
            case BasisCameraBackgroundMode.Custom: return BasisLocalization.Get("camera.background.custom");
            case BasisCameraBackgroundMode.Transparent: return BasisLocalization.Get("camera.background.transparent");
            default: return BasisLocalization.Get("camera.background.world");
        }
    }
}
