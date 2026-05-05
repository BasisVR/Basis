using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.Settings;

namespace Basis.BasisUI
{
    public static class BasisSettingsDefaults
    {
        public static BasisSettingsBinding<float> MainVolume = new("main volume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> MenuVolume = new("menuvolume", new BasisPlatformDefault<float>(75));
        public static BasisSettingsBinding<float> MediaVolume = new("mediavolume", new BasisPlatformDefault<float>(75));
        public static BasisSettingsBinding<float> WorldVolume = new("worldvolume", new BasisPlatformDefault<float>(75));

        public static BasisSettingsBinding<float> VoiceVolume = new("voicevolume", new BasisPlatformDefault<float>(75));
        public static BasisSettingsBinding<float> AvatarVolume = new("avatarvolume", new BasisPlatformDefault<float>(75));
        public static BasisSettingsBinding<float> PropVolume = new("propvolume", new BasisPlatformDefault<float>(75));
        public static BasisSettingsBinding<float> MicrophoneVolume = new("microphonevolume", new BasisPlatformDefault<float>(1));

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

        public static BasisSettingsBinding<bool> CustomScale = new("customscale", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FootIKEnabled = new("footik", new BasisPlatformDefault<bool>(false));

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

        public static BasisSettingsBinding<float> AvatarRange = new("avatarrange", new BasisPlatformDefault<float>(25));

        /// <summary>
        /// Maximum number of remote players allowed to show their real avatar at once.
        /// 0 = unlimited (all in-range players show real avatars).
        /// Players beyond this limit fall back to the default avatar.
        /// Closest players get priority; currently-visible avatars are sticky to prevent pulsing.
        /// </summary>
        public static BasisSettingsBinding<float> MaxVisibleAvatars = new("maxvisibleavatars", new BasisPlatformDefault<float>(0));
        public static BasisSettingsBinding<bool> UseMaxVisibleAvatars = new("usemaxvisibleavatars", new BasisPlatformDefault<bool>(false));

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
        public static BasisSettingsBinding<float> OpenLipSyncMaxSlots = new("openlipsyncmaxslots", new BasisPlatformDefault<float>(30));

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
        public static BasisSettingsBinding<float> PoseLOD = new("poselod", new BasisPlatformDefault<float>(0));

        public static BasisSettingsBinding<bool> UseViewConeAvatars = new("useviewconeavatars", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Full cone angle in degrees for view-cone avatar visibility.
        /// 180 = hemisphere in front, 360 = disabled (everything visible).
        /// Default 180 degrees.
        /// </summary>
        public static BasisSettingsBinding<float> ViewConeAngle = new("viewconeangle", new BasisPlatformDefault<float>(180f));

        public static BasisSettingsBinding<float> SnapTurnAngle = new("snapturnangle", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<float> mousesensitivty = new("mousesensitivty", new BasisPlatformDefault<float>(1));

        public static BasisSettingsBinding<bool> InvertMouse = new("invertmouse", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Dominant hand preference. "right" or "left". Affects placement raycast and pickup priority.
        /// </summary>
        public static BasisSettingsBinding<string> DominantHand = new("dominanthand", new BasisPlatformDefault<string>("right"));

        public static BasisSettingsBinding<bool> usesnapturn = new("usesnapturn", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<float> SmoothTurnSpeed = new("smoothturnspeed", new BasisPlatformDefault<float>(200f));

        public static BasisSettingsBinding<string> QualityLevel = new("qualitylevel", new BasisPlatformDefault<string>
        {
            windows = "Ultra",
            android = "Very Low",
            linux = "Ultra",
            other = "Ultra"
        });

        public static BasisSettingsBinding<string> ShadowQuality = new("shadowquality", new BasisPlatformDefault<string>
        {
            windows = "Ultra",
            android = "Very Low",
            linux = "Ultra",
            other = "Ultra"
        });

        public static BasisSettingsBinding<string> HDRSupport = new("hdrsupport", new BasisPlatformDefault<string>
        {
            windows = "64bit",
            android = "Off",
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

        public static BasisSettingsBinding<bool> MicrophoneDenoiser = new("voicedenoiser", new BasisPlatformDefault<bool>
        {
            windows = true,
            android = false,
            linux = false,
            other = false
        });

        public static BasisSettingsBinding<string> Antialiasing = new("antialiasing", new BasisPlatformDefault<string>("msaa 2x"));

        // Master gizmo gate. When off, every gizmo sub-toggle below is inert and
        // BasisGizmoManager tears down its parent + cached gizmo dictionaries.
        public static BasisSettingsBinding<bool> ShowGizmos = new("showgizmos", new BasisPlatformDefault<bool>(false));

        // Sub-gizmos default on so flipping ShowGizmos preserves the pre-split
        // experience (skeleton lines + calibration spheres + jiggle render all visible).
        public static BasisSettingsBinding<bool> GizmoSkeletonLines = new("gizmoskeletonlines", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> GizmoCalibrationSpheres = new("gizmocalibrationspheres", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> GizmoJiggleVisuals = new("gizmojigglevisuals", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<bool> TrackerGizmos = new("trackergizmos", new BasisPlatformDefault<bool>(false));

        // Yellow line gizmo drawn between the two physical trackers of every
        // active linked pair. Off by default; toggled separately from
        // TrackerGizmos so a user debugging the pairing system can see only
        // the link visualization without the tracker spheres cluttering the view.
        public static BasisSettingsBinding<bool> LinkedTrackerLines = new("linkedtrackerlines", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> EnableStatistics = new("enablestatistics", new BasisPlatformDefault<bool>(false));

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

        public static BasisSettingsBinding<bool> AvatarShowTextureStats = new("avatarshowtexturestats", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> AvatarShowTrackerRoles = new("avatarshowtrackerroles", new BasisPlatformDefault<bool>(false));

        // Debug toggles for the avatar diagnostics on the Developer tab. Separate
        // from EnableFaceTracking / EnableEyeTracking — those drive the actual
        // avatar; these only gate the visibility of the diagnostic panels.
        public static BasisSettingsBinding<bool> DevDebugFaceTracking = new("devdebugfacetracking", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> DevDebugEyeTracking = new("devdebugeyetracking", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> DevShowBuildInfo = new("devshowbuildinfo", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> DevShowConsole = new("devshowconsole", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> DevShowEuroFilter = new("devshowfilter", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> DevShowNetStats = new("devshownetstats", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// When enabled, suppresses all <see cref="BasisDebug"/> log output (Log, LogWarning, LogError).
        /// Raw <see cref="UnityEngine.Debug"/> calls are unaffected.
        /// </summary>
        public static BasisSettingsBinding<bool> DisableLogging = new("disablelogging", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> AudioDebugEnabled = new("audiodebugenabled", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> AudioDebugShowSource = new("audiodebugshowsource", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AudioDebugShowVolume = new("audiodebugshowvolume", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AudioDebugShowRingBuffer = new("audiodebugshowringbuffer", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AudioDebugShowJitter = new("audiodebugshowjitter", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AudioDebugShowSilence = new("audiodebugshowsilence", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> AudioDebugShowViseme = new("audiodebugshowviseme", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<string> MemoryAllocation = new("memoryallocation", new BasisPlatformDefault<string>
        {
            windows = "Dynamic",
            android = "Dnamic",
            linux = "Dynamic",
            other = "Dynamic"
        });

        public static BasisSettingsBinding<bool> AvatarPreview = new("avatarpreview", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> DesktopReticle = new("desktopreticle", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> EnableThirdPersonCamera = new("enablethirdpersoncamera", new BasisPlatformDefault<bool>(true));

        // True = listener stays at the player's head while third-person is active.
        // False = listener follows the orbital camera (audio shifts behind the player on zoom).
        // Only takes effect when the camera is currently in third-person mode.
        public static BasisSettingsBinding<bool> AudioListenerFollowsHead = new("audiolistenerfollowshead", new BasisPlatformDefault<bool>(true));

        public static BasisSettingsBinding<string> MicrophoneIcon = new("microphoneicon", new BasisPlatformDefault<string>("alwaysvisible"));

        public static BasisSettingsBinding<float> MicrophoneIconOffsetX = new("microphoneiconoffsetx", new BasisPlatformDefault<float>(0f));
        public static BasisSettingsBinding<float> MicrophoneIconOffsetY = new("microphoneiconoffsety", new BasisPlatformDefault<float>(0f));

        public static BasisSettingsBinding<string> VisualState = new("visualstate", new BasisPlatformDefault<string>("off"));

        public static BasisSettingsBinding<string> IKMode = new("ikmode", new BasisPlatformDefault<string>("eye height"));

        public static BasisSettingsBinding<string> IKLockMode = new("iklockmode", new BasisPlatformDefault<string>("lock hips"));

        public static BasisSettingsBinding<bool> PitchCalibration = new("pitchcalibration", new BasisPlatformDefault<bool>(false));

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

        public static BasisSettingsBinding<float> AvatarDownloadSize = new("avatardownloadsize", new BasisPlatformDefault<float>(256));

        public static BasisSettingsBinding<float> CacheMaxSizeGB = new("cachemaxsizegb", new BasisPlatformDefault<float>(128));

        /// <summary>
        /// Maximum number of avatar asset bundles that can be downloaded from the network
        /// concurrently. Downloads are bandwidth-bound: a small value keeps each transfer
        /// at full speed, while a large value splits bandwidth and makes every player wait
        /// longer on the loading avatar. Tune higher only if you have lots of bandwidth and
        /// the server is fast.
        /// </summary>
        public static BasisSettingsBinding<float> MaxConcurrentAvatarDownloads = new("maxconcurrentavatardownloads", new BasisPlatformDefault<float>(5));

        /// <summary>
        /// Maximum number of cached avatar asset bundles that can be loaded from disc at
        /// once. Disc loads are I/O + decryption + bundle-decompression bound. This can be
        /// higher than the download gate because no network is involved.
        /// </summary>
        public static BasisSettingsBinding<float> MaxConcurrentAvatarDiscLoads = new("maxconcurrentavatardiscloads", new BasisPlatformDefault<float>(15));

        /// <summary>
        /// Maximum number of addressable (in-build) avatars that can be instantiated
        /// concurrently. Addressable loads are CPU-bound and typically very fast, so this
        /// gate can be the largest of the three.
        /// </summary>
        public static BasisSettingsBinding<float> MaxConcurrentAvatarAddressables = new("maxconcurrentavataraddressables", new BasisPlatformDefault<float>(25));

        // ---------------- AVATAR PERFORMANCE LIMITS ----------------
        // Client-side safety net that inspects the pre-download metadata header on each
        // remote avatar bundle and swaps the avatar for the fallback when any enabled
        // limit is exceeded. Every limit ships as an opt-in pair: a Use* bool gate plus
        // a Max* threshold. All Use* flags default to false so out-of-the-box behavior
        // on modern hardware is unchanged — this only kicks in when the user opts in.
        // Changing any value at runtime re-evaluates every currently-loaded remote
        // avatar (see SMModuleAvatarPerformanceLimits).

        public static BasisSettingsBinding<bool> UsePerfLimitTriangles = new("useperflimittriangles", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfTriangles = new("maxperftriangles", new BasisPlatformDefault<float>(2000000));

        public static BasisSettingsBinding<bool> UsePerfLimitBoundsSize = new("useperflimitboundssize", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfBoundsSize = new("maxperfboundssize", new BasisPlatformDefault<float>(50f));

        // Texture memory defaults on — 512 MB is generous for a single avatar but
        // catches the 2–4 GB outliers that trip out-of-memory on lower-end hardware.
        public static BasisSettingsBinding<bool> UsePerfLimitTextureMemory = new("useperflimittexturememory", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfTextureMemoryMB = new("maxperftexturememorymb", new BasisPlatformDefault<float>(512));

        public static BasisSettingsBinding<bool> UsePerfLimitSkinnedMeshes = new("useperflimitskinnedmeshes", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfSkinnedMeshes = new("maxperfskinnedmeshes", new BasisPlatformDefault<float>(64));

        public static BasisSettingsBinding<bool> UsePerfLimitBasicMeshes = new("useperflimitbasicmeshes", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfBasicMeshes = new("maxperfbasicmeshes", new BasisPlatformDefault<float>(128));

        public static BasisSettingsBinding<bool> UsePerfLimitMaterialSlots = new("useperflimitmaterialslots", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfMaterialSlots = new("maxperfmaterialslots", new BasisPlatformDefault<float>(256));

        public static BasisSettingsBinding<bool> UsePerfLimitJiggleBones = new("useperflimitjigglebones", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfJiggleBones = new("maxperfjigglebones", new BasisPlatformDefault<float>(128));

        public static BasisSettingsBinding<bool> UsePerfLimitJiggleColliders = new("useperflimitjigglecolliders", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfJiggleColliders = new("maxperfjigglecolliders", new BasisPlatformDefault<float>(64));

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

        // Particles default on at 1 — one ambient system is fine, more is a hand
        // grenade in a crowd. Trimmed, not blocked.
        public static BasisSettingsBinding<bool> UsePerfLimitParticleSystems = new("useperflimitparticlesystems", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfParticleSystems = new("maxperfparticlesystems", new BasisPlatformDefault<float>(1));

        // Trails default on at 1.
        public static BasisSettingsBinding<bool> UsePerfLimitTrailRenderers = new("useperflimittrailrenderers", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfTrailRenderers = new("maxperftrailrenderers", new BasisPlatformDefault<float>(1));

        // Line renderers default on at 1.
        public static BasisSettingsBinding<bool> UsePerfLimitLineRenderers = new("useperflimitlinerenderers", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfLineRenderers = new("maxperflinerenderers", new BasisPlatformDefault<float>(1));

        // Cloth defaults on at 1 — Unity Cloth is CPU-expensive per instance.
        public static BasisSettingsBinding<bool> UsePerfLimitCloth = new("useperflimitcloth", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfCloth = new("maxperfcloth", new BasisPlatformDefault<float>(1));

        // Unity colliders default on at 1 — physics colliders on an avatar
        // aren't free. Jiggle colliders are a separate limit.
        public static BasisSettingsBinding<bool> UsePerfLimitColliders = new("useperflimitcolliders", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfColliders = new("maxperfcolliders", new BasisPlatformDefault<float>(1));

        // Cilbox script behaviours default on at 5 — every CilboxProxy on a remote
        // avatar is one sandboxed MonoBehaviour with its own Update/FixedUpdate tick.
        public static BasisSettingsBinding<bool> UsePerfLimitCilboxBehaviours = new("useperflimitcilboxbehaviours", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> MaxPerfCilboxBehaviours = new("maxperfcilboxbehaviours", new BasisPlatformDefault<float>(5));

        public static BasisSettingsBinding<float> AvatarMeshLOD = new("avatarmeshlod", new BasisPlatformDefault<float>
        {
            windows = 0.05f,
            android = 0.1f,
            linux = 0.05f,
            other = 0.05f
        });

        public static BasisSettingsBinding<float> GlobalMeshLOD = new("globalmeshlod", new BasisPlatformDefault<float>
        {
            windows = 0,
            android = 30,
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
            linux = "Capped",
            other = "On"
        });

        public static BasisSettingsBinding<float> RenderResolution = new("render resolution", new BasisPlatformDefault<float>(1));

        public static BasisSettingsBinding<string> MicrophoneMode = new("microphonemode", new BasisPlatformDefault<string>("onactivation"));

        public static BasisSettingsBinding<string> MicStartBehavior = new("micstartbehavior", new BasisPlatformDefault<string>(BasisLocalMicrophoneDriver.SettingStartOff));

        public static BasisSettingsBinding<string> MicMuteBehavior = new("micmutebehavior", new BasisPlatformDefault<string>(BasisLocalMicrophoneDriver.SettingMuteShutdown));

        public static BasisSettingsBinding<bool> UseAutomaticGain = new("automaticgainenabled", new BasisPlatformDefault<bool>
        {
            windows = true,
            android = true,
            linux = true,
            other = true
        });

        // ---------------- NETWORKING ----------------
        public static BasisSettingsBinding<bool> AutoConnect = new("autoconnect", new BasisPlatformDefault<bool>(false));

        // Network Euro filter parameters (remote player interpolation)
        public static BasisSettingsBinding<float> NetEuroMinCutoff = new("neteuromincutoff", new BasisPlatformDefault<float>(0.05f));
        public static BasisSettingsBinding<float> NetEuroBeta = new("neteurobeta", new BasisPlatformDefault<float>(2f));
        public static BasisSettingsBinding<float> NetEuroDerivativeCutoff = new("neteuroderivativecutoff", new BasisPlatformDefault<float>(2f));

        // ---------------- DEVICE SWAP MODE ----------------
        /// <summary>
        /// Controls how the system handles switching between VR and Desktop modes.
        /// "Shutdown Runtime" — full XR shutdown on swap.
        /// "Auto Swap" — automatically swaps based on headset presence, keeping XR alive. (default).
        /// </summary>
        public static BasisSettingsBinding<string> SwapMode = new("swap_mode", new BasisPlatformDefault<string>("Auto Swap"));

        public const string SwapMode_Shutdown = "Shutdown Runtime";
        public const string SwapMode_AutoSwap = "Auto Swap";

        // ---------------- INTERACTIONS ----------------
        public static BasisSettingsBinding<bool> DisableSeats = new("disableseats", new BasisPlatformDefault<bool>(false));

        // ---------------- NOTIFICATIONS ----------------
        public static BasisSettingsBinding<bool> JoinNotifications = new("joinnotifications", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> LeaveNotifications = new("leavenotifications", new BasisPlatformDefault<bool>(false));

        // ---------------- CHAT ----------------
        /// <summary>
        /// Global kill switch for text chat. When true, incoming chat is dropped before
        /// being applied to nameplates and local sends are short-circuited.
        /// </summary>
        public static BasisSettingsBinding<bool> ChatDisabled = new("chatdisabled", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> FalseBinding = new("falsebinding", new BasisPlatformDefault<bool>(false));

        public static BasisSettingsBinding<bool> TrueBinding = new("truebinding", new BasisPlatformDefault<bool>(false));

        // ---------------- GLOBAL ONE EURO PARAMS ----------------
        public static BasisSettingsBinding<float> FBIKMinCutoff = new("fbikmincutoff", new BasisPlatformDefault<float>(5.5f));

        public static BasisSettingsBinding<float> FBIKBeta = new("fbikbeta", new BasisPlatformDefault<float>(3.25f));

        public static BasisSettingsBinding<float> FBIKDerivativeCutoff = new("fbikderivativecutoff", new BasisPlatformDefault<float>(3f));

        public static BasisSettingsBinding<float> FBIKPositionSmoothingHz =
            new("fbikpositionsmoothinghz", new BasisPlatformDefault<float>(20f));

        public static BasisSettingsBinding<float> FBIKRotationSmoothingHz =
            new("fbikrotationsmoothinghz", new BasisPlatformDefault<float>(25f));

        public static BasisSettingsBinding<float> FBIKSmoothingStrength =
            new("fbiksmoothingstrength", new BasisPlatformDefault<float>(1f));

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
            linux = "120",
            other = "120"
        });

        // ---------------- REMOTE PLAYER AUDIO ----------------
        // AudioSource
        public static BasisSettingsBinding<float> RAMinDistance = new("ra_mindistance", new BasisPlatformDefault<float>(0.5f));
        public static BasisSettingsBinding<float> RASpread = new("ra_spread", new BasisPlatformDefault<float>(70f));
        public static BasisSettingsBinding<float> RADopplerLevel = new("ra_dopplerlevel", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> RASpatialBlend = new("ra_spatialblend", new BasisPlatformDefault<float>(1f));

        // Steam Audio - HRTF
        public static BasisSettingsBinding<bool> RADirectBinaural = new("ra_directbinaural", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> RAPerspectiveCorrection = new("ra_perspectivecorrection", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<string> RAInterpolation = new("ra_interpolation", new BasisPlatformDefault<string>("nearest"));

        // Steam Audio - Propagation
        public static BasisSettingsBinding<bool> RADistanceAttenuation = new("ra_distanceattenuation", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<bool> RAAirAbsorption = new("ra_airabsorption", new BasisPlatformDefault<bool>(true));

        // Steam Audio - Directivity
        public static BasisSettingsBinding<bool> RADirectivity = new("ra_directivity", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> RADipoleWeight = new("ra_dipoleweight", new BasisPlatformDefault<float>(0.25f));
        public static BasisSettingsBinding<float> RADipolePower = new("ra_dipolepower", new BasisPlatformDefault<float>(1f));

        // Steam Audio - Occlusion
        public static BasisSettingsBinding<bool> RAOcclusion = new("ra_occlusion", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<string> RAOcclusionType = new("ra_occlusiontype", new BasisPlatformDefault<string>("volumetric"));
        public static BasisSettingsBinding<float> RAOcclusionRadius = new("ra_occlusionradius", new BasisPlatformDefault<float>(0.15f));
        public static BasisSettingsBinding<float> RAOcclusionSamples = new("ra_occlusionsamples", new BasisPlatformDefault<float>(16f));

        // Steam Audio - Transmission
        public static BasisSettingsBinding<bool> RATransmission = new("ra_transmission", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<string> RATransmissionType = new("ra_transmissiontype", new BasisPlatformDefault<string>("frequency dependent"));
        public static BasisSettingsBinding<float> RAMaxTransmissionSurfaces = new("ra_maxtransmissionsurfaces", new BasisPlatformDefault<float>(4f));

        // AudioSource - Rolloff
        public static BasisSettingsBinding<string> RARolloffMode = new("ra_rolloffmode", new BasisPlatformDefault<string>("custom"));
        public static BasisSettingsBinding<string> RARolloffCurvePreset = new("ra_rolloffcurvepreset", new BasisPlatformDefault<string>("default"));
        public static BasisSettingsBinding<float> RACurvePoint25 = new("ra_curvepoint25", new BasisPlatformDefault<float>(0.6f));
        public static BasisSettingsBinding<float> RACurvePoint50 = new("ra_curvepoint50", new BasisPlatformDefault<float>(0.3f));
        public static BasisSettingsBinding<float> RACurvePoint75 = new("ra_curvepoint75", new BasisPlatformDefault<float>(0.1f));
        public static BasisSettingsBinding<float> RAPriority = new("ra_priority", new BasisPlatformDefault<float>(128f));

        // Listener Directional Dampening
        public static BasisSettingsBinding<float> RAListenerConeAngle = new("ra_listenerconeangle", new BasisPlatformDefault<float>(150f));
        public static BasisSettingsBinding<float> RAListenerDampenAmount = new("ra_listenerdampenamount", new BasisPlatformDefault<float>(75f));

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
        public static BasisSettingsBinding<bool> FBIKUseHandCapsule = new("fbikusehandcapsule", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> FBIKChestRadius = new("fbikchestradius", new BasisPlatformDefault<float>(0.18f));
        public static BasisSettingsBinding<float> FBIKCollisionSkin = new("fbikcollisionskin", new BasisPlatformDefault<float>(0.02f));
        public static BasisSettingsBinding<float> FBIKHandRadius = new("fbikhandradius", new BasisPlatformDefault<float>(0.05f));
        public static BasisSettingsBinding<float> FBIKHandSkin = new("fbikhandskin", new BasisPlatformDefault<float>(0.01f));
        public static BasisSettingsBinding<bool> FBIKShoulderSolveEnabled = new("fbikshouldersolveenabled", new BasisPlatformDefault<bool>(true));
        public static BasisSettingsBinding<float> FBIKShoulderElevation = new("fbikshoulderelevation", new BasisPlatformDefault<float>(0.4f));
        public static BasisSettingsBinding<float> FBIKShoulderProtraction = new("fbikshoulderprotraction", new BasisPlatformDefault<float>(0.3f));
        public static BasisSettingsBinding<float> FBIKMaxBendDeg = new("fbikmaxbenddeg", new BasisPlatformDefault<float>(90f));
        public static BasisSettingsBinding<float> FBIKStruggleStart = new("fbikstrugglestart", new BasisPlatformDefault<float>(0.9f));
        public static BasisSettingsBinding<float> FBIKStruggleEnd = new("fbikstruggleend", new BasisPlatformDefault<float>(1f));
        public static BasisSettingsBinding<float> FBIKMaxChestDelta = new("fbikmaxchestdelta", new BasisPlatformDefault<float>(90f));
        public static BasisSettingsBinding<float> FBIKMaxHipDelta = new("fbikmaxhipdelta", new BasisPlatformDefault<float>(90f));

        // Spine relax: per-axis bend distribution onto lumbar (spine) and thoracic (upperChest)
        public static BasisSettingsBinding<float> FBIKSpineBendPitch = new("fbikspinebendpitch", new BasisPlatformDefault<float>(0.45f));
        public static BasisSettingsBinding<float> FBIKSpineBendYaw = new("fbikspinebendyaw", new BasisPlatformDefault<float>(0.10f));
        public static BasisSettingsBinding<float> FBIKSpineBendRoll = new("fbikspinebendroll", new BasisPlatformDefault<float>(0.35f));
        public static BasisSettingsBinding<float> FBIKUpperChestBendPitch = new("fbikupperchestbendpitch", new BasisPlatformDefault<float>(0.25f));
        public static BasisSettingsBinding<float> FBIKUpperChestBendYaw = new("fbikupperchestbendyaw", new BasisPlatformDefault<float>(0.30f));
        public static BasisSettingsBinding<float> FBIKUpperChestBendRoll = new("fbikupperchestbendroll", new BasisPlatformDefault<float>(0.20f));
        // Spine relax: hip hinge coupling
        public static BasisSettingsBinding<float> FBIKHipHingeStartDeg = new("fbikhiphingestartdeg", new BasisPlatformDefault<float>(30f));
        public static BasisSettingsBinding<float> FBIKHipHingeMaxAddDeg = new("fbikhiphingemaxadddeg", new BasisPlatformDefault<float>(15f));
        // Spine relax: chest follow spring (velocity lag)
        public static BasisSettingsBinding<float> FBIKChestSpringHz = new("fbikchestspringhz", new BasisPlatformDefault<float>(12f));
        public static BasisSettingsBinding<float> FBIKChestSpringDamping = new("fbikchestspringdamping", new BasisPlatformDefault<float>(1f));
        // Spine relax: asymmetric flexion clamps (apply to spine + upperChest contributions)
        public static BasisSettingsBinding<float> FBIKSpineMaxForwardDeg = new("fbikspinemaxforwarddeg", new BasisPlatformDefault<float>(60f));
        public static BasisSettingsBinding<float> FBIKSpineMaxBackwardDeg = new("fbikspinemaxbackwarddeg", new BasisPlatformDefault<float>(25f));
        public static BasisSettingsBinding<float> FBIKSpineMaxLateralDeg = new("fbikspinemaxlateraldeg", new BasisPlatformDefault<float>(25f));
        // Spine relax: squish-driven bend coupling
        public static BasisSettingsBinding<float> FBIKSpineSquishBoost = new("fbikspinesquishboost", new BasisPlatformDefault<float>(0.5f));
        // Spine relax: arm-swing chest follow (only when no chest tracker)
        public static BasisSettingsBinding<float> FBIKChestArmSwingFactor = new("fbikchestarmswingfactor", new BasisPlatformDefault<float>(0.3f));
        public static BasisSettingsBinding<float> FBIKChestArmSwingMaxDeg = new("fbikchestarmswingmaxdeg", new BasisPlatformDefault<float>(15f));
        // Arm twist distribution: fraction of wrist/elbow roll absorbed by twist bones
        public static BasisSettingsBinding<float> FBIKLowerArmTwistFraction = new("fbiklowerarmtwistfraction", new BasisPlatformDefault<float>(0.5f));
        public static BasisSettingsBinding<float> FBIKUpperArmTwistFraction = new("fbikupperarmtwistfraction", new BasisPlatformDefault<float>(0.3f));

        // Anatomy (Experimental) — opt-in IK refinements modeled on real biomechanics. All off
        // by default because they change spine / shoulder / foot behavior in subtle ways that
        // some users may prefer to tune manually via the existing per-axis sliders.
        public static BasisSettingsBinding<bool> FBIKAnatDifferentialStiffness = new("fbikanatdiffstiffness", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> FBIKAnatShoulderSlide = new("fbikanatshoulderslide", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> FBIKAnatCervicalLordosis = new("fbikanatcervicallordosis", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<bool> FBIKAnatPelvicTwistRouting = new("fbikanatpelvictwistrouting", new BasisPlatformDefault<bool>(false));


        // ---------------- TRACKER PAIRING (virtual midpoint) ----------------
        // Hides the pairing tuning sliders behind an advanced toggle so the
        // tracker linking page stays approachable for the common case.
        public static BasisSettingsBinding<bool> TrackerLinkingAdvancedVisible = new("trackerlinking_advancedvisible", new BasisPlatformDefault<bool>(false));
        // Hides the per-tracker connector list (linking + role-override
        // dropdowns) until the user opts in. The page is mostly useful once
        // for setup; routine open/close shouldn't have to scroll past the
        // full device list.
        public static BasisSettingsBinding<bool> TrackerLinkingConnectorVisible = new("trackerlinking_connectorvisible", new BasisPlatformDefault<bool>(false));
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

        // ---------------- ADMIN ----------------
        public static BasisSettingsBinding<bool> AdminAutoRefreshPlayerList = new("admin_autorefresh_playerlist", new BasisPlatformDefault<bool>(true));

        // Limiter
        public static BasisSettingsBinding<float> LimitThreshold = new("limitthreshold", new BasisPlatformDefault<float>(0.95f)); // pre-clip

        public static BasisSettingsBinding<float> LimitKnee = new("limitknee", new BasisPlatformDefault<float>(0.05f)); // soft knee width

        // Denoise extra params (post gain + wet/dry)
        public static BasisSettingsBinding<float> DenoiseMakeupDb = new("denoisemakeupdb", new BasisPlatformDefault<float>(3f));

        public static BasisSettingsBinding<float> DenoiseWet = new("denoisewet", new BasisPlatformDefault<float>(1f)); // 0..1


        public static BasisSettingsBinding<float> AgcTargetRms = new("agctargetrms", new BasisPlatformDefault<float>(0.1f)); // ~ -24 dBFS

        public static BasisSettingsBinding<float> AgcMaxGainDb = new("agcdbgainmax", new BasisPlatformDefault<float>(8f));

        public static BasisSettingsBinding<float> AgcAttack = new("agcattack", new BasisPlatformDefault<float>(0.10f)); // 0..1

        public static BasisSettingsBinding<float> AgcRelease = new("agcrelease", new BasisPlatformDefault<float>(0.01f)); // 0..1

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

        // ---------------- MIRROR ----------------
        public static BasisSettingsBinding<bool> UseMirrorQualityOverride = new("usemirrorqualityoverride", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<string> MirrorQuality = new("mirrorquality", new BasisPlatformDefault<string>("2048"));

        // ---------------- CAMERA CLIP OVERRIDE ----------------
        public static BasisSettingsBinding<bool> UseCameraClipOverride = new("usecameraclipoverride", new BasisPlatformDefault<bool>(false));
        public static BasisSettingsBinding<float> CameraClipNear = new("cameraclipnear", new BasisPlatformDefault<float>(0.01f));
        public static BasisSettingsBinding<float> CameraClipFar = new("cameraclipfar", new BasisPlatformDefault<float>(1000f));

        // Noise Gate
        public static BasisSettingsBinding<bool> UseNoiseGate = new("usenoisegate", new BasisPlatformDefault<bool>(false));
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
            MediaVolume.LoadBindingValue();

            MicrophoneVolume.LoadBindingValue();
            MicrophoneRange.LoadBindingValue();
            HearingRange.LoadBindingValue();
            MicrophoneDenoiser.LoadBindingValue();
            MicrophoneMode.LoadBindingValue();
            MicStartBehavior.LoadBindingValue();
            MicMuteBehavior.LoadBindingValue();
            UseAutomaticGain.LoadBindingValue();
            DenoiseMakeupDb.LoadBindingValue();
            DenoiseWet.LoadBindingValue();
            AgcTargetRms.LoadBindingValue();
            AgcMaxGainDb.LoadBindingValue();
            AgcAttack.LoadBindingValue();
            AgcRelease.LoadBindingValue();
            UseNoiseGate.LoadBindingValue();
            NoiseGateThreshold.LoadBindingValue();
            NoiseGateAttack.LoadBindingValue();
            NoiseGateRelease.LoadBindingValue();

            // Audio Debug
            AudioDebugEnabled.LoadBindingValue();
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

            // Avatar / IK / Body
            SelectedHeight.LoadBindingValue();
            SelectedScale.LoadBindingValue();
            realworldeyeheight.LoadBindingValue();
            CustomScale.LoadBindingValue();
            AvatarRange.LoadBindingValue();
            UseMaxVisibleAvatars.LoadBindingValue();
            MaxVisibleAvatars.LoadBindingValue();
            UseMaxAudioSources.LoadBindingValue();
            MaxAudioSources.LoadBindingValue();
            UseOpenLipSyncLimit.LoadBindingValue();
            OpenLipSyncMaxSlots.LoadBindingValue();
            PoseLOD.LoadBindingValue();
            UseViewConeAvatars.LoadBindingValue();
            ViewConeAngle.LoadBindingValue();
            SelectedBone.LoadBindingValue();
            IKMode.LoadBindingValue();
            IKLockMode.LoadBindingValue();
            PitchCalibration.LoadBindingValue();
            SitStand.LoadBindingValue();
            EnableFBT.LoadBindingValue();
            EnableOSC.LoadBindingValue();
            EnableFaceTracking.LoadBindingValue();
            EnableEyeTracking.LoadBindingValue();
            FootIKEnabled.LoadBindingValue();
            DisableAnimationsInFBT.LoadBindingValue();
            LocalHeadBlendShapes.LoadBindingValue();

            // Rendering / Graphics
            QualityLevel.LoadBindingValue();
            ShadowQuality.LoadBindingValue();
            HDRSupport.LoadBindingValue();
            Antialiasing.LoadBindingValue();
            UseBloomOverride.LoadBindingValue();
            BloomIntensity.LoadBindingValue();
            ShowGizmos.LoadBindingValue();
            GizmoSkeletonLines.LoadBindingValue();
            GizmoCalibrationSpheres.LoadBindingValue();
            GizmoJiggleVisuals.LoadBindingValue();
            TrackerGizmos.LoadBindingValue();
            LinkedTrackerLines.LoadBindingValue();
            AvatarShowTrackerRoles.LoadBindingValue();
            AvatarShowTextureStats.LoadBindingValue();
            EnableStatistics.LoadBindingValue();
            DevDebugFaceTracking.LoadBindingValue();
            DevDebugEyeTracking.LoadBindingValue();
            DevShowBuildInfo.LoadBindingValue();
            DevShowConsole.LoadBindingValue();
            DevShowEuroFilter.LoadBindingValue();
            DevShowNetStats.LoadBindingValue();
            DisableLogging.LoadBindingValue();
            BasisDebug.LoggingDisabled = DisableLogging.RawValue;
            DisableLogging.OnChanged += value => BasisDebug.LoggingDisabled = value;
            EnableStreamingMeta.LoadBindingValue();
            StreamingMetaPort.LoadBindingValue();
            MemoryAllocation.LoadBindingValue();
            VisualState.LoadBindingValue();
            FoveatedRendering.LoadBindingValue();
            FieldOfView.LoadBindingValue();
            RenderResolution.LoadBindingValue();
            VSync.LoadBindingValue();
            VSyncCapFps.LoadBindingValue();

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
            NetEuroMinCutoff.LoadBindingValue();
            NetEuroBeta.LoadBindingValue();
            NetEuroDerivativeCutoff.LoadBindingValue();

            // Device Swap Mode
            SwapMode.LoadBindingValue();

            // Notifications
            JoinNotifications.LoadBindingValue();
            LeaveNotifications.LoadBindingValue();

            // Chat
            ChatDisabled.LoadBindingValue();

            // UI
            AvatarPreview.LoadBindingValue();
            DesktopReticle.LoadBindingValue();
            EnableThirdPersonCamera.LoadBindingValue();
            AudioListenerFollowsHead.LoadBindingValue();
            MicrophoneIcon.LoadBindingValue();
            MicrophoneIconOffsetX.LoadBindingValue();
            MicrophoneIconOffsetY.LoadBindingValue();

            // Misc
            FalseBinding.LoadBindingValue();
            TrueBinding.LoadBindingValue();
            LimitThreshold.LoadBindingValue();
            LimitKnee.LoadBindingValue();
            DisableSeats.LoadBindingValue();

            // Global FBIK parameters
            FBIKMinCutoff.LoadBindingValue();
            FBIKBeta.LoadBindingValue();
            FBIKDerivativeCutoff.LoadBindingValue();
            FBIKPositionSmoothingHz.LoadBindingValue();
            FBIKRotationSmoothingHz.LoadBindingValue();
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
            FBIKUseHandCapsule.LoadBindingValue();
            FBIKChestRadius.LoadBindingValue();
            FBIKCollisionSkin.LoadBindingValue();
            FBIKHandRadius.LoadBindingValue();
            FBIKHandSkin.LoadBindingValue();
            FBIKShoulderSolveEnabled.LoadBindingValue();
            FBIKShoulderElevation.LoadBindingValue();
            FBIKShoulderProtraction.LoadBindingValue();
            FBIKMaxBendDeg.LoadBindingValue();
            FBIKStruggleStart.LoadBindingValue();
            FBIKStruggleEnd.LoadBindingValue();
            FBIKMaxChestDelta.LoadBindingValue();
            FBIKMaxHipDelta.LoadBindingValue();
            FBIKSpineBendPitch.LoadBindingValue();
            FBIKSpineBendYaw.LoadBindingValue();
            FBIKSpineBendRoll.LoadBindingValue();
            FBIKUpperChestBendPitch.LoadBindingValue();
            FBIKUpperChestBendYaw.LoadBindingValue();
            FBIKUpperChestBendRoll.LoadBindingValue();
            FBIKHipHingeStartDeg.LoadBindingValue();
            FBIKHipHingeMaxAddDeg.LoadBindingValue();
            FBIKChestSpringHz.LoadBindingValue();
            FBIKChestSpringDamping.LoadBindingValue();
            FBIKSpineMaxForwardDeg.LoadBindingValue();
            FBIKSpineMaxBackwardDeg.LoadBindingValue();
            FBIKSpineMaxLateralDeg.LoadBindingValue();
            FBIKSpineSquishBoost.LoadBindingValue();
            FBIKChestArmSwingFactor.LoadBindingValue();
            FBIKChestArmSwingMaxDeg.LoadBindingValue();
            FBIKLowerArmTwistFraction.LoadBindingValue();
            FBIKUpperArmTwistFraction.LoadBindingValue();
            FBIKAnatDifferentialStiffness.LoadBindingValue();
            FBIKAnatShoulderSlide.LoadBindingValue();
            FBIKAnatCervicalLordosis.LoadBindingValue();
            FBIKAnatPelvicTwistRouting.LoadBindingValue();

            // Tracker pairing
            TrackerLinkingAdvancedVisible.LoadBindingValue();
            TrackerLinkingConnectorVisible.LoadBindingValue();
            PairingSurprisePenalty.LoadBindingValue();
            PairingSurpriseClamp.LoadBindingValue();
            PairingEmaFloor.LoadBindingValue();
            PairingMaxCorrectionStrength.LoadBindingValue();
            PairingSoftSnapHalfLife.LoadBindingValue();
            PairingLockstepTolerance.LoadBindingValue();
            PairingEmaAlpha.LoadBindingValue();
            PairingDistanceEmaAlpha.LoadBindingValue();
            PairingWeightSmoothing.LoadBindingValue();

            // Remote Nameplate
            NPEnabled.LoadBindingValue();
            NPMenuOnly.LoadBindingValue();
            NPHoverMenuOnly.LoadBindingValue();
            NPSize.LoadBindingValue();
            NPTransparency.LoadBindingValue();

            // Admin
            AdminAutoRefreshPlayerList.LoadBindingValue();

            // Remote Player Audio
            RAMinDistance.LoadBindingValue();
            RASpread.LoadBindingValue();
            RADopplerLevel.LoadBindingValue();
            RASpatialBlend.LoadBindingValue();
            RADirectBinaural.LoadBindingValue();
            RAPerspectiveCorrection.LoadBindingValue();
            RAInterpolation.LoadBindingValue();
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

            // Subscribers that read RawValue (Apply* in OnSettingsFinishedChanges)
            // ran during Initalize before bindings were refreshed from the file —
            // re-notify so they pick up the loaded values.
            BasisSettingsSystem.NotifyFinishedChanges();
        }
    }
}
