using System;
using System.Collections.Generic;
using Basis.Cinematics;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityCamera = UnityEngine.Camera;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// A handheld camera assembled far enough to exercise its settings: a capture camera, the full
    /// set of post-processing overrides the UI writes to, and the prop's own sliders with the ranges
    /// <c>SetupSliderRanges</c> gives them.
    ///
    /// <para>
    /// The prefab's sliders are part of the rig on purpose. <c>CreateCurrentCameraSettings</c> reads
    /// several values back off those widgets rather than off the camera, so a rig without them would
    /// quietly test a code path the shipping prefab never takes.
    /// </para>
    ///
    /// <para>
    /// Awake never runs here — outside play mode Unity does not call it for a plain MonoBehaviour —
    /// so the component arrives with its field initializers applied and no scene dependencies.
    /// Anything <c>Initialize</c> would have wired is wired here instead, explicitly.
    /// </para>
    /// </summary>
    internal sealed class BasisCameraSettingsRig : IDisposable
    {
        public readonly BasisHandHeldCamera Camera;
        public readonly BasisHandHeldCameraUI UI;
        public readonly UnityCamera CaptureCamera;

        public readonly DepthOfField DepthOfField;
        public readonly Bloom Bloom;
        public readonly ColorAdjustments ColorAdjustments;
        public readonly Vignette Vignette;
        public readonly ChromaticAberration ChromaticAberration;
        public readonly FilmGrain FilmGrain;
        public readonly WhiteBalance WhiteBalance;
        public readonly LensDistortion LensDistortion;
        public readonly MotionBlur MotionBlur;
        public readonly PaniniProjection PaniniProjection;
        public readonly SplitToning SplitToning;
        public readonly LiftGammaGain LiftGammaGain;

        public readonly Slider FovSlider;
        public readonly Slider ExposureSlider;
        public readonly Slider ApertureSlider;
        public readonly Slider FocusSlider;

        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<ScriptableObject> _assets = new List<ScriptableObject>();

        public BasisCameraSettingsRig()
        {
            GameObject root = NewObject("HandHeldCameraUnderTest");
            Camera = root.AddComponent<BasisHandHeldCamera>();
            // The interactable half of the camera reaches the camera half through a serialized
            // back-reference that the prefab supplies. Several settings — the field of view among
            // them — go nowhere without it, so the rig has to wire it the way the prefab does.
            Camera.HHC = Camera;

            GameObject captureGo = NewObject("CaptureCamera");
            CaptureCamera = captureGo.AddComponent<UnityCamera>();
            captureGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Camera.captureCamera = CaptureCamera;

            DepthOfField = NewOverride<DepthOfField>();
            Bloom = NewOverride<Bloom>();
            ColorAdjustments = NewOverride<ColorAdjustments>();
            Vignette = NewOverride<Vignette>();
            ChromaticAberration = NewOverride<ChromaticAberration>();
            FilmGrain = NewOverride<FilmGrain>();
            WhiteBalance = NewOverride<WhiteBalance>();
            LensDistortion = NewOverride<LensDistortion>();
            MotionBlur = NewOverride<MotionBlur>();
            PaniniProjection = NewOverride<PaniniProjection>();
            SplitToning = NewOverride<SplitToning>();
            LiftGammaGain = NewOverride<LiftGammaGain>();

            BasisHandHeldCameraMetaData metaData = Camera.MetaData;
            metaData.depthOfField = DepthOfField;
            metaData.bloom = Bloom;
            metaData.colorAdjustments = ColorAdjustments;
            metaData.vignette = Vignette;
            metaData.chromaticAberration = ChromaticAberration;
            metaData.filmGrain = FilmGrain;
            metaData.whiteBalance = WhiteBalance;
            metaData.lensDistortion = LensDistortion;
            metaData.motionBlur = MotionBlur;
            metaData.paniniProjection = PaniniProjection;
            metaData.splitToning = SplitToning;
            metaData.liftGammaGain = LiftGammaGain;

            // Mirrors CachePostProcessingReferences: colour grading is always live, the added
            // effects start switched off so an unconfigured one never alters the shot.
            ColorAdjustments.active = true;
            Vignette.active = false;
            ChromaticAberration.active = false;
            FilmGrain.active = false;
            WhiteBalance.active = false;
            LensDistortion.active = false;
            MotionBlur.active = false;
            PaniniProjection.active = false;

            // Ranges copied from SetupSliderRanges. A Slider defaults to 0..1, so without these
            // every value the settings carry would be clamped away on the way in.
            FovSlider = NewSlider("FOV", 20f, 120f);
            ExposureSlider = NewSlider("Exposure", 0f, BasisHandHeldCameraUI.ExposureStopCount - 1);
            ApertureSlider = NewSlider("Aperture", BasisHandHeldCameraUI.MinAperture, BasisHandHeldCameraUI.MaxAperture);
            FocusSlider = NewSlider("Focus", 0.1f, 100f);

            UI = new BasisHandHeldCameraUI
            {
                HHC = Camera,
                FOVSlider = FovSlider,
                ExposureSlider = ExposureSlider,
                DepthApertureSlider = ApertureSlider,
                DepthFocusDistanceSlider = FocusSlider,
            };

            // The back-link the prefab supplies, and the other half of the pair wired above. Without
            // it the camera holds a second, empty UI with no camera and no sliders, so anything that
            // reaches back through the camera to refresh the prop's HUD silently does nothing — and
            // saving harvests the field of view and aperture from those very sliders, so the miss
            // shows up as settings that quietly fail to round-trip.
            Camera.HandHeld = UI;
        }

        /// <summary>
        /// A settings object with every field moved off its default, so a round trip that drops a
        /// field shows up as a mismatch rather than coincidentally matching the default.
        /// </summary>
        public static BasisHandHeldCameraUI.CameraSettings DistinctiveSettings()
        {
            return new BasisHandHeldCameraUI.CameraSettings
            {
                resolutionIndex = 2,
                formatIndex = BasisHandHeldCameraUI.FORMAT_EXR,
                msaaSamples = 8,
                apertureIndex = 3,
                shutterSpeedIndex = 4,
                isoIndex = 5,
                exposureIndex = 9,
                showExposureOnCamera = true,
                fov = 77f,
                focusDistance = 12.5f,
                sensorSizeX = 23.6f,
                sensorSizeY = 15.7f,
                bloomIntensity = 1.75f,
                bloomThreshold = 1.1f,
                contrast = 22f,
                saturation = -14f,
                hueShift = 35f,
                depthAperture = 5.6f,
                depthFocusDistance = 4.25f,
                depthIsActive = true,
                dofMode = 1,
                dofFocalLength = 85f,
                dofBladeCount = 7,
                useManualFocus = false,
                focusPeaking = true,
                focusPeakingSensitivity = 0.72f,
                focusPeakingColour = 2,
                focusPeakingGreyPicture = true,
                viewfinderGrid = true,
                viewfinderGridPattern = (int)BasisCameraGridPattern.GoldenRatio,
                viewfinderGridOpacity = 0.85f,
                autoBrightness = true,
                autoBrightnessTarget = 0.62f,
                autoBrightnessSpeed = 3.4f,
                autoBrightnessMetering = (int)BasisCameraMeteringMode.Spot,
                autoBrightnessRange = 4.5f,
                overrideVolumetricFog = true,
                VolumetricFogVolumedensity = 0.42f,
                VolumetricFogenableAPVContribution = false,
                VolumetricFogenableMainLightContribution = false,
                overrideGlobalIllumination = true,
                giMode = 1,
                giSkinnedMeshes = 0,
                giLayers = 0,
                giQuality = 3,
                giFallback = 0,
                giIgnoreBakedEmission = true,
                giIntensity = 2.4f,
                giSaturation = 1.6f,
                giObscurance = 0.85f,
                giRayLength = 32f,
                giSmoothing = 1.5f,
                giWideBlur = false,
                giRayReuse = false,
                giEmitters = false,
                giEmitterIntensity = 5.5f,
                giSpecular = true,
                giObscuranceRadius = 1.25f,
                giFadeDistance = 200f,
                giNormalBias = 0.18f,
                giDistanceBias = 0.008f,
                giBounceThreshold = 0.15f,
                giFireflyClamp = 12f,
                giReflectionProbes = true,
                giMirrors = false,
                overrideRTAO = true,
                rtaoMode = 1,
                rtaoIntensity = 0.72f,
                rtaoRadius = 0.065f,
                rtaoApplyMode = 1,
                rtaoDenoisePasses = 3,
                rtaoDirectStrength = 0.15f,
                rtaoLayers = 1,
                rtaoSkinnedMeshes = 0,
                rtaoNormalBias = 0.24f,
                rtaoDistanceBias = 0.012f,
                rtaoFalloff = 3.5f,
                rtaoPower = 2.25f,
                rtaoFadeStart = 90f,
                rtaoFadeEnd = 150f,
                rtaoSpecularRelief = 0.6f,
                vignette = 0.35f,
                chromaticAberration = 0.2f,
                filmGrain = 0.15f,
                whiteBalanceTemperature = 18f,
                whiteBalanceTint = -12f,
                lensDistortion = 0.4f,
                lensDistortionScale = 1.35f,
                bloomScatter = 0.45f,
                vignetteSmoothness = 0.65f,
                paniniDistance = 0.55f,
                paniniCropToFit = 0.3f,
                captureTonemapping = (int)UnityEngine.Rendering.Universal.TonemappingMode.Neutral,
                motionBlurIntensity = 0.6f,
                motionBlurClamp = 0.12f,
                motionBlurQuality = 2,
                motionBlurMode = 1,
                autoFocusFollowSubject = true,
                modifiers = DistinctiveModifiers(),
                detachedMarker = (int)BasisCameraDetachedMarker.Gizmo,
                detachedMarkerScale = 1.75f,
                puckLookAtPreview = true,
                anchorFollowsBody = true,
                capture360 = true,
                useAutoLeveling = true,
                cameraRoll = true,
                useVRHandheldSmoothing = true,
                vrStabilizationPositionDamping = 0.55f,
                vrStabilizationYawDamping = 1.25f,
                vrStabilizationPitchDamping = 0.8f,
                vrStabilizationRollDamping = 1.6f,
                zoomStabilization = false,
                zoomStabilizationResponse = 1.75f,
                zoomStabilizationMinScale = 0.6f,
                zoomStabilizationMaxScale = 5.5f,
                useSmoothDrag = true,
                smoothDragPositionDamping = 0.65f,
                smoothDragRotationDamping = 0.85f,
                smoothDragMaxDistance = 0.4f,
                flySpeed = 3.5f,
                flyClimbSpeed = 4.5f,
                flyFastMultiplier = 2.5f,
                flyTurnSpeed = 120f,
                flyMouseSensitivity = 0.8f,
                flyMomentum = false,
                flyMovementFollowsPitch = false,
                showFlyOnMainMenu = true,
                vrLeftHandFlyEnabled = true,
                vrRightHandFlyRotateEnabled = true,
                vrHandFlyMoveDeadzone = 0.05f,
                vrHandFlyMoveReach = 0.4f,
                vrHandFlyMoveSensitivity = 1.5f,
                vrHandFlyTurnDeadzone = 8f,
                vrHandFlyTurnReach = 60f,
                vrHandFlyTurnSensitivity = 1.5f,
                resizeWithGesture = true,
                printPhoto = true,
                gifDurationSeconds = 8f,
                gifFrameRate = 24,
                gifWidth = 640,
                gifLoop = false,
                gifDither = false,
                videoDurationSeconds = 45f,
                videoFrameRate = 24,
                videoWidth = 1280,
                videoQuality = 65,
                videoTimeLimit = false,
                videoContinuousClips = true,
                photogrammetryDistanceMeters = 0.55f,
                photogrammetryAngleDegrees = 22f,
                photogrammetryWidth = 854,
                streamTransport = (int)BasisVideoTransport.Web,
                streamWidth = 2560,
                streamHeight = 1440,
                streamFrameRate = 24f,
                streamQuality = 55,
                streamPort = 9123,
                streamSenderName = "Distinctive Sender",
                directToScreen = true,
                backgroundMode = (int)BasisCameraBackgroundMode.BlueScreen,
                backgroundCustomColor = new Color(0.1f, 0.2f, 0.3f, 1f),
                backgroundKeepsWorld = true,
            };
        }

        /// <summary>
        /// A stack with every block moved off its default, so the persistence walk can tell a
        /// setting that survived a round trip from one that came back as the shipped value.
        /// </summary>
        public static BasisCameraModifierStack DistinctiveModifiers()
        {
            BasisCameraModifierStack stack = new BasisCameraModifierStack
            {
                positionModifier = BasisCameraPositionModifier.Orbit,
                rotationModifier = BasisCameraRotationModifier.Compose,
            };

            stack.follow.positionOffset = new Vector3(1.25f, 0.6f, 2.4f);
            stack.follow.bindingMode = BasisCameraBindingMode.WorldSpace;
            stack.follow.damping = new Vector3(0.11f, 0.22f, 0.33f);
            stack.follow.lateralTracking = 0.8f;
            stack.follow.teleportDistance = 14f;

            stack.framing.directionOffset = new Vector3(2.1f, 0.9f, 3.7f);
            stack.framing.bindingMode = BasisCameraBindingMode.SimpleFollow;
            stack.framing.damping = new Vector3(0.44f, 0.55f, 0.66f);
            stack.framing.screenFraction = 0.42f;
            stack.framing.minDistance = 0.9f;
            stack.framing.maxDistance = 15f;
            stack.framing.usesZoom = true;
            stack.framing.teleportDistance = 16f;

            stack.dolly.position = 3.5f;
            stack.dolly.mode = BasisCameraDollyMode.Play;
            stack.dolly.playing = true;
            stack.dolly.damping = 0.75f;
            stack.dolly.speed = 1.25f;
            stack.dolly.offset = new Vector3(0.2f, 0.3f, 0.4f);

            stack.orbit.heading = 42f;
            stack.orbit.verticalAxis = 0.8f;
            stack.orbit.headingDamping = 0.9f;
            stack.orbit.verticalDamping = 0.7f;
            stack.orbit.followSubjectHeading = false;
            stack.orbit.top = new BasisCameraOrbitRig(2.2f, 1.9f);
            stack.orbit.middle = new BasisCameraOrbitRig(0.4f, 2.4f);
            stack.orbit.bottom = new BasisCameraOrbitRig(-0.9f, 1.7f);

            stack.lookAt.rotationOffset = new Vector3(11f, -23f, 0f);
            stack.lookAt.damping = new Vector3(0.15f, 0.25f, 0.35f);

            stack.compose.rotationOffset = new Vector3(4f, -6f, 2f);
            stack.compose.composer.screenX = 0.35f;
            stack.compose.composer.screenY = 0.65f;
            stack.compose.composer.deadZoneWidth = 0.2f;
            stack.compose.composer.deadZoneHeight = 0.24f;
            stack.compose.composer.softZoneWidth = 0.9f;
            stack.compose.composer.softZoneHeight = 0.95f;
            stack.compose.composer.biasX = 0.12f;
            stack.compose.composer.biasY = -0.08f;
            stack.compose.composer.horizontalDamping = 0.55f;
            stack.compose.composer.verticalDamping = 0.75f;

            stack.matchSubject.rotationOffset = new Vector3(-3f, 8f, 1f);
            stack.matchSubject.damping = new Vector3(0.45f, 0.5f, 0.9f);

            stack.trackAim.rotationOffset = new Vector3(6f, -14f, 3f);
            stack.trackAim.damping = new Vector3(0.18f, 0.28f, 0.38f);

            stack.lookAhead.time = 0.4f;
            stack.lookAhead.limit = 3.5f;

            stack.occlusion.padding = 0.4f;
            stack.occlusion.minDistance = 0.7f;
            stack.occlusion.returnDamping = 0.85f;
            stack.occlusion.probeRadius = 0.2f;

            stack.shake = BasisCameraNoiseSettings.ForProfile(BasisCameraNoiseProfile.Drone);
            stack.shake.amplitudeGain = 1.4f;
            stack.shake.frequencyGain = 0.6f;

            stack.lens.fov = 62f;
            stack.lens.damping = 1.1f;

            stack.steady.smoothing = 0.42f;
            stack.steady.verticalDeadZone = 0.28f;

            stack.collision.radius = 0.33f;
            stack.collision.padding = 0.17f;

            stack.dollyZoom.minFov = 18f;
            stack.dollyZoom.maxFov = 88f;

            stack.rigWeight.responsiveness = 3.5f;
            stack.rigWeight.bounce = 0.7f;

            stack.subject.modifier = BasisCameraSubjectModifier.TargetGroup;
            stack.subject.aimPoint = BasisCameraAimPoint.Head;
            stack.subject.anchorToBody = false;
            stack.subject.aimHeightOffset = -0.35f;
            stack.subject.framingRadius = 0.8f;
            stack.subject.groupIncludesLocal = false;
            stack.subject.fixedPoint = new Vector3(3f, 1.5f, -2f);

            stack.AddEffect(BasisCameraEffectModifier.LookAhead);
            stack.AddEffect(BasisCameraEffectModifier.Shake);
            stack.AddEffect(BasisCameraEffectModifier.SteadySubject);
            stack.AddEffect(BasisCameraEffectModifier.RigWeight);

            return stack;
        }

        private Slider NewSlider(string name, float min, float max)
        {
            GameObject go = NewObject("Slider_" + name, typeof(RectTransform));
            Slider slider = go.AddComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;
            return slider;
        }

        private GameObject NewObject(string name, params Type[] components)
        {
            GameObject go = new GameObject(name, components);
            _objects.Add(go);
            return go;
        }

        private T NewOverride<T>() where T : UnityEngine.Rendering.VolumeComponent
        {
            T component = ScriptableObject.CreateInstance<T>();
            _assets.Add(component);
            return component;
        }

        public void Dispose()
        {
            for (int Index = 0; Index < _objects.Count; Index++)
            {
                if (_objects[Index] != null) UnityEngine.Object.DestroyImmediate(_objects[Index]);
            }
            for (int Index = 0; Index < _assets.Count; Index++)
            {
                if (_assets[Index] != null) UnityEngine.Object.DestroyImmediate(_assets[Index]);
            }
            _objects.Clear();
            _assets.Clear();
        }
    }
}
