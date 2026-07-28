using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Mathematics;

namespace Basis.Scripts.Drivers
{
    public enum BasisSmoothingGroup : byte
    {
        Head = 0,
        Hands = 1,
        Elbows = 2,
        Chest = 3,
        Hips = 4,
        Knees = 5,
        Feet = 6,
    }

    public struct BasisSmoothingProfile
    {
        public float MinCutoff;
        public float Beta;
        public float DerivativeCutoff;
        public float PositionHz;
        public float RotationHz;

        public BasisSmoothingProfile(float minCutoff, float beta, float derivativeCutoff, float positionHz, float rotationHz)
        {
            MinCutoff = minCutoff;
            Beta = beta;
            DerivativeCutoff = derivativeCutoff;
            PositionHz = positionHz;
            RotationHz = rotationHz;
        }
    }

    public static class BasisSmoothingProfiles
    {
        public const int GroupCount = 7;

        /// <summary>
        /// The framerate at which the *RotationSpeed settings were tuned, and the rate whose behaviour
        /// FramerateIndependentAlpha reproduces exactly. Changing this re-tunes every user's spine.
        /// </summary>
        private const float SmoothingReferenceFps = 90f;

        /// <summary>
        /// Converts a legacy "speed" (which the old code used as a per-frame lerp fraction: alpha = dt*speed)
        /// into a framerate-independent alpha with the same time constant at ANY dt.
        ///
        /// The old form was `alpha = saturate(dt * speed)`, and its time constant was a function of the
        /// user's GPU. Smaller dt gives a smaller alpha gives a SLOWER filter, so the fingerprint was
        /// perverse: a 144 Hz headset got ~60% MORE neck lag than a 72 Hz one (18 ms vs 11 ms at 1 Hz) from
        /// the very same setting. And `saturate` clamps once dt*speed >= 1, so at the default
        /// NeckRotationSpeed of 40 the smoothing VANISHED below 40 fps -- the neck snapped every frame,
        /// exactly when the framerate was bad enough to need smoothing most (standalone under load, desktop).
        ///
        /// The speeds are user-facing sliders with persisted values, so the number's MEANING is preserved
        /// rather than redefined: it is still read as "the per-frame fraction at the reference rate", then
        /// converted to the true exponential rate that reproduces it. This is therefore a bit-for-bit no-op
        /// at SmoothingReferenceFps and a correction at every other framerate -- nobody's tuning changes, it
        /// just stops depending on their hardware. Same discipline as the foot-IK scale fix: exactly a no-op
        /// at the reference, proportional everywhere else.
        ///
        /// Gated by BasisBlendSpeedTests.
        /// </summary>
        public static float FramerateIndependentAlpha(float speed, float dt)
        {
            float alphaAtRef = math.saturate(math.max(0f, speed) / SmoothingReferenceFps);
            // The legacy form snapped outright at and above the reference rate; preserve that endpoint
            // rather than dividing by zero on the way to it.
            if (alphaAtRef >= 0.999f) return 1f;
            if (alphaAtRef <= 0f) return 0f;
            float rate = -SmoothingReferenceFps * math.log(1f - alphaAtRef);
            return 1f - math.exp(-rate * math.max(0f, dt));
        }

        public const string PresetOff = "Off";
        public const string PresetLight = "Light";
        public const string PresetStandard = "Standard";
        public const string PresetHeavy = "Heavy";
        public const string PresetOptical = "Optical";

        // Custom is a preset slot rather than a separate toggle so the states stay mutually exclusive;
        // "Off + custom values" was reachable before and meant nothing.
        public const string PresetCustom = "Custom";

        // Resolved per frame from the hardware actually driving each group, then treated as whichever
        // preset it picked. A sentinel like Off and Custom: it never resolves through TryGetPreset.
        public const string PresetAuto = "Auto";

        public static readonly string[] PresetOrder =
        {
            PresetAuto,
            PresetOff,
            PresetLight,
            PresetStandard,
            PresetHeavy,
            PresetOptical,
            PresetCustom,
        };

        public static readonly string[] PresetLocalizationKeys =
        {
            "settings.bodyTracking.smoothing.preset.auto",
            "settings.bodyTracking.smoothing.preset.off",
            "settings.bodyTracking.smoothing.preset.light",
            "settings.bodyTracking.smoothing.preset.standard",
            "settings.bodyTracking.smoothing.preset.heavy",
            "settings.bodyTracking.smoothing.preset.optical",
            "settings.bodyTracking.smoothing.preset.custom",
        };

        private static readonly BasisSmoothingProfile Light = new(8f, 2f, 3f, 30f, 35f);
        private static readonly BasisSmoothingProfile Heavy = new(2f, 5f, 2f, 10f, 12f);
        private static readonly BasisSmoothingProfile Optical = new(1f, 9f, 1.5f, 8f, 10f);

        public static bool IsOff(string preset) => preset == PresetOff;

        public static bool IsCustom(string preset) => preset == PresetCustom;

        public static bool IsAuto(string preset) => preset == PresetAuto;

        /// <summary>
        /// Picks the preset that suits a tracking technology. Lighthouse and inside-out rigs are already
        /// quiet enough that filtering them hard only adds latency; IMU trackers buzz and drift and need
        /// the heavy curve; markerless camera tracking is steady at rest but noisy in motion, which is
        /// exactly what Optical is tuned for. Unknown hardware gets Standard — the same behaviour it had
        /// before Auto existed.
        /// </summary>
        public static string PresetForHardware(BasisTrackingHardware hardware)
        {
            switch (hardware)
            {
                case BasisTrackingHardware.Simulated:
                    return PresetOff;
                case BasisTrackingHardware.Lighthouse:
                case BasisTrackingHardware.InsideOut:
                    return PresetLight;
                case BasisTrackingHardware.Optical:
                    return PresetOptical;
                case BasisTrackingHardware.Estimated:
                case BasisTrackingHardware.Inertial:
                    return PresetHeavy;
                default:
                    return PresetStandard;
            }
        }

        /// <summary>
        /// Which smoothing group a tracked role belongs to, mirroring <see cref="SlotGroup"/> for the
        /// roles a device can actually claim. Returns false for roles no tracker drives.
        /// </summary>
        public static bool TryGetGroupForRole(BasisBoneTrackedRole role, out int group)
        {
            switch (role)
            {
                case BasisBoneTrackedRole.CenterEye:
                case BasisBoneTrackedRole.Head:
                    group = (int)BasisSmoothingGroup.Head;
                    return true;
                case BasisBoneTrackedRole.LeftHand:
                case BasisBoneTrackedRole.RightHand:
                case BasisBoneTrackedRole.LeftShoulder:
                case BasisBoneTrackedRole.RightShoulder:
                    group = (int)BasisSmoothingGroup.Hands;
                    return true;
                case BasisBoneTrackedRole.LeftLowerArm:
                case BasisBoneTrackedRole.RightLowerArm:
                    group = (int)BasisSmoothingGroup.Elbows;
                    return true;
                case BasisBoneTrackedRole.Chest:
                    group = (int)BasisSmoothingGroup.Chest;
                    return true;
                case BasisBoneTrackedRole.Hips:
                    group = (int)BasisSmoothingGroup.Hips;
                    return true;
                case BasisBoneTrackedRole.LeftLowerLeg:
                case BasisBoneTrackedRole.RightLowerLeg:
                    group = (int)BasisSmoothingGroup.Knees;
                    return true;
                case BasisBoneTrackedRole.LeftFoot:
                case BasisBoneTrackedRole.RightFoot:
                case BasisBoneTrackedRole.LeftToes:
                case BasisBoneTrackedRole.RightToes:
                    group = (int)BasisSmoothingGroup.Feet;
                    return true;
                default:
                    group = -1;
                    return false;
            }
        }

        public static bool TryGetPreset(string preset, out BasisSmoothingProfile profile)
        {
            switch (preset)
            {
                case PresetLight:
                    profile = Light;
                    return true;
                case PresetHeavy:
                    profile = Heavy;
                    return true;
                case PresetOptical:
                    profile = Optical;
                    return true;
                default:
                    profile = default;
                    return false;
            }
        }

        public static readonly byte[] SlotGroup = BuildSlotGroups();

        private static byte[] BuildSlotGroups()
        {
            byte[] map = new byte[BasisLocalRigDriver.SlotCount];
            map[BasisLocalRigDriver.S_Head] = (byte)BasisSmoothingGroup.Head;
            map[BasisLocalRigDriver.S_LeftHand] = (byte)BasisSmoothingGroup.Hands;
            map[BasisLocalRigDriver.S_RightHand] = (byte)BasisSmoothingGroup.Hands;
            map[BasisLocalRigDriver.S_LeftShoulder] = (byte)BasisSmoothingGroup.Hands;
            map[BasisLocalRigDriver.S_RightShoulder] = (byte)BasisSmoothingGroup.Hands;
            map[BasisLocalRigDriver.S_LeftLowerArm] = (byte)BasisSmoothingGroup.Elbows;
            map[BasisLocalRigDriver.S_RightLowerArm] = (byte)BasisSmoothingGroup.Elbows;
            map[BasisLocalRigDriver.S_Chest] = (byte)BasisSmoothingGroup.Chest;
            map[BasisLocalRigDriver.S_Hips] = (byte)BasisSmoothingGroup.Hips;
            map[BasisLocalRigDriver.S_LeftLowerLeg] = (byte)BasisSmoothingGroup.Knees;
            map[BasisLocalRigDriver.S_RightLowerLeg] = (byte)BasisSmoothingGroup.Knees;
            map[BasisLocalRigDriver.S_LeftFoot] = (byte)BasisSmoothingGroup.Feet;
            map[BasisLocalRigDriver.S_RightFoot] = (byte)BasisSmoothingGroup.Feet;
            map[BasisLocalRigDriver.S_LeftToe] = (byte)BasisSmoothingGroup.Feet;
            map[BasisLocalRigDriver.S_RightToe] = (byte)BasisSmoothingGroup.Feet;
            return map;
        }
    }
}
