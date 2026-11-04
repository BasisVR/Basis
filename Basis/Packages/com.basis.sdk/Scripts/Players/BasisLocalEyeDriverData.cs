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
        public const float MinUserLookAngle = 1f, MaxUserLookAngle = 90f;
        public static bool AvatarMaxLookAngleEnabled;
        public static float AvatarMaxLookAngleDeg = BasisAvatar.DefaultEyeMaxLookAngle;
        public static bool UserMaxLookAngleEnabled;
        public static float UserMaxLookAngleDeg = BasisAvatar.DefaultEyeMaxLookAngle;
        public static void SetMaxLookAngle(bool enabled, float degrees)
        {
            AvatarMaxLookAngleEnabled = enabled;
            AvatarMaxLookAngleDeg = enabled ? BasisAvatar.ClampEyeMaxLookAngle(degrees) : BasisAvatar.DefaultEyeMaxLookAngle;
            ResolveMaxLookAngle();
        }
        public static void SetUserMaxLookAngle(bool enabled, float degrees)
        {
            UserMaxLookAngleEnabled = enabled;
            UserMaxLookAngleDeg = ClampUserLookAngle(degrees);
            ResolveMaxLookAngle();
        }
        public static float ClampUserLookAngle(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees) || degrees <= 0f) return BasisAvatar.DefaultEyeMaxLookAngle;
            return UnityEngine.Mathf.Clamp(degrees, MinUserLookAngle, MaxUserLookAngle);
        }
        static void ResolveMaxLookAngle()
        {
            MaxLookAngleEnabled = UserMaxLookAngleEnabled || AvatarMaxLookAngleEnabled;
            MaxLookAngleDeg = UserMaxLookAngleEnabled ? UserMaxLookAngleDeg : AvatarMaxLookAngleDeg;
        }
    }
}
