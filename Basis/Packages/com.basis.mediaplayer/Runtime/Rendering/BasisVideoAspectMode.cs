// Aspect-fit mode, applied as a UV scale/offset on the sampled video texture.
//
// FitInside (letterbox/pillarbox) scales the bar axis > 1, so the sampled UV runs
// outside [0,1] over the bars. It only renders those as black bars with a
// shader/material that treats out-of-range UVs as black (e.g. "Basis/Media Player
// Video"). On a clamp/repeat material — or a RawImage, which follows the UI
// sampler — it smears or repeats the edge instead; there, prefer Original + a
// RectTransform AspectRatioFitter, or FitOutside (crop, which stays within [0,1]).
public enum BasisVideoAspectMode
{
    Original = 0,     // sample untransformed (no aspect correction)
    Stretch = 1,      // same as Original — fill, ignore aspect
    FitInside = 2,    // letterbox/pillarbox — needs a black-out-of-range shader (see above)
    FitOutside = 3,   // crop to fill (stays within [0,1], safe on any material)
    PixelPerfect = 4, // 1:1 source pixels at the chosen scale
}
