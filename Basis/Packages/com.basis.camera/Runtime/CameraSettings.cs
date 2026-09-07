using System;
using Basis;
using Basis.Cinematics;
using UnityEngine;

public partial class BasisHandHeldCameraUI
{
    [Serializable]
    public class CameraSettings
    {
        /// <summary>
        /// Bumped whenever fields are added whose zero-fill value (JsonUtility leaves absent fields
        /// at 0/false) differs from their intended default. LoadSettings migrates older files.
        /// v2 added the auto-follow config, capture toggles and MSAA. v9 replaced the auto-follow
        /// block and the shot list with the modifier stack. v10 added the camera body. v11 added
        /// the film grading — grain shape, halation tint, vignette colour, split toning and lift.
        /// v12 added the Aim Along Track block.
        /// </summary>
        public const int CurrentVersion = 12;
        public int settingsVersion = CurrentVersion;

        public CameraSettings()
        {
            settingsVersion = CurrentVersion;

            cameraMode = (int)BasisCameraMode.Photo;

            // A digital camera with a full load and its flash armed. The load matters: JsonUtility
            // builds the object through this constructor before filling it, so a file written
            // before bodies existed arrives with a fresh roll rather than an empty one — which is
            // the difference between an older camera loading normally and one that will not fire.
            cameraBody = (int)BasisCameraBodyKind.Digital;
            exposuresRemaining = BasisHandHeldCamera.FullRoll;
            flashEnabled = true;

            backgroundMode = 0;
            backgroundCustomColor = BasisHandHeldCamera.ChromaGreen;
            backgroundKeepsWorld = false;

            modifiers = new BasisCameraModifierStack();
            detachedMarker = (int)BasisCameraDetachedMarker.Puck;
            // Defaulted here rather than migrated: a file written before the marker could be
            // resized has no field to read, and JsonUtility leaves it holding this rather than
            // zeroing it — which is the difference between the shipped marker and no marker at all.
            detachedMarkerScale = 1f;

            dofMode = 2;          // Bokeh, matching the authored profile
            dofFocalLength = 50f;
            dofBladeCount = 5;

            resolutionIndex = 1;
            formatIndex = 0;
            apertureIndex = 0;
            shutterSpeedIndex = 0;
            isoIndex = 0;
            fov = 40;
            focusDistance = 10f;
            sensorSizeX = 36f;
            sensorSizeY = 24f;
            bloomIntensity = 0.5f;
            bloomThreshold = 0.5f;

            // URP's own defaults for the parts of an effect that shape it rather than switch it on.
            // Set here rather than left to the zero fill, so a file written before they existed
            // loads the look it was saved with instead of a hard-edged bloom and a flat vignette.
            bloomScatter = 0.7f;
            vignetteSmoothness = 0.2f;

            // The film grading, at the values that mean "leave the picture alone". None of these is
            // its own zero: grain falls back to the thinnest texture rather than to none, bloom and
            // the vignette tint to white and black rather than to nothing, and split toning is
            // neutral at GREY at both ends — a black shadow colour is the strongest shift there is,
            // not the absence of one, which is the trap in defaulting a colour to default(Color).
            filmGrainType = 0;                      // Thin1
            filmGrainResponse = 0.8f;               // URP's own default: grain backs off in highlights
            bloomTint = Color.white;
            vignetteColour = Color.black;
            vignetteRounded = false;
            splitToningShadows = Color.grey;
            splitToningHighlights = Color.grey;
            splitToningBalance = 0f;
            filmLift = 0f;
            lensDistortionScale = 1f;
            paniniCropToFit = 1f;
            captureTonemapping = (int)UnityEngine.Rendering.Universal.TonemappingMode.ACES;
            contrast = 1f;
            saturation = 1f;
            depthAperture = 2.8f;
            depthFocusDistance = 10f;
            depthIsActive = false;
            useManualFocus = true;
            showExposureOnCamera = false;

            // Off, but with a usable sensitivity already set, so the file a camera loads on the day
            // the feature arrives is not one that switches it on at the least sensitive end.
            focusPeaking = false;
            focusPeakingSensitivity = BasisHandHeldCamera.DefaultFocusPeakingSensitivity;
            focusPeakingColour = 0;
            focusPeakingGreyPicture = false;

            // Same shape again: off, but already holding an opacity that reads, so the file a
            // camera loads on the day the grid arrives is not one that switches it on at the
            // faintest setting there is.
            viewfinderGrid = false;
            viewfinderGridPattern = (int)BasisCameraGridPattern.Thirds;
            viewfinderGridOpacity = BasisHandHeldCamera.DefaultGridOpacity;

            // Same again for the meter: off, but already set up to behave the moment it is on.
            autoBrightness = false;
            autoBrightnessTarget = BasisHandHeldCamera.DefaultBrightnessTarget;
            autoBrightnessSpeed = BasisHandHeldCamera.DefaultBrightnessSpeed;
            autoBrightnessMetering = (int)BasisCameraMeteringMode.CentreWeighted;
            autoBrightnessRange = BasisHandHeldCamera.DefaultBrightnessRange;

            // Off by default (a still photo of a moving world is not usually what is wanted), but
            // with the shape of the effect already sane for the moment it is switched on.
            motionBlurIntensity = 0f;
            motionBlurClamp = 0.05f;
            motionBlurQuality = 1;   // Medium
            motionBlurMode = 0;      // Camera only — no motion vector pass

            overrideVolumetricFog = false;
            VolumetricFogVolumedensity = 0.01f;
            VolumetricFogenableAPVContribution = true;
            VolumetricFogenableMainLightContribution = true;

            msaaSamples = 2;

            smoothDragPositionDamping = 0.4f;
            smoothDragRotationDamping = 0.5f;
            smoothDragMaxDistance = 0.25f;

            vrStabilizationPositionDamping = 0.2f;
            vrStabilizationYawDamping = 0.9f;
            vrStabilizationPitchDamping = 0.9f;
            vrStabilizationRollDamping = 0.9f;
            zoomStabilization = true;
            zoomStabilizationResponse = 1f;
            zoomStabilizationMinScale = 0.35f;
            zoomStabilizationMaxScale = 4f;

            flySpeed = 2f;
            flyClimbSpeed = 2f;
            flyFastMultiplier = 3f;
            flyTurnSpeed = 90f;
            flyMouseSensitivity = 0.5f;
            flyMomentum = true;
            flyMovementFollowsPitch = true;

            vrHandFlyMoveDeadzone = 0.02f;
            vrHandFlyMoveReach = 0.25f;
            vrHandFlyMoveSensitivity = 1f;
            vrHandFlyTurnDeadzone = 4f;
            vrHandFlyTurnReach = 45f;
            vrHandFlyTurnSensitivity = 1f;

            gifDurationSeconds = 5f;
            gifFrameRate = 15;
            gifWidth = 480;
            gifLoop = true;
            gifDither = true;

            videoDurationSeconds = 30f;
            videoFrameRate = 30;
            videoWidth = 1920;
            videoQuality = 80;
            videoTimeLimit = true;
            videoContinuousClips = false;

            photogrammetryDistanceMeters = 0.3f;
            photogrammetryAngleDegrees = 15f;
            photogrammetryWidth = 1280;

            streamTransport = (int)(BasisHandHeldCamera.IsVideoOutputSupported ? BasisVideoTransport.Platform : BasisVideoTransport.Web);
            streamWidth = BasisVideoOutputSettings.DefaultWidth;
            streamHeight = BasisVideoOutputSettings.DefaultHeight;
            streamFrameRate = BasisVideoOutputSettings.DefaultFrameRate;
            streamQuality = BasisVideoOutputSettings.DefaultWebQuality;
            streamPort = BasisVideoOutputSettings.DefaultWebPort;
            streamSenderName = BasisVideoOutputSettings.DefaultSenderName;

            // Global Illumination photo overrides. Off, but matching the live BasisSettingsDefaults
            // GI defaults for every value field underneath — so turning the override on for the
            // first time starts from what the player already sees live, not a jarring difference,
            // and a camera that never touches these controls behaves exactly as before they existed.
            overrideGlobalIllumination = false;
            giMode = 0;               // Screen Space
            giSkinnedMeshes = 1;      // Proxy
            giLayers = 2;             // World And Avatars
            giQuality = 1;            // Medium
            giFallback = 2;           // Reflection Probe
            giIgnoreBakedEmission = false;
            giIntensity = 1f;
            giSaturation = 1f;
            giObscurance = 0.5f;
            giRayLength = 16f;
            giSmoothing = 1f;
            giWideBlur = true;
            giRayReuse = true;
            giEmitters = true;
            giEmitterIntensity = 3f;
            giSpecular = false;
            giObscuranceRadius = 0.5f;
            giFadeDistance = 120f;
            giNormalBias = 0.02f;
            giDistanceBias = 0.0015f;
            giBounceThreshold = 0.02f;
            giFireflyClamp = 6f;
            giReflectionProbes = false;
            giMirrors = true;

            // Ray Traced Ambient Occlusion photo overrides. Off, but matching the live
            // BasisSettingsDefaults RTAO defaults underneath, for the same reason the Global
            // Illumination ones do above.
            overrideRTAO = false;
            rtaoMode = 0;              // Screen Space
            rtaoIntensity = 1f;
            rtaoRadius = 0.02f;
            rtaoApplyMode = 0;         // Lighting
            rtaoDenoisePasses = 2;     // High
            rtaoDirectStrength = 0.5f;
            rtaoLayers = 0;            // Avatars
            rtaoSkinnedMeshes = 1;     // Proxy
            rtaoNormalBias = 0.005f;
            rtaoDistanceBias = 0.0005f;
            rtaoFalloff = 1f;
            rtaoPower = 1f;
            rtaoFadeStart = 40f;
            rtaoFadeEnd = 60f;
            rtaoSpecularRelief = 0f;
        }

        /// <summary>
        /// The <see cref="BasisCameraMode"/> the camera was last in. Restored on load and then
        /// immediately re-derived from the values that loaded alongside it, so a file that no
        /// longer matches the mode it names settles on Custom instead of mislabelling itself.
        /// </summary>
        public int cameraMode;

        /// <summary>
        /// The physical camera, as <see cref="BasisCameraBodyKind"/>. Saved separately from
        /// <see cref="cameraMode"/> because it outlives it: touch one slider on a disposable and
        /// the mode is Custom, and you are still holding a disposable.
        /// </summary>
        public int cameraBody;

        /// <summary>
        /// Frames left on the load, or <see cref="BasisHandHeldCamera.FullRoll"/> for a fresh one.
        /// Saved because a disposable that refilled itself every session would never run out at
        /// all, which is most of what makes it a disposable.
        /// </summary>
        public int exposuresRemaining;

        /// <summary>Whether the flash is armed. Ignored on a body with nothing on the front.</summary>
        public bool flashEnabled;

        public int resolutionIndex = 1;
        public int formatIndex = 0;
        public int msaaSamples = 2;

        public int apertureIndex;
        public int shutterSpeedIndex;
        public int isoIndex;

        public int exposureIndex = 6;

        /// <summary>Whether the exposure slider is shown on the camera's own interface. Off unless turned on from the camera panel.</summary>
        public bool showExposureOnCamera = false;


        public float fov;
        public float focusDistance;
        public float sensorSizeX;
        public float sensorSizeY;

        public float bloomIntensity;
        public float bloomThreshold;

        public float contrast;
        public float saturation;
        public float hueShift;

        public float depthAperture;
        public float depthFocusDistance;
        public bool depthIsActive;
        public int dofMode;
        public float dofFocalLength;
        public int dofBladeCount;

        public bool useManualFocus = true;

        /// <summary>
        /// The viewfinder focus aid. A view preference rather than part of the shot — the overlay
        /// is produced into a texture of its own that no capture path reads — but it is saved for
        /// the same reason the detached marker is: it is a control with nowhere else to be
        /// remembered, and one that resets every session is one nobody leaves on.
        /// </summary>
        public bool focusPeaking;
        public float focusPeakingSensitivity;
        /// <summary>Index into <see cref="BasisHandHeldCamera.FocusPeakingColours"/>; 0 is red.</summary>
        public int focusPeakingColour;
        public bool focusPeakingGreyPicture;

        /// <summary>
        /// The viewfinder alignment grid, saved for the reason focus peaking is: a view preference
        /// that no capture path can reach, but one with nowhere else to be remembered.
        /// </summary>
        public bool viewfinderGrid;
        /// <summary>Which grid, as <see cref="BasisCameraGridPattern"/>; 0 is the rule of thirds.</summary>
        public int viewfinderGridPattern;
        public float viewfinderGridOpacity;

        /// <summary>
        /// Auto brightness. The stops the meter is currently adding are deliberately not saved:
        /// they describe the room the camera was last in, and a file that restored them would open
        /// every session mis-exposed until the loop had walked it back.
        /// </summary>
        public bool autoBrightness;
        public float autoBrightnessTarget;
        public float autoBrightnessSpeed;
        /// <summary>Which part of the frame is metered, as <see cref="BasisCameraMeteringMode"/>.</summary>
        public int autoBrightnessMetering;
        public float autoBrightnessRange;

        /// <summary>Uses this camera's volumetric-fog profile instead of the world's fog.</summary>
        public bool overrideVolumetricFog;
        public float VolumetricFogVolumedensity;
        public bool VolumetricFogenableAPVContribution;
        public bool VolumetricFogenableMainLightContribution;

        // Extra post-processing (0 = effect off, so a fresh install adds nothing to the shot).
        public float vignette;
        public float chromaticAberration;
        public float filmGrain;
        public float whiteBalanceTemperature;
        public float whiteBalanceTint;
        public float lensDistortion;
        public float paniniDistance;

        /// <summary>
        /// Which grain texture is used, as <c>FilmGrainLookup</c>. Size rather than strength — the
        /// difference between the grain of a fast negative and digital noise is how big it is.
        /// </summary>
        public int filmGrainType;

        /// <summary>How far the grain backs off in the highlights. 0 lays it evenly; 1 confines it to the shadows.</summary>
        public float filmGrainResponse;

        /// <summary>
        /// The colour of the glow around a highlight. Orange on film, where it is halation rather
        /// than bloom; white on anything with a sensor.
        /// </summary>
        public Color bloomTint;

        /// <summary>What the corners darken toward, and whether they do it in a circle or with the frame.</summary>
        public Color vignetteColour;
        public bool vignetteRounded;

        /// <summary>
        /// The two ends of the colour split, and where the split falls. Neutral is grey at both
        /// ends; this is the control that makes a stock rather than a filter.
        /// </summary>
        public Color splitToningShadows;
        public Color splitToningHighlights;
        public float splitToningBalance;

        /// <summary>
        /// The black point, raised. Stored as the flat offset rather than as the whole lift trackball
        /// — the colour half is held at neutral so this cannot fight the split toning above it.
        /// </summary>
        public float filmLift;

        /// <summary>
        /// Shape, as opposed to strength. Each belongs to an effect whose own slider is the on/off,
        /// so these carry usable values even while the effect they shape is switched off.
        /// </summary>
        public float bloomScatter;
        public float vignetteSmoothness;
        public float lensDistortionScale;
        public float paniniCropToFit;

        /// <summary>
        /// Which tonemapper grades the saved photo, as <c>TonemappingMode</c>. The preview is always
        /// Neutral — the capture is rendered at a different resolution and exposure and has always
        /// been graded on its own — so this is the still's look, not the viewfinder's.
        /// </summary>
        public int captureTonemapping;

        /// <summary>
        /// Motion blur. The strength is the on/off — URP only runs the pass above zero — so the
        /// shape settings below carry usable values even in a file that has the effect switched
        /// off, and no migration is needed: JsonUtility leaves a field absent from an older file
        /// holding the constructor default rather than zeroing it.
        /// </summary>
        public float motionBlurIntensity;
        public float motionBlurClamp;
        /// <summary>0 = Low, 1 = Medium, 2 = High.</summary>
        public int motionBlurQuality;
        /// <summary>0 = camera movement only, 1 = camera and moving objects.</summary>
        public int motionBlurMode;

        public bool autoFocusFollowSubject;

        /// <summary>
        /// How the camera is driven, including who it films. Which modifiers are fitted is saved
        /// in full: unlike the auto follow flag it replaced, an empty stack is the resting state,
        /// so a restored file can only ever fly the camera off on spawn if that is what was
        /// actually saved. The follow target itself is per-session and not persisted.
        /// </summary>
        public BasisCameraModifierStack modifiers = new BasisCameraModifierStack();


        /// <summary>
        /// Which marker shows where the camera has gone while it is detached, as
        /// <see cref="BasisCameraDetachedMarker"/>. A view preference like the follow framing
        /// around it, not part of the shot — but it was the only control in the Follow section
        /// with nowhere to be saved, so it reset to Puck every session.
        /// </summary>
        public int detachedMarker;

        /// <summary>
        /// How big that marker is drawn, as a ratio of its natural size — the two-hand resize on
        /// the puck and the panel's own slider both write here. Defaulted in the constructor rather
        /// than migrated: an older file has no field to read and arrives holding that default, and
        /// the zero fill would be a marker with no size at all.
        /// </summary>
        public float detachedMarkerScale;

        /// <summary>
        /// Whether a detached camera turned back toward you puts its feed up in front of it. Off is
        /// the zero fill and is what shipped before it existed, so an older file loads as the
        /// camera it was saved as and no version bump is owed.
        /// </summary>
        public bool puckLookAtPreview;

        /// <summary>
        /// Whether a playspace anchor rides your body rather than your playspace origin. Off is the
        /// zero fill and is the steadier of the two, so an older file loads as the playspace anchor
        /// it was written as. Which anchor is selected is deliberately not saved, for the same
        /// reason a fitted follow was not: a camera that restored bolted to a vehicle from the last
        /// world has nothing to be bolted to in this one.
        /// </summary>
        public bool anchorFollowsBody;

        // Capture-mode toggles.
        public bool capture360;
        public bool useAutoLeveling;

        /// <summary>
        /// Whether the detached camera rolls with the grip flying it. Off is the zero fill and the
        /// intended default — a shot that stays level however the puck is turned — so a file from
        /// before this existed loads that way and no version bump is owed, even though the grip
        /// used to roll the camera freely.
        /// </summary>
        public bool cameraRoll;

        public bool useVRHandheldSmoothing;
        public float vrStabilizationPositionDamping;
        public float vrStabilizationYawDamping;
        public float vrStabilizationPitchDamping;
        public float vrStabilizationRollDamping;

        /// <summary>
        /// Stabilization follows the zoom. On is the default and defaulted in the constructor rather
        /// than migrated, so a file written before the lens drove it loads holding the shape the
        /// camera ships with rather than the zero fill, which would be no link at all.
        /// </summary>
        public bool zoomStabilization;
        public float zoomStabilizationResponse;
        public float zoomStabilizationMinScale;
        public float zoomStabilizationMaxScale;

        /// <summary>
        /// The held camera trails the hand instead of being locked to it. Off is the zero fill, so
        /// an older file loads as the rigid hold it was written as, and the three numbers below it
        /// are defaulted in the constructor rather than migrated.
        /// </summary>
        public bool useSmoothDrag;
        public float smoothDragPositionDamping;
        public float smoothDragRotationDamping;
        public float smoothDragMaxDistance;
        public float flySpeed;
        public float flyClimbSpeed;
        public float flyFastMultiplier;
        public float flyTurnSpeed;
        public float flyMouseSensitivity;

        /// <summary>
        /// On, releasing the fly controls coasts the camera to a stop; off, it stops dead. Defaulted
        /// in the constructor rather than migrated, so a file written before it existed still glides.
        /// </summary>
        public bool flyMomentum;

        /// <summary>
        /// On (the fixed behaviour), VR fly's forward/strafe follow wherever the lens is aimed,
        /// pitch included. Off restores the earlier level-only glide. Defaulted in the constructor
        /// rather than migrated, so a file written before this existed still gets the fix rather than
        /// silently reverting to the old behaviour it never asked to keep.
        /// </summary>
        public bool flyMovementFollowsPitch;

        /// <summary>
        /// On, the main menu's hotbar carries a fly switch, so flight can be armed and landed
        /// without opening this panel. Off is the zero fill, so an older file loads without it.
        /// </summary>
        public bool showFlyOnMainMenu;

        /// <summary>
        /// On, the left hand's own tracked position and rotation fly the camera directly while in
        /// VR flight, in place of the left stick. Off is the zero fill, so an older file loads with
        /// the stick still in charge.
        /// </summary>
        public bool vrLeftHandFlyEnabled;

        /// <summary>
        /// On, the right hand's own tracked rotation turns the camera while in VR flight, in place
        /// of the right stick's yaw/pitch. Off is the zero fill, so an older file loads with the
        /// stick still in charge.
        /// </summary>
        public bool vrRightHandFlyRotateEnabled;

        /// <summary>
        /// Shape of the hand-fly deadzone→reach response curve and its overall gain — see the
        /// matching fields on <see cref="BasisHandHeldCameraInteractable"/>. All defaulted in the
        /// constructor rather than migrated, so a file written before they existed still gets a
        /// working curve instead of a zero-fill reach/sensitivity that would make hand-fly inert.
        /// </summary>
        public float vrHandFlyMoveDeadzone;
        public float vrHandFlyMoveReach;
        public float vrHandFlyMoveSensitivity;
        public float vrHandFlyTurnDeadzone;
        public float vrHandFlyTurnReach;
        public float vrHandFlyTurnSensitivity;
        public bool resizeWithGesture;

        // GIF recording. Every default is set in the constructor, so an older file that lacks
        // them loads the intended values without a migration.
        public float gifDurationSeconds;
        public int gifFrameRate;
        public int gifWidth;
        public bool gifLoop;
        public bool gifDither;

        // Video recording (MJPEG AVI), defaulted the same way.
        public float videoDurationSeconds;
        public int videoFrameRate;
        public int videoWidth;
        public int videoQuality;

        /// <summary>On, a recording stops itself after <see cref="videoDurationSeconds"/>; off, it runs until stopped.</summary>
        public bool videoTimeLimit;

        /// <summary>
        /// With a time limit set, on: reaching it starts the next clip instead of ending the
        /// recording, which then runs until it is stopped by hand. Off is the zero fill, so an
        /// older file loads as the single-clip recording it was written as.
        /// </summary>
        public bool videoContinuousClips;

        // Photogrammetry capture: a still every time the camera moves or turns past these
        // thresholds, at this width. Defaulted the same way as the GIF/video fields above.
        public float photogrammetryDistanceMeters;
        public float photogrammetryAngleDegrees;
        public int photogrammetryWidth;

        public int streamTransport;
        public int streamWidth;
        public int streamHeight;
        public float streamFrameRate;
        public int streamQuality;
        public int streamPort;
        public string streamSenderName;

        /// <summary>
        /// Direct To Screen: the feed drawn over the game window in place of the headset mirror
        /// while the operator is in VR. Saved because it is a way of working rather than a shot —
        /// a streamer who films from the headset wants the monitor to be the camera every session
        /// — and off is the zero fill, so an older file loads with the window left alone.
        /// </summary>
        public bool directToScreen;

        /// <summary>
        /// Whether each saved photo is also printed into the world as a shared image pickup,
        /// exactly as if its file had been drag-and-dropped onto the window.
        /// </summary>
        public bool printPhoto;

        // Background. Mode 0 is World, so a zero-filled old file keeps the world background.
        public int backgroundMode;
        public Color backgroundCustomColor;
        public bool backgroundKeepsWorld;

        /// <summary>
        /// Whether this camera's photo captures substitute the 24 fields below for the player's own
        /// live Global Illumination settings. Off by default, so an old file — and a fresh camera —
        /// behaves exactly as it did before this existed: a capture uses whatever GI the player has
        /// live, same as every other camera. Unconditional (no platform guard) like every other
        /// field here, even though only a <c>BASIS_HAS_GI</c> build ever reads it, so the file still
        /// round-trips on a platform that compiles GI out.
        /// </summary>
        public bool overrideGlobalIllumination;

        /// <summary>Index into <c>SMModuleGlobalIlluminationURP.ModeOptions</c> (Screen Space / Ray Traced).</summary>
        public int giMode;
        /// <summary>Index into <c>SMModuleGlobalIlluminationURP.SkinnedMeshesOptions</c> (Off / Proxy). Ray traced only.</summary>
        public int giSkinnedMeshes;
        /// <summary>Index into <c>SMModuleGlobalIlluminationURP.LayersOptions</c> (Avatars / World / World And Avatars). Ray traced only.</summary>
        public int giLayers;
        /// <summary>Index into <c>SMModuleGlobalIlluminationURP.QualityOptions</c> (Low / Medium / High / Ultra).</summary>
        public int giQuality;
        /// <summary>Index into <c>SMModuleGlobalIlluminationURP.FallbackOptions</c> (None / Sky / Reflection Probe).</summary>
        public int giFallback;
        /// <summary>Ray traced only — lets a baked-emissive surface's light back into the gather.</summary>
        public bool giIgnoreBakedEmission;
        public float giIntensity;
        public float giSaturation;
        public float giObscurance;
        public float giRayLength;
        public float giSmoothing;
        public bool giWideBlur;
        /// <summary>Screen space only.</summary>
        public bool giRayReuse;
        public bool giEmitters;
        public float giEmitterIntensity;
        /// <summary>Ray traced reflections. Independent of <see cref="giMode"/> by design.</summary>
        public bool giSpecular;
        public float giObscuranceRadius;
        public float giFadeDistance;
        /// <summary>Ray traced only.</summary>
        public float giNormalBias;
        /// <summary>Ray traced only.</summary>
        public float giDistanceBias;
        /// <summary>Ray traced only.</summary>
        public float giBounceThreshold;
        public float giFireflyClamp;
        public bool giReflectionProbes;
        public bool giMirrors;

        /// <summary>
        /// Whether this camera's photo captures substitute the 15 fields below for the player's own
        /// live Ray Traced Ambient Occlusion settings. Off by default, mirroring
        /// <see cref="overrideGlobalIllumination"/> exactly - same reasoning, same shape.
        /// </summary>
        public bool overrideRTAO;

        /// <summary>0 = Screen Space, 1 = Ray Traced.</summary>
        public int rtaoMode;
        public float rtaoIntensity;
        public float rtaoRadius;
        /// <summary>0 = Lighting, 1 = Final Image.</summary>
        public int rtaoApplyMode;
        /// <summary>0-3, matching <c>BasisRTAOSettingsMap.ReadDenoisePasses</c> (Off/Standard/High/Maximum).</summary>
        public int rtaoDenoisePasses;
        /// <summary>Only matters when <see cref="rtaoApplyMode"/> is Lighting.</summary>
        public float rtaoDirectStrength;
        /// <summary>0 = Avatars, 1 = World, 2 = World And Avatars. Ray traced only.</summary>
        public int rtaoLayers;
        /// <summary>0 = Off, 1 = Proxy. Ray traced only.</summary>
        public int rtaoSkinnedMeshes;
        /// <summary>Ray traced only.</summary>
        public float rtaoNormalBias;
        /// <summary>Ray traced only.</summary>
        public float rtaoDistanceBias;
        public float rtaoFalloff;
        public float rtaoPower;
        public float rtaoFadeStart;
        public float rtaoFadeEnd;
        public float rtaoSpecularRelief;
    }
}
