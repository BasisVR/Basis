using Unity.Mathematics;

/// <summary>
/// Cached personality parameters derived from Liveliness and Attentiveness.
/// Liveliness controls saccade frequency and amplitude (low = settled, high = active).
/// Attentiveness controls eye contact commitment (low = avoidant, high = direct sustained gaze).
/// </summary>
public struct BasisEyePersonality
{
    // From Liveliness (saccade frequency + amplitude)
    public float holdMin, holdMax;
    public float centerBias;
    public float centerReturnChance;
    public float maxFocusedJitterRad;

    // From Attentiveness (eye contact commitment)
    public float holdScaleAtFullGaze;
    public float gazeBlendInSpeed, gazeBlendOutSpeed;
    public float socialHoldScale;

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

            holdScaleAtFullGaze = math.lerp(0.5f, 1.3f, A),
            gazeBlendInSpeed    = math.lerp(3.0f, 6.0f, A),
            gazeBlendOutSpeed   = math.lerp(2.5f, 0.8f, A),
            socialHoldScale     = math.lerp(0.6f, 1.2f, A),
        };
    }
}
