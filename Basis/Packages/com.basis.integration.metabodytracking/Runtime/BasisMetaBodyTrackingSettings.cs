using Unity.Scripting.LifecycleManagement;
#if BASIS_FRAMEWORK_EXISTS
using Basis.Scripts.Settings;

namespace Basis.Integration.MetaBodyTracking
{
    /// <summary>
    /// Persistent settings for Meta body tracking. Bindings self-load on construction, but this
    /// class is first touched from RuntimeInitializeOnLoadMethod hooks — before BasisSettingsSystem
    /// has read the settings file — so the post-load registration below re-loads them once the store
    /// is populated (otherwise saved values never restore).
    /// </summary>
    [NoAutoStaticsCleanup]
    public static class BasisMetaBodyTrackingSettings
    {
        static BasisMetaBodyTrackingSettings()
        {
            BasisSettingsBindingPostLoad.Register(typeof(BasisMetaBodyTrackingSettings));
        }

        /// <summary>
        /// Ratio the framework uses between eye height and full standing height, for converting the
        /// height Basis measures into the one Meta's body calibration wants.
        /// </summary>
        public const float EyeHeightToFullHeightRatio = 0.936f;

        public const string TrackerSourceOff = "off";
        public const string TrackerSourceAuto = "auto";
        public const string TrackerSourceForce = "force";

        /// <summary>
        /// Whether to source trackers from the headset's body tracking at all. "auto" only fills the
        /// gap — a body part already held by a physical tracker is left alone — while "force" drives
        /// every supported body part from the headset and removes the duplicate device. "off" keeps
        /// the OpenXR body tracker from being used even when the runtime offers it.
        /// </summary>
        public static readonly BasisSettingsBinding<string> TrackerSource =
            new BasisSettingsBinding<string>("metabody_tracker_source", new BasisPlatformDefault<string>(TrackerSourceAuto));

        /// <summary>Hips, chest and both elbows. Available from XR_FB_body_tracking alone.</summary>
        public static readonly BasisSettingsBinding<bool> TrackUpperBody =
            new BasisSettingsBinding<bool>("metabody_upper_body", new BasisPlatformDefault<bool>(true));

        /// <summary>Both knees and both feet. Needs XR_META_body_tracking_full_body.</summary>
        public static readonly BasisSettingsBinding<bool> TrackLegs =
            new BasisSettingsBinding<bool>("metabody_legs", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// Ask the runtime for its high fidelity body solve, which tracks the arms and legs with the
        /// headset cameras instead of inferring them from the head and controllers alone. Costs
        /// headset compute, so it is worth turning off on a frame budget.
        /// </summary>
        public static readonly BasisSettingsBinding<bool> HighFidelity =
            new BasisSettingsBinding<bool>("metabody_high_fidelity", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// Give the runtime the height Basis measured for the player instead of letting its body
        /// solve estimate one, so the two agree on how tall the person actually is.
        /// </summary>
        public static readonly BasisSettingsBinding<bool> ApplyPlayerHeight =
            new BasisSettingsBinding<bool>("metabody_apply_height", new BasisPlatformDefault<bool>(true));

        /// <summary>
        /// Bind the body tracking trackers to their body parts automatically, the same way an
        /// announced physical tracker binds. Off runs them through the normal manual calibration.
        /// </summary>
        public static readonly BasisSettingsBinding<bool> AutoBindTrackers =
            new BasisSettingsBinding<bool>("metabody_autobind", new BasisPlatformDefault<bool>(true));
    }
}
#endif
