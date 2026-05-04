namespace Basis.Scripts.BasisSdk.Players
{
    // SDK owns eye personality state. Framework's BasisLocalEyeDriver reads from
    // here; set PersonalityDirty = true after any Liveliness/Attentiveness write
    // so the driver knows to recompute its cached BasisEyePersonality.
    // In normal runtime nothing changes after avatar load, so the dirty check
    // stays a single bool read per frame.
    public static class BasisLocalEyeDriverService
    {
        public static float Liveliness = 0.5f;
        public static float Attentiveness = 0.5f;
        public static bool PersonalityDirty;
    }
}
