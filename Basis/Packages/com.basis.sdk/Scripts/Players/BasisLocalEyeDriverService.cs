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
        public static IBasisLocalEyeDriver Instance;
    }
}
