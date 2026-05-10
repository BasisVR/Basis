namespace Basis.Scripts.BasisSdk.Players
{
    // SDK-side eye personality data. Framework's BasisLocalEyeDriver reads
    // these fields; set PersonalityDirty = true after any Liveliness or
    // Attentiveness write so the driver recomputes its cached
    // BasisEyePersonality. In normal runtime nothing changes after avatar
    // load, so the dirty check stays a single bool read per frame.
    public static class BasisLocalEyeDriverData
    {
        public static float Liveliness = 0.5f;
        public static float Attentiveness = 0.5f;
        public static bool PersonalityDirty;
    }
}
