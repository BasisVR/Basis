using Unity.Scripting.LifecycleManagement;
namespace Basis.Scripts.BasisSdk.Players
{
    // SDK-side eye personality data. Framework's BasisLocalEyeDriver reads
    // these fields; set PersonalityDirty = true after any Liveliness or
    // Attentiveness write so the driver recomputes its cached
    // BasisEyePersonality. In normal runtime nothing changes after avatar
    // load, so the dirty check stays a single bool read per frame.
    [AutoStaticsCleanup]
    public static partial class BasisLocalEyeDriverData
    {
        public static float Liveliness = 0.5f;
        public static float Attentiveness = 0.5f;
        public static bool MaxLookAngleEnabled;
        public static float MaxLookAngleDeg = BasisAvatar.DefaultEyeMaxLookAngle;
        public static bool PersonalityDirty;
        public static void SetMaxLookAngle(bool enabled, float degrees)
        {
            MaxLookAngleEnabled = enabled;
            MaxLookAngleDeg = enabled ? BasisAvatar.ClampEyeMaxLookAngle(degrees) : BasisAvatar.DefaultEyeMaxLookAngle;
        }
    }
}
