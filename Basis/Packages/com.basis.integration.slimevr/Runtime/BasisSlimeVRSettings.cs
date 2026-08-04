#if BASIS_FRAMEWORK_EXISTS
using Basis.Scripts.Settings;

namespace Basis.Integration.SlimeVR
{
    /// <summary>
    /// Persistent settings for the SlimeVR integration. Bindings self-load on construction, but
    /// this class is first touched from RuntimeInitializeOnLoadMethod hooks — before
    /// BasisSettingsSystem has read the settings file — so the post-load registration below
    /// re-loads them once the store is populated (otherwise saved values never restore).
    /// </summary>
    public static class BasisSlimeVRSettings
    {
        static BasisSlimeVRSettings()
        {
            BasisSettingsBindingPostLoad.Register(typeof(BasisSlimeVRSettings));
        }

        /// <summary>Run the SlimeVR client at all. Connection attempts are cheap while no server is running.</summary>
        public static readonly BasisSettingsBinding<bool> Enable =
            new BasisSettingsBinding<bool>("slimevr_enable", new BasisPlatformDefault<bool>(true));

        /// <summary>Automatically apply SlimeVR's body proportions (eye height + arm span) to the Basis height system.</summary>
        public static readonly BasisSettingsBinding<bool> ApplyBodyMeasurements =
            new BasisSettingsBinding<bool>("slimevr_applybodymeasurements", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// SlimeVR trackers announce their body part in their serial and bind to it automatically
        /// (registered with the framework's BasisAnnouncedTrackerRoles scanner). Switch off to run
        /// them through the normal manual/geometric calibration instead.
        /// </summary>
        public static readonly BasisSettingsBinding<bool> AutoBindSlimeVRTrackers =
            new BasisSettingsBinding<bool>("slimevr_autobind", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// Automatically re-run full-body calibration when SlimeVR's tracker mounting orientation
        /// changes (i.e. after a mounting or full reset). Basis captures each FBT offset relative to
        /// the tracker's reported orientation, so a reset silently invalidates every offset until the
        /// next calibration; this heals it without a manual recalibration.
        /// </summary>
        public static readonly BasisSettingsBinding<bool> RecalibrateOnMountingChange =
            new BasisSettingsBinding<bool>("slimevr_recalibrate_on_reset", new BasisPlatformDefault<bool>(true));

        // Commented out 2026-08-04: the skeleton-seeding implementation this described is not
        // in the codebase — no UI exposes the toggle and nothing reads it. Restore alongside
        // the seeding logic if it lands again.
        ///// <summary>
        ///// EXPERIMENTAL, default off. Pull SlimeVR's solved skeleton (bones datafeed + tracker
        ///// positions) and use it to seed the FBT position offsets after calibration, instead of relying
        ///// only on the calibration-pose capture. Guarded: it solves the SlimeVR→Basis frame from the
        ///// shared trackers, refuses to apply when that fit is poor, seeds position only (rotation stays
        ///// empirical), and rejects any per-tracker seed that departs too far from the captured offset.
        ///// Requesting the extra datafeed data is the only added bandwidth, and only while this is on.
        ///// </summary>
        //public static readonly BasisSettingsBinding<bool> SkeletonSeeding =
        //    new BasisSettingsBinding<bool>("slimevr_skeleton_seeding", new BasisPlatformDefault<bool>(false));

        /// <summary>
        /// Seconds the reset/recalibrate buttons wait before firing, giving time to get into pose.
        /// 0 fires instantly. Whole seconds; read at countdown start.
        /// </summary>
        public static readonly BasisSettingsBinding<float> PoseCountdownSeconds =
            new BasisSettingsBinding<float>("slimevr_pose_countdown_seconds", new BasisPlatformDefault<float>(4f));

        public const string TrackerSourceOff = "off";
        public const string TrackerSourceAuto = "auto";
        public const string TrackerSourceForce = "force";

        /// <summary>
        /// EXPERIMENTAL, default off. Source SlimeVR's trackers straight from the SlimeVR server over
        /// SolarXR instead of relying on the OpenVR/OpenXR runtime to surface them. "auto" adds a
        /// server-driven tracker only when the active XR runtime isn't already providing that body part
        /// (the fallback for OpenXR sessions where SteamVR's trackers never reach the app); "force"
        /// always drives them from the server and takes over any runtime-provided duplicates. Needs
        /// SlimeVR to have an HMD reference (SteamVR in the background, or a future OSC HMD feed) — with
        /// none, SlimeVR's poses have no anchor and nothing is created.
        /// </summary>
        public static readonly BasisSettingsBinding<string> TrackerSource =
            new BasisSettingsBinding<string>("slimevr_tracker_source", new BasisPlatformDefault<string>(TrackerSourceOff));

        public const string TransportWebSocket = "websocket";
        public const string TransportPipe = "pipe";

        /// <summary>
        /// How to talk to the SlimeVR server: "websocket" (works on every released server today)
        /// or "pipe" (SlimeVR's native transport — Windows named pipe / unix socket — the one that
        /// stays once websockets are deprecated, but it needs a server whose pipe bridge works).
        /// </summary>
        public static readonly BasisSettingsBinding<string> Transport =
            new BasisSettingsBinding<string>("slimevr_transport", new BasisPlatformDefault<string>(TransportWebSocket));
    }
}
#endif
