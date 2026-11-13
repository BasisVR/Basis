#ifndef BASIS_GAZE_FOVEATION_INCLUDED
#define BASIS_GAZE_FOVEATION_INCLUDED

// Gaze foveation for screen-wide effects. The Basis VRS build pass sets these at the start of the
// local player's camera and the reset pass clears them at its end, so every other camera (mirrors,
// handheld cameras) and every frame without live eye-tracked gaze reads weight 0 and full quality.
float4 _BasisGazeFocal;   // xyz = world point the player is looking at, w = gaze weight (0 = off)
float4 _BasisGazeBands;   // x = cos of the angle where effects start thinning, y = cos where they reach the floor, z = floor scale
float4 _BasisGazeEffects; // x = reflections, y = volumetric fog, z = ray traced GI rays, w = GI cascades (1 = foveate that effect)

// Multiplier for an effect's per-pixel loop count: 1 near the gaze, falling to the floor scale in the
// periphery. cameraWS is the eye position the effect marches from, directionWS any vector along the view ray.
float BasisGazeEffectScale(float3 cameraWS, float3 directionWS, float effect)
{
    float weight = _BasisGazeFocal.w * effect;
    if (weight <= 0.0)
        return 1.0;
    float3 toFocal = _BasisGazeFocal.xyz - cameraWS;
    float lengths = dot(toFocal, toFocal) * dot(directionWS, directionWS);
    if (lengths < 1e-12)
        return 1.0;
    float c = dot(toFocal, directionWS) * rsqrt(lengths);
    float t = saturate((_BasisGazeBands.x - c) / max(1e-4, _BasisGazeBands.x - _BasisGazeBands.y));
    return lerp(1.0, _BasisGazeBands.z, t * weight);
}

#endif
