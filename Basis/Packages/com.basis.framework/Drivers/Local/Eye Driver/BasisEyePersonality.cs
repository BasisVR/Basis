using Unity.Mathematics;

/// <summary>
/// Cached personality parameters derived from Liveliness and Attentiveness.
/// Liveliness controls saccade frequency and amplitude (low = settled, high = active).
/// Attentiveness controls eye contact commitment (low = avoidant, high = direct sustained gaze).
/// </summary>
public struct BasisEyePersonality
{
    // === Liveliness  ===  (saccade frequency + amplitude)
    public float holdMin, holdMax;
    public float centerBias;
    public float centerReturnChance;
    public float maxFocusedJitterRad;

    // // === Attentiveness === (eye contact commitment)
    public float holdScaleAtFullGaze;
    public float gazeBlendInSpeed, gazeBlendOutSpeed;
    public float socialHoldScale;
    // Gaze disengagement break timing (low attentiveness = frequent breaks)
    public float gazeBreakMin, gazeBreakMax;
    public float avertedMin, avertedMax;
    // Reaction delay to new targets
    public float reactionMin, reactionMax;

    public static BasisEyePersonality Compute(float liveliness, float attentiveness)
    {
        float L = liveliness;
        float A = attentiveness;
        return new BasisEyePersonality
        {
            holdMin             = math.lerp(1.2f, 0.25f, L),
            holdMax             = math.lerp(6.0f, 1.5f, L),
            centerBias          = math.lerp(4.0f, 1.2f, L),
            centerReturnChance  = math.lerp(0.30f, 0.05f, L),
            maxFocusedJitterRad = math.radians(math.lerp(0.15f, 1.0f, L)),

            holdScaleAtFullGaze = math.lerp(0.3f, 2.0f, A),
            gazeBlendInSpeed    = math.lerp(1.5f, 8.0f, A),
            gazeBlendOutSpeed   = math.lerp(4.0f, 0.5f, A),
            socialHoldScale     = math.lerp(0.3f, 1.5f, A),
            gazeBreakMin = math.lerp(1.0f, 30.0f, A),
            gazeBreakMax = math.lerp(3.0f, 60.0f, A),
            avertedMin   = math.lerp(0.8f, 0.1f, A),
            avertedMax   = math.lerp(2.0f, 0.3f, A),
            reactionMin  = math.lerp(0.30f, 0.08f, A),
            reactionMax  = math.lerp(0.60f, 0.15f, A),
        };
    }
}
