using UnityEngine;
using UnityEngine.UI;

public static class ButterflyGatePreview
{
    // Match the same deadzone math you use for inputs.
    private static float ApplyDeadzone(float v, float dz)
    {
        float av = Mathf.Abs(v);
        if (av <= dz) return 0f;
        return Mathf.Sign(v) * ((av - dz) / (1f - dz));
    }

    /// <summary>
    /// Generates a preview texture for the "butterfly wings" gate.
    /// White = horizontal allowed, Black = horizontal blocked.
    /// If smooth = true, produces a gradient instead of a hard mask.
    /// </summary>
    public static Texture2D Generate(
        int size,
        float baseXDeadzone,
        float extraXDeadzoneAtFullY,
        float yDeadzone,
        float wingExponent,
        bool smooth = true)
    {
        size = Mathf.Clamp(size, 32, 2048);

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        var pixels = new Color32[size * size];

        // Precompute y boundary per row so it's cheap.
        for (int j = 0; j < size; j++)
        {
            float y = Mathf.Lerp(-1f, 1f, j / (size - 1f));

            float yDz = ApplyDeadzone(y, yDeadzone);
            float yInfluence = Mathf.Pow(Mathf.Clamp01(Mathf.Abs(yDz)), wingExponent);
            float xDz = Mathf.Clamp01(baseXDeadzone + extraXDeadzoneAtFullY * yInfluence);

            for (int i = 0; i < size; i++)
            {
                float x = Mathf.Lerp(-1f, 1f, i / (size - 1f));
                float ax = Mathf.Abs(x);

                byte v;
                if (!smooth)
                {
                    // Hard mask
                    v = (byte)(ax > xDz ? 255 : 0);
                }
                else
                {
                    // Smooth edge so the shape reads well.
                    // EdgeWidth controls how "soft" the boundary is in stick units.
                    const float edgeWidth = 0.06f;
                    float t = Mathf.InverseLerp(xDz - edgeWidth, xDz + edgeWidth, ax);
                    v = (byte)(Mathf.Clamp01(t) * 255f);
                }

                pixels[j * size + i] = new Color32(v, v, v, 255);
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        return tex;
    }

    [SerializeField] private static RawImage preview;
    private static Texture2D _tex;
    public static void RebuildPreview(float controllerDeadZone, float baseX, float extraX, float yDz, float wingExp01)
    {
        // If your wing exponent is stored 0..1, remap it to a useful real range:
        float wingExp = Mathf.Lerp(1.0f, 3.0f, wingExp01);

        // (Optional) If you also apply a radial deadzone in the real filter, you can
        // incorporate that too, but the "wings" shape is mostly from the X gating.

        if (_tex != null) GameObject.Destroy(_tex);

        _tex = ButterflyGatePreview.Generate(
            size: 256,
            baseXDeadzone: baseX,
            extraXDeadzoneAtFullY: extraX,
            yDeadzone: yDz,
            wingExponent: wingExp,
            smooth: true);

        preview.texture = _tex;
    }

    private static void OnDestroy()
    {
        if (_tex != null) GameObject.Destroy(_tex);
    }
}
