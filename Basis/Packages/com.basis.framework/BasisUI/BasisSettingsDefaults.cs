using Basis.Scripts.Networking.Receivers;
using System;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.Settings;
using Basis.Streaming;

namespace Basis.BasisUI
{
    public static class BasisSettingsDefaults
    {
        public static BasisSettingsBinding<float> MainVolume = new("main volume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> MenuVolume = new("menuvolume", new BasisPlatformDefault<float>(75));
        public static BasisSettingsBinding<float> WorldVolume = new("worldvolume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> VoiceVolume = new("voicevolume", new BasisPlatformDefault<float>(75));
        public static BasisSettingsBinding<float> AvatarVolume = new("avatarvolume", new BasisPlatformDefault<float>(75));
        public static BasisSettingsBinding<float> PropVolume = new("propvolume", new BasisPlatformDefault<float>(75));
        public static BasisSettingsBinding<float> MicrophoneVolume = new("microphonevolume", new BasisPlatformDefault<float>(1));

        // ---------------- INTERFACE SOUNDS ----------------
        // Per-event toggles for the UI/interaction sound effects (BasisUISounds).
        public static BasisSettingsBinding<bool> SoundHover = new("soundhover", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> SoundPress = new("soundpress", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> SoundGrab = new("soundgrab", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> SoundChat = new("soundchat", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> SoundMicrophone = new("soundmicrophone", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> SoundMicrophonePushToTalk = new("soundmicrophonepushtotalk", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> SoundCamera = new("soundcamera", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<float> ControllerDeadZone = new("joystickdeadzone", new BasisPlatformDefault<float>(0.01f));

        public static BasisSettingsBinding<float> Basexdeadzone = new("basexdeadzone", new BasisPlatformDefault<float>(0.08f));

        public static BasisSettingsBinding<float> Extraxdeadzoneatfully = new("extraxdeadzoneatfully", new BasisPlatformDefault<float>(0.35f));

        public static BasisSettingsBinding<float> Ydeadzone = new("ydeadzone", new BasisPlatformDefault<float>(0.10f));

        public static BasisSettingsBinding<float> Wingexponent = new("wingexponent", new BasisPlatformDefault<float>(1.6f));

        public static BasisSettingsBinding<float> MicrophoneRange = new("microphonerange", new BasisPlatformDefault<float>(25));

        public static BasisSettingsBinding<float> HearingRange = new("hearingrange", new BasisPlatformDefault<float>(25));

        public static BasisSettingsBinding<float> SelectedHeight = new("selectedheight", new BasisPlatformDefault<float>(1.6f));

        public static BasisSettingsBinding<float> SelectedScale = new("selectedscale", new BasisPlatformDefault<float>(1.6f));

        public static BasisSettingsBinding<float> realworldeyeheight = new("realworldeyeheight", new BasisPlatformDefault<float>(1.61f));

        public static BasisSettingsBinding<bool> RememberMenuState = new("remembermenustate", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<bool> ShowDeveloperTab = new("showdevelopertab", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> CustomScale = new("customscale", new BasisPlatformDefault<bool>(false));

        // Key bumped to _v2: BasisSettingsSystem.LoadString persists a default the first time it is read, so
        // "footik" is already pinned to false on every install that has ever launched. Flipping the value
        // alone would only reach fresh installs.
        public static BasisSettingsBinding<bool> FootIKEnabled = new("footik_v2", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// When enabled, suppresses jump/landing Mecanim animations and the landing hip dip
        /// while full-body trackers are calibrated, so they don't fight real tracker data.
        /// </summary>
        public static BasisSettingsBinding<bool> DisableAnimationsInFBT = new("disableanimationsinfbt", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Master switch for full-body tracking. When disabled, hip/chest/foot/knee
        /// trackers are ignored and the avatar falls back to head + hands + procedural
        /// foot IK, even if FBT trackers are connected and calibrated.
        /// </summary>
        public static BasisSettingsBinding<bool> EnableFBT = new("enablefbt", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// Master switch for the OSC acquisition server (face/body parameter ingest on
        /// UDP 9000/9001). When disabled, no OSC client is opened and no external
        /// programs can push avatar parameters.
        /// </summary>
        public static BasisSettingsBinding<bool> EnableOSC = new("enableosc", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// User-facing toggle for face tracking. When disabled, the blendshape actuation
        /// driving the avatar's facial expressions is held inactive even if face tracking
        /// data is flowing, and the face tracking diagnostics panel is collapsed.
        /// </summary>
        public static BasisSettingsBinding<bool> EnableFaceTracking = new("enablefacetracking", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// User-facing toggle for eye tracking. When disabled, the eye tracking bone
        /// actuation is held inactive so incoming eye parameters do not drive the avatar's
        /// eye bones, and the eye tracking diagnostics panel is collapsed. The procedural
        /// natural eye look keeps running.
        /// </summary>
        public static BasisSettingsBinding<bool> EnableEyeTracking = new("enableeyetracking", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<float> AvatarRange = new("avatarrange", new BasisPlatformDefault<float>
        {
            windows = 25,
            android = 15,
            ios = 15,
            linux = 25,
            other = 25
        });

        /// <summary>
        /// Maximum number of remote players allowed to show their real avatar at once.
        /// 0 = unlimited (all in-range players show real avatars).
        /// Players beyond this limit fall back to the default avatar.
        /// Closest players get priority; currently-visible avatars are sticky to prevent pulsing.
        /// </summary>
        public static BasisSettingsBinding<float> MaxVisibleAvatars = new("maxvisibleavatars", new BasisPlatformDefault<float>(0));
        public static BasisSettingsBinding<bool> UseMaxVisibleAvatars = new("usemaxvisibleavatars", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// When enabled, joining or being in an instance with a very high player count offers
        /// to turn on High Player Cap Performance Mode. Disable to never see those prompts.
        /// </summary>
        public static BasisSettingsBinding<bool> HighPlayerCapSuggestions = new("highplayercapsuggestions", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// Lets downloads proceed when the system resolver answers with an address in the RFC 2544
        /// benchmarking range (198.18.0.0/15). Off by default. Fake-IP proxies (Clash, sing-box in
        /// TUN mode) hand every domain a synthetic address out of that range and carry the
        /// connection themselves, so the security gate that blocks non-routable addresses refuses
        /// every file. Only that one range is affected — loopback, LAN and cloud-metadata
        /// addresses stay blocked with this on.
        /// </summary>
        public static BasisSettingsBinding<bool> AllowProxyBenchmarkRange = new("allowproxybenchmarkrange", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Active Performance Mode level: "Off", "Light", "Balanced" or "Aggressive".
        /// See <see cref="BasisPerformanceMode"/> — the level owns a table of graphics,
        /// crowd and avatar-cost settings and restores the player's own values when
        /// it returns to "Off".
        /// </summary>
        public static BasisSettingsBinding<string> PerformanceModeLevel = new("performancemodelevel", new BasisPlatformDefault<string>(BasisPerformanceMode.LevelOff));

        /// <summary>
        /// When enabled, the Performance Mode level follows the instance population
        /// (over 250 / 500 / 1000 occupants) instead of waiting for a prompt.
        /// </summary>
        public static BasisSettingsBinding<bool> PerformanceModeAuto = new("performancemodeauto", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Serialized snapshot of every Performance Mode controlled setting as it was
        /// before the mode was switched on. Empty while the mode is off.
        /// </summary>
        public static BasisSettingsBinding<string> PerformanceModeBaseline = new("performancemodebaseline", new BasisPlatformDefault<string>(string.Empty));

        /// <summary>
        /// Maximum number of remote players allowed to have active audio sources at once.
        /// 0 = unlimited (all in-range players get audio).
        /// Players beyond this limit lose their audio source.
        /// Closest players get priority; currently-active sources are sticky to prevent popping.
        /// </summary>
        public static BasisSettingsBinding<float> MaxAudioSources = new("maxaudiosources", new BasisPlatformDefault<float>(0));
        public static BasisSettingsBinding<bool> UseMaxAudioSources = new("usemaxaudiosources", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// When enabled, caps the number of OpenLipSync (neural viseme) slots to <see cref="OpenLipSyncMaxSlots"/>.
        /// When disabled (default), slot count is unlimited — bounded only by the number of players in viseme range.
        /// </summary>
        public static BasisSettingsBinding<bool> UseOpenLipSyncLimit = new("useopenlipsynclimit", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Maximum number of OpenLipSync (neural viseme) slots when <see cref="UseOpenLipSyncLimit"/> is enabled.
        /// Players beyond this limit get no visemes until a slot frees up.
        /// Higher values look better in crowds but cost more CPU.
        /// </summary>
        public static BasisSettingsBinding<float> OpenLipSyncMaxSlots = new("openlipsyncmaxslots", new BasisPlatformDefault<float>(100));

        /// <summary>
        /// When enabled, only remote players within the local player's view cone
        /// (based on camera forward direction) will show their real avatar.
        /// Players outside the cone fall back to the default avatar.
        /// </summary>
        /// <summary>
        /// Controls how aggressively distant players skip pose updates.
        /// 0 = off (every player updates every frame).
        /// 1 = gentle (LOD 3 skips every other frame).
        /// 4 = default (LOD 3 updates every 8th frame).
        /// 8 = aggressive (LOD 3 updates every 32nd frame).
        /// </summary>
        public static BasisSettingsBinding<float> PoseLOD = new("poselod", new BasisPlatformDefault<float>
        {
            windows = 0,
            android = 3,
            ios = 3,
            linux = 0,
            other = 0
        });

        public static BasisSettingsBinding<float> SnapTurnAngle = new("snapturnangle", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<float> mousesensitivty = new("mousesensitivty", new BasisPlatformDefault<float>(1));

        public static BasisSettingsBinding<bool> InvertMouse = new("invertmouse", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Dominant hand preference. "right" or "left". Affects placement raycast and pickup priority.
        /// </summary>
        public static BasisSettingsBinding<string> DominantHand = new("dominanthand", new BasisPlatformDefault<string>("right"));

        public static BasisSettingsBinding<bool> usesnapturn = new("usesnapturn", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<float> SmoothTurnSpeed = new("smoothturnspeed", new BasisPlatformDefault<float>(200f));

        public static BasisSettingsBinding<float> ScrollSpeed = new("scrollspeed", new BasisPlatformDefault<float>(90f));

        // ---------------- PLAYSPACE MOVER ----------------
        // VR-only grab-and-drag of the play space. Off by default; opt-in under Body Tracking.
        public static BasisSettingsBinding<bool> EnablePlayspaceMover = new("enableplayspacemover", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<string> PlayspaceMoverInput = new("playspacemoverinput", new BasisPlatformDefault<string>(BasisLocalPlayspaceMover.InputGrip));
        public static BasisSettingsBinding<string> PlayspaceMoverRotateInput = new("playspacemoverrotateinput", new BasisPlatformDefault<string>(BasisLocalPlayspaceMover.InputTrigger));
        public static BasisSettingsBinding<string> PlayspaceMoverHand = new("playspacemoverhand", new BasisPlatformDefault<string>(BasisLocalPlayspaceMover.HandBoth));
        public static BasisSettingsBinding<bool> PlayspaceMoverRotate = new("playspacemoverrotate", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> PlayspaceMoverScale = new("playspacemoverscale", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> PlayspaceMoverVertical = new("playspacemoververtical", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> PlayspaceMoverFlip = new("playspacemoverflip", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> PlayspaceMoverFlipAngle = new("playspacemoverflipangle", new BasisPlatformDefault<float>(180f));
        public static BasisSettingsBinding<string> PlayspaceMoverFlipAxis = new("playspacemoverflipaxis", new BasisPlatformDefault<string>(BasisLocalPlayspaceMover.AxisRoll));

        // Play-space visualiser (BasisPlayspaceGizmos). The master toggle is off by default; the layer
        // toggles under it are on, so turning it on draws the whole picture rather than nothing.
        public static BasisSettingsBinding<bool> PlayspaceGizmos = new("playspacegizmos", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> PlayspaceGizmoBoundary = new("playspacegizmoboundary", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> PlayspaceGizmoOrigin = new("playspacegizmoorigin", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> PlayspaceGizmoOffset = new("playspacegizmooffset", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> PlayspaceGizmoHands = new("playspacegizmohands", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> PlayspaceGizmoReadouts = new("playspacegizmoreadouts", new BasisPlatformDefault<bool>(true));

        // Lets the avatar you are wearing feed synthetic locomotion / play-space input through its
        // cilbox script. Only the locally worn avatar is ever accepted; turn off to ignore all of it.
        public static BasisSettingsBinding<bool> EnableScriptedPlayerInput = new("enablescriptedplayerinput", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<string> QualityLevel = new("qualitylevel", new BasisPlatformDefault<string>
        {
            windows = "Ultra",
            android = "Very Low",
            ios = "Very Low",
            linux = "Ultra",
            other = "Ultra"
        });

        public static BasisSettingsBinding<string> ShadowQuality = new("shadowquality", new BasisPlatformDefault<string>
        {
            windows = "Ultra",
            android = "Very Low",
            ios = "Very Low",
            linux = "Ultra",
            other = "Ultra"
        });

        public static BasisSettingsBinding<string> HDRSupport = new("hdrsupport", new BasisPlatformDefault<string>
        {
            windows = "64bit",
            android = "Off",
            ios = "Off",
            linux = "64bit",
            other = "64bit"
        });

        // ---------------- ACCESSIBILITY ----------------
        /// <summary>
        /// When enabled, the bloom intensity override is applied via a high-priority global Volume.
        /// </summary>
        public static BasisSettingsBinding<bool> UseBloomOverride = new("usebloomoverride", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Bloom intensity override. 0 = bloom disabled, 1 = default scene bloom.
        /// Only applied when <see cref="UseBloomOverride"/> is enabled.
        /// </summary>
        public static BasisSettingsBinding<float> BloomIntensity = new("bloomintensity", new BasisPlatformDefault<float>(1f));

        public const float BLOOM_INTENSITY_MIN = 0f;
        public const float BLOOM_INTENSITY_MAX = 5f;

        /// <summary>
        /// When enabled, the volumetric fog density override is applied to all
        /// VolumetricFogVolumeComponent overrides on existing Volumes in the scene.
        /// </summary>
        public static BasisSettingsBinding<bool> UseVolumetricFogOverride = new("usevolumetricfogoverride", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Volumetric fog density override. 0 = fog disabled, higher = denser fog.
        /// Only applied when <see cref="UseVolumetricFogOverride"/> is enabled.
        /// </summary>
        public static BasisSettingsBinding<float> VolumetricFogDensity = new("volumetricfogdensity", new BasisPlatformDefault<float>(0.2f));

        public const float FOG_DENSITY_MIN = 0f;
        public const float FOG_DENSITY_MAX = 1f;

        /// <summary>
        /// When enabled, volumetric fog samples APV lighting from a fast runtime-baked static volume
        /// instead of evaluating the adaptive probe volumes live every raymarch step. The bake is
        /// produced on demand from the world's APV and used immediately.
        /// </summary>
        public static BasisSettingsBinding<bool> VolumetricFogBakedAPV = new("volumetricfogbakedapv", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// When enabled, motion blur is driven from a high-priority global Volume owned by
        /// <c>SMModuleMotionBlurOverrideURP</c>, which outranks whatever the world authored.
        /// </summary>
        public static BasisSettingsBinding<bool> UseMotionBlurOverride = new("usemotionbluroverride", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Motion blur strength — a multiplier on velocity. 0 turns motion blur off outright,
        /// which is how a world's own motion blur gets suppressed.
        /// Only applied when <see cref="UseMotionBlurOverride"/> is enabled.
        /// </summary>
        public static BasisSettingsBinding<float> MotionBlurIntensity = new("motionblurintensity", new BasisPlatformDefault<float>(0.5f));

        /// <summary>
        /// Longest a blur streak may get, as a fraction of the screen. This is what stops a fast
        /// turn from smearing the whole view.
        /// </summary>
        public static BasisSettingsBinding<float> MotionBlurClamp = new("motionblurclamp", new BasisPlatformDefault<float>(0.05f));

        /// <summary>Samples taken per streak: Low, Medium or High.</summary>
        public static BasisSettingsBinding<string> MotionBlurQuality = new("motionblurquality", new BasisPlatformDefault<string>("Low"));

        /// <summary>
        /// Camera Only or Camera And Objects. Camera And Objects also blurs things moving in front
        /// of a still view, and URP schedules a motion vector pass for it — see
        /// <c>SMModuleMotionVectorsURP</c> for why that pass is otherwise kept off on Standalone.
        /// </summary>
        public static BasisSettingsBinding<string> MotionBlurMode = new("motionblurmode", new BasisPlatformDefault<string>("Camera Only"));

        // URP's own ClampedFloatParameter ranges on MotionBlur.
        public const float MOTION_BLUR_INTENSITY_MIN = 0f;
        public const float MOTION_BLUR_INTENSITY_MAX = 1f;
        public const float MOTION_BLUR_CLAMP_MIN = 0f;
        public const float MOTION_BLUR_CLAMP_MAX = 0.2f;

        public static BasisSettingsBinding<bool> UseGlobalIllumination = new("useglobalillumination", new BasisPlatformDefault<bool>(false));
        // Screen Space marches the depth buffer and can only gather what the frame already drew. Ray Traced
        // traces the scene itself against an acceleration structure, so it also carries light from behind the
        // camera and shades what it hits with the real lights and emissive materials - and it needs a GPU
        // with ray tracing, falling back to Screen Space on one that has none.
        public static BasisSettingsBinding<string> GlobalIlluminationMode = new("globalilluminationmode", new BasisPlatformDefault<string>("Screen Space"));
        // Avatars are skinned meshes, so this is what decides whether the people in the room bounce and
        // occlude light in the pose they are standing in. Dynamic re-bakes them on a per-frame budget.
        public static BasisSettingsBinding<string> GlobalIlluminationSkinnedMeshes = new("globalilluminationskinnedmeshes", new BasisPlatformDefault<string>("Dynamic"));
        public static BasisSettingsBinding<string> GlobalIlluminationQuality = new("globalilluminationquality", new BasisPlatformDefault<string>("Medium"));
        // Half resolution is what makes the effect affordable at VR framerates; Full is for photo mode and
        // for people who would rather spend the frame on it.
        public static BasisSettingsBinding<string> GlobalIlluminationResolution = new("globalilluminationresolution", new BasisPlatformDefault<string>("Half"));
        public static BasisSettingsBinding<string> GlobalIlluminationFallback = new("globalilluminationfallback", new BasisPlatformDefault<string>("Reflection Probe"));
        public static BasisSettingsBinding<float> GlobalIlluminationIntensity = new("globalilluminationintensity", new BasisPlatformDefault<float>(1f));
        public const float GI_INTENSITY_MIN = 0.1f;
        public const float GI_INTENSITY_MAX = 4f;
        public static BasisSettingsBinding<float> GlobalIlluminationSaturation = new("globalilluminationsaturation", new BasisPlatformDefault<float>(1f));
        public const float GI_SATURATION_MIN = 0.1f;
        public const float GI_SATURATION_MAX = 2f;
        // Near-field obscurance is the effect's own ambient occlusion, gathered from the same rays, so it
        // costs nothing extra to turn up.
        public static BasisSettingsBinding<float> GlobalIlluminationObscurance = new("globalilluminationobscurance", new BasisPlatformDefault<float>(0.5f));
        public const float GI_OBSCURANCE_MIN = 0.05f;
        public const float GI_OBSCURANCE_MAX = 1f;
        public static BasisSettingsBinding<float> GlobalIlluminationRayLength = new("globalilluminationraylength", new BasisPlatformDefault<float>(16f));
        public const float GI_RAY_LENGTH_MIN = 1f;
        public const float GI_RAY_LENGTH_MAX = 64f;
        public static BasisSettingsBinding<float> GlobalIlluminationSmoothing = new("globalilluminationsmoothing", new BasisPlatformDefault<float>(1f));
        public const float GI_SMOOTHING_MIN = 0.25f;
        public const float GI_SMOOTHING_MAX = 2f;
        // How much of this frame's trace replaces the accumulated bounce. Low keeps the image stillest but
        // smears the bounce behind anything that moves, which is a comfort question in VR, so it is the
        // player's call rather than the world's.
        public static BasisSettingsBinding<float> GlobalIlluminationTemporalResponse = new("globalilluminationtemporalresponse", new BasisPlatformDefault<float>(0.15f));
        public const float GI_TEMPORAL_RESPONSE_MIN = 0.05f;
        public const float GI_TEMPORAL_RESPONSE_MAX = 1f;
        public static BasisSettingsBinding<bool> GlobalIlluminationTemporalFilter = new("globalilluminationtemporalfilter", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> GlobalIlluminationWideBlur = new("globalilluminationwideblur", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> GlobalIlluminationRayReuse = new("globalilluminationrayreuse", new BasisPlatformDefault<bool>(true));
        // Emitters carry light from sources the trace cannot see: a sign or a strip light is under one
        // texel at traced resolution, and anything behind the camera is off screen entirely.
        public static BasisSettingsBinding<bool> GlobalIlluminationEmitters = new("globalilluminationemitters", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> GlobalIlluminationEmitterIntensity = new("globalilluminationemitterintensity", new BasisPlatformDefault<float>(1f));
        public const float GI_EMITTER_INTENSITY_MIN = 0.1f;
        public const float GI_EMITTER_INTENSITY_MAX = 8f;
        // Off by default: a realtime reflection probe pays for the whole effect once per face.
        public static BasisSettingsBinding<bool> GlobalIlluminationReflectionProbes = new("globalilluminationreflectionprobes", new BasisPlatformDefault<bool>(false));
        /// <summary>
        /// Whether mirrors show the bounce. On by default because the alternative is a mirror rendering the
        /// room without any of the light the same room has when looked at directly, which reads as a broken
        /// mirror rather than as a performance setting. Each mirror camera pays for its own gather, so this
        /// is the lever for a world where that is too much.
        /// </summary>
        public static BasisSettingsBinding<bool> GlobalIlluminationMirrors = new("globalilluminationmirrors", new BasisPlatformDefault<bool>(true));

        // Ray traced ambient occlusion. Off by default because it is a real slice of the frame and it
        // needs a ray tracing GPU for the traced path.
        public static BasisSettingsBinding<bool> UseRayTracedAmbientOcclusion = new("useraytracedambientocclusion", new BasisPlatformDefault<bool>(false));
        // Auto traces against the scene on a ray tracing GPU and drops to the screen space estimator on one
        // without. Direct3D11 has no ray tracing path at all, so it always lands on Screen Space there.
        public static BasisSettingsBinding<string> RayTracedAmbientOcclusionMode = new("raytracedambientocclusionmode", new BasisPlatformDefault<string>("Auto"));
        public static BasisSettingsBinding<string> RayTracedAmbientOcclusionQuality = new("raytracedambientocclusionquality", new BasisPlatformDefault<string>("Medium"));
        public static BasisSettingsBinding<float> RayTracedAmbientOcclusionIntensity = new("raytracedambientocclusionintensity", new BasisPlatformDefault<float>(1f));
        public const float RTAO_INTENSITY_MIN = 0.05f;
        public const float RTAO_INTENSITY_MAX = 1f;
        // How far a surface looks for occluders, in metres. The whole range is contact shadow scale - two to
        // ten centimetres - because that is the distance an avatar's own geometry occludes itself over.
        public static BasisSettingsBinding<float> RayTracedAmbientOcclusionRadius = new("raytracedambientocclusionradius", new BasisPlatformDefault<float>(RTAO_RADIUS_MIN));
        public const float RTAO_RADIUS_MIN = 0.02f;
        public const float RTAO_RADIUS_MAX = 0.1f;
        // Avatars are skinned meshes, so this decides whether people in the room cast contact shadows.
        // Dynamic re-bakes them on a per-frame budget, which is CPU the traced path does not otherwise spend.
        public static BasisSettingsBinding<string> RayTracedAmbientOcclusionSkinnedMeshes = new("raytracedambientocclusionskinnedmeshes", new BasisPlatformDefault<string>("Dynamic"));
        // URP multiplies the whole indirect term by the occlusion but only lerps the direct term toward it by
        // this much, so in a scene carried by a bright directional light the effect reads far weaker than the
        // occlusion buffer looks. Raising this pulls the shaded image back toward the buffer.
        public static BasisSettingsBinding<float> RayTracedAmbientOcclusionDirectStrength = new("raytracedambientocclusiondirectstrength", new BasisPlatformDefault<float>(0.5f));
        // Each denoise pass reuses the same edge aware kernel with its taps spread twice as far, so the reach
        // doubles per pass for the same cost. More passes means less grain and softer contact shadows.
        public static BasisSettingsBinding<string> RayTracedAmbientOcclusionDenoise = new("raytracedambientocclusiondenoise", new BasisPlatformDefault<string>("High"));
        // Mirrors and the handheld camera each trace and denoise their own view. Correct, and a real
        // multiple of the cost, so it is the first thing to turn off when the frame gets tight.
        public static BasisSettingsBinding<bool> RayTracedAmbientOcclusionOtherCameras = new("raytracedambientocclusionothercameras", new BasisPlatformDefault<bool>(true));
        // Lighting feeds URP's lighting, which is honest but only reaches shaders that read the occlusion
        // texture and is clamped by a material's own occlusion map. Final Image multiplies the finished
        // opaque frame the way Unity's own SSAO does in After Opaque, so it lands on everything.
        public static BasisSettingsBinding<string> RayTracedAmbientOcclusionApply = new("raytracedambientocclusionapply", new BasisPlatformDefault<string>("Lighting"));
        public const float RTAO_DIRECT_STRENGTH_MIN = 0f;
        public const float RTAO_DIRECT_STRENGTH_MAX = 1f;
        public static BasisSettingsBinding<bool> DevRtaoDebugView = new("devrtaodebugview", new BasisPlatformDefault<bool>(false));
        // Which buffer the debug view draws. An artifact looks the same in the finished picture whichever
        // stage made it, so stepping through these is how you find the one that did.
        public static BasisSettingsBinding<string> DevRtaoDebugStage = new("devrtaodebugstage", new BasisPlatformDefault<string>("Final"));
        // How many avatars may be re-posed for the ray tracer in one frame. Zero follows the occlusion
        // quality (1 / 4 / 16 / 100); anything else pins it, which is the knob for measuring what a busy
        // instance actually costs.
        public static BasisSettingsBinding<float> DevRtaoSkinnedBudget = new("devrtaoskinnedbudget", new BasisPlatformDefault<float>(0f));
        public const float RTAO_SKINNED_BUDGET_MIN = 0f;
        public const float RTAO_SKINNED_BUDGET_MAX = 128f;

        // Commented out 2026-08-04: the realtime-reflection-probe driver these described was
        // never implemented — no UI exposes them and nothing reads them (the Performance Mode
        // table only wrote the bool). Restore both together with the probe driver.
        ///// <summary>
        ///// When enabled, ReflectionProbe components in the scene whose mode is Realtime are
        ///// driven by Basis at the rate selected by RealtimeReflectionProbeRate.
        ///// When disabled, Basis does not modify any probe state.
        ///// </summary>
        //public static BasisSettingsBinding<bool> UseRealtimeReflectionProbes = new("userealtimereflectionprobes", new BasisPlatformDefault<bool>(false));

        ///// <summary>
        ///// Tick rate for realtime reflection probes when UseRealtimeReflectionProbes
        ///// is on. "Match Render" delegates to Unity's per-frame mode; the others use ViaScripting
        ///// and Basis calls RenderProbe at the chosen interval.
        ///// </summary>
        //public static BasisSettingsBinding<string> RealtimeReflectionProbeRate = new("realtimereflectionproberate", new BasisPlatformDefault<string>("30hz"));

        public static BasisSettingsBinding<bool> MicrophoneDenoiser = new("voicedenoiser", new BasisPlatformDefault<bool>
        {
            windows = true,
            android = false,
            ios = false,
            linux = false,
            other = false
        });

        public static BasisSettingsBinding<string> Antialiasing = new("antialiasing", new BasisPlatformDefault<string>
        {
            windows = "msaa 2x",
            android = "msaa 4x",
            ios = "msaa 4x",
            linux = "msaa 2x",
            other = "msaa 2x"
        });

        public static BasisSettingsBinding<bool> DevVariableRateShading = new("devvariablerateshading", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> DevVariableRateShadingDesktop = new("devvariablerateshadingdesktop", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<string> DevGiDebugView = new("devgidebugview", new BasisPlatformDefault<string>("Off"));

        public static BasisSettingsBinding<bool> EyeTrackingPreferOsc = new("eyetrackingpreferosc", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> EyeFoveationAutoManage = new("eyefoveationautomanage", new BasisPlatformDefault<bool>(true));

        public const float VRS_RADIUS_MIN = 0f;
        public const float VRS_RADIUS_MAX = 1f;
        public static BasisSettingsBinding<float> VrsFovealInnerRadius = new("vrsfovealinner_v2", new BasisPlatformDefault<float>(0.25f));
        public static BasisSettingsBinding<float> VrsFovealOuterRadius = new("vrsfovealouter_v2", new BasisPlatformDefault<float>(0.31f));

        // Legacy master gizmo gate. The Developer UI no longer exposes this — gizmo
        // rendering now turns on whenever any individual gizmo toggle below is enabled
        // (see SMModuleDebugOptions.RecomputeUseGizmos). Kept so resets/loads stay valid.
        public static BasisSettingsBinding<bool> ShowGizmos = new("showgizmos", new BasisPlatformDefault<bool>(false));

        // Every gizmo defaults off so the derived render gate stays off until the user
        // opts in to a specific gizmo. Keys bumped (_v2) so installs that persisted the
        // old default-on values don't suddenly render gizmos once the master gate is gone.
        public static BasisSettingsBinding<bool> GizmoSkeletonLines = new("gizmoskeletonlines_v2", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> GizmoCalibrationSpheres = new("gizmocalibrationspheres_v2", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> GizmoJiggleVisuals = new("gizmojigglevisuals_v2", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> TrackerGizmos = new("trackergizmos", new BasisPlatformDefault<bool>(false));

        // IK self-collision capsule visualization (chest + hand capsules used to
        // keep the hands from intersecting the torso). Off by default — only
        // useful when tuning ChestRadius / HandRadius / CollisionSkin.
        public static BasisSettingsBinding<bool> GizmoIKColliders = new("gizmoikcolliders", new BasisPlatformDefault<bool>(false));

        // Pointer/physics raycast ray + placement box (was BasisPointRaycaster.OnDrawGizmosSelected).
        public static BasisSettingsBinding<bool> GizmoPointerRay = new("gizmopointerray", new BasisPlatformDefault<bool>(false));
        // IK hint tracker raw/biased offset markers + orientation triad.
        public static BasisSettingsBinding<bool> GizmoHintOffsets = new("gizmohintoffsets", new BasisPlatformDefault<bool>(false));
        // Procedural foot-placement phases, step arcs, knee hints.
        public static BasisSettingsBinding<bool> GizmoFootPlacement = new("gizmofootplacement", new BasisPlatformDefault<bool>(false));
        // Interaction hover spheres + hover/target lines.
        public static BasisSettingsBinding<bool> GizmoInteractionHover = new("gizmointeractionhover", new BasisPlatformDefault<bool>(false));
        // VR finger-touch fingertip sphere, tinted by touch phase (BasisDirectTouch).
        public static BasisSettingsBinding<bool> GizmoFingerTouch = new("gizmofingertouch", new BasisPlatformDefault<bool>(false));
        // Seated-pose solve targets (back/knee/foot) + axes.
        public static BasisSettingsBinding<bool> GizmoSeatTargets = new("gizmoseattargets", new BasisPlatformDefault<bool>(false));

        // Jiggle grab-and-pull debug: grabbed bone, pull target, per-point reach limit, and the
        // pick spheres a grab press searches from.
        public static BasisSettingsBinding<bool> GizmoJiggleGrab = new("gizmojigglegrab", new BasisPlatformDefault<bool>(false));

        // Canonical hand frame held objects weld to, on every hand local and remote: palm point,
        // frame axes, and the wrist-to-palm offset the weld applies.
        public static BasisSettingsBinding<bool> GizmoHandGrip = new("gizmohandgrip", new BasisPlatformDefault<bool>(false));

        // Center-eye and mouth face anchors on every avatar local and remote: the driven point, the
        // forward the gaze selector and the voice AudioSource use, the offset back to the head, and
        // a red leg out to where a head-parented anchor belongs when the two disagree.
        public static BasisSettingsBinding<bool> GizmoMouthEye = new("gizmomoutheye", new BasisPlatformDefault<bool>(false));

        // Eye-gaze ray + endpoint-target gizmo. Off by default — only relevant on
        // headsets that surface gaze through OpenXR EyeGazeInteraction or a SteamVR
        // pose action, and the line in your face is noisy.
        public static BasisSettingsBinding<bool> GizmoEyeGaze = new("gizmoeyegaze", new BasisPlatformDefault<bool>(false));

        // Spatial-voice debug gizmos (see BasisAudioGizmos). All off by default.
        // Ranges: the listener hearing-distance sphere + per-speaker full-volume ring.
        public static BasisSettingsBinding<bool> GizmoAudioRanges = new("gizmoaudioranges", new BasisPlatformDefault<bool>(false));
        // ListenerCone: the directional-dampening cone projected from the listener.
        public static BasisSettingsBinding<bool> GizmoAudioListenerCone = new("gizmoaudiolistenercone", new BasisPlatformDefault<bool>(false));
        // Levels: per-speaker loudness meter (raw output vs. what survives) plus a
        // breakdown of every factor reducing the voice before it reaches you.
        public static BasisSettingsBinding<bool> GizmoAudioLevels = new("gizmoaudiolevels", new BasisPlatformDefault<bool>(false));
        // Labels: billboarded world-text labels on whichever gizmos are visible —
        // tracker roles, linked-pair ids, IK collider names, and the audio gizmos
        // (speaker name + gain %, hearing distance, cone angle). One switch for all.
        public static BasisSettingsBinding<bool> GizmoLabels = new("gizmolabels", new BasisPlatformDefault<bool>(false));

        // Depth behaviour shared by every gizmo — spheres, lines and labels alike. Off (the
        // default) depth-tests them so the world occludes them, which is the only way to see
        // whether a probe is actually in front of the geometry it describes; on draws them
        // through the world so a marker buried inside an avatar stays readable.
        public static BasisSettingsBinding<bool> GizmoDrawOnTop = new("gizmodrawontop", new BasisPlatformDefault<bool>(false));

        // Which cameras see the gizmos. Off (the default) keeps them on OverlayUI, which the
        // handheld camera culls, so they stay out of photos, streams and the follow PIP; on moves
        // them to the layer the world itself is on, so every camera in the scene renders them.
        public static BasisSettingsBinding<bool> GizmoRenderInAllCameras = new("gizmorenderinallcameras", new BasisPlatformDefault<bool>(false));

        // Network value-sync debug gizmos (see BasisSyncGizmos): from→to interpolation
        // window, live-position sphere, extrapolation overshoot, jitter-buffer-health colour.
        public static BasisSettingsBinding<bool> GizmoNetworkSync = new("gizmonetworksync", new BasisPlatformDefault<bool>(false));

        // Per-object received bytes-on-wire + packet rate readout for the sync gizmos.
        public static BasisSettingsBinding<bool> GizmoNetworkSyncBandwidth = new("gizmonetworksyncbandwidth", new BasisPlatformDefault<bool>(false));

        // Per-remote-player pose interpolation gizmos (see BasisPlayerNetworkGizmos): from→to
        // hips path, live-position sphere, playback-health colour; plus a bytes-on-wire readout.
        public static BasisSettingsBinding<bool> GizmoNetworkPlayers = new("gizmonetworkplayers", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> GizmoNetworkPlayersBandwidth = new("gizmonetworkplayersbandwidth", new BasisPlatformDefault<bool>(false));

        // Additional network information (see BasisNetworkOverviewGizmos): per-player voice
        // bandwidth appended to the network labels, and a floating overall readout of the
        // avatar / voice / scene channel totals.
        public static BasisSettingsBinding<bool> GizmoNetworkAdditionalInfo = new("gizmonetworkadditionalinfo", new BasisPlatformDefault<bool>(false));

        // Yellow line gizmo drawn between the two physical trackers of every
        // active linked pair. Off by default; toggled separately from
        // TrackerGizmos so a user debugging the pairing system can see only
        // the link visualization without the tracker spheres cluttering the view.
        public static BasisSettingsBinding<bool> LinkedTrackerLines = new("linkedtrackerlines", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> EnableStatistics = new("enablestatistics", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> ShowVoiceRange = new("showvoicerange", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// When on, the client runs a loopback-only HTTP listener on
        /// 127.0.0.1:<see cref="StreamingMetaPort"/> exposing /stats.json and
        /// /overlay.html so OBS Browser Source (or any local tool) can pull
        /// FPS / CCU / ping. Off by default — the listener is only opened
        /// after the user explicitly enables this.
        /// </summary>
        public static BasisSettingsBinding<bool> EnableStreamingMeta = new("enablestreamingmeta", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// TCP port used by the streaming meta listener. Stored as a string so
        /// it can be edited in a single-line numeric text field. Consumers
        /// should parse it and fall back to 9080 on invalid/out-of-range input.
        /// </summary>
        public static BasisSettingsBinding<string> StreamingMetaPort = new("streamingmetaport", new BasisPlatformDefault<string>("9080"));

        public static BasisSettingsBinding<bool> DevShowCalibrationDebug = new("devshowcalibrationdebug", new BasisPlatformDefault<bool>(false));

        // The calibrate button normally hides itself in desktop unless FBT is on and a real
        // (non-camera) body tracker is connected. This forces it visible in every mode, so
        // desktop users can reach the calibration panel — height recapture works without trackers.
        public static BasisSettingsBinding<bool> DevAlwaysShowCalibration = new("devalwaysshowcalibration", new BasisPlatformDefault<bool>(false));

        // When on, the local avatar calibration pipeline dumps every stage's scales/positions/
        // rotation eulers + offsets to a CSV under persistentDataPath/CalibrationDebug. Read once
        // at the start of each calibration; leave off in normal play.
        public static BasisSettingsBinding<bool> DumpCalibrationCsv = new("devdumpcalibrationcsv", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> SpawnAnchorHandles = new("spawnanchorhandles", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> SpawnAnchorSeatOnSurface = new("spawnanchorseatonsurface", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> SpawnAnchorPositionSnap = new("spawnanchorpositionsnap", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> SpawnAnchorPositionSnapSize = new("spawnanchorpositionsnapsize", new BasisPlatformDefault<float>(0.25f));
        public static BasisSettingsBinding<bool> SpawnAnchorRotationSnap = new("spawnanchorrotationsnap", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> SpawnAnchorRotationSnapDegrees = new("spawnanchorrotationsnapdegrees", new BasisPlatformDefault<float>(15f));

        public static BasisSettingsBinding<bool> EnableShaderPrewarm = new("enableshaderprewarm", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> EnableMaterialCorrection = new("enablematerialcorrection", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> EnableShaderBlocklist = new("enableshaderblocklist", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<string> ShaderBlocklistPatterns = new("shaderblocklistpatterns", new BasisPlatformDefault<string>(string.Empty));
        public static BasisSettingsBinding<bool> EnableGraphicsStatePrewarm = new("enablegraphicsstateprewarm_v2", new BasisPlatformDefault<bool> { windows = true, android = false, ios = false, linux = false, other = false });
        public static BasisSettingsBinding<bool> ContentPoliceLogging = new("contentpolicelogging", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// When enabled, suppresses all <see cref="BasisDebug"/> log output (Log, LogWarning, LogError).
        /// Raw <see cref="UnityEngine.Debug"/> calls are unaffected.
        /// </summary>
        public static BasisSettingsBinding<bool> DisableLogging = new("disablelogging", new BasisPlatformDefault<bool>(false));

        public const string DebugLogFilterAll = "All";
        public const string DebugLogLevelWarningsAndErrors = "Warnings & Errors";
        public const string DebugLogLevelErrorsOnly = "Errors Only";

        public static BasisSettingsBinding<string> DebugLogTagFilter = new("debuglogtagfilter", new BasisPlatformDefault<string>(DebugLogFilterAll));
        public static BasisSettingsBinding<string> DebugLogLevelFilter = new("debugloglevelfilter", new BasisPlatformDefault<string>(DebugLogFilterAll));

        public static BasisSettingsBinding<bool> AudioDebugEnabled = new("audiodebugenabled", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> DisableLipSyncForFaceTracking = new("disablelipsyncforfacetracking", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> AudioDebugShowSource = new("audiodebugshowsource", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AudioDebugShowVolume = new("audiodebugshowvolume", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AudioDebugShowRingBuffer = new("audiodebugshowringbuffer", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AudioDebugShowJitter = new("audiodebugshowjitter", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AudioDebugShowSilence = new("audiodebugshowsilence", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AudioDebugShowViseme = new("audiodebugshowviseme", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> AvatarDataDebugEnabled = new("avatardatadebugenabled", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> AvatarDataDebugShowReceive = new("avatardatadebugshowreceive", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AvatarDataDebugShowStaging = new("avatardatadebugshowstaging", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AvatarDataDebugShowInterp = new("avatardatadebugshowinterp", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AvatarDataDebugShowMeta = new("avatardatadebugshowmeta", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<string> MemoryAllocation = new("memoryallocation", new BasisPlatformDefault<string>
        {
            windows = "Dynamic",
            android = "Dynamic",
            ios = "Dynamic",
            linux = "Dynamic",
            other = "Dynamic"
        });

        public static BasisSettingsBinding<bool> AvatarPreview = new("avatarpreview", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> AvatarPreviewMirror = new("avatarpreviewmirror", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<bool> LimitHandHeldCameraRate = new("limithandheldcamerarate", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<float> HandHeldCameraRenderHz = new("handheldcamerarenderhz_v2", new BasisPlatformDefault<float>(30));

        public static BasisSettingsBinding<bool> LimitAvatarPreviewRate = new("limitavatarpreviewrate", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<float> AvatarPreviewRenderHz = new("avatarpreviewrenderhz_v2", new BasisPlatformDefault<float>(30));

        public static BasisSettingsBinding<bool> DesktopReticle = new("desktopreticle", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> EnablePassthrough = new("enablepassthrough", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> EnableThirdPersonCamera = new("enablethirdpersoncamera", new BasisPlatformDefault<bool>(true));

        // True = listener stays at the player's head while third-person is active.
        // False = listener follows the orbital camera (audio shifts behind the player on zoom).
        // Only takes effect when the camera is currently in third-person mode.
        public static BasisSettingsBinding<bool> AudioListenerFollowsHead = new("audiolistenerfollowshead", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<string> MicrophoneIcon = new("microphoneicon", new BasisPlatformDefault<string>("alwaysvisible"));

        public static BasisSettingsBinding<float> MicrophoneIconOffsetX = new("microphoneiconoffsetx", new BasisPlatformDefault<float>(0f));
        public static BasisSettingsBinding<float> MicrophoneIconOffsetY = new("microphoneiconoffsety", new BasisPlatformDefault<float>(0f));

        // Swaps the microphone glyph for a shader-drawn circle outline that grows with voice level.
        public static BasisSettingsBinding<bool> MicrophoneIconLevelRing = new("microphoneiconlevelring_v2", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<bool> AvatarRangeIndicator = new("avatarrangeindicator", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> HearingRangeIndicator = new("hearingrangeindicator", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> MicrophoneRangeIndicator = new("microphonerangeindicator", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<string> IKMode = new("ikmode", new BasisPlatformDefault<string>("auto"));

        public static BasisSettingsBinding<string> IKLockMode = new("iklockmode_v3", new BasisPlatformDefault<string>("lock head"));

        public static BasisSettingsBinding<bool> CalibrationMirror = new("calibrationmirror", new BasisPlatformDefault<bool>(false));

        // Arm-to-height ratio: scale the avatar by a percentage between the two measurements instead of a
        // single mode -- 0 = eye height, 1 = arm distance (range in BasisCalibrationMath.ArmToHeightBlendMin/Max).
        // Chooses which measurement the uniform scale matches; FBIKBodyFit takes up the residual per-segment.
        // While enabled it replaces the Avatar Scaling Mode dropdown (VR only; desktop always scales by eye height).
        public static BasisSettingsBinding<bool> EnableArmToHeightBlend = new("enablearmtoheightblend", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> ArmToHeightBlend = new("armtoheightblend", new BasisPlatformDefault<float>(0.5f));

        // The player's body size measured at their last explicit calibration (metres). Seeded back into
        // BasisHeightDriver at boot so the very first avatar fits at the right scale instead of the
        // 1.61 m fallback / a stance-dependent first poll. 0 = never calibrated (no seed). Cleared by
        // Reset Calibration. Standing measurements only -- seated calibrations use the virtual standing
        // eye and are never saved here.
        public static BasisSettingsBinding<float> SavedPlayerEyeHeight = new("savedplayereyeheight", new BasisPlatformDefault<float>(0f));
        public static BasisSettingsBinding<float> SavedPlayerArmSpan = new("savedplayerarmspan", new BasisPlatformDefault<float>(0f));

        // Estimate a ballpark scale from the live HMD/controllers while the player hasn't calibrated yet, so an
        // uncalibrated VR session isn't wildly mis-sized. A real calibration overrides it. See BasisAutoScaleEstimator.
        // Superseded by ContinuousBodyMeasurement below, which does the same job for calibrated sessions too.
        public static BasisSettingsBinding<bool> AutoScaleEstimateEnabled = new("autoscaleestimateenabled_v2", new BasisPlatformDefault<bool>(false));

        // The player's own stated body height in metres (0 = they have not told us). The most reliable
        // measurement available -- and the only one a permanently-seated player has. Used to fill in
        // before anything is observed, and to reject readings anatomy cannot produce. See BasisStatedHeight.
        public static BasisSettingsBinding<float> StatedBodyHeight = new("statedbodyheight", new BasisPlatformDefault<float>(0f));

        // Keep watching the player's real eye height and arm span while they use the world, and refit the
        // avatar when a better measurement turns up, instead of trusting whatever pose they happened to be
        // in on the one frame an avatar loaded. Both measurements only ever read SHORT, so this only ever
        // corrects upward and settles. See BasisBodyEvidenceSampler.
        public static BasisSettingsBinding<bool> ContinuousBodyMeasurement = new("continuousbodymeasurement", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<string> SelectedBone = new("selectedbone", new BasisPlatformDefault<string>("selectedbone"));

        public static BasisSettingsBinding<float> FoveatedRendering = new("foveatedrendering", new BasisPlatformDefault<float>
        {
            windows = 0,
            android = 1,
            linux = 0,
            other = 0,
            ios = 0
        });

        public static BasisSettingsBinding<float> FieldOfView = new("fieldofview", new BasisPlatformDefault<float>(75));

        public const float FOV_MIN = 50;
        public const float FOV_MAX = 120;

        public static BasisSettingsBinding<float> AvatarDownloadSize = new("avatardownloadsize", new BasisPlatformDefault<float>
        {
            windows = 256,
            android = 64,
            ios = 64,
            linux = 256,
            other = 256
        });

        public static BasisSettingsBinding<float> CacheMaxSizeGB = new("cachemaxsizegb", new BasisPlatformDefault<float>
        {
            windows = 128,
            android = 8,
            ios = 8,
            linux = 128,
            other = 128
        });

        /// <summary>
        /// Maximum number of avatar asset bundles that can be downloaded from the network
        /// concurrently. Downloads are bandwidth-bound: a small value keeps each transfer
        /// at full speed, while a large value splits bandwidth and makes every player wait
        /// longer on the loading avatar. Tune higher only if you have lots of bandwidth and
        /// the server is fast.
        /// </summary>
        public static BasisSettingsBinding<float> MaxConcurrentAvatarDownloads = new("maxconcurrentavatardownloads", new BasisPlatformDefault<float>
        {
            windows = 5,
            android = 3,
            ios = 3,
            linux = 5,
            other = 5
        });

        /// <summary>
        /// Maximum number of cached avatar asset bundles that can be loaded from disc at
        /// once. Disc loads are I/O + decryption + bundle-decompression bound. This can be
        /// higher than the download gate because no network is involved.
        /// </summary>
        public static BasisSettingsBinding<float> MaxConcurrentAvatarDiscLoads = new("maxconcurrentavatardiscloads", new BasisPlatformDefault<float>
        {
            windows = 15,
            android = 4,
            ios = 4,
            linux = 15,
            other = 15
        });

        /// <summary>
        /// Maximum number of addressable (in-build) avatars that can be instantiated
        /// concurrently. Addressable loads are CPU-bound and typically very fast, so this
        /// gate can be the largest of the three.
        /// </summary>
        public static BasisSettingsBinding<float> MaxConcurrentAvatarAddressables = new("maxconcurrentavataraddressables", new BasisPlatformDefault<float>
        {
            windows = 25,
            android = 6,
            ios = 6,
            linux = 25,
            other = 25
        });

        // ---------------- AVATAR PERFORMANCE LIMITS ----------------
        // Client-side safety net that inspects the pre-download metadata header on each
        // remote avatar bundle and swaps the avatar for the fallback when any enabled
        // limit is exceeded. Every limit ships as an opt-in pair: a Use* bool gate plus
        // a Max* threshold. All Use* flags default to false so out-of-the-box behavior
        // on modern hardware is unchanged — this only kicks in when the user opts in.
        // Changing any value at runtime re-evaluates every currently-loaded remote
        // avatar (see SMModuleAvatarPerformanceLimits).

        public static BasisSettingsBinding<bool> UsePerfLimitTriangles = new("useperflimittriangles", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfTriangles = new("maxperftriangles", new BasisPlatformDefault<float>
        {
            windows = 2000000,
            android = 100000,
            ios = 100000,
            linux = 2000000,
            other = 2000000
        });

        public static BasisSettingsBinding<bool> UsePerfLimitBoundsSize = new("useperflimitboundssize", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfBoundsSize = new("maxperfboundssize", new BasisPlatformDefault<float>(50f));

        // Texture memory defaults on — 512 MB is generous for a single avatar but
        // catches the 2–4 GB outliers that trip out-of-memory on lower-end hardware.
        public static BasisSettingsBinding<bool> UsePerfLimitTextureMemory = new("useperflimittexturememory", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfTextureMemoryMB = new("maxperftexturememorymb", new BasisPlatformDefault<float>
        {
            windows = 512,
            android = 64,
            ios = 64,
            linux = 512,
            other = 512
        });

        public static BasisSettingsBinding<bool> UsePerfLimitSkinnedMeshes = new("useperflimitskinnedmeshes", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfSkinnedMeshes = new("maxperfskinnedmeshes", new BasisPlatformDefault<float>
        {
            windows = 64,
            android = 8,
            ios = 8,
            linux = 64,
            other = 64
        });

        public static BasisSettingsBinding<bool> UsePerfLimitBasicMeshes = new("useperflimitbasicmeshes", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfBasicMeshes = new("maxperfbasicmeshes", new BasisPlatformDefault<float>
        {
            windows = 128,
            android = 16,
            ios = 16,
            linux = 128,
            other = 128
        });

        public static BasisSettingsBinding<bool> UsePerfLimitMaterialSlots = new("useperflimitmaterialslots", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfMaterialSlots = new("maxperfmaterialslots", new BasisPlatformDefault<float>
        {
            windows = 256,
            android = 16,
            ios = 16,
            linux = 256,
            other = 256
        });

        public static BasisSettingsBinding<bool> UsePerfLimitJiggleBones = new("useperflimitjigglebones", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfJiggleBones = new("maxperfjigglebones", new BasisPlatformDefault<float>
        {
            windows = 128,
            android = 48,
            ios = 48,
            linux = 128,
            other = 128
        });

        public static BasisSettingsBinding<bool> UsePerfLimitJiggleColliders = new("useperflimitjigglecolliders", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfJiggleColliders = new("maxperfjigglecolliders", new BasisPlatformDefault<float>
        {
            windows = 64,
            android = 16,
            ios = 16,
            linux = 64,
            other = 64
        });

        public static BasisSettingsBinding<bool> UseJiggleCollisionFrustumCull = new("usejigglecollisionfrustumcull", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> UseJiggleCollisionDistanceCull = new("usejigglecollisiondistancecull", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> JiggleCollisionCullDistance = new("jigglecollisionculldistance", new BasisPlatformDefault<float>
        {
            windows = 20,
            android = 10,
            ios = 10,
            linux = 20,
            other = 20
        });
        public static BasisSettingsBinding<float> JiggleCullFrustumExpansion = new("jigglecullfrustumexpansion", new BasisPlatformDefault<float>(1.2f));
        public static BasisSettingsBinding<float> JiggleCullNearKeepRadius = new("jigglecullnearkeepradius", new BasisPlatformDefault<float>(2.5f));
        public static BasisSettingsBinding<float> JiggleBroadPhaseCellSize = new("jigglebroadphasecellsize", new BasisPlatformDefault<float>(0.5f));

        // Distance-based reduction of remote avatars' jiggle colliders: past Near drop the finger
        // colliders (hands become a single sphere), past Mid drop the arm/foot colliders too, past
        // Far remove them entirely.
        public static BasisSettingsBinding<bool> UseJiggleColliderDistanceLod = new("usejigglecolliderdistancelod", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> JiggleColliderLodNearDistance = new("jigglecolliderlodneardistance", new BasisPlatformDefault<float>
        {
            windows = 25,
            android = 10,
            ios = 10,
            linux = 25,
            other = 25
        });
        public static BasisSettingsBinding<float> JiggleColliderLodMidDistance = new("jigglecolliderlodmiddistance", new BasisPlatformDefault<float>
        {
            windows = 50,
            android = 20,
            ios = 20,
            linux = 50,
            other = 50
        });
        public static BasisSettingsBinding<float> JiggleColliderLodFarDistance = new("jigglecolliderlodfardistance", new BasisPlatformDefault<float>
        {
            windows = 100,
            android = 40,
            ios = 40,
            linux = 100,
            other = 100
        });

        // Animators default on at 1 — extras are a common perf trap (every child
        // Animator ticks every frame). Excess Animators are trimmed, not blocked.
        public static BasisSettingsBinding<bool> UsePerfLimitAnimators = new("useperflimitanimators", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfAnimators = new("maxperfanimators", new BasisPlatformDefault<float>(1));

        // Hard-block cap on skinned bone count. Unlike the others this one is intended
        // as a guard rail for the genuinely bad bundles (tens of thousands of bones)
        // rather than as a daily-driver cap, hence the high default.
        public static BasisSettingsBinding<bool> UsePerfLimitBones = new("useperflimitbones", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfBones = new("maxperfbones", new BasisPlatformDefault<float>(16384));

        // Lights default on at 0 — dynamic Light components on an avatar force an
        // extra pass per frame; not safe at crowd scale, so trim them all by default.
        public static BasisSettingsBinding<bool> UsePerfLimitLights = new("useperflimitlights", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfLights = new("maxperflights", new BasisPlatformDefault<float>(0));

        // Particles default on at 4 — a few ambient systems are fine, more is a
        // hand grenade in a crowd. Trimmed, not blocked.
        public static BasisSettingsBinding<bool> UsePerfLimitParticleSystems = new("useperflimitparticlesystems", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfParticleSystems = new("maxperfparticlesystems_v2", new BasisPlatformDefault<float>
        {
            windows = 4,
            android = 1,
            ios = 1,
            linux = 4,
            other = 4
        });

        // Trails default on at 4.
        public static BasisSettingsBinding<bool> UsePerfLimitTrailRenderers = new("useperflimittrailrenderers", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfTrailRenderers = new("maxperftrailrenderers_v2", new BasisPlatformDefault<float>
        {
            windows = 4,
            android = 1,
            ios = 1,
            linux = 4,
            other = 4
        });

        // Line renderers default on at 4.
        public static BasisSettingsBinding<bool> UsePerfLimitLineRenderers = new("useperflimitlinerenderers", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfLineRenderers = new("maxperflinerenderers_v2", new BasisPlatformDefault<float>
        {
            windows = 4,
            android = 1,
            ios = 1,
            linux = 4,
            other = 4
        });

        // Cloth defaults on at 1 — Unity Cloth is CPU-expensive per instance.
        public static BasisSettingsBinding<bool> UsePerfLimitCloth = new("useperflimitcloth", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfCloth = new("maxperfcloth", new BasisPlatformDefault<float>
        {
            windows = 1,
            android = 0,
            ios = 0,
            linux = 1,
            other = 1
        });

        // Unity colliders default on at 1 — physics colliders on an avatar
        // aren't free. Jiggle colliders are a separate limit.
        public static BasisSettingsBinding<bool> UsePerfLimitColliders = new("useperflimitcolliders", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfColliders = new("maxperfcolliders", new BasisPlatformDefault<float>(1));

        // Cilbox script behaviours default on at 5 — every CilboxProxy on a remote
        // avatar is one sandboxed MonoBehaviour with its own Update/FixedUpdate tick.
        public static BasisSettingsBinding<bool> UsePerfLimitCilboxBehaviours = new("useperflimitcilboxbehaviours", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfCilboxBehaviours = new("maxperfcilboxbehaviours", new BasisPlatformDefault<float>
        {
            windows = 5,
            android = 2,
            ios = 2,
            linux = 5,
            other = 5
        });

        public static BasisSettingsBinding<float> AvatarMeshLOD = new("avatarmeshlod", new BasisPlatformDefault<float>
        {
            windows = 0.05f,
            android = 0.1f,
            ios = 0.1f,
            linux = 0.05f,
            other = 0.05f
        });

        // Skins remote avatars with fewer bone influences per vertex as they drop through the mesh
        // LOD levels (4 / 2 / 1 influences). Distant avatars are left on the full influence set
        // otherwise, which is invisible past a few metres and pays for itself on every skinned vertex.
        public static BasisSettingsBinding<bool> UseAvatarSkinLod = new("useavatarskinlod", new BasisPlatformDefault<bool>(true));

        // Stops remote avatars casting shadows once they drop past the two near mesh LOD levels.
        // Nothing wrote shadowCastingMode on a remote renderer before this, so a distant crowd was
        // paying a full extra skinned draw per shadow cascade each.
        public static BasisSettingsBinding<bool> UseAvatarShadowLod = new("useavatarshadowlod", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> UseAvatarVisibilityCull = new("useavatarvisibilitycull", new BasisPlatformDefault<bool>
        {
            windows = false,
            android = true,
            ios = true,
            linux = false,
            other = false
        });

        // URP's GPU Resident Drawer occlusion test, for world geometry (MeshRenderer only — avatars
        // are skinned and the drawer never sees them). Read at boot only: the drawer snapshots its
        // settings when it is built, and rebuilding it re-registers every renderer in the scene, so
        // changing this mid-session waits for a restart. See BasisGpuOcclusionCulling.
        public static BasisSettingsBinding<bool> UseGpuOcclusionCulling = new("usegpuocclusionculling", new BasisPlatformDefault<bool>(false));

        // Graphics API name (GraphicsDeviceType.ToString()) the player should start on. Empty means
        // whatever the build picks. Unity only takes this from the command line, so a change lands
        // by relaunching into it — see BasisGraphicsApiSelection.
        public static BasisSettingsBinding<string> GraphicsApi = new("graphicsapi", new BasisPlatformDefault<string>(string.Empty));

        // Shows the baked far avatar carried in a player's bundle (driven by the same
        // networked bone data) whenever their real avatar isn't loaded — past the max avatar
        // range, mid-download, or platform-missing. When off, those players show the loading
        // dummy instead.
        public static BasisSettingsBinding<bool> UseAvatarFarLod = new("useavatarfarlod", new BasisPlatformDefault<bool>(true));
        // Far avatars now begin where the max avatar range ends — no separate distance.
        //public static BasisSettingsBinding<float> AvatarFarLodDistance = new("avatarfarloddistance", new BasisPlatformDefault<float>
        //{
        //    windows = 20,
        //    android = 12,
        //    ios = 12,
        //    linux = 20,
        //    other = 20
        //});

        public static BasisSettingsBinding<float> GlobalMeshLOD = new("globalmeshlod", new BasisPlatformDefault<float>
        {
            windows = 0,
            android = 30,
            ios = 30,
            linux = 0,
            other = 0
        });

        /// <summary>
        /// When enabled, the local head duplicate (shadow-only clone used so the
        /// local player's own head casts shadows without rendering the head mesh
        /// in their view) mirrors the source mesh's blendshape weights every
        /// frame. When disabled (default), the duplicate keeps its initial
        /// blendshape pose and the per-frame ScheduleReadBlendShapes /
        /// ApplyShadowCloneBlendShapes work is skipped entirely.
        /// </summary>
        public static BasisSettingsBinding<bool> LocalHeadBlendShapes = new("localheadblendshapes", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<string> SitStand = new("seatedmode", new BasisPlatformDefault<string>(SettingsProviderIK.SeatedMode_Standing));

        public static BasisSettingsBinding<string> VSync = new("verticalsync", new BasisPlatformDefault<string>
        {
            windows = "On",
            android = "On",
            ios = "On",
            linux = "Capped",
            other = "On"
        });

        /// <summary>
        /// Display refresh rate to ask the headset for. Auto keeps whatever the OpenXR runtime
        /// chose. A rate the headset does not enumerate falls back to the closest one it does.
        /// </summary>
        public static BasisSettingsBinding<string> HeadsetRefreshRate = new("headsetrefreshrate", new BasisPlatformDefault<string>("Auto"));

        public static BasisSettingsBinding<float> RenderResolution = new("render resolution", new BasisPlatformDefault<float>(1));

        public static BasisSettingsBinding<bool> DynamicResolutionEnabled = new("dynamicresolutionenabled", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<float> DynamicResolutionMinimumScale = new("dynamicresolutionminimumscale", new BasisPlatformDefault<float>(0.6f));

        public static BasisSettingsBinding<float> DynamicResolutionMaximumScale = new("dynamicresolutionmaximumscale", new BasisPlatformDefault<float>(1f));

        public static BasisSettingsBinding<bool> DynamicResolutionTargetOverride = new("dynamicresolutiontargetoverride", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<float> DynamicResolutionTargetFrameRate = new("dynamicresolutiontargetframerate", new BasisPlatformDefault<float>
        {
            windows = 90,
            android = 72,
            ios = 72,
            linux = 90,
            other = 90
        });

        public static BasisSettingsBinding<string> MicrophoneMode = new("microphonemode", new BasisPlatformDefault<string>("onactivation"));

        public static BasisSettingsBinding<float> P2PAvatarSyncRate = new("p2pavatarsyncrate", new BasisPlatformDefault<float>(60));

        public static BasisSettingsBinding<bool> P2PVoiceBitrateOverride = new("p2pvoicebitrateoverride", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<float> P2PVoiceBitrate = new("p2pvoicebitrate", new BasisPlatformDefault<float>(32000));

        public static BasisSettingsBinding<bool> DisableDirectConnections = new("disabledirectconnections", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<string> MicStartBehavior = new("micstartbehavior", new BasisPlatformDefault<string>(BasisLocalMicrophoneDriver.SettingStartOff));

        public static BasisSettingsBinding<string> MicMuteBehavior = new("micmutebehavior", new BasisPlatformDefault<string>(BasisLocalMicrophoneDriver.SettingMuteShutdown));

        public static BasisSettingsBinding<bool> TalkToNoOne = new("talktonoone", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> UseAutomaticGain = new("automaticgainenabled", new BasisPlatformDefault<bool>
        {
            windows = true,
            android = true,
            ios = true,
            linux = true,
            other = true
        });

        // ---------------- NETWORKING ----------------
        public static BasisSettingsBinding<bool> AutoConnect = new("autoconnect", new BasisPlatformDefault<bool>(false));

        // Remote-player jitter buffer target depth (packets to stay behind by).
        // Higher = smoother under network jitter, more latency. Default 2 (~100ms at 50ms sync).
        // Only takes effect when NetworkJitterBufferOverride is on — otherwise the default
        // adaptive depth (2/3/4) is used.
        public static BasisSettingsBinding<bool> NetworkJitterBufferOverride = new("networkjitterbufferoverride", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> NetworkJitterBufferDepth = new("networkjitterbufferdepth", new BasisPlatformDefault<float>(2f));

        // ---------------- DEVICE SWAP MODE ----------------
        /// <summary>
        /// Controls how the system handles switching between VR and Desktop modes.
        /// "Shutdown Runtime" — full XR shutdown on swap.
        /// "Auto Swap" — automatically swaps based on headset presence, keeping XR alive. (default).
        /// </summary>
        public static BasisSettingsBinding<string> SwapMode = new("swap_mode", new BasisPlatformDefault<string>("Auto Swap"));

        public const string SwapMode_Shutdown = "Shutdown Runtime";
        public const string SwapMode_AutoSwap = "Auto Swap";

        /// <summary>
        /// Whether the headset's presence sensor is allowed to drive the swap. Off leaves the sensor
        /// readable but stops it changing modes, which is the way out when a headset reports presence
        /// wrongly — a sensor stuck unworn otherwise drops the user to Desktop and keeps them there.
        /// Only consulted under <see cref="SwapMode_AutoSwap"/>; manual mode switching is unaffected.
        /// </summary>
        public static BasisSettingsBinding<bool> UsePresenceSensor = new("use_presence_sensor", new BasisPlatformDefault<bool>(true));

        // ---------------- TRACKER VISUALS ----------------
        /// <summary>
        /// Chooses what is rendered for tracked input devices (controllers/trackers/etc.) while
        /// tracker visuals are active (e.g. during calibration).
        /// "Off" — nothing. "Markers" — the generic placeholder marker (default). "Device Models" — the real
        /// device model loaded from the XR runtime, falling back to a marker when unavailable.
        /// </summary>
        public static BasisSettingsBinding<string> TrackerVisuals = new("tracker_visuals_v2", new BasisPlatformDefault<string>(TrackerVisuals_Markers));

        public const string TrackerVisuals_Off = "Off";
        public const string TrackerVisuals_Markers = "Markers";
        public const string TrackerVisuals_DeviceModels = "Device Models";

        // ---------------- INTERACTIONS ----------------
        public static BasisSettingsBinding<bool> DisableSeats = new("disableseats", new BasisPlatformDefault<bool>(false));

        // Hide remote players' handheld camera pucks (and their owner tags) locally.
        public static BasisSettingsBinding<bool> HideRemoteCameraPucks = new("hideremotecamerapucks", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> DisablePropPickup = new("disableproppickup", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> DisableVRAutoHold = new("disablevrautohold", new BasisPlatformDefault<bool>(false));
        // Master switch for jiggle grab-and-pull: off = never initiate, never be grabbed, never apply observed grabs.
        public static BasisSettingsBinding<bool> JiggleGrabInteractions = new("jigglegrabinteractions", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> UIHaptics = new("uihaptics", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> UIClickPressThreshold = new("uiclickpressthreshold", new BasisPlatformDefault<float>(0.5f));
        public static BasisSettingsBinding<float> UIClickReleaseThreshold = new("uiclickreleasethreshold", new BasisPlatformDefault<float>(0.4f));

        // Seconds a fully pushed thumbstick takes to cross a bound slider's whole range
        // (BasisPanelJoystickBind). The response is squared, so a nudge is far slower than this.
        public static BasisSettingsBinding<float> JoystickBindSweepSeconds = new("joystickbindsweepseconds", new BasisPlatformDefault<float>(4f));

        // ---------------- VR FINGER TOUCH ----------------
        // Direct fingertip presses on world-space UI in VR (BasisDirectTouch).
        // Distances are meters; defaults mirror the original hardcoded tuning.
        public static BasisSettingsBinding<bool> DisableVRFingerTouch = new("disablevrfingertouch", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<string> FingerTouchFinger = new("fingertouchfinger", new BasisPlatformDefault<string>(FingerTouchFinger_Index));
        public static BasisSettingsBinding<string> FingerTouchHands = new("fingertouchhands", new BasisPlatformDefault<string>(FingerTouchHands_Both));
        public static BasisSettingsBinding<float> FingerTouchTipOffset = new("fingertouchtipoffset", new BasisPlatformDefault<float>(0.015f));
        public static BasisSettingsBinding<float> FingerTouchFingerLength = new("fingertouchfingerlength", new BasisPlatformDefault<float>(0.1f));
        public static BasisSettingsBinding<float> FingerTouchRadius = new("fingertouchradius_v2", new BasisPlatformDefault<float>(0.00375f));
        public static BasisSettingsBinding<float> FingerTouchHoverDistance = new("fingertouchhoverdistance", new BasisPlatformDefault<float>(0.04f));
        public static BasisSettingsBinding<float> FingerTouchPressDepth = new("fingertouchpressdepth", new BasisPlatformDefault<float>(0.01f));
        public static BasisSettingsBinding<float> FingerTouchReleaseDistance = new("fingertouchreleasedistance", new BasisPlatformDefault<float>(0.025f));
        public static BasisSettingsBinding<float> FingerTouchScrollSensitivity = new("fingertouchscrollsensitivity", new BasisPlatformDefault<float>(800f));
        public static BasisSettingsBinding<bool> FingerTouchHaptics = new("fingertouchhaptics", new BasisPlatformDefault<bool>(true));

        public const string FingerTouchFinger_Thumb = "Thumb";
        public const string FingerTouchFinger_Index = "Index";
        public const string FingerTouchFinger_Middle = "Middle";
        public const string FingerTouchFinger_Ring = "Ring";
        public const string FingerTouchFinger_Little = "Little";

        public const string FingerTouchHands_Both = "Both";
        public const string FingerTouchHands_Left = "Left";
        public const string FingerTouchHands_Right = "Right";

        /// <summary>
        /// How keyboard/mouse/gamepad input is pumped while in OpenVR mode; Desktop and OpenXR
        /// always pump every frame. Values map to <see cref="Basis.Scripts.Device_Management.BasisInputPumpMode"/>.
        /// </summary>
        public static BasisSettingsBinding<string> DesktopInputInVR = new("desktopinputinvr", new BasisPlatformDefault<string>(DesktopInputInVR_Adaptive));

        public const string DesktopInputInVR_Adaptive = "Adaptive";
        public const string DesktopInputInVR_AlwaysOn = "Always On";
        public const string DesktopInputInVR_Off = "Off";
        public static BasisSettingsBinding<bool> ForceGridSnap = new("forcegridsnap", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> GridSnapSize = new("gridsnapsize", new BasisPlatformDefault<float>(0.25f));
        public static BasisSettingsBinding<bool> ForceRotationSnap = new("forcerotationsnap", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> RotationSnapDegrees = new("rotationsnapdegrees", new BasisPlatformDefault<float>(15f));

        // ---------------- NOTIFICATIONS ----------------
        public static BasisSettingsBinding<bool> JoinNotifications = new("joinnotifications", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> LeaveNotifications = new("leavenotifications", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> ExceptionNotifications = new("exceptionnotifications", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> ErrorNotifications = new("errornotifications", new BasisPlatformDefault<bool>(false));

        // ---------------- CHAT ----------------
        /// <summary>
        /// Global kill switch for text chat. When true, incoming chat is dropped before
        /// being applied to nameplates and local sends are short-circuited.
        /// </summary>
        public static BasisSettingsBinding<bool> ChatDisabled = new("chatdisabled", new BasisPlatformDefault<bool>(false));

        // Commented out 2026-08-04: never referenced anywhere — leftover scaffolding.
        //public static BasisSettingsBinding<bool> FalseBinding = new("falsebinding", new BasisPlatformDefault<bool>(false));

        //public static BasisSettingsBinding<bool> TrueBinding = new("truebinding", new BasisPlatformDefault<bool>(false));

        // ---------------- CAMERA / PHOTO ----------------
        public const string PhotoTagging_NoOne = "No One";
        public const string PhotoTagging_EveryoneInPhoto = "Everyone In Photo";
        public const string PhotoTagging_JustMe = "Just Me";

        public static BasisSettingsBinding<string> PhotoMetadataTagging = new("photometadatatagging", new BasisPlatformDefault<string>(PhotoTagging_NoOne));

        // Additional photo metadata, all opt-in (off by default).
        public static BasisSettingsBinding<bool> PhotoEmbedCameraSettings = new("photoembedcamerasettings", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> PhotoEmbedCaptureInfo = new("photoembedcaptureinfo", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> PhotoEmbedPhotographer = new("photoembedphotographer", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> PhotoEmbedWorld = new("photoembedworld", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> PhotoEmbedPersonDetails = new("photoembedpersondetails", new BasisPlatformDefault<bool>(false));

        // ---------------- RAYCAST / INTERACTION VISUALS ----------------
        public static BasisSettingsBinding<float> RaycastLineWidth = new("raycastlinewidth", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<string> RaycastLineColor = new("raycastlinecolor", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> HighlightColor = new("highlightcolor", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> PickupLineColor = new("pickuplinecolor", new BasisPlatformDefault<string>(""));

        // ---------------- GLOBAL ONE EURO PARAMS ----------------
        public static BasisSettingsBinding<float> FBIKMinCutoff = new("fbikmincutoff", new BasisPlatformDefault<float>(5.5f));

        public static BasisSettingsBinding<float> FBIKBeta = new("fbikbeta", new BasisPlatformDefault<float>(3.25f));

        public static BasisSettingsBinding<float> FBIKDerivativeCutoff = new("fbikderivativecutoff", new BasisPlatformDefault<float>(3f));

        public static BasisSettingsBinding<float> FBIKPositionSmoothingHz =
            new("fbikpositionsmoothinghz", new BasisPlatformDefault<float>(20f));

        public static BasisSettingsBinding<float> FBIKRotationSmoothingHz =
            new("fbikrotationsmoothinghz", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<float> FBIKSmoothingStrength =
            new("fbiksmoothingstrength", new BasisPlatformDefault<float>(2.5f));

        // ---------------- PER-GROUP SMOOTHING PROFILES ----------------
        // One profile per tracker group so hardware with different noise characteristics can be tuned
        // independently. Preset picks a tuning curve, or "Custom" to use the five sliders below instead.
        public struct SmoothingGroupBindings
        {
            public string NameKey;
            public BasisSettingsBinding<string> Preset;
            // Superseded by the "Custom" preset entry. Kept bound and persisted so the toggle can be
            // restored without stranding saved values; nothing reads it.
            public BasisSettingsBinding<bool> Custom;
            public BasisSettingsBinding<float> MinCutoff;
            public BasisSettingsBinding<float> Beta;
            public BasisSettingsBinding<float> Strength;
            public BasisSettingsBinding<float> PositionHz;
            public BasisSettingsBinding<float> RotationHz;
        }

        private const string SmoothingPresetDefault = Basis.Scripts.Drivers.BasisSmoothingProfiles.PresetAuto;

        public static BasisSettingsBinding<string> FBIKSmoothPresetHead = new("fbiksmoothpresethead_v2", new BasisPlatformDefault<string>(SmoothingPresetDefault));
        public static BasisSettingsBinding<bool> FBIKSmoothCustomHead = new("fbiksmoothcustomhead", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> FBIKSmoothMinCutoffHead = new("fbiksmoothmincutoffhead", new BasisPlatformDefault<float>(5.5f));
        public static BasisSettingsBinding<float> FBIKSmoothBetaHead = new("fbiksmoothbetahead", new BasisPlatformDefault<float>(3.25f));
        public static BasisSettingsBinding<float> FBIKSmoothStrengthHead = new("fbiksmoothstrengthhead", new BasisPlatformDefault<float>(2.5f));
        public static BasisSettingsBinding<float> FBIKSmoothPosHzHead = new("fbiksmoothposhzhead", new BasisPlatformDefault<float>(20f));
        public static BasisSettingsBinding<float> FBIKSmoothRotHzHead = new("fbiksmoothrothzhead", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<string> FBIKSmoothPresetHands = new("fbiksmoothpresethands_v2", new BasisPlatformDefault<string>(SmoothingPresetDefault));
        public static BasisSettingsBinding<bool> FBIKSmoothCustomHands = new("fbiksmoothcustomhands", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> FBIKSmoothMinCutoffHands = new("fbiksmoothmincutoffhands", new BasisPlatformDefault<float>(5.5f));
        public static BasisSettingsBinding<float> FBIKSmoothBetaHands = new("fbiksmoothbetahands", new BasisPlatformDefault<float>(3.25f));
        public static BasisSettingsBinding<float> FBIKSmoothStrengthHands = new("fbiksmoothstrengthhands", new BasisPlatformDefault<float>(2.5f));
        public static BasisSettingsBinding<float> FBIKSmoothPosHzHands = new("fbiksmoothposhzhands", new BasisPlatformDefault<float>(20f));
        public static BasisSettingsBinding<float> FBIKSmoothRotHzHands = new("fbiksmoothrothzhands", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<string> FBIKSmoothPresetElbows = new("fbiksmoothpresetelbows_v2", new BasisPlatformDefault<string>(SmoothingPresetDefault));
        public static BasisSettingsBinding<bool> FBIKSmoothCustomElbows = new("fbiksmoothcustomelbows", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> FBIKSmoothMinCutoffElbows = new("fbiksmoothmincutoffelbows", new BasisPlatformDefault<float>(5.5f));
        public static BasisSettingsBinding<float> FBIKSmoothBetaElbows = new("fbiksmoothbetaelbows", new BasisPlatformDefault<float>(3.25f));
        public static BasisSettingsBinding<float> FBIKSmoothStrengthElbows = new("fbiksmoothstrengthelbows", new BasisPlatformDefault<float>(2.5f));
        public static BasisSettingsBinding<float> FBIKSmoothPosHzElbows = new("fbiksmoothposhzelbows", new BasisPlatformDefault<float>(20f));
        public static BasisSettingsBinding<float> FBIKSmoothRotHzElbows = new("fbiksmoothrothzelbows", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<string> FBIKSmoothPresetChest = new("fbiksmoothpresetchest_v2", new BasisPlatformDefault<string>(SmoothingPresetDefault));
        public static BasisSettingsBinding<bool> FBIKSmoothCustomChest = new("fbiksmoothcustomchest", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> FBIKSmoothMinCutoffChest = new("fbiksmoothmincutoffchest", new BasisPlatformDefault<float>(5.5f));
        public static BasisSettingsBinding<float> FBIKSmoothBetaChest = new("fbiksmoothbetachest", new BasisPlatformDefault<float>(3.25f));
        public static BasisSettingsBinding<float> FBIKSmoothStrengthChest = new("fbiksmoothstrengthchest", new BasisPlatformDefault<float>(2.5f));
        public static BasisSettingsBinding<float> FBIKSmoothPosHzChest = new("fbiksmoothposhzchest", new BasisPlatformDefault<float>(20f));
        public static BasisSettingsBinding<float> FBIKSmoothRotHzChest = new("fbiksmoothrothzchest", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<string> FBIKSmoothPresetHips = new("fbiksmoothpresethips_v2", new BasisPlatformDefault<string>(SmoothingPresetDefault));
        public static BasisSettingsBinding<bool> FBIKSmoothCustomHips = new("fbiksmoothcustomhips", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> FBIKSmoothMinCutoffHips = new("fbiksmoothmincutoffhips", new BasisPlatformDefault<float>(5.5f));
        public static BasisSettingsBinding<float> FBIKSmoothBetaHips = new("fbiksmoothbetahips", new BasisPlatformDefault<float>(3.25f));
        public static BasisSettingsBinding<float> FBIKSmoothStrengthHips = new("fbiksmoothstrengthhips", new BasisPlatformDefault<float>(2.5f));
        public static BasisSettingsBinding<float> FBIKSmoothPosHzHips = new("fbiksmoothposhzhips", new BasisPlatformDefault<float>(20f));
        public static BasisSettingsBinding<float> FBIKSmoothRotHzHips = new("fbiksmoothrothzhips", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<string> FBIKSmoothPresetKnees = new("fbiksmoothpresetknees_v2", new BasisPlatformDefault<string>(SmoothingPresetDefault));
        public static BasisSettingsBinding<bool> FBIKSmoothCustomKnees = new("fbiksmoothcustomknees", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> FBIKSmoothMinCutoffKnees = new("fbiksmoothmincutoffknees", new BasisPlatformDefault<float>(5.5f));
        public static BasisSettingsBinding<float> FBIKSmoothBetaKnees = new("fbiksmoothbetaknees", new BasisPlatformDefault<float>(3.25f));
        public static BasisSettingsBinding<float> FBIKSmoothStrengthKnees = new("fbiksmoothstrengthknees", new BasisPlatformDefault<float>(2.5f));
        public static BasisSettingsBinding<float> FBIKSmoothPosHzKnees = new("fbiksmoothposhzknees", new BasisPlatformDefault<float>(20f));
        public static BasisSettingsBinding<float> FBIKSmoothRotHzKnees = new("fbiksmoothrothzknees", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<string> FBIKSmoothPresetFeet = new("fbiksmoothpresetfeet_v2", new BasisPlatformDefault<string>(SmoothingPresetDefault));
        public static BasisSettingsBinding<bool> FBIKSmoothCustomFeet = new("fbiksmoothcustomfeet", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> FBIKSmoothMinCutoffFeet = new("fbiksmoothmincutofffeet", new BasisPlatformDefault<float>(5.5f));
        public static BasisSettingsBinding<float> FBIKSmoothBetaFeet = new("fbiksmoothbetafeet", new BasisPlatformDefault<float>(3.25f));
        public static BasisSettingsBinding<float> FBIKSmoothStrengthFeet = new("fbiksmoothstrengthfeet", new BasisPlatformDefault<float>(2.5f));
        public static BasisSettingsBinding<float> FBIKSmoothPosHzFeet = new("fbiksmoothposhzfeet", new BasisPlatformDefault<float>(20f));
        public static BasisSettingsBinding<float> FBIKSmoothRotHzFeet = new("fbiksmoothrothzfeet", new BasisPlatformDefault<float>(25f));

        // Order MUST match Basis.Scripts.Drivers.BasisSmoothingGroup.
        public static readonly SmoothingGroupBindings[] FBIKSmoothingGroups =
        {
            new() { NameKey = "settings.bodyTracking.smoothing.group.head", Preset = FBIKSmoothPresetHead, Custom = FBIKSmoothCustomHead, MinCutoff = FBIKSmoothMinCutoffHead, Beta = FBIKSmoothBetaHead, Strength = FBIKSmoothStrengthHead, PositionHz = FBIKSmoothPosHzHead, RotationHz = FBIKSmoothRotHzHead },
            new() { NameKey = "settings.bodyTracking.smoothing.group.hands", Preset = FBIKSmoothPresetHands, Custom = FBIKSmoothCustomHands, MinCutoff = FBIKSmoothMinCutoffHands, Beta = FBIKSmoothBetaHands, Strength = FBIKSmoothStrengthHands, PositionHz = FBIKSmoothPosHzHands, RotationHz = FBIKSmoothRotHzHands },
            new() { NameKey = "settings.bodyTracking.smoothing.group.elbows", Preset = FBIKSmoothPresetElbows, Custom = FBIKSmoothCustomElbows, MinCutoff = FBIKSmoothMinCutoffElbows, Beta = FBIKSmoothBetaElbows, Strength = FBIKSmoothStrengthElbows, PositionHz = FBIKSmoothPosHzElbows, RotationHz = FBIKSmoothRotHzElbows },
            new() { NameKey = "settings.bodyTracking.smoothing.group.chest", Preset = FBIKSmoothPresetChest, Custom = FBIKSmoothCustomChest, MinCutoff = FBIKSmoothMinCutoffChest, Beta = FBIKSmoothBetaChest, Strength = FBIKSmoothStrengthChest, PositionHz = FBIKSmoothPosHzChest, RotationHz = FBIKSmoothRotHzChest },
            new() { NameKey = "settings.bodyTracking.smoothing.group.hips", Preset = FBIKSmoothPresetHips, Custom = FBIKSmoothCustomHips, MinCutoff = FBIKSmoothMinCutoffHips, Beta = FBIKSmoothBetaHips, Strength = FBIKSmoothStrengthHips, PositionHz = FBIKSmoothPosHzHips, RotationHz = FBIKSmoothRotHzHips },
            new() { NameKey = "settings.bodyTracking.smoothing.group.knees", Preset = FBIKSmoothPresetKnees, Custom = FBIKSmoothCustomKnees, MinCutoff = FBIKSmoothMinCutoffKnees, Beta = FBIKSmoothBetaKnees, Strength = FBIKSmoothStrengthKnees, PositionHz = FBIKSmoothPosHzKnees, RotationHz = FBIKSmoothRotHzKnees },
            new() { NameKey = "settings.bodyTracking.smoothing.group.feet", Preset = FBIKSmoothPresetFeet, Custom = FBIKSmoothCustomFeet, MinCutoff = FBIKSmoothMinCutoffFeet, Beta = FBIKSmoothBetaFeet, Strength = FBIKSmoothStrengthFeet, PositionHz = FBIKSmoothPosHzFeet, RotationHz = FBIKSmoothRotHzFeet },
        };

        // ---------------- HIPS ----------------
        public static BasisSettingsBinding<bool> FBIKHipsSmoothPos =
            new("fbikhipssmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKHipsSmoothRot =
            new("fbikhipssmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKHipsEuroPos =
            new("fbikhipseuropos", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<bool> FBIKHipsEuroRot =
            new("fbikhipseurorot", new BasisPlatformDefault<bool>(true));

        // ---------------- HEAD ----------------
        public static BasisSettingsBinding<bool> FBIKHeadSmoothPos =
            new("fbikheadsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKHeadSmoothRot =
            new("fbikheadsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKHeadEuroPos =
            new("fbikheadeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKHeadEuroRot =
            new("fbikheadeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT FOOT ----------------
        public static BasisSettingsBinding<bool> FBIKLeftFootSmoothPos =
            new("fbikleftfootsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftFootSmoothRot =
            new("fbikleftfootsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftFootEuroPos =
            new("fbikleftfooteuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftFootEuroRot =
            new("fbikleftfooteurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT FOOT ----------------
        public static BasisSettingsBinding<bool> FBIKRightFootSmoothPos =
            new("fbikrightfootsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightFootSmoothRot =
            new("fbikrightfootsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightFootEuroPos =
            new("fbikrightfooteuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightFootEuroRot =
            new("fbikrightfooteurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- CHEST ----------------
        public static BasisSettingsBinding<bool> FBIKChestSmoothPos =
            new("fbikchestsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKChestSmoothRot =
            new("fbikchestsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKChestEuroPos =
            new("fbikchesteuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKChestEuroRot =
            new("fbikchesteurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT LOWER LEG ----------------
        public static BasisSettingsBinding<bool> FBIKLeftLowerLegSmoothPos =
            new("fbikleftlowerlegsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerLegSmoothRot =
            new("fbikleftlowerlegsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerLegEuroPos =
            new("fbikleftlowerlegeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerLegEuroRot =
            new("fbikleftlowerlegeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT LOWER LEG ----------------
        public static BasisSettingsBinding<bool> FBIKRightLowerLegSmoothPos =
            new("fbikrightlowerlegsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerLegSmoothRot =
            new("fbikrightlowerlegsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerLegEuroPos =
            new("fbikrightlowerlegeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerLegEuroRot =
            new("fbikrightlowerlegeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT HAND ----------------
        public static BasisSettingsBinding<bool> FBIKLeftHandSmoothPos =
            new("fbiklefthandsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftHandSmoothRot =
            new("fbiklefthandsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftHandEuroPos =
            new("fbikleftehandeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftHandEuroRot =
            new("fbikleftehandeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT HAND ----------------
        public static BasisSettingsBinding<bool> FBIKRightHandSmoothPos =
            new("fbikrighthandsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightHandSmoothRot =
            new("fbikrighthandsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightHandEuroPos =
            new("fbikrighthandeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightHandEuroRot =
            new("fbikrighthandeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT LOWER ARM ----------------
        public static BasisSettingsBinding<bool> FBIKLeftLowerArmSmoothPos =
            new("fbikleftlowerarmsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerArmSmoothRot =
            new("fbikleftlowerarmsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerArmEuroPos =
            new("fbikleftlowerarmeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftLowerArmEuroRot =
            new("fbikleftlowerarmeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT LOWER ARM ----------------
        public static BasisSettingsBinding<bool> FBIKRightLowerArmSmoothPos =
            new("fbikrightlowerarmsmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerArmSmoothRot =
            new("fbikrightlowerarmsmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerArmEuroPos =
            new("fbikrightlowerarmeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightLowerArmEuroRot =
            new("fbikrightlowerarmeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT TOE ----------------
        public static BasisSettingsBinding<bool> FBIKLeftToeSmoothPos =
            new("fbiklefttoesmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftToeSmoothRot =
            new("fbiklefttoesmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftToeEuroPos =
            new("fbiklefttoeeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftToeEuroRot =
            new("fbiklefttoeeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT TOE ----------------
        public static BasisSettingsBinding<bool> FBIKRightToeSmoothPos =
            new("fbikrighttoesmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightToeSmoothRot =
            new("fbikrighttoesmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightToeEuroPos =
            new("fbikrighttoeeuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightToeEuroRot = new("fbikrighttoeeurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- LEFT SHOULDER ----------------
        public static BasisSettingsBinding<bool> FBIKLeftShoulderSmoothPos = new("fbikleftshouldersmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftShoulderSmoothRot = new("fbikleftshouldersmoothrot", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<bool> FBIKLeftShoulderEuroPos = new("fbikleftshouldereuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKLeftShoulderEuroRot = new("fbikleftshouldereurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- RIGHT SHOULDER ----------------
        public static BasisSettingsBinding<bool> FBIKRightShoulderSmoothPos = new("fbikrightshouldersmoothpos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightShoulderSmoothRot = new("fbikrightshouldersmoothrot", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightShoulderEuroPos = new("fbikrightshouldereuropos", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FBIKRightShoulderEuroRot = new("fbikrightshouldereurorot", new BasisPlatformDefault<bool>(false));

        // ---------------- PER-BONE CALIBRATION ENABLE ----------------
        // Defaults match the legacy BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker hardcode
        // (true for FB tracker roles, false otherwise) — except the shoulders, which now
        // default off so calibration ignores them unless the user opts in.
        public static BasisSettingsBinding<bool> FBIKHipsUseCalibration = new("fbikhipsusecalibration", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKHeadUseCalibration = new("fbikheadusecalibration", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> FBIKLeftFootUseCalibration = new("fbikleftfootusecalibration", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKRightFootUseCalibration = new("fbikrightfootusecalibration", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKChestUseCalibration = new("fbikchestusecalibration", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKLeftLowerLegUseCalibration = new("fbikleftlowerlegusecalibration", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKRightLowerLegUseCalibration = new("fbikrightlowerlegusecalibration", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKLeftHandUseCalibration = new("fbiklefthandusecalibration", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> FBIKRightHandUseCalibration = new("fbikrighthandusecalibration", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> FBIKLeftLowerArmUseCalibration = new("fbikleftlowerarmusecalibration", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKRightLowerArmUseCalibration = new("fbikrightlowerarmusecalibration", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKLeftToeUseCalibration = new("fbiklefttoeusecalibration", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKRightToeUseCalibration = new("fbikrighttoeusecalibration", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKLeftShoulderUseCalibration = new("fbikleftshoulderusecalibration", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> FBIKRightShoulderUseCalibration = new("fbikrightshoulderusecalibration", new BasisPlatformDefault<bool>(false));

        // ---------------- CALIBRATION TOLERANCE ----------------
        // Sigma multiplier for the FBIK constellation classifier. 1 = stock acceptance
        // regions; higher widens every role's accept band so players with atypical body
        // proportions, high-mounted trackers, or an imperfect calibration pose can still
        // bind. Read in BasisAvatarIKStageCalibration.BuildPriors via GetCalibrationTolerance.
        public static BasisSettingsBinding<float> CalibrationTolerance = new("fbikcalibrationtolerance", new BasisPlatformDefault<float>(1f));

        /// <summary>
        /// Returns the per-role "use for calibration" binding, or null for roles that have
        /// no UI entry (e.g. CenterEye, Neck, Spine, UpperArm/UpperLeg, Mouth).
        /// </summary>
        public static BasisSettingsBinding<bool> GetCalibrationBinding(BasisBoneTrackedRole role)
        {
            return role switch
            {
                BasisBoneTrackedRole.Hips => FBIKHipsUseCalibration,
                BasisBoneTrackedRole.Head => FBIKHeadUseCalibration,
                BasisBoneTrackedRole.LeftFoot => FBIKLeftFootUseCalibration,
                BasisBoneTrackedRole.RightFoot => FBIKRightFootUseCalibration,
                BasisBoneTrackedRole.Chest => FBIKChestUseCalibration,
                BasisBoneTrackedRole.LeftLowerLeg => FBIKLeftLowerLegUseCalibration,
                BasisBoneTrackedRole.RightLowerLeg => FBIKRightLowerLegUseCalibration,
                BasisBoneTrackedRole.LeftHand => FBIKLeftHandUseCalibration,
                BasisBoneTrackedRole.RightHand => FBIKRightHandUseCalibration,
                BasisBoneTrackedRole.LeftLowerArm => FBIKLeftLowerArmUseCalibration,
                BasisBoneTrackedRole.RightLowerArm => FBIKRightLowerArmUseCalibration,
                BasisBoneTrackedRole.LeftToes => FBIKLeftToeUseCalibration,
                BasisBoneTrackedRole.RightToes => FBIKRightToeUseCalibration,
                BasisBoneTrackedRole.LeftShoulder => FBIKLeftShoulderUseCalibration,
                BasisBoneTrackedRole.RightShoulder => FBIKRightShoulderUseCalibration,
                _ => null,
            };
        }

        /// <summary>
        /// Replacement for the legacy BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker
        /// hardcode in calibration paths. Returns true if the role is exposed in the
        /// per-bone UI and the user has it enabled.
        /// </summary>
        public static bool IsRoleEnabledForCalibration(BasisBoneTrackedRole role)
        {
            BasisSettingsBinding<bool> binding = GetCalibrationBinding(role);
            return binding != null && binding.RawValue;
        }

        public static BasisSettingsBinding<string> VSyncCapFps = new("vsynccappedset", new BasisPlatformDefault<string>
        {
            windows = "120",
            android = "60",
            ios = "60",
            linux = "120",
            other = "120"
        });

        // ---------------- REMOTE PLAYER AUDIO ----------------
        // AudioSource
        public static BasisSettingsBinding<float> RAMinDistance = new("ra_mindistance", new BasisPlatformDefault<float>(0.5f));
        public static BasisSettingsBinding<float> RASpread = new("ra_spread", new BasisPlatformDefault<float>(70f));
        // Per-source doppler scale. Whether any pitch shift actually happens is
        // decided globally by Doppler Factor in AudioManager.asset, which the project
        // ships at 0 — so this is the per-source multiplier on a global that is
        // currently off, not a live pitch shift. Note the prefab authors DopplerLevel
        // 0 and this binding overwrites it at load; the two disagree by design.
        public static BasisSettingsBinding<float> RADopplerLevel = new("ra_dopplerlevel", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> RASpatialBlend = new("ra_spatialblend", new BasisPlatformDefault<float>(1f));

        // Steam Audio - HRTF
        public static BasisSettingsBinding<bool> RADirectBinaural = new("ra_directbinaural", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> RAPerspectiveCorrection = new("ra_perspectivecorrection", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<string> RAInterpolation = new("ra_interpolation_v2", new BasisPlatformDefault<string>("Bilinear"));
        public static BasisSettingsBinding<string> RAHrtfProfile = new("ra_hrtfprofile", new BasisPlatformDefault<string>("Default"));

        // Steam Audio - Propagation
        public static BasisSettingsBinding<bool> RADistanceAttenuation = new("ra_distanceattenuation", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> RAAirAbsorption = new("ra_airabsorption", new BasisPlatformDefault<bool>(true));

        // Steam Audio - Directivity
        public static BasisSettingsBinding<bool> RADirectivity = new("ra_directivity", new BasisPlatformDefault<bool>(true));
        // 0.25 broadband overshot measured speech directivity by up to 2 dB behind
        // the talker. The mouth-directivity shelf now carries the frequency-dependent
        // part, and 0.10 is what pairs with it to land on the measured curve.
        // Key bumped so the retune actually reaches existing installs.
        public static BasisSettingsBinding<float> RADipoleWeight = new("ra_dipoleweight_v2", new BasisPlatformDefault<float>(BasisVoiceAcoustics.DipoleWeight));
        public static BasisSettingsBinding<float> RADipolePower = new("ra_dipolepower", new BasisPlatformDefault<float>(1f));

        // Steam Audio - Occlusion
        public static BasisSettingsBinding<bool> RAOcclusion = new("ra_occlusion", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<string> RAOcclusionType = new("ra_occlusiontype_v2", new BasisPlatformDefault<string>("volumetric"));
        public static BasisSettingsBinding<float> RAOcclusionRadius = new("ra_occlusionradius", new BasisPlatformDefault<float>(0.15f));
        public static BasisSettingsBinding<float> RAOcclusionSamples = new("ra_occlusionsamples_v2", new BasisPlatformDefault<float>
        {
            windows = 32f,
            android = 16f,
            linux = 32f,
            ios = 16f,
            other = 32f
        });

        // Steam Audio - Transmission
        public static BasisSettingsBinding<bool> RATransmission = new("ra_transmission", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<string> RATransmissionType = new("ra_transmissiontype", new BasisPlatformDefault<string>("frequency dependent"));
        public static BasisSettingsBinding<float> RAMaxTransmissionSurfaces = new("ra_maxtransmissionsurfaces_v2", new BasisPlatformDefault<float>
        {
            windows = 2f,
            android = 1f,
            linux = 2f,
            ios = 1f,
            other = 2f
        });

        // AudioSource - Rolloff
        public static BasisSettingsBinding<string> RARolloffMode = new("ra_rolloffmode", new BasisPlatformDefault<string>("custom"));
        public static BasisSettingsBinding<string> RARolloffCurvePreset = new("ra_rolloffcurvepreset_v2", new BasisPlatformDefault<string>("natural"));
        public static BasisSettingsBinding<float> RACurvePoint25 = new("ra_curvepoint25", new BasisPlatformDefault<float>(0.6f));
        public static BasisSettingsBinding<float> RACurvePoint50 = new("ra_curvepoint50", new BasisPlatformDefault<float>(0.3f));
        public static BasisSettingsBinding<float> RACurvePoint75 = new("ra_curvepoint75", new BasisPlatformDefault<float>(0.1f));
        public static BasisSettingsBinding<float> RAPriority = new("ra_priority", new BasisPlatformDefault<float>(128f));

        // Listener Directional Dampening
        public static BasisSettingsBinding<float> RAListenerConeAngle = new("ra_listenerconeangle", new BasisPlatformDefault<float>(150f));
        // 75 % is 12 dB, roughly 3x a real head+torso shadow, and steep enough
        // (1.9 dB per 10 deg of head rotation) to read as a fader tracking your head.
        // 60 % is the deepest cone whose leftover broadband term stays under the
        // ~1.2 dB/10 deg audibility threshold once the head-shadow shelf takes its
        // share. Key bumped so the change reaches installs that never touched it.
        public static BasisSettingsBinding<float> RAListenerDampenAmount = new("ra_listenerdampenamount_v2", new BasisPlatformDefault<float>(60f));

        /// <summary>
        /// Frequency-dependent spatial shaping: mouth directivity and listener head
        /// shadow, applied per remote voice on the audio thread. Costs two one-pole
        /// filters per audible speaker.
        /// </summary>
        public static BasisSettingsBinding<bool> RAVoiceToneShaping = new("ra_voicetoneshaping_v2", new BasisPlatformDefault<bool>(false));

        // Steam Audio - Attenuation Input
        public static BasisSettingsBinding<string> RADistanceAttenuationInput = new("ra_distanceattenuationinput", new BasisPlatformDefault<string>("curve driven"));

        // Steam Audio - Air Absorption Bands
        public static BasisSettingsBinding<string> RAAirAbsorptionInput = new("ra_airabsorptioninput", new BasisPlatformDefault<string>("simulation defined"));
        public static BasisSettingsBinding<float> RAAirAbsorptionLow = new("ra_airabsorptionlow", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> RAAirAbsorptionMid = new("ra_airabsorptionmid", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> RAAirAbsorptionHigh = new("ra_airabsorptionhigh", new BasisPlatformDefault<float>(1f));

        // Steam Audio - Mix
        public static BasisSettingsBinding<float> RADirectMixLevel = new("ra_directmixlevel", new BasisPlatformDefault<float>(1f));

        // Steam Audio - Reflections
        public static BasisSettingsBinding<bool> RAReflections = new("ra_reflections", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> RAReflectionsMixLevel = new("ra_reflectionsmixlevel", new BasisPlatformDefault<float>(0.1f));
        public static BasisSettingsBinding<bool> RAApplyHRTFToReflections = new("ra_applyhrtftoreflections", new BasisPlatformDefault<bool>(false));

        // Voice jitter buffer depth (in 20ms Opus frames). Lower = less latency,
        // higher = more resilience to network jitter / packet loss before underrun.
        public static BasisSettingsBinding<float> RAJitterBufferDepth = new("ra_jitterbufferdepth", new BasisPlatformDefault<float>(1f));

        // Multiplier on the AudioClip pool's clip duration. Sits between the
        // decoded PCM queue and Unity's AudioSource as a secondary playback
        // buffer. Lower = less latency, higher = more headroom against
        // mid-callback decoded-queue stalls.
        public static BasisSettingsBinding<float> RAClipBufferScalar = new("ra_clipbufferscalar", new BasisPlatformDefault<float>(2f));

        public static BasisSettingsBinding<bool> FBIKEuroAll = new("euroall");

        // ---------------- CALIBRATION SPHERE SCALE (per bone) ----------------
        public static BasisSettingsBinding<float> CalibSphereScaleHips = new("calibspherescalehips", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleChest = new("calibspherescalechest", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleLeftFoot = new("calibspherescaleleftfoot", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleRightFoot = new("calibspherescalerightfoot", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleLeftLowerLeg = new("calibspherescaleleftlowerleg", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleRightLowerLeg = new("calibspherescalerightlowerleg", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleLeftLowerArm = new("calibspherescaleleftlowerarm", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleRightLowerArm = new("calibspherescalerightlowerarm", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleLeftHand = new("calibspherescalelefthand", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleRightHand = new("calibspherescalerighthand", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleLeftToes = new("calibspherescalelefttoes", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleRightToes = new("calibspherescalerighttoes", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleLeftShoulder = new("calibspherescaleleftshoulder", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> CalibSphereScaleRightShoulder = new("calibspherescalerightshoulder", new BasisPlatformDefault<float>(1f));

        // ---------------- IK COLLIDER & TUNING ----------------
        public static BasisSettingsBinding<bool> FBIKAdvancedVisible = new("fbikadvancedvisible", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> FBIKCollisionsEnabled = new("fbikcollisionsenabled", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKProtectElbow = new("fbikprotectelbow", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKCollideTrackedElbow = new("fbikcollidetrackedelbow", new BasisPlatformDefault<bool>(false));
        // Wrist axial bound (BasisArmSolveCore). Caps hand-vs-forearm roll at 15 deg by taking the excess off
        // the HAND, so it is the one stage in the arm that moves the hand away from the controller's rotation.
        // Default OFF: the hand leaving its rotation target is more visible in a headset than an over-twisted
        // wrist, because the user is holding the reference.
        public static BasisSettingsBinding<bool> FBIKWristAxialBound = new("fbikwristaxialbound", new BasisPlatformDefault<bool>(false));
        // Elbow DRAG — no-elbow-tracker arms only. Lags the predicted pole with a fixed time constant so a
        // waved hand does not throw the elbow around; a real elbow tracker is the user's own input and is
        // never lagged. Hz is a corner frequency, so LOWER = heavier drag (tau = 1/(2*pi*hz)).
        //
        // 1.25 Hz (tau 127 ms) measured on a 3 Hz hand shake: elbow swing 10.5 -> 4.3 cm (59% off), peak
        // speed 105 -> 42 cm/s, at a cost of 7.5 cm of transient lag on a deliberate 60 deg reach which
        // settles in 311 ms. Was 2.5 Hz (34% off, 4.3 cm, 100 ms); the user asked to double the damping,
        // and since this is a corner frequency doubling the damping means HALVING the number. Every cost
        // roughly doubled with it, which is what a first-order lag does.
        //
        // For reference across the slider: 4 Hz = 18% off / 2.7 cm / 33 ms, 2.5 Hz = 34% / 4.3 / 100,
        // 1.5 Hz = 53% / 6.6 / 233, 1 Hz = 66% / 8.8 / 422. Feel is subjective — tune in a headset.
        // _v2 on the Hz: the key had already been persisted at the old 2.5 default, and a default only ever
        // reaches an install through a NEW key — edited in place it would have been a silent no-op on every
        // machine that had already run once. The bool keeps its key; its default (true) has not moved.
        public static BasisSettingsBinding<bool> FBIKElbowDrag = new("fbikelbowdrag", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> FBIKElbowDragHz = new("fbikelbowdraghz_v2", new BasisPlatformDefault<float>(1.25f));
        // Collision capsule dimensions in meters at default (1.6m) avatar height; runtime
        // multiplies by AvatarToDefaultRatioScaledWithAvatarScale. Keys bumped to _v2 so existing
        // installs pick up the corrected defaults — the previous slider values disagreed with the
        // hardcoded constants in SetHandCollisionScale and were never actually consumed.
        //
        // _v3: the torso was too fat. Chest radius 0.07 -> 0.055 and skin 0.05 -> 0.025 takes the chest's
        // effective lateral half-width (radius + skin + the upper arm's own 0.048) from 0.168 m to 0.128 m
        // — a 33 cm wide no-arm zone down to 25 cm — and the hips from 0.196 to 0.150. The skin took the
        // larger cut because at 0.05 it was 42% of the chest radius, i.e. most of the bulk was padding.
        // Bumped rather than edited in place: a default only reaches an existing install through a new
        // key, so editing the value alone would have changed nothing for anyone who had already run it.
        public static BasisSettingsBinding<float> FBIKChestRadius = new("fbikchestradius_v3", new BasisPlatformDefault<float>(0.055f));
        public static BasisSettingsBinding<float> FBIKCollisionSkin = new("fbikcollisionskin_v3", new BasisPlatformDefault<float>(0.025f));
        public static BasisSettingsBinding<float> FBIKHandRadius = new("fbikhandradius_v2", new BasisPlatformDefault<float>(0.01f));
        public static BasisSettingsBinding<float> FBIKHandSkin = new("fbikhandskin_v2", new BasisPlatformDefault<float>(0.03f));
        public static BasisSettingsBinding<bool> FBIKShoulderSolveEnabled = new("fbikshouldersolveenabled", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKShoulderShrug = new("fbikshouldershrug", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKShoulderRetraction = new("fbikshoulderretraction", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> FBIKShoulderElevation = new("fbikshoulderelevation", new BasisPlatformDefault<float>(0.4f));
        public static BasisSettingsBinding<float> FBIKShoulderProtraction = new("fbikshoulderprotraction", new BasisPlatformDefault<float>(0.3f));
        // Scapulohumeral coupling: how much of the humeral swing the girdle takes, and the clamp on the result.
        public static BasisSettingsBinding<float> FBIKShoulderCoupleRatio = new("fbikshouldercoupleratio", new BasisPlatformDefault<float>(0.4f));
        public static BasisSettingsBinding<float> FBIKShoulderMaxDeg = new("fbikshouldermaxdeg", new BasisPlatformDefault<float>(25f));
        // Anatomical shoulder slide (Anatomy > Shoulder Slide): past Start degrees of chest yaw the girdle
        // counter-rotates by Fraction of the excess, capped at Max.
        public static BasisSettingsBinding<float> FBIKShoulderSlideStartDeg = new("fbikshoulderslidestartdeg", new BasisPlatformDefault<float>(30f));
        public static BasisSettingsBinding<float> FBIKShoulderSlideMaxDeg = new("fbikshoulderslidemaxdeg", new BasisPlatformDefault<float>(15f));
        public static BasisSettingsBinding<float> FBIKShoulderSlideFraction = new("fbikshoulderslidefraction", new BasisPlatformDefault<float>(0.4f));
        public static BasisSettingsBinding<float> FBIKMaxBendDeg = new("fbikmaxbenddeg", new BasisPlatformDefault<float>(90f));
        public static BasisSettingsBinding<float> FBIKMaxChestDelta = new("fbikmaxchestdelta", new BasisPlatformDefault<float>(90f));
        // Butterfly knees: with foot trackers (no knee tracker), tilting the feet outward and pulling them in lets
        // the knees fall open -- both laying on your back and sitting upright (cross-legged). MaxOpenDeg clamps the
        // splay to the hip's natural abduction.
        public static BasisSettingsBinding<bool> FBIKButterflyKnees = new("fbikbutterflyknees", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> FBIKButterflyKneeMaxOpenDeg = new("fbikbutterflykneemaxopendeg", new BasisPlatformDefault<float>(60f));
        // Knee-forward follow: with foot trackers (no knee tracker), aim the knee along the tracked foot's toe
        // instead of straight body-forward. Coupling is the upright follow fraction; foot-dominant by default (a
        // deliberate foot yaw is a hip rotation that carries the knee), and it fades to zero as the leg reclines and
        // the foot's toe lines up with the leg, handing off to the butterfly splay.
        public static BasisSettingsBinding<bool> FBIKKneeFollowsFoot = new("fbikkneefollowsfoot", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> FBIKKneeFootFollowUpright = new("fbikkneefootfollowupright", new BasisPlatformDefault<float>(0.75f));
        // Knee-swivel damping on the FOOT-DERIVED pole only (foot tracker, no knee tracker, no model hint). A knee
        // HINT tracker always gets the hold and never the low-pass; these two only widen that treatment to the
        // invented pole. Off by default = the shipped behaviour.
        public static BasisSettingsBinding<bool> FBIKKneeFootPoleHold = new("fbikkneefootpolehold", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> FBIKKneeFootPoleConditioning = new("fbikkneefootpoleconditioning", new BasisPlatformDefault<bool>(false));

        // Spine relax: per-axis bend distribution onto lumbar (spine) and thoracic (upperChest)
        public static BasisSettingsBinding<float> FBIKSpineBendPitch = new("fbikspinebendpitch", new BasisPlatformDefault<float>(0.45f));
        public static BasisSettingsBinding<float> FBIKSpineBendYaw = new("fbikspinebendyaw", new BasisPlatformDefault<float>(0.10f));
        public static BasisSettingsBinding<float> FBIKSpineBendRoll = new("fbikspinebendroll", new BasisPlatformDefault<float>(0.35f));
        public static BasisSettingsBinding<float> FBIKChestBendPitch = new("fbikchestbendpitch", new BasisPlatformDefault<float>(0.20f));
        public static BasisSettingsBinding<float> FBIKChestBendYaw = new("fbikchestbendyaw", new BasisPlatformDefault<float>(0.15f));
        public static BasisSettingsBinding<float> FBIKChestBendRoll = new("fbikchestbendroll", new BasisPlatformDefault<float>(0.15f));
        public static BasisSettingsBinding<float> FBIKUpperChestBendPitch = new("fbikupperchestbendpitch", new BasisPlatformDefault<float>(0.25f));
        public static BasisSettingsBinding<float> FBIKUpperChestBendYaw = new("fbikupperchestbendyaw", new BasisPlatformDefault<float>(0.30f));
        public static BasisSettingsBinding<float> FBIKUpperChestBendRoll = new("fbikupperchestbendroll", new BasisPlatformDefault<float>(0.20f));
        // OFF by default (_v2 re-keys the installs that ran the 0.5 build). Splitting a head turn onto the neck
        // bone is anatomically right, but in-headset it made the neck read worse -- it stacks on whatever the
        // torso-yaw deadzone already leaves for the neck to carry. Live-tunable; needs a headset A/B before it
        // is turned back on.
        public static BasisSettingsBinding<float> FBIKNeckYawShare = new("fbikneckyawshare_v2", new BasisPlatformDefault<float>(0f));
        public static BasisSettingsBinding<float> FBIKSpineStretchMax = new("fbikspinestretchmax", new BasisPlatformDefault<float>(0.03f));
        // Spine relax: hip hinge coupling
        public static BasisSettingsBinding<float> FBIKHipHingeStartDeg = new("fbikhiphingestartdeg_v2", new BasisPlatformDefault<float>(40f));
        public static BasisSettingsBinding<float> FBIKHipHingeMaxAddDeg = new("fbikhiphingemaxadddeg_v2", new BasisPlatformDefault<float>(52f));
        // Spine relax: chest follow spring (velocity lag)
        public static BasisSettingsBinding<float> FBIKChestSpringHz = new("fbikchestspringhz", new BasisPlatformDefault<float>(12f));
        public static BasisSettingsBinding<float> FBIKChestSpringDamping = new("fbikchestspringdamping", new BasisPlatformDefault<float>(1f));
        // Hip relax: hip-frame follow spring -- decouples the no-elbow-tracker derived elbow pole from hip
        // jitter/sway (lower Hz = more decoupling; 0 = off). No effect for users WITH elbow trackers.
        // Arm: chicken-wing elbow flare (no elbow tracker) -- turning the controllers inward pushes the derived
        // elbow OUT to the half-T-pose mark and caps it there. MaxDeg = the cap; InwardGain is signed (negative
        // flips the roll direction, 0 = off); FullRollDeg = the controller roll that is a full chicken-wing.
        // Spine relax: asymmetric flexion clamps (apply to spine + upperChest contributions)
        public static BasisSettingsBinding<float> FBIKSpineMaxForwardDeg = new("fbikspinemaxforwarddeg", new BasisPlatformDefault<float>(60f));
        public static BasisSettingsBinding<float> FBIKSpineMaxBackwardDeg = new("fbikspinemaxbackwarddeg", new BasisPlatformDefault<float>(25f));
        public static BasisSettingsBinding<float> FBIKSpineMaxLateralDeg = new("fbikspinemaxlateraldeg", new BasisPlatformDefault<float>(25f));
        // Spine relax: squish-driven bend coupling
        public static BasisSettingsBinding<float> FBIKSpineSquishBoost = new("fbikspinesquishboost", new BasisPlatformDefault<float>(0.5f));
        // Chest gaze-follow (no chest tracker): a little forward chest fold when you look down. 0=rigid.
        public static BasisSettingsBinding<float> FBIKSpineGazeFollow = new("fbikspinegazefollow", new BasisPlatformDefault<float>(0.25f));
        // Extra forward neck curve on a look-down (no chest tracker). 0 = lordosis only.
        public static BasisSettingsBinding<float> FBIKNeckGazeFollow = new("fbikneckgazefollow", new BasisPlatformDefault<float>(0.3f));
        // How much of a look-UP's lever swing to REMOVE, when the torso is estimated by re-attaching the T-pose
        // head->neck lever to the head (BasisNeckCueCore -- both the FBIK neck cue and the virtual spine's neck
        // bone). Swinging that lever by the WHOLE gaze assumes a nod pivots at the neck bone; cervical extension
        // is short and a look-up is mostly thoracic arching, so the skull barely slides back and the estimated
        // neck walks forward and up instead, and the chest chord strung under it follows. Removing 0.65 leaves a
        // 0.35 carry, matching DesktopHeadSwingBackward -- the same physiology measured from the eye end. 0 = the
        // old rigid re-attachment (a true off switch). Look-down and pure yaw are untouched at any value.
        public static BasisSettingsBinding<float> FBIKNeckExtensionDamp = new("fbikneckextensiondamp", new BasisPlatformDefault<float>(0.65f));
        // Flexion is the look-DOWN half of the same lever. It was undamped, which left the neck estimate --
        // and through it the pelvis -- rising 5 cm on a steep look down with the feet planted.
        public static BasisSettingsBinding<float> FBIKNeckFlexionDamp = new("fbikneckflexiondamp", new BasisPlatformDefault<float>(0.5f));
        // Spine relax: crouch counterweight (hips shift back as the head drops)
        public static BasisSettingsBinding<float> FBIKMoveBodyBackWhenCrouching = new("fbikmovebodybackwhencrouching", new BasisPlatformDefault<float>(1f));
        // Postural counterbalance: how far the pelvis travels BACK as the trunk folds forward, as a fraction
        // of how far the neck has travelled forward. ~0.38 falls out of segment masses (keep the COM over the
        // feet); 0 pins the pelvis, which is what made a look-down drive the chest down into the body.
        public static BasisSettingsBinding<float> FBIKTrunkCounterbalance = new("fbiktrunkcounterbalance", new BasisPlatformDefault<float>(0.38f));
        // Swing continuity: max elbow/knee swing speed (deg/s); lower = smoother, 0 = off
        public static BasisSettingsBinding<float> FBIKSwingSmoothRate = new("fbikswingsmoothrate", new BasisPlatformDefault<float>(720f));
        // On/off for the swing continuity above (off forces the rate to 0). Lets the elbow swing free.
        public static BasisSettingsBinding<bool> FBIKElbowSwingEnabled = new("fbikelbowswingenabled", new BasisPlatformDefault<bool>(true));
        // Spine relax: CCD solve smoothing + neck overbend cone limit
        // _v2: default retuned 0.8 -> 1.0 against the mocap corpus. Full relax measured strictly better on
        // every axis: spine-vs-human error 2.02 -> 1.86 cm mean / 9.29 -> 8.56 p95 (10 CMU clips), AND a
        // quieter standing noise floor (worst-case neck step p95 0.224 -> 0.190 deg at 0.5 mm tracker
        // noise) — the damping was buying nothing measurable. Key bumped so existing installs pick it up.
        public static BasisSettingsBinding<float> FBIKSpineCCDRelax = new("fbikspineccdrelax_v2", new BasisPlatformDefault<float>(1.0f));
        public static BasisSettingsBinding<float> FBIKNeckMaxConeDeg = new("fbikneckmaxconedeg", new BasisPlatformDefault<float>(45f));
        // Spine CCD axial-twist allowance, graded lumbar (lower) -> cervical (neck). Lower lumbar = a sideways
        // head reach bends instead of corkscrewing. Key bumped to _v2 to re-default the grading on existing installs.
        public static BasisSettingsBinding<float> FBIKSpineTwistKeep = new("fbikspinetwistkeep_v2", new BasisPlatformDefault<float>(0.25f));
        public static BasisSettingsBinding<float> FBIKSpineNeckTwistKeep = new("fbikspinenecktwistkeep", new BasisPlatformDefault<float>(0.9f));
        // Mid-thoracic bend stiffness: scales down the swing of the middle spine joints (ends unaffected) so a
        // lean curves at the flexible lumbar + cervical instead of kinking at one joint. 0 = uniform.
        public static BasisSettingsBinding<float> FBIKThoracicBendStiffen = new("fbikthoracicbendstiffen", new BasisPlatformDefault<float>(0.3f));
        // Width of the spine CCD's taut band as a fraction of hips->head length. Must exceed the sub-millimetre
        // compressions an upright head commands through the neck-pivot lever, or the solver sits on its
        // full-extension singularity.
        // ⚠ MEASURED 2026-08-25: narrowing this to 0.003 to cut the head's few-millimetre band residual
        // reintroduces the standing buzz it exists to prevent (buzz-band, both avatar-scale gates and the
        // full-extension continuity gate all go red) and costs corpus accuracy (1.61 -> 1.73 cm mean). The
        // width is not the knob -- the regularizer's TAIL is, and it is quartic in SolveSequentialSpineIK so
        // the head is released within a fraction of a millimetre once the compression is real.
        public static BasisSettingsBinding<float> FBIKSpineTautBandFrac = new("fbikspinetautbandfrac", new BasisPlatformDefault<float>(0.015f));
        // Lateral bend -> a little same-side axial rotation, so a sustained lean reads as a spinal coupling
        // rather than a pure hinge.
        public static BasisSettingsBinding<float> FBIKBendTwistCoupling = new("fbikbendtwistcoupling", new BasisPlatformDefault<float>(0.15f));
        // Cap on how far the neck may lead a gaze ahead of the spine chain.
        public static BasisSettingsBinding<float> FBIKNeckGazeFollowMaxDeg = new("fbikneckgazefollowmaxdeg", new BasisPlatformDefault<float>(18f));
        // Ceiling on the posterior pelvic shift, as a fraction of T-pose spine length.
        public static BasisSettingsBinding<float> FBIKTrunkCounterbalanceMaxFrac = new("fbiktrunkcounterbalancemaxfrac", new BasisPlatformDefault<float>(0.45f));
        // Chest-as-secondary-IK-target (Anatomy > Chest IK Target): pull weight, iterations, head-restore sweeps
        // per iteration, the cap on the spine's positional pull, and the distance past which a chest target is
        // treated as a glitching tracker and ignored.
        public static BasisSettingsBinding<float> FBIKChestIkWeight = new("fbikchestikweight", new BasisPlatformDefault<float>(0.5f));
        public static BasisSettingsBinding<float> FBIKChestIkIterations = new("fbikchestikiterations", new BasisPlatformDefault<float>(8f));
        public static BasisSettingsBinding<float> FBIKChestIkHeadRestoreSweeps = new("fbikchestikheadrestoresweeps", new BasisPlatformDefault<float>(2f));
        public static BasisSettingsBinding<float> FBIKChestPosPullMaxDeg = new("fbikchestpospullmaxdeg", new BasisPlatformDefault<float>(20f));
        public static BasisSettingsBinding<float> FBIKChestPullMaxDist = new("fbikchestpullmaxdist", new BasisPlatformDefault<float>(0.5f));
        // Chest share of the arm-swing torso follow; the upper chest takes the remainder.
        public static BasisSettingsBinding<float> FBIKChestFollowChestShare = new("fbikchestfollowchestshare", new BasisPlatformDefault<float>(0.6f));
        // One Euro parameters for a knee whose pole comes from a tracker: a higher floor than the standing path,
        // and 4x the beta so real shin motion isn't lagged.
        public static BasisSettingsBinding<float> FBIKTrackedKneeSwivelMinCutoffHz = new("fbiktrackedkneeswivelmincutoffhz", new BasisPlatformDefault<float>(1.5f));
        public static BasisSettingsBinding<float> FBIKTrackedKneeSwivelBeta = new("fbiktrackedkneeswivelbeta", new BasisPlatformDefault<float>(0.20f));
        public static BasisSettingsBinding<float> FBIKTrackedKneeSwivelDerivCutoffHz = new("fbiktrackedkneeswivelderivcutoffhz", new BasisPlatformDefault<float>(1.0f));
        // Spine relax: arm-swing chest follow (only when no chest tracker)
        public static BasisSettingsBinding<float> FBIKChestArmSwingFactor = new("fbikchestarmswingfactor", new BasisPlatformDefault<float>(0.3f));
        public static BasisSettingsBinding<float> FBIKChestArmSwingMaxDeg = new("fbikchestarmswingmaxdeg", new BasisPlatformDefault<float>(15f));
        // Arm twist DISTRIBUTION STRENGTH (1 = fully even: each twist bone takes a share equal to its position
        // along the bone -> linear roll gradient; 0 = no twist bone, roll piles up at the wrist). Key bumped to
        // _v2 because the meaning changed from a raw roll fraction (old 0.5/0.3) to a position-scaled strength.
        public static BasisSettingsBinding<float> FBIKLowerArmTwistFraction = new("fbiklowerarmtwistfraction_v2", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> FBIKUpperArmTwistFraction = new("fbikupperarmtwistfraction_v2", new BasisPlatformDefault<float>(1f));

        // Anatomy — IK refinements modeled on real biomechanics. Persistence keys are versioned
        // (_v2) so existing installs with the old off-by-default values saved pick up the new
        // on-by-default behavior.
        public static BasisSettingsBinding<bool> FBIKAnatDifferentialStiffness = new("fbikanatdiffstiffness_v2", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKAnatShoulderSlide = new("fbikanatshoulderslide_v2", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKAnatCervicalLordosis = new("fbikanatcervicallordosis_v2", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKAnatPelvicTwistRouting = new("fbikanatpelvictwistrouting_v2", new BasisPlatformDefault<bool>(true));
        // The anatomical range-of-motion envelope on every solved vertebra (BasisSpineAnatomy). ON by
        // default: the lumbar spine, the upper chest and the neck previously had NO per-joint limit at all
        // once the spine CCD ran, and nothing anywhere limited axial rotation. That is not a safe fallback
        // to keep, it is a measured defect.
        // Key bumped to _v3: the _v2 rename shipped this false alongside the deliberate FBIKChestIKTarget
        // disable, so every install that ran that build has false pinned on disk and a value-only change
        // would not reach them.
        public static BasisSettingsBinding<bool> FBIKSpineAnatomicalRom = new("fbikspineanatomicalrom_v3", new BasisPlatformDefault<bool>(true));
        // The chest becomes a real (secondary) IK target instead of a free FK consequence of the head
        // solve. Placed by the lower spine, with the head restored by the upper joints so it is never
        // traded away.
        // ON by default. Measured: pooled spine 0.471 vs 2.181 cm and UpperChest/Neck 3.2x better with a
        // chest tracker; chest POSITION goes 3.23 -> 0.29 cm. Without a chest tracker it is marginal but
        // harmless.
        // ⚠ HISTORY, so this is not "fixed" back and forth a third time: it was ON, the _v2 rename shipped it
        // false, and it stayed false because in-headset it CRANED THE NECK -- having passed every position
        // test first. That is the precedent that makes a corpus number alone insufficient here. It is being
        // re-enabled now because the head CCD's drag on a tracked chest was separately root-caused and fixed
        // (ReassertTrackedChest, 0.402 -> 0.125 deg per deg of gaze), which is the most likely cause of the
        // craning. If the neck cranes again in a headset, THAT is the finding -- turn this off and say so,
        // rather than compensating for it somewhere downstream.
        // Key bumped _v2 -> _v3 because a value-only change cannot reach installs that already ran the build
        // which pinned false on disk -- exactly why FBIKSpineAnatomicalRom above is _v3.
        public static BasisSettingsBinding<bool> FBIKChestIKTarget = new("fbikchestiktarget_v3", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKLegSwivelSmoothing = new("fbiklegswivelsmoothing", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> FBIKTrackerBendNormal = new("fbiktrackerbendnormal", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<bool> FBIKBodyFit = new("fbikbodyfit", new BasisPlatformDefault<bool>(true));

        // Job-driven locomotion: step the stock locomotion controller as data and blend its baked clips
        // in a Burst job overlapping Simulate, instead of evaluating the Animator on the main thread.
        // Only engages on avatars wearing the stock controller; custom animators keep the Animator path.
        public static BasisSettingsBinding<bool> FBIKJobLocomotion = new("fbikjoblocomotion", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> FBIKBodyFitMaxDeviation = new("fbikbodyfitmaxdeviation", new BasisPlatformDefault<float>(Basis.IK.BasisBodyFitCore.DefaultMaxDeviation));

        // Cervical lordosis pitch coupling: when AnatCervicalLordosis is on, the base 5° forward
        // bend gets extra angle proportional to head pitch-down. 0 = constant 5°; positive = more
        // bend when looking at the floor, less (down to zero) when looking up.
        public static BasisSettingsBinding<float> FBIKLordosisPitchGainDeg = new("fbiklordosispitchgaindeg", new BasisPlatformDefault<float>(8f));
        // Cervical lordosis shaping (see ApplyCervicalLordosis): neutral-pose base bend + neck/upperChest
        // split, head pitch clamp, and the extreme-look window that drives extra spine roll plus
        // hips/chest counter-translation. Horizontal/Down maxima are in meters. Only used when
        // Cervical Lordosis (Anatomy) is on.
        public static BasisSettingsBinding<float> FBIKLordosisBaseDeg = new("fbiklordosisbasedeg", new BasisPlatformDefault<float>(5f));
        public static BasisSettingsBinding<float> FBIKLordosisNeckShare = new("fbiklordosisneckshare", new BasisPlatformDefault<float>(0.65f));
        public static BasisSettingsBinding<float> FBIKLordosisMaxHeadPitchDeg = new("fbiklordosismaxheadpitchdeg", new BasisPlatformDefault<float>(80f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeStartDeg = new("fbiklordosisextremestartdeg", new BasisPlatformDefault<float>(50f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeFullDeg = new("fbiklordosisextremefulldeg", new BasisPlatformDefault<float>(80f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeRollForwardMaxDeg = new("fbiklordosisextremerollforwardmaxdeg", new BasisPlatformDefault<float>(10f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeRollBackwardMaxDeg = new("fbiklordosisextremerollbackwardmaxdeg", new BasisPlatformDefault<float>(4f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeHipsHorizontalMax = new("fbiklordosisextremehipshorizontalmax", new BasisPlatformDefault<float>(0.025f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeChestHorizontalMax = new("fbiklordosisextremechesthorizontalmax", new BasisPlatformDefault<float>(0.04f));
        // The look-UP half of the pair above. A deep look-down sits the whole body back; a deep look-up is an
        // ARCH, and in an arch the pelvis leads and the sternum stays over or behind it -- so the chest gets a
        // much smaller number than the hips here, where on the look-down side it gets a larger one. Mirroring
        // the look-down values put the chest 4 cm in front of the hips' 2.5 cm, i.e. out in front of the body.
        public static BasisSettingsBinding<float> FBIKLordosisExtremeHipsHorizontalLookUp = new("fbiklordosisextremehipshorizontallookup", new BasisPlatformDefault<float>(0.025f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeChestHorizontalLookUp = new("fbiklordosisextremechesthorizontallookup", new BasisPlatformDefault<float>(0.010f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeHipsDownMax = new("fbiklordosisextremehipsdownmax", new BasisPlatformDefault<float>(0.015f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeChestDownMax = new("fbiklordosisextremechestdownmax", new BasisPlatformDefault<float>(0.025f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeHipsDownLookUp = new("fbiklordosisextremehipsdownlookup", new BasisPlatformDefault<float>(0.0005f));
        public static BasisSettingsBinding<float> FBIKLordosisExtremeChestDownLookUp = new("fbiklordosisextremechestdownlookup", new BasisPlatformDefault<float>(0.001f));

        // Arm vs height ratio (arm-distance IK mode): when enabled, the player's arm span is
        // derived from eye height * ratio instead of the T-pose measurement, so reach can be
        // dialed in per avatar (fixes "stubby arms"). Ratio is arm span / eye height (~1.0).
        public static BasisSettingsBinding<bool> FBIKArmHeightRatioEnabled = new("fbikarmheightratioenabled", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> FBIKArmHeightRatio = new("fbikarmheightratio", new BasisPlatformDefault<float>(1.05f));

        // Desktop only. The head pitches about the base of the neck, so looking down carries the eye FORWARD --
        // the swing an HMD makes for free, which desktop used to pin away. Strength 1 = the avatar's own
        // neck->eye geometry; lower it if the camera travel feels like too much.
        public static BasisSettingsBinding<bool> DesktopHeadSwingEnabled = new("desktopheadswingenabled", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> DesktopHeadSwingStrength = new("desktopheadswingstrength", new BasisPlatformDefault<float>(1f));
        // Looking up is not the mirror of looking down: the thoracic spine takes most of it, so the skull does
        // not slide back over the shoulders anything like as far as the raw neck geometry claims.
        public static BasisSettingsBinding<float> DesktopHeadSwingBackward = new("desktopheadswingbackward", new BasisPlatformDefault<float>(0.35f));

        // ---------------- VIRTUAL SPINE (no torso tracker) ----------------
        // Per-axis cascade fractions of head-relative pitch/roll that the synthesized chest and
        // spine carry when no chest tracker is present. Yaw fractions are derived from bone-length
        // ratios automatically (current behavior). Defaults give a mild but visible improvement
        // over the previous yaw-only chain.
        public static BasisSettingsBinding<float> VSpineChestPitchFrac = new("vspinechestpitchfrac", new BasisPlatformDefault<float>(0.30f));
        public static BasisSettingsBinding<float> VSpineChestRollFrac = new("vspinechestrollfrac", new BasisPlatformDefault<float>(0.30f));
        public static BasisSettingsBinding<float> VSpineSpinePitchFrac = new("vspinespinepitchfrac", new BasisPlatformDefault<float>(0.10f));
        public static BasisSettingsBinding<float> VSpineSpineRollFrac = new("vspinespinerollfrac", new BasisPlatformDefault<float>(0.10f));

        // Per-joint slew rates (deg/sec equivalent via slerp dt scaling). Higher = more responsive
        // / less smoothing. Defaults match the original inspector field values so existing avatars
        // are unchanged.
        public static BasisSettingsBinding<float> VSpineNeckRotationSpeed = new("vspineneckrotationspeed", new BasisPlatformDefault<float>(40f));
        public static BasisSettingsBinding<float> VSpineChestRotationSpeed = new("vspinechestrotationspeed", new BasisPlatformDefault<float>(25f));
        public static BasisSettingsBinding<float> VSpineSpineRotationSpeed = new("vspinespinerotationspeed", new BasisPlatformDefault<float>(30f));
        public static BasisSettingsBinding<float> VSpineHipsRotationSpeed = new("vspinehipsrotationspeed", new BasisPlatformDefault<float>(20f));

        // Hips forward bias: small forward offset (meters at default avatar scale) so hips don't
        // sit perfectly under the neck. Now 0 — it is the only unconditional forward translation of
        // the pelvis, and with no hips tracker (desktop, headset-only VR) it stacked on an already
        // head-anchored pelvis and read as "body too far forward". Key bumped to _v2 so installs
        // that persisted 0.02 pick the new default up. (The former VSpineHipsXZFollowBlend setting
        // was removed in favor of a hard-coded counterbalance/pendulum model in the virtual spine
        // driver — see BasisLocalVirtualSpineDriver.ComputeRealisticHipsXZ.)
        // How much of the gaze-induced eye swing is removed before the pelvis stance leash sees it. The leash
        // estimates WHERE THE USER IS STANDING and it tracks the HMD -- but the HMD is not a body-fixed point
        // in EITHER axis, because the head swings about the base of the neck and the HMD sits a hand's width
        // in front of that pivot. A look-up carries it ~8 cm backward and a look-down forward; a head TURN
        // sweeps it sideways through a ~9 cm arc. The leash follows fast enough to adopt all of that in about
        // a frame, with the feet planted, so the pelvis rode the gaze and walked out from under the player --
        // sideways as "the hips jut out left and right", forward/back as a pelvis that wanders on a glance.
        //
        // This removes both halves, and it must remove both halves of each: the PITCH swing is modelled
        // (DesktopHeadSwingBackward supplies the look-up share, so the VR and desktop halves of that model
        // cannot drift apart), while the YAW arc is cancelled exactly -- the reference is carried back onto
        // the neck, the pivot the head actually turns about, and the pelvis's own anchor arm is re-measured
        // from that same pivot so the two cancel algebraically at any yaw. Both ends move together on this
        // one number, which is why it is one number. 1 = leash only real travel; 0 = the old behaviour.
        // If a deep look-DOWN starts feeling different, this is the number to turn down.
        public static BasisSettingsBinding<float> VSpineGazeSwingRemoval = new("vspinegazeswingremoval", new BasisPlatformDefault<float>(1f));
        // Learn the arm the HMD really nods on instead of trusting the avatar's authored eye-to-head
        // offset, which is a rendering offset and has no reason to match the wearer's neck.
        public static BasisSettingsBinding<bool> VSpineNodPivotEstimate = new("vspinenodpivotestimate", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> VSpineHipsForwardBias = new("vspinehipsforwardbias_v2", new BasisPlatformDefault<float>(0f));

        // Spine compression: the synthesized hips Y is neck - rigid spine length, so lowering the head
        // (leaning to touch toes, sitting) drags the pelvis straight down the full rest length and the
        // leg IK folds the knees to keep the planted feet. These two let the spine SHORTEN instead:
        // once the head drops below standing, the hips' downward travel saturates toward MaxDrop so the
        // chest scrunches and the pelvis stays up. Strength 0 = rigid (legacy), 1 = full saturation.
        public static BasisSettingsBinding<float> VSpineHipsCompressionStrength = new("vspinehipscompressionstrength", new BasisPlatformDefault<float>(0.85f));
        public static BasisSettingsBinding<float> VSpineHipsMaxDropMeters = new("vspinehipsmaxdropmeters", new BasisPlatformDefault<float>(0.3f));

        // ⭐ THE PELVIS POSTURE MODEL -- replaces the two knobs above with a law fitted to real humans.
        //
        // The saturation above cannot be right, because it answers a question with one number that has two
        // answers. A low head is either a WAIST-BEND (spine folds, pelvis stays high) or a SQUAT (spine
        // stays stacked, pelvis rides the head down), and measured on 44 CMU clips the real pelvis coupling
        // is 0.02-0.14 for the first and 0.78-0.99 for the second. One constant serves neither: on a squat
        // the saturation holds the pelvis 32.8 cm ABOVE where a real one goes, and since the head is welded
        // to the HMD, the spine and neck have to find those centimetres by rotating -- the "gamer neck /
        // tortoise" users report when they bend over.
        //
        // BasisPelvisPostureModel tells the two apart using the head's FORWARD LEAN, the input the old law
        // never had. Pelvis-height error against a real human: 8.3 cm -> 4.5 cm (5.2 cm leave-one-clip-out).
        //
        // A TOGGLE, not a slider: these are two different laws, not two ends of one, so there is nothing
        // meaningful in between. ON by default because the old law is a measured 32.8 cm error rather than a
        // safe fallback -- but it is one switch to A/B in a headset, and one switch to revert.
        // The two knobs above are consulted ONLY when this is off.
        public static BasisSettingsBinding<bool> VSpinePostureModel = new("vspineposturemodel", new BasisPlatformDefault<bool>(true));

        // Torso yaw "play": degrees the head can turn before the synthesized chest/spine/hips begin
        // following. The torso holds inside this cone, catches up once the head leaves it, then the
        // cone re-centers when the head stops. 0 = follow immediately (legacy behavior).
        public static BasisSettingsBinding<float> VSpineTorsoYawDeadzoneDeg = new("vspinetorsoyawdeadzonedeg", new BasisPlatformDefault<float>(45f));

        // How quickly the torso eases into/out of following once it leaves the deadzone cone (slerp
        // dt scaling, like the rotation speeds). Lower = softer blend, higher = snappier.
        public static BasisSettingsBinding<float> VSpineTorsoYawBlendSpeed = new("vspinetorsoyawblendspeed", new BasisPlatformDefault<float>(8f));

        // Off forces the yaw deadzone to 0 in VR (torso follows immediately); on uses the configured
        // VSpineTorsoYawDeadzoneDeg cone in VR too. Desktop always uses the configured value.
        // ⚠ Left OFF: turning this on in VR was speculative and never headset-verified. With a deadzone the
        // torso stops following the head, so every degree inside it is charged to the neck -- reported as the
        // neck reading worse. The de-orbit fix it was premised on is real, but this is a FEEL change and needs
        // its own A/B.
        public static BasisSettingsBinding<bool> VSpineTorsoYawPlayInVR = new("vspinetorsoyawplayinvr", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> VSpineTorsoYawDeadzoneVRDeg = new("vspinetorsoyawdeadzonevrdeg", new BasisPlatformDefault<float>(30f));


        // ---------------- TRACKER PAIRING (virtual midpoint) ----------------
        // Hides the pairing tuning sliders behind an advanced toggle so the
        // tracker linking page stays approachable for the common case.
        public static BasisSettingsBinding<bool> TrackerLinkingAdvancedVisible = new("trackerlinking_advancedvisible", new BasisPlatformDefault<bool>(false));
        // Hides the per-tracker connector list (linking + role-override
        // dropdowns) until the user opts in. The page is mostly useful once
        // for setup; routine open/close shouldn't have to scroll past the
        // full device list.
        public static BasisSettingsBinding<bool> TrackerLinkingConnectorVisible = new("trackerlinking_connectorvisible", new BasisPlatformDefault<bool>(false));
        // Honor body-part roles assigned to trackers by hand in SteamVR settings
        // ("vive_tracker_waist", ...). Off by default: most people never set those roles (or
        // leave stale ones), and a wrong announced role is forced with no geometric check.
        public static BasisSettingsBinding<bool> TrustSteamVRRoles = new("trackerlinking_truststeamvrroles", new BasisPlatformDefault<bool>(false));
        // Drop the trackers hand-tracking software publishes (HANDL, VRLINKQ_Hand_Left, ...) from
        // full-body calibration — see BasisIgnoredCalibrationTrackers. Off by default: the match is
        // on device name alone, so a real body tracker that happens to share one of those names
        // would silently stop calibrating with no way to tell why.
        public static BasisSettingsBinding<bool> IgnoreHandTrackingDevices = new("trackerlinking_ignorehandtracking", new BasisPlatformDefault<bool>(false));
        // Confidence falloff for a tracker that's spiking relative to its own
        // recent baseline. weight = 1 / (1 + max(surprise - 1, 0)^2 * penalty).
        // Higher = more aggressive shift to the steadier half on a glitch.
        public static BasisSettingsBinding<float> PairingSurprisePenalty = new("pairing_surprisepenalty", new BasisPlatformDefault<float>(2f));
        // Surprise multiplier above which the velocity EMA freezes — without this
        // a sustained glitch would drag the baseline up and stop being detected.
        public static BasisSettingsBinding<float> PairingSurpriseClamp = new("pairing_surpriseclamp", new BasisPlatformDefault<float>(3f));
        // Floor added to the velocity EMA when computing surprise so a frozen
        // tracker (EMA ≈ 0) doesn't treat any tiny twitch as a giant spike.
        public static BasisSettingsBinding<float> PairingEmaFloor = new("pairing_emafloor", new BasisPlatformDefault<float>(0.005f));
        // Cap on how far the soft rest-distance pull can drag each half. 0 = no
        // pull (raw measurements only); 1 would fully snap to a rigid solution.
        public static BasisSettingsBinding<float> PairingMaxCorrectionStrength = new("pairing_maxcorrection", new BasisPlatformDefault<float>(0.3f));
        // Distance error at which the soft pull reaches half its cap. Smaller =
        // tighter rigid behavior; larger = more give for skin/mount flex.
        public static BasisSettingsBinding<float> PairingSoftSnapHalfLife = new("pairing_softsnaphalflife", new BasisPlatformDefault<float>(0.05f));
        // Distance-error window inside which the rest-distance EMA is allowed to
        // track the current value. Outside it the baseline freezes.
        public static BasisSettingsBinding<float> PairingLockstepTolerance = new("pairing_lockstep", new BasisPlatformDefault<float>(0.05f));
        // EMA smoothing for the per-tracker velocity baseline. Higher = faster
        // adaption (more reactive to motion-pattern changes, but spikier).
        public static BasisSettingsBinding<float> PairingEmaAlpha = new("pairing_emaalpha", new BasisPlatformDefault<float>(0.1f));
        // EMA smoothing for the inter-tracker rest distance.
        public static BasisSettingsBinding<float> PairingDistanceEmaAlpha = new("pairing_distemaalpha", new BasisPlatformDefault<float>(0.05f));
        // EMA smoothing applied to the per-tracker confidence weights themselves.
        // Without this, weights swing wildly frame-to-frame on motion onset (a
        // moving tracker briefly looks "surprising" to its own baseline) and the
        // midpoint snaps. Higher = more reactive (catches glitches faster but
        // jitters more); lower = smoother (longer to recover from a glitch).
        public static BasisSettingsBinding<float> PairingWeightSmoothing = new("pairing_weightsmoothing", new BasisPlatformDefault<float>(0.25f));
        // Half-life (seconds) of the low-pass on the fused midpoint rotation; 0 = raw (default). Off by
        // default because the bone sim already slerp-smooths the rotation -- a second low-pass here just
        // lags the hip behind a fast turn (~46 deg at 0.08s), which a single tracker never does. Key bumped
        // to _v2 so installs that persisted the old 0.08 pick up the new default.
        public static BasisSettingsBinding<float> PairingRotationHalfLife = new("pairing_rothalflife_v2", new BasisPlatformDefault<float>(0f));

        // ---------------- REMOTE NAMEPLATE ----------------
        public static BasisSettingsBinding<bool> NPEnabled = new("np_enabled", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> NPMenuOnly = new("np_menuonly", new BasisPlatformDefault<bool>
        {
            android = true,
            ios = true,
            linux = false,
            other = true,
            windows = false,
        });
        public static BasisSettingsBinding<bool> NPHoverMenuOnly = new("np_hovermenuonly", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> NPSize = new("np_size", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> NPTransparency = new("np_transparency", new BasisPlatformDefault<float>(0.45f));
        public static BasisSettingsBinding<float> ChatSize = new("chat_size", new BasisPlatformDefault<float>(1.5f));

        // ---------------- ADMIN ----------------
        public static BasisSettingsBinding<bool> AdminAutoRefreshPlayerList = new("admin_autorefresh_playerlist", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<bool> ShoutShowOnMenuBar = new("admin_shout_on_menubar", new BasisPlatformDefault<bool>(false));

        // Limiter
        public static BasisSettingsBinding<float> LimitThreshold = new("limitthreshold", new BasisPlatformDefault<float>(0.95f)); // pre-clip

        public static BasisSettingsBinding<float> LimitKnee = new("limitknee", new BasisPlatformDefault<float>(0.05f)); // soft knee width

        // Denoise extra params (post gain + wet/dry)
        public static BasisSettingsBinding<float> DenoiseMakeupDb = new("denoisemakeupdb", new BasisPlatformDefault<float>(3f));

        public static BasisSettingsBinding<float> DenoiseWet = new("denoisewet", new BasisPlatformDefault<float>(1f)); // 0..1


        // Target loudness is fixed in BasisMicrophoneAgc.DefaultTargetRms — it only equalises
        // people if everyone agrees on it, so it is not user-tunable.
        // public static BasisSettingsBinding<float> AgcTargetRms = new("agctargetrmsv2", new BasisPlatformDefault<float>(0.1f)); // ~ -20 dBFS

        public static BasisSettingsBinding<float> AgcMaxGainDb = new("agcdbgainmaxv2", new BasisPlatformDefault<float>(24f));

        public static BasisSettingsBinding<float> AgcAttack = new("agcattackv2", new BasisPlatformDefault<float>(0.75f)); // 0..1

        public static BasisSettingsBinding<float> AgcRelease = new("agcreleasev2", new BasisPlatformDefault<float>(0.85f)); // 0..1

        // ---------------- UI STYLE PALETTE ----------------
        public static BasisSettingsBinding<string> UIPaletteBG1 = new("ui_palette_bg1", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteBG2 = new("ui_palette_bg2", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteBG3 = new("ui_palette_bg3", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteLayer = new("ui_palette_layer", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteAccent = new("ui_palette_accent", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteFont1 = new("ui_palette_font1", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteFont2 = new("ui_palette_font2", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteFont3 = new("ui_palette_font3", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteInputField = new("ui_palette_inputfield", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteButton = new("ui_palette_button", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteWhite = new("ui_palette_white", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteClear = new("ui_palette_clear", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteBlack = new("ui_palette_black", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteSuccess = new("ui_palette_success", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteCaution = new("ui_palette_caution", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteDanger = new("ui_palette_danger", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<string> UIPaletteScrollbar = new("ui_palette_scrollbar", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<bool> MenuEdgeWhite = new("menu_edge_white", new BasisPlatformDefault<bool>(false));

        // ---------------- MENU BACKGROUND ----------------
        public static BasisSettingsBinding<float> MenuBGAccentAmount = new("menubg_accentamount", new BasisPlatformDefault<float>(0.2f));
        public static BasisSettingsBinding<float> MenuBGAccentFeather = new("menubg_accentfeather", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> MenuBGAccentSoftness = new("menubg_accentsoftness", new BasisPlatformDefault<float>(1.75f));
        public static BasisSettingsBinding<float> MenuBGBrandGradient = new("menubg_brandgradient", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> MenuBGGradientCycle = new("menubg_gradientcycle", new BasisPlatformDefault<float>(15f));
        public static BasisSettingsBinding<float> MenuBGAnimationSpeed = new("menubg_animationspeed", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> MenuBGSheen = new("menubg_sheen", new BasisPlatformDefault<float>(0.135f));
        public static BasisSettingsBinding<string> MenuBGCursorGlowColor = new("menubg_cursorglowcolor", new BasisPlatformDefault<string>(""));
        public static BasisSettingsBinding<float> MenuBGCursorGlow = new("menubg_cursorglow", new BasisPlatformDefault<float>(0.18f));
        public static BasisSettingsBinding<float> MenuBGCursorGlowRadius = new("menubg_cursorglowradius", new BasisPlatformDefault<float>(0.3f));
        public static BasisSettingsBinding<float> MenuBGVignette = new("menubg_vignette", new BasisPlatformDefault<float>(0.4f));
        public static BasisSettingsBinding<float> MenuBGExposure = new("menubg_exposure", new BasisPlatformDefault<float>(0.84f));
        public static BasisSettingsBinding<float> MenuBGGrain = new("menubg_grain", new BasisPlatformDefault<float>(3f));
        public static BasisSettingsBinding<float> MenuBGGrainScale = new("menubg_grainscale", new BasisPlatformDefault<float>(900f));

        // ---------------- MIRROR ----------------
        public static BasisSettingsBinding<bool> UseMirrorQualityOverride = new("usemirrorqualityoverride", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<string> MirrorQuality = new("mirrorquality", new BasisPlatformDefault<string>("2048"));

        // ---------------- CAMERA CLIP OVERRIDE ----------------
        public static BasisSettingsBinding<bool> UseCameraClipOverride = new("usecameraclipoverride", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> CameraClipNear = new("cameraclipnear", new BasisPlatformDefault<float>(0.01f));
        public static BasisSettingsBinding<float> CameraClipFar = new("cameraclipfar", new BasisPlatformDefault<float>(1000f));

        // Noise Gate
        public static BasisSettingsBinding<bool> UseNoiseGate = new("usenoisegate", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> AutoNoiseGate = new("autonoisegate", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> NoiseGateThreshold = new("noisegatethreshold", new BasisPlatformDefault<float>(0.01f)); // RMS threshold
        public static BasisSettingsBinding<float> NoiseGateAttack = new("noisegateattack", new BasisPlatformDefault<float>(0.10f)); // 0..1
        public static BasisSettingsBinding<float> NoiseGateRelease = new("noisegaterelease", new BasisPlatformDefault<float>(0.05f)); // 0..1

        /// <summary>
        /// We’ll initialize the language settings elsewhere.
        /// see <see cref="BasisLocalization.Initialize"/>
        /// </summary>
        public static BasisSettingsBinding<string> Language = new("language", new BasisPlatformDefault<string>(string.Empty));

        public static void LoadAll()
        {
            // Localization
            Language.LoadBindingValue();

            // Audio
            MainVolume.LoadBindingValue();
            MenuVolume.LoadBindingValue();
            WorldVolume.LoadBindingValue();
            VoiceVolume.LoadBindingValue();
            AvatarVolume.LoadBindingValue();
            PropVolume.LoadBindingValue();

            SoundHover.LoadBindingValue();
            SoundPress.LoadBindingValue();
            SoundGrab.LoadBindingValue();
            SoundChat.LoadBindingValue();
            SoundMicrophone.LoadBindingValue();
            SoundMicrophonePushToTalk.LoadBindingValue();
            SoundCamera.LoadBindingValue();

            MicrophoneVolume.LoadBindingValue();
            MicrophoneRange.LoadBindingValue();
            HearingRange.LoadBindingValue();
            MicrophoneDenoiser.LoadBindingValue();
            MicrophoneMode.LoadBindingValue();
            P2PAvatarSyncRate.LoadBindingValue();
            P2PVoiceBitrateOverride.LoadBindingValue();
            P2PVoiceBitrate.LoadBindingValue();
            DisableDirectConnections.LoadBindingValue();
            MicStartBehavior.LoadBindingValue();
            MicMuteBehavior.LoadBindingValue();
            TalkToNoOne.LoadBindingValue();
            UseAutomaticGain.LoadBindingValue();
            DenoiseMakeupDb.LoadBindingValue();
            DenoiseWet.LoadBindingValue();
            // AgcTargetRms.LoadBindingValue();
            AgcMaxGainDb.LoadBindingValue();
            AgcAttack.LoadBindingValue();
            AgcRelease.LoadBindingValue();
            UseNoiseGate.LoadBindingValue();
            AutoNoiseGate.LoadBindingValue();
            NoiseGateThreshold.LoadBindingValue();
            NoiseGateAttack.LoadBindingValue();
            NoiseGateRelease.LoadBindingValue();

            // Audio Debug
            AudioDebugEnabled.LoadBindingValue();
            DisableLipSyncForFaceTracking.LoadBindingValue();
            AudioDebugShowSource.LoadBindingValue();
            AudioDebugShowVolume.LoadBindingValue();
            AudioDebugShowRingBuffer.LoadBindingValue();
            AudioDebugShowJitter.LoadBindingValue();
            AudioDebugShowSilence.LoadBindingValue();
            AudioDebugShowViseme.LoadBindingValue();

            // Input / Movement
            ControllerDeadZone.LoadBindingValue();
            Basexdeadzone.LoadBindingValue();
            Extraxdeadzoneatfully.LoadBindingValue();
            Ydeadzone.LoadBindingValue();
            Wingexponent.LoadBindingValue();
            SnapTurnAngle.LoadBindingValue();
            mousesensitivty.LoadBindingValue();
            InvertMouse.LoadBindingValue();
            DominantHand.LoadBindingValue();
            usesnapturn.LoadBindingValue();
            SmoothTurnSpeed.LoadBindingValue();
            ScrollSpeed.LoadBindingValue();
            DesktopInputInVR.LoadBindingValue();

            // Avatar / IK / Body
            SelectedHeight.LoadBindingValue();
            SelectedScale.LoadBindingValue();
            realworldeyeheight.LoadBindingValue();
            CustomScale.LoadBindingValue();
            AvatarRange.LoadBindingValue();
            UseMaxVisibleAvatars.LoadBindingValue();
            MaxVisibleAvatars.LoadBindingValue();
            HighPlayerCapSuggestions.LoadBindingValue();
            AllowProxyBenchmarkRange.LoadBindingValue();
            // Pushed here rather than only from BasisFakeIpCompatibilityPrompt's initialiser:
            // LoadBindingValue deliberately does not raise OnChanged, so a player who turned this
            // on last session would otherwise start the next one with the gate still closed.
            Basis.Scripts.Common.BasisUrlSecurity.AllowBenchmarkRangeFromDns = AllowProxyBenchmarkRange.RawValue;
            PerformanceModeLevel.LoadBindingValue();
            PerformanceModeAuto.LoadBindingValue();
            PerformanceModeBaseline.LoadBindingValue();
            UseMaxAudioSources.LoadBindingValue();
            MaxAudioSources.LoadBindingValue();
            UseOpenLipSyncLimit.LoadBindingValue();
            OpenLipSyncMaxSlots.LoadBindingValue();
            PoseLOD.LoadBindingValue();
            SelectedBone.LoadBindingValue();
            IKMode.LoadBindingValue();
            IKLockMode.LoadBindingValue();
            CalibrationMirror.LoadBindingValue();
            EnableArmToHeightBlend.LoadBindingValue();
            ArmToHeightBlend.LoadBindingValue();
            SavedPlayerEyeHeight.LoadBindingValue();
            SavedPlayerArmSpan.LoadBindingValue();
            AutoScaleEstimateEnabled.LoadBindingValue();
            ContinuousBodyMeasurement.LoadBindingValue();
            StatedBodyHeight.LoadBindingValue();
            SitStand.LoadBindingValue();
            EnableFBT.LoadBindingValue();
            TrackerVisuals.LoadBindingValue();
            EnableOSC.LoadBindingValue();
            EnableFaceTracking.LoadBindingValue();
            EnableEyeTracking.LoadBindingValue();
            EyeTrackingPreferOsc.LoadBindingValue();
            FootIKEnabled.LoadBindingValue();
            DisableAnimationsInFBT.LoadBindingValue();
            LocalHeadBlendShapes.LoadBindingValue();
            EnablePlayspaceMover.LoadBindingValue();
            PlayspaceMoverInput.LoadBindingValue();
            PlayspaceMoverRotateInput.LoadBindingValue();
            PlayspaceMoverHand.LoadBindingValue();
            PlayspaceMoverRotate.LoadBindingValue();
            PlayspaceMoverScale.LoadBindingValue();
            PlayspaceMoverVertical.LoadBindingValue();
            PlayspaceMoverFlip.LoadBindingValue();
            PlayspaceMoverFlipAngle.LoadBindingValue();
            PlayspaceMoverFlipAxis.LoadBindingValue();
            PlayspaceGizmos.LoadBindingValue();
            PlayspaceGizmoBoundary.LoadBindingValue();
            PlayspaceGizmoOrigin.LoadBindingValue();
            PlayspaceGizmoOffset.LoadBindingValue();
            PlayspaceGizmoHands.LoadBindingValue();
            PlayspaceGizmoReadouts.LoadBindingValue();
            EnableScriptedPlayerInput.LoadBindingValue();

            // Rendering / Graphics
            QualityLevel.LoadBindingValue();
            ShadowQuality.LoadBindingValue();
            HDRSupport.LoadBindingValue();
            Antialiasing.LoadBindingValue();
            DevVariableRateShading.LoadBindingValue();
            DevVariableRateShadingDesktop.LoadBindingValue();
            DevGiDebugView.LoadBindingValue();
            VrsFovealInnerRadius.LoadBindingValue();
            VrsFovealOuterRadius.LoadBindingValue();
            UseBloomOverride.LoadBindingValue();
            BloomIntensity.LoadBindingValue();
            UseVolumetricFogOverride.LoadBindingValue();
            VolumetricFogDensity.LoadBindingValue();
            VolumetricFogBakedAPV.LoadBindingValue();
            UseMotionBlurOverride.LoadBindingValue();
            MotionBlurIntensity.LoadBindingValue();
            MotionBlurClamp.LoadBindingValue();
            MotionBlurQuality.LoadBindingValue();
            MotionBlurMode.LoadBindingValue();
            UseGlobalIllumination.LoadBindingValue();
            GlobalIlluminationMode.LoadBindingValue();
            GlobalIlluminationSkinnedMeshes.LoadBindingValue();
            GlobalIlluminationQuality.LoadBindingValue();
            GlobalIlluminationResolution.LoadBindingValue();
            GlobalIlluminationFallback.LoadBindingValue();
            GlobalIlluminationIntensity.LoadBindingValue();
            GlobalIlluminationSaturation.LoadBindingValue();
            GlobalIlluminationObscurance.LoadBindingValue();
            GlobalIlluminationRayLength.LoadBindingValue();
            GlobalIlluminationSmoothing.LoadBindingValue();
            GlobalIlluminationTemporalResponse.LoadBindingValue();
            GlobalIlluminationTemporalFilter.LoadBindingValue();
            GlobalIlluminationWideBlur.LoadBindingValue();
            GlobalIlluminationRayReuse.LoadBindingValue();
            GlobalIlluminationEmitters.LoadBindingValue();
            GlobalIlluminationEmitterIntensity.LoadBindingValue();
            GlobalIlluminationReflectionProbes.LoadBindingValue();
            GlobalIlluminationMirrors.LoadBindingValue();

            UseRayTracedAmbientOcclusion.LoadBindingValue();
            RayTracedAmbientOcclusionMode.LoadBindingValue();
            RayTracedAmbientOcclusionQuality.LoadBindingValue();
            RayTracedAmbientOcclusionIntensity.LoadBindingValue();
            RayTracedAmbientOcclusionRadius.LoadBindingValue();
            RayTracedAmbientOcclusionSkinnedMeshes.LoadBindingValue();
            RayTracedAmbientOcclusionDirectStrength.LoadBindingValue();
            RayTracedAmbientOcclusionDenoise.LoadBindingValue();
            RayTracedAmbientOcclusionOtherCameras.LoadBindingValue();
            RayTracedAmbientOcclusionApply.LoadBindingValue();
            DevRtaoDebugView.LoadBindingValue();
            DevRtaoDebugStage.LoadBindingValue();
            DevRtaoSkinnedBudget.LoadBindingValue();
            //UseRealtimeReflectionProbes.LoadBindingValue();
            //RealtimeReflectionProbeRate.LoadBindingValue();
            ShowGizmos.LoadBindingValue();
            GizmoSkeletonLines.LoadBindingValue();
            GizmoCalibrationSpheres.LoadBindingValue();
            GizmoJiggleVisuals.LoadBindingValue();
            TrackerGizmos.LoadBindingValue();
            LinkedTrackerLines.LoadBindingValue();
            GizmoEyeGaze.LoadBindingValue();
            GizmoIKColliders.LoadBindingValue();
            Basis.Scripts.Debugging.BasisIKSolveGizmoStages.LoadAll();
            GizmoPointerRay.LoadBindingValue();
            GizmoHintOffsets.LoadBindingValue();
            GizmoFootPlacement.LoadBindingValue();
            GizmoInteractionHover.LoadBindingValue();
            GizmoFingerTouch.LoadBindingValue();
            GizmoSeatTargets.LoadBindingValue();
            GizmoJiggleGrab.LoadBindingValue();
            GizmoHandGrip.LoadBindingValue();
            GizmoMouthEye.LoadBindingValue();
            GizmoAudioRanges.LoadBindingValue();
            GizmoAudioListenerCone.LoadBindingValue();
            GizmoAudioLevels.LoadBindingValue();
            GizmoNetworkSync.LoadBindingValue();
            GizmoNetworkSyncBandwidth.LoadBindingValue();
            GizmoNetworkPlayers.LoadBindingValue();
            GizmoNetworkPlayersBandwidth.LoadBindingValue();
            GizmoNetworkAdditionalInfo.LoadBindingValue();
            GizmoLabels.LoadBindingValue();
            GizmoDrawOnTop.LoadBindingValue();
            GizmoRenderInAllCameras.LoadBindingValue();
            EnableStatistics.LoadBindingValue();
            ShowVoiceRange.LoadBindingValue();
            AvatarDataDebugEnabled.LoadBindingValue();
            AvatarDataDebugShowReceive.LoadBindingValue();
            AvatarDataDebugShowStaging.LoadBindingValue();
            AvatarDataDebugShowInterp.LoadBindingValue();
            AvatarDataDebugShowMeta.LoadBindingValue();
            DevShowCalibrationDebug.LoadBindingValue();
            DevAlwaysShowCalibration.LoadBindingValue();
            DumpCalibrationCsv.LoadBindingValue();
            SpawnAnchorHandles.LoadBindingValue();
            SpawnAnchorSeatOnSurface.LoadBindingValue();
            SpawnAnchorPositionSnap.LoadBindingValue();
            SpawnAnchorPositionSnapSize.LoadBindingValue();
            SpawnAnchorRotationSnap.LoadBindingValue();
            SpawnAnchorRotationSnapDegrees.LoadBindingValue();
            DisableLogging.LoadBindingValue();
            BasisDebug.LoggingDisabled = DisableLogging.RawValue;
            DisableLogging.OnChanged += value => BasisDebug.LoggingDisabled = value;
            EnableShaderPrewarm.LoadBindingValue();
            ContentPoliceControl.ShaderPrewarmEnabled = EnableShaderPrewarm.RawValue;
            EnableShaderPrewarm.OnChanged += value => ContentPoliceControl.ShaderPrewarmEnabled = value;
            EnableMaterialCorrection.LoadBindingValue();
            ContentPoliceControl.MaterialCorrectionEnabled = EnableMaterialCorrection.RawValue;
            EnableMaterialCorrection.OnChanged += value => ContentPoliceControl.MaterialCorrectionEnabled = value;
            EnableShaderBlocklist.LoadBindingValue();
            ContentPoliceControl.ShaderBlocklistEnabled = EnableShaderBlocklist.RawValue;
            EnableShaderBlocklist.OnChanged += value => ContentPoliceControl.ShaderBlocklistEnabled = value;
            ShaderBlocklistPatterns.LoadBindingValue();
            BasisShaderFallback.SetBlocklist(ShaderBlocklistPatterns.RawValue);
            ShaderBlocklistPatterns.OnChanged += BasisShaderFallback.SetBlocklist;
            EnableGraphicsStatePrewarm.LoadBindingValue();
            BasisGraphicsStatePrewarm.Enabled = EnableGraphicsStatePrewarm.RawValue;
            BasisGraphicsStateWarmPump.Apply(EnableGraphicsStatePrewarm.RawValue);
            EnableGraphicsStatePrewarm.OnChanged += value => { BasisGraphicsStatePrewarm.Enabled = value; BasisGraphicsStateWarmPump.Apply(value); };
            ContentPoliceLogging.LoadBindingValue();
            ContentPoliceControl.VerboseLogging = ContentPoliceLogging.RawValue;
            ContentPoliceLogging.OnChanged += value => ContentPoliceControl.VerboseLogging = value;
            DebugLogTagFilter.LoadBindingValue();
            ApplyDebugLogTagFilter(DebugLogTagFilter.RawValue);
            DebugLogTagFilter.OnChanged += ApplyDebugLogTagFilter;
            DebugLogLevelFilter.LoadBindingValue();
            ApplyDebugLogLevelFilter(DebugLogLevelFilter.RawValue);
            DebugLogLevelFilter.OnChanged += ApplyDebugLogLevelFilter;
            EnableStreamingMeta.LoadBindingValue();
            StreamingMetaPort.LoadBindingValue();
            // Bindings are now populated from disk. The streaming runtime bootstraps at
            // AfterSceneLoad — before this runs — so its initial read saw the default (off),
            // and LoadBindingValue does not fire OnChanged. Re-apply so the listener comes up
            // on startup when the user already had it enabled, not only after a manual toggle.
            BasisStreamingMetaRuntime.ApplyFromSettings();
            MemoryAllocation.LoadBindingValue();
            AvatarRangeIndicator.LoadBindingValue();
            HearingRangeIndicator.LoadBindingValue();
            MicrophoneRangeIndicator.LoadBindingValue();
            FoveatedRendering.LoadBindingValue();
            EyeFoveationAutoManage.LoadBindingValue();
            FieldOfView.LoadBindingValue();
            RenderResolution.LoadBindingValue();
            DynamicResolutionEnabled.LoadBindingValue();
            DynamicResolutionMinimumScale.LoadBindingValue();
            DynamicResolutionMaximumScale.LoadBindingValue();
            DynamicResolutionTargetOverride.LoadBindingValue();
            DynamicResolutionTargetFrameRate.LoadBindingValue();
            VSync.LoadBindingValue();
            VSyncCapFps.LoadBindingValue();
            HeadsetRefreshRate.LoadBindingValue();

            // Mirror
            UseMirrorQualityOverride.LoadBindingValue();
            MirrorQuality.LoadBindingValue();

            // Camera Clip Override
            UseCameraClipOverride.LoadBindingValue();
            CameraClipNear.LoadBindingValue();
            CameraClipFar.LoadBindingValue();

            // LOD / Download limits
            AvatarDownloadSize.LoadBindingValue();
            MaxConcurrentAvatarDownloads.LoadBindingValue();
            MaxConcurrentAvatarDiscLoads.LoadBindingValue();
            MaxConcurrentAvatarAddressables.LoadBindingValue();
            CacheMaxSizeGB.LoadBindingValue();
            AvatarMeshLOD.LoadBindingValue();
            UseAvatarSkinLod.LoadBindingValue();
            UseAvatarShadowLod.LoadBindingValue();
            UseAvatarVisibilityCull.LoadBindingValue();
            UseGpuOcclusionCulling.LoadBindingValue();
            UseAvatarFarLod.LoadBindingValue();
            //AvatarFarLodDistance.LoadBindingValue();
            GlobalMeshLOD.LoadBindingValue();

            // Performance Limits
            UsePerfLimitTriangles.LoadBindingValue();
            MaxPerfTriangles.LoadBindingValue();
            UsePerfLimitBoundsSize.LoadBindingValue();
            MaxPerfBoundsSize.LoadBindingValue();
            UsePerfLimitTextureMemory.LoadBindingValue();
            MaxPerfTextureMemoryMB.LoadBindingValue();
            UsePerfLimitSkinnedMeshes.LoadBindingValue();
            MaxPerfSkinnedMeshes.LoadBindingValue();
            UsePerfLimitBasicMeshes.LoadBindingValue();
            MaxPerfBasicMeshes.LoadBindingValue();
            UsePerfLimitMaterialSlots.LoadBindingValue();
            MaxPerfMaterialSlots.LoadBindingValue();
            UsePerfLimitJiggleBones.LoadBindingValue();
            MaxPerfJiggleBones.LoadBindingValue();
            UsePerfLimitJiggleColliders.LoadBindingValue();
            MaxPerfJiggleColliders.LoadBindingValue();
            UseJiggleCollisionFrustumCull.LoadBindingValue();
            UseJiggleCollisionDistanceCull.LoadBindingValue();
            JiggleCollisionCullDistance.LoadBindingValue();
            JiggleCullFrustumExpansion.LoadBindingValue();
            JiggleCullNearKeepRadius.LoadBindingValue();
            JiggleBroadPhaseCellSize.LoadBindingValue();
            UseJiggleColliderDistanceLod.LoadBindingValue();
            JiggleColliderLodNearDistance.LoadBindingValue();
            JiggleColliderLodMidDistance.LoadBindingValue();
            JiggleColliderLodFarDistance.LoadBindingValue();
            UsePerfLimitAnimators.LoadBindingValue();
            MaxPerfAnimators.LoadBindingValue();
            UsePerfLimitBones.LoadBindingValue();
            MaxPerfBones.LoadBindingValue();
            UsePerfLimitLights.LoadBindingValue();
            MaxPerfLights.LoadBindingValue();
            UsePerfLimitParticleSystems.LoadBindingValue();
            MaxPerfParticleSystems.LoadBindingValue();
            UsePerfLimitTrailRenderers.LoadBindingValue();
            MaxPerfTrailRenderers.LoadBindingValue();
            UsePerfLimitLineRenderers.LoadBindingValue();
            MaxPerfLineRenderers.LoadBindingValue();
            UsePerfLimitCloth.LoadBindingValue();
            MaxPerfCloth.LoadBindingValue();
            UsePerfLimitColliders.LoadBindingValue();
            MaxPerfColliders.LoadBindingValue();
            UsePerfLimitCilboxBehaviours.LoadBindingValue();
            MaxPerfCilboxBehaviours.LoadBindingValue();

            // Networking
            AutoConnect.LoadBindingValue();
            NetworkJitterBufferOverride.LoadBindingValue();
            NetworkJitterBufferDepth.LoadBindingValue();

            // Device Swap Mode
            SwapMode.LoadBindingValue();
            UsePresenceSensor.LoadBindingValue();

            // Notifications
            JoinNotifications.LoadBindingValue();
            LeaveNotifications.LoadBindingValue();
            ExceptionNotifications.LoadBindingValue();
            ErrorNotifications.LoadBindingValue();

            // Chat
            ChatDisabled.LoadBindingValue();

            // UI
            RememberMenuState.LoadBindingValue();
            ShowDeveloperTab.LoadBindingValue();
            AvatarPreview.LoadBindingValue();
            AvatarPreviewMirror.LoadBindingValue();
            LimitHandHeldCameraRate.LoadBindingValue();
            HandHeldCameraRenderHz.LoadBindingValue();
            LimitAvatarPreviewRate.LoadBindingValue();
            AvatarPreviewRenderHz.LoadBindingValue();
            DesktopReticle.LoadBindingValue();
            EnablePassthrough.LoadBindingValue();
            EnableThirdPersonCamera.LoadBindingValue();
            AudioListenerFollowsHead.LoadBindingValue();
            MicrophoneIcon.LoadBindingValue();
            MicrophoneIconOffsetX.LoadBindingValue();
            MicrophoneIconOffsetY.LoadBindingValue();
            MicrophoneIconLevelRing.LoadBindingValue();

            // Photo metadata
            PhotoMetadataTagging.LoadBindingValue();
            PhotoEmbedCameraSettings.LoadBindingValue();
            PhotoEmbedCaptureInfo.LoadBindingValue();
            PhotoEmbedPhotographer.LoadBindingValue();
            PhotoEmbedWorld.LoadBindingValue();
            PhotoEmbedPersonDetails.LoadBindingValue();

            // Misc
            //FalseBinding.LoadBindingValue();
            //TrueBinding.LoadBindingValue();
            LimitThreshold.LoadBindingValue();
            LimitKnee.LoadBindingValue();
            DisableSeats.LoadBindingValue();
            HideRemoteCameraPucks.LoadBindingValue();
            BasisNetworkPIPCameraDriver.SetHideRemoteCameraPucks(HideRemoteCameraPucks.RawValue);
            HideRemoteCameraPucks.OnChanged += BasisNetworkPIPCameraDriver.SetHideRemoteCameraPucks;
            DisablePropPickup.LoadBindingValue();
            DisableVRAutoHold.LoadBindingValue();
            JiggleGrabInteractions.LoadBindingValue();
            Basis.Scripts.BasisSdk.Interactions.BasisJiggleGrabPermissions.MasterEnabled = JiggleGrabInteractions.RawValue;
            JiggleGrabInteractions.OnChanged += on =>
            {
                Basis.Scripts.BasisSdk.Interactions.BasisJiggleGrabPermissions.MasterEnabled = on;
                if (!on)
                {
                    Basis.Scripts.BasisSdk.Interactions.BasisJiggleGrabDriver.ReleaseLocalGrabs();
                }
            };
            UIHaptics.LoadBindingValue();
            UIClickPressThreshold.LoadBindingValue();
            UIClickReleaseThreshold.LoadBindingValue();
            JoystickBindSweepSeconds.LoadBindingValue();
            DisableVRFingerTouch.LoadBindingValue();
            FingerTouchFinger.LoadBindingValue();
            FingerTouchHands.LoadBindingValue();
            FingerTouchTipOffset.LoadBindingValue();
            FingerTouchFingerLength.LoadBindingValue();
            FingerTouchRadius.LoadBindingValue();
            FingerTouchHoverDistance.LoadBindingValue();
            FingerTouchPressDepth.LoadBindingValue();
            FingerTouchReleaseDistance.LoadBindingValue();
            FingerTouchScrollSensitivity.LoadBindingValue();
            FingerTouchHaptics.LoadBindingValue();
            ForceGridSnap.LoadBindingValue();
            GridSnapSize.LoadBindingValue();
            ForceRotationSnap.LoadBindingValue();
            RotationSnapDegrees.LoadBindingValue();

            // Global FBIK parameters
            FBIKMinCutoff.LoadBindingValue();
            FBIKBeta.LoadBindingValue();
            FBIKDerivativeCutoff.LoadBindingValue();
            FBIKPositionSmoothingHz.LoadBindingValue();
            FBIKRotationSmoothingHz.LoadBindingValue();

            for (int Index = 0; Index < FBIKSmoothingGroups.Length; Index++)
            {
                SmoothingGroupBindings group = FBIKSmoothingGroups[Index];
                group.Preset.LoadBindingValue();
                group.Custom.LoadBindingValue();
                group.MinCutoff.LoadBindingValue();
                group.Beta.LoadBindingValue();
                group.Strength.LoadBindingValue();
                group.PositionHz.LoadBindingValue();
                group.RotationHz.LoadBindingValue();
            }
            FBIKSmoothingStrength.LoadBindingValue();

            // Hips
            FBIKHipsSmoothPos.LoadBindingValue();
            FBIKHipsSmoothRot.LoadBindingValue();
            FBIKHipsEuroPos.LoadBindingValue();
            FBIKHipsEuroRot.LoadBindingValue();

            // Head
            FBIKHeadSmoothPos.LoadBindingValue();
            FBIKHeadSmoothRot.LoadBindingValue();
            FBIKHeadEuroPos.LoadBindingValue();
            FBIKHeadEuroRot.LoadBindingValue();

            // Left Foot
            FBIKLeftFootSmoothPos.LoadBindingValue();
            FBIKLeftFootSmoothRot.LoadBindingValue();
            FBIKLeftFootEuroPos.LoadBindingValue();
            FBIKLeftFootEuroRot.LoadBindingValue();

            // Right Foot
            FBIKRightFootSmoothPos.LoadBindingValue();
            FBIKRightFootSmoothRot.LoadBindingValue();
            FBIKRightFootEuroPos.LoadBindingValue();
            FBIKRightFootEuroRot.LoadBindingValue();

            // Chest
            FBIKChestSmoothPos.LoadBindingValue();
            FBIKChestSmoothRot.LoadBindingValue();
            FBIKChestEuroPos.LoadBindingValue();
            FBIKChestEuroRot.LoadBindingValue();

            // Left Lower Leg
            FBIKLeftLowerLegSmoothPos.LoadBindingValue();
            FBIKLeftLowerLegSmoothRot.LoadBindingValue();
            FBIKLeftLowerLegEuroPos.LoadBindingValue();
            FBIKLeftLowerLegEuroRot.LoadBindingValue();

            // Right Lower Leg
            FBIKRightLowerLegSmoothPos.LoadBindingValue();
            FBIKRightLowerLegSmoothRot.LoadBindingValue();
            FBIKRightLowerLegEuroPos.LoadBindingValue();
            FBIKRightLowerLegEuroRot.LoadBindingValue();

            // Left Hand
            FBIKLeftHandSmoothPos.LoadBindingValue();
            FBIKLeftHandSmoothRot.LoadBindingValue();
            FBIKLeftHandEuroPos.LoadBindingValue();
            FBIKLeftHandEuroRot.LoadBindingValue();

            // Right Hand
            FBIKRightHandSmoothPos.LoadBindingValue();
            FBIKRightHandSmoothRot.LoadBindingValue();
            FBIKRightHandEuroPos.LoadBindingValue();
            FBIKRightHandEuroRot.LoadBindingValue();

            // Left Lower Arm
            FBIKLeftLowerArmSmoothPos.LoadBindingValue();
            FBIKLeftLowerArmSmoothRot.LoadBindingValue();
            FBIKLeftLowerArmEuroPos.LoadBindingValue();
            FBIKLeftLowerArmEuroRot.LoadBindingValue();

            // Right Lower Arm
            FBIKRightLowerArmSmoothPos.LoadBindingValue();
            FBIKRightLowerArmSmoothRot.LoadBindingValue();
            FBIKRightLowerArmEuroPos.LoadBindingValue();
            FBIKRightLowerArmEuroRot.LoadBindingValue();

            // Left Toe
            FBIKLeftToeSmoothPos.LoadBindingValue();
            FBIKLeftToeSmoothRot.LoadBindingValue();
            FBIKLeftToeEuroPos.LoadBindingValue();
            FBIKLeftToeEuroRot.LoadBindingValue();

            // Right Toe
            FBIKRightToeSmoothPos.LoadBindingValue();
            FBIKRightToeSmoothRot.LoadBindingValue();
            FBIKRightToeEuroPos.LoadBindingValue();
            FBIKRightToeEuroRot.LoadBindingValue();

            // Shoulders
            FBIKLeftShoulderSmoothPos.LoadBindingValue();
            FBIKLeftShoulderSmoothRot.LoadBindingValue();
            FBIKLeftShoulderEuroPos.LoadBindingValue();
            FBIKLeftShoulderEuroRot.LoadBindingValue();

            FBIKRightShoulderSmoothPos.LoadBindingValue();
            FBIKRightShoulderSmoothRot.LoadBindingValue();
            FBIKRightShoulderEuroPos.LoadBindingValue();
            FBIKRightShoulderEuroRot.LoadBindingValue();

            // Per-bone "use for calibration" toggles
            FBIKHipsUseCalibration.LoadBindingValue();
            FBIKHeadUseCalibration.LoadBindingValue();
            FBIKLeftFootUseCalibration.LoadBindingValue();
            FBIKRightFootUseCalibration.LoadBindingValue();
            FBIKChestUseCalibration.LoadBindingValue();
            FBIKLeftLowerLegUseCalibration.LoadBindingValue();
            FBIKRightLowerLegUseCalibration.LoadBindingValue();
            FBIKLeftHandUseCalibration.LoadBindingValue();
            FBIKRightHandUseCalibration.LoadBindingValue();
            FBIKLeftLowerArmUseCalibration.LoadBindingValue();
            FBIKRightLowerArmUseCalibration.LoadBindingValue();
            FBIKLeftToeUseCalibration.LoadBindingValue();
            FBIKRightToeUseCalibration.LoadBindingValue();
            FBIKLeftShoulderUseCalibration.LoadBindingValue();
            FBIKRightShoulderUseCalibration.LoadBindingValue();

            // Global toggle
            FBIKEuroAll.LoadBindingValue();

            // Calibration sphere scale (per bone)
            CalibSphereScaleHips.LoadBindingValue();
            CalibSphereScaleChest.LoadBindingValue();
            CalibSphereScaleLeftFoot.LoadBindingValue();
            CalibSphereScaleRightFoot.LoadBindingValue();
            CalibSphereScaleLeftLowerLeg.LoadBindingValue();
            CalibSphereScaleRightLowerLeg.LoadBindingValue();
            CalibSphereScaleLeftLowerArm.LoadBindingValue();
            CalibSphereScaleRightLowerArm.LoadBindingValue();
            CalibSphereScaleLeftHand.LoadBindingValue();
            CalibSphereScaleRightHand.LoadBindingValue();
            CalibSphereScaleLeftToes.LoadBindingValue();
            CalibSphereScaleRightToes.LoadBindingValue();
            CalibSphereScaleLeftShoulder.LoadBindingValue();
            CalibSphereScaleRightShoulder.LoadBindingValue();

            // IK Collider & Tuning
            FBIKAdvancedVisible.LoadBindingValue();
            FBIKCollisionsEnabled.LoadBindingValue();
            FBIKProtectElbow.LoadBindingValue();
            FBIKCollideTrackedElbow.LoadBindingValue();
            FBIKWristAxialBound.LoadBindingValue();
            FBIKElbowDrag.LoadBindingValue();
            FBIKElbowDragHz.LoadBindingValue();
            FBIKChestRadius.LoadBindingValue();
            FBIKCollisionSkin.LoadBindingValue();
            FBIKHandRadius.LoadBindingValue();
            FBIKHandSkin.LoadBindingValue();
            FBIKShoulderSolveEnabled.LoadBindingValue();
            FBIKShoulderShrug.LoadBindingValue();
            FBIKShoulderRetraction.LoadBindingValue();
            FBIKShoulderElevation.LoadBindingValue();
            FBIKShoulderProtraction.LoadBindingValue();
            FBIKMaxBendDeg.LoadBindingValue();
            FBIKMaxChestDelta.LoadBindingValue();
            FBIKButterflyKnees.LoadBindingValue();
            FBIKButterflyKneeMaxOpenDeg.LoadBindingValue();
            FBIKKneeFollowsFoot.LoadBindingValue();
            FBIKKneeFootFollowUpright.LoadBindingValue();
            FBIKKneeFootPoleHold.LoadBindingValue();
            FBIKKneeFootPoleConditioning.LoadBindingValue();
            FBIKTrackedKneeSwivelMinCutoffHz.LoadBindingValue();
            FBIKTrackedKneeSwivelBeta.LoadBindingValue();
            FBIKTrackedKneeSwivelDerivCutoffHz.LoadBindingValue();
            FBIKShoulderCoupleRatio.LoadBindingValue();
            FBIKShoulderMaxDeg.LoadBindingValue();
            FBIKShoulderSlideStartDeg.LoadBindingValue();
            FBIKShoulderSlideMaxDeg.LoadBindingValue();
            FBIKShoulderSlideFraction.LoadBindingValue();
            FBIKThoracicBendStiffen.LoadBindingValue();
            FBIKSpineTautBandFrac.LoadBindingValue();
            FBIKBendTwistCoupling.LoadBindingValue();
            FBIKNeckGazeFollowMaxDeg.LoadBindingValue();
            FBIKTrunkCounterbalanceMaxFrac.LoadBindingValue();
            FBIKChestIkWeight.LoadBindingValue();
            FBIKChestIkIterations.LoadBindingValue();
            FBIKChestIkHeadRestoreSweeps.LoadBindingValue();
            FBIKChestPosPullMaxDeg.LoadBindingValue();
            FBIKChestPullMaxDist.LoadBindingValue();
            FBIKChestFollowChestShare.LoadBindingValue();
         //   FBIKNeuralPole.LoadBindingValue();
            FBIKSpineAnatomicalRom.LoadBindingValue();
            FBIKChestIKTarget.LoadBindingValue();
            FBIKBodyFit.LoadBindingValue();
            FBIKJobLocomotion.LoadBindingValue();
            FBIKBodyFitMaxDeviation.LoadBindingValue();
            FBIKSpineBendPitch.LoadBindingValue();
            FBIKSpineBendYaw.LoadBindingValue();
            FBIKSpineBendRoll.LoadBindingValue();
            FBIKUpperChestBendPitch.LoadBindingValue();
            FBIKUpperChestBendYaw.LoadBindingValue();
            FBIKUpperChestBendRoll.LoadBindingValue();
            FBIKChestBendPitch.LoadBindingValue();
            FBIKChestBendYaw.LoadBindingValue();
            FBIKChestBendRoll.LoadBindingValue();
            FBIKNeckYawShare.LoadBindingValue();
            FBIKSpineStretchMax.LoadBindingValue();
            FBIKHipHingeStartDeg.LoadBindingValue();
            FBIKHipHingeMaxAddDeg.LoadBindingValue();
            FBIKChestSpringHz.LoadBindingValue();
            FBIKChestSpringDamping.LoadBindingValue();
            FBIKSpineMaxForwardDeg.LoadBindingValue();
            FBIKSpineMaxBackwardDeg.LoadBindingValue();
            FBIKSpineMaxLateralDeg.LoadBindingValue();
            FBIKSpineSquishBoost.LoadBindingValue();
            FBIKSpineGazeFollow.LoadBindingValue();
            FBIKNeckGazeFollow.LoadBindingValue();
            FBIKNeckExtensionDamp.LoadBindingValue();
            FBIKNeckFlexionDamp.LoadBindingValue();
            FBIKMoveBodyBackWhenCrouching.LoadBindingValue();
            FBIKTrunkCounterbalance.LoadBindingValue();
            FBIKSwingSmoothRate.LoadBindingValue();
            FBIKElbowSwingEnabled.LoadBindingValue();
            FBIKSpineCCDRelax.LoadBindingValue();
            FBIKNeckMaxConeDeg.LoadBindingValue();
            FBIKSpineTwistKeep.LoadBindingValue();
            FBIKSpineNeckTwistKeep.LoadBindingValue();
            FBIKChestArmSwingFactor.LoadBindingValue();
            FBIKChestArmSwingMaxDeg.LoadBindingValue();
            FBIKLowerArmTwistFraction.LoadBindingValue();
            FBIKUpperArmTwistFraction.LoadBindingValue();
            FBIKArmHeightRatioEnabled.LoadBindingValue();
            FBIKArmHeightRatio.LoadBindingValue();
            DesktopHeadSwingEnabled.LoadBindingValue();
            DesktopHeadSwingStrength.LoadBindingValue();
            DesktopHeadSwingBackward.LoadBindingValue();
            FBIKAnatDifferentialStiffness.LoadBindingValue();
            FBIKAnatShoulderSlide.LoadBindingValue();
            FBIKAnatCervicalLordosis.LoadBindingValue();
            FBIKAnatPelvicTwistRouting.LoadBindingValue();
            FBIKLegSwivelSmoothing.LoadBindingValue();
            FBIKTrackerBendNormal.LoadBindingValue();
            FBIKLordosisPitchGainDeg.LoadBindingValue();
            FBIKLordosisBaseDeg.LoadBindingValue();
            FBIKLordosisNeckShare.LoadBindingValue();
            FBIKLordosisMaxHeadPitchDeg.LoadBindingValue();
            FBIKLordosisExtremeStartDeg.LoadBindingValue();
            FBIKLordosisExtremeFullDeg.LoadBindingValue();
            FBIKLordosisExtremeRollForwardMaxDeg.LoadBindingValue();
            FBIKLordosisExtremeRollBackwardMaxDeg.LoadBindingValue();
            FBIKLordosisExtremeHipsHorizontalMax.LoadBindingValue();
            FBIKLordosisExtremeChestHorizontalMax.LoadBindingValue();
            FBIKLordosisExtremeHipsHorizontalLookUp.LoadBindingValue();
            FBIKLordosisExtremeChestHorizontalLookUp.LoadBindingValue();
            FBIKLordosisExtremeHipsDownMax.LoadBindingValue();
            FBIKLordosisExtremeChestDownMax.LoadBindingValue();
            FBIKLordosisExtremeHipsDownLookUp.LoadBindingValue();
            FBIKLordosisExtremeChestDownLookUp.LoadBindingValue();
            VSpineChestPitchFrac.LoadBindingValue();
            VSpineChestRollFrac.LoadBindingValue();
            VSpineSpinePitchFrac.LoadBindingValue();
            VSpineSpineRollFrac.LoadBindingValue();
            VSpineNeckRotationSpeed.LoadBindingValue();
            VSpineChestRotationSpeed.LoadBindingValue();
            VSpineSpineRotationSpeed.LoadBindingValue();
            VSpineHipsRotationSpeed.LoadBindingValue();
            VSpineHipsForwardBias.LoadBindingValue();
            VSpineGazeSwingRemoval.LoadBindingValue();
            VSpineNodPivotEstimate.LoadBindingValue();
            VSpineHipsCompressionStrength.LoadBindingValue();
            VSpineHipsMaxDropMeters.LoadBindingValue();
            VSpinePostureModel.LoadBindingValue();
            VSpineTorsoYawDeadzoneDeg.LoadBindingValue();
            VSpineTorsoYawBlendSpeed.LoadBindingValue();
            VSpineTorsoYawPlayInVR.LoadBindingValue();
            VSpineTorsoYawDeadzoneVRDeg.LoadBindingValue();

            // Tracker pairing
            TrackerLinkingAdvancedVisible.LoadBindingValue();
            TrackerLinkingConnectorVisible.LoadBindingValue();
            TrustSteamVRRoles.LoadBindingValue();
            IgnoreHandTrackingDevices.LoadBindingValue();
            PairingSurprisePenalty.LoadBindingValue();
            PairingSurpriseClamp.LoadBindingValue();
            PairingEmaFloor.LoadBindingValue();
            PairingMaxCorrectionStrength.LoadBindingValue();
            PairingSoftSnapHalfLife.LoadBindingValue();
            PairingLockstepTolerance.LoadBindingValue();
            PairingEmaAlpha.LoadBindingValue();
            PairingDistanceEmaAlpha.LoadBindingValue();
            PairingWeightSmoothing.LoadBindingValue();
            PairingRotationHalfLife.LoadBindingValue();
            CalibrationTolerance.LoadBindingValue();

            // Remote Nameplate
            NPEnabled.LoadBindingValue();
            NPMenuOnly.LoadBindingValue();
            NPHoverMenuOnly.LoadBindingValue();
            NPSize.LoadBindingValue();
            NPTransparency.LoadBindingValue();
            ChatSize.LoadBindingValue();

            // Admin
            AdminAutoRefreshPlayerList.LoadBindingValue();
            ShoutShowOnMenuBar.LoadBindingValue();

            // Remote Player Audio
            RAMinDistance.LoadBindingValue();
            RASpread.LoadBindingValue();
            RADopplerLevel.LoadBindingValue();
            RASpatialBlend.LoadBindingValue();
            RADirectBinaural.LoadBindingValue();
            RAPerspectiveCorrection.LoadBindingValue();
            RAInterpolation.LoadBindingValue();
            RAHrtfProfile.LoadBindingValue();
            RADistanceAttenuation.LoadBindingValue();
            RAAirAbsorption.LoadBindingValue();
            RADirectivity.LoadBindingValue();
            RADipoleWeight.LoadBindingValue();
            RADipolePower.LoadBindingValue();
            RAOcclusion.LoadBindingValue();
            RAOcclusionType.LoadBindingValue();
            RAOcclusionRadius.LoadBindingValue();
            RAOcclusionSamples.LoadBindingValue();
            RATransmission.LoadBindingValue();
            RATransmissionType.LoadBindingValue();
            RAMaxTransmissionSurfaces.LoadBindingValue();
            RADirectMixLevel.LoadBindingValue();
            RAListenerConeAngle.LoadBindingValue();
            RAListenerDampenAmount.LoadBindingValue();
            RARolloffMode.LoadBindingValue();
            RARolloffCurvePreset.LoadBindingValue();
            RACurvePoint25.LoadBindingValue();
            RACurvePoint50.LoadBindingValue();
            RACurvePoint75.LoadBindingValue();
            RAPriority.LoadBindingValue();
            RADistanceAttenuationInput.LoadBindingValue();
            RAAirAbsorptionInput.LoadBindingValue();
            RAAirAbsorptionLow.LoadBindingValue();
            RAAirAbsorptionMid.LoadBindingValue();
            RAAirAbsorptionHigh.LoadBindingValue();
            RAReflections.LoadBindingValue();
            RAReflectionsMixLevel.LoadBindingValue();
            RAApplyHRTFToReflections.LoadBindingValue();
            RAJitterBufferDepth.LoadBindingValue();
            RAClipBufferScalar.LoadBindingValue();
            RAVoiceToneShaping.LoadBindingValue();

            // UI Style Palette
            UIPaletteBG1.LoadBindingValue();
            UIPaletteBG2.LoadBindingValue();
            UIPaletteBG3.LoadBindingValue();
            UIPaletteLayer.LoadBindingValue();
            UIPaletteAccent.LoadBindingValue();
            UIPaletteFont1.LoadBindingValue();
            UIPaletteFont2.LoadBindingValue();
            UIPaletteFont3.LoadBindingValue();
            UIPaletteInputField.LoadBindingValue();
            UIPaletteButton.LoadBindingValue();
            UIPaletteWhite.LoadBindingValue();
            UIPaletteClear.LoadBindingValue();
            UIPaletteBlack.LoadBindingValue();
            UIPaletteSuccess.LoadBindingValue();
            UIPaletteCaution.LoadBindingValue();
            UIPaletteDanger.LoadBindingValue();
            UIPaletteScrollbar.LoadBindingValue();
            MenuEdgeWhite.LoadBindingValue();

            // Menu Background
            MenuBGAccentAmount.LoadBindingValue();
            MenuBGAccentFeather.LoadBindingValue();
            MenuBGAccentSoftness.LoadBindingValue();
            MenuBGBrandGradient.LoadBindingValue();
            MenuBGGradientCycle.LoadBindingValue();
            MenuBGAnimationSpeed.LoadBindingValue();
            MenuBGSheen.LoadBindingValue();
            MenuBGCursorGlowColor.LoadBindingValue();
            MenuBGCursorGlow.LoadBindingValue();
            MenuBGCursorGlowRadius.LoadBindingValue();
            MenuBGVignette.LoadBindingValue();
            MenuBGExposure.LoadBindingValue();
            MenuBGGrain.LoadBindingValue();
            MenuBGGrainScale.LoadBindingValue();

            RaycastLineWidth.LoadBindingValue();
            RaycastLineColor.LoadBindingValue();
            HighlightColor.LoadBindingValue();
            PickupLineColor.LoadBindingValue();

            // Holds its binding privately (JSON blob, not a plain primitive) — reload
            // through its own accessor so a pre-load touch can't pin the default blocklist.
            Basis.Scripts.Avatar.BasisContentTagFilter.ReloadBinding();

            // Subscribers that read RawValue (Apply* in OnSettingsFinishedChanges)
            // ran during Initialize before bindings were refreshed from the file —
            // re-notify so they pick up the loaded values.
            BasisSettingsSystem.NotifyFinishedChanges();
        }

        public static void ApplyDebugLogTagFilter(string value)
        {
            if (string.IsNullOrEmpty(value) || value == DebugLogFilterAll)
            {
                BasisDebug.TagFilter = null;
                return;
            }
            if (Enum.TryParse(value, out BasisDebug.LogTag tag))
            {
                BasisDebug.TagFilter = tag;
            }
            else
            {
                BasisDebug.TagFilter = null;
            }
        }

        public static void ApplyDebugLogLevelFilter(string value)
        {
            BasisDebug.MinimumLevel = value switch
            {
                DebugLogLevelErrorsOnly => BasisDebug.MessageType.Error,
                DebugLogLevelWarningsAndErrors => BasisDebug.MessageType.Warning,
                _ => BasisDebug.MessageType.Info,
            };
        }
    }
}
