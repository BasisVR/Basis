namespace Basis.Scripts.BasisSdk.Players
{
    public interface IBasisLocalEyeDriver
    {
        float Liveliness { get; set; }
        float Attentiveness { get; set; }
        void ApplyPersonality();
    }

    // Use from sdk code; use BasisLocalEyeDriver from framework code.
    // Framework's BasisLocalEyeDriver registers when present; otherwise the
    // SDK editor preview registers a stand-in (gated by BASIS_FRAMEWORK_EXISTS
    // so only one ever registers).
    public static class BasisLocalEyeDriverService
    {
        public static IBasisLocalEyeDriver Instance { get; private set; }

        public static void Register(IBasisLocalEyeDriver impl) => Instance = impl;
        public static void Unregister(IBasisLocalEyeDriver impl)
        {
            if (Instance == impl) Instance = null;
        }

        public static float Liveliness
        {
            get => Instance.Liveliness;
            set => Instance.Liveliness = value;
        }

        public static float Attentiveness
        {
            get => Instance.Attentiveness;
            set => Instance.Attentiveness = value;
        }

        public static void ApplyPersonality() => Instance.ApplyPersonality();
    }
}
