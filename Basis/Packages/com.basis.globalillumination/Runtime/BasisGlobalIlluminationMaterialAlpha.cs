using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

public enum BasisGlobalIlluminationAlphaKind { Opaque, Cutout, Blend, BlendCutout }

[NoAutoStaticsCleanup]
public static class BasisGlobalIlluminationMaterialAlpha
{
    private static readonly int idCutoff = Shader.PropertyToID("_Cutoff"), idAlphaClip = Shader.PropertyToID("_AlphaClip"), idSurface = Shader.PropertyToID("_Surface");
    private static readonly int idSrcBlend = Shader.PropertyToID("_SrcBlend"), idDstBlend = Shader.PropertyToID("_DstBlend"), idBlend = Shader.PropertyToID("_Blend");
    private static readonly int idAlphaToMask = Shader.PropertyToID("_AlphaToMask"), idAlphaToCoverage = Shader.PropertyToID("_AlphaToCoverage");

    public static BasisGlobalIlluminationAlphaKind Classify(Material m, MaterialPropertyBlock block, out float cutoff)
    {
        cutoff = 0f;
        if (m == null) { return BasisGlobalIlluminationAlphaKind.Opaque; }
        int queue = m.renderQueue;
        string renderType = m.GetTag("RenderType", false, string.Empty);
        bool coverage = Read(m, block, idAlphaToMask, 0f) > 0.5f || Read(m, block, idAlphaToCoverage, 0f) > 0.5f;
        bool clip = Read(m, block, idAlphaClip, 0f) > 0.5f || m.IsKeywordEnabled("_ALPHATEST_ON") || (queue >= 2450 && queue < 3000) || coverage || renderType == "TransparentCutout";
        bool blend = Read(m, block, idSurface, 0f) > 0.5f || queue >= 3000 || renderType == "Transparent" || renderType == "Fade" || BlendsWithBackground(m, block);
        if (clip) { cutoff = Read(m, block, idCutoff, 0.5f); }
        return blend ? (clip ? BasisGlobalIlluminationAlphaKind.BlendCutout : BasisGlobalIlluminationAlphaKind.Blend) : clip ? BasisGlobalIlluminationAlphaKind.Cutout : BasisGlobalIlluminationAlphaKind.Opaque;
    }

    public static bool IsBlend(BasisGlobalIlluminationAlphaKind kind) => kind == BasisGlobalIlluminationAlphaKind.Blend || kind == BasisGlobalIlluminationAlphaKind.BlendCutout;

    public static bool BlendsWithBackground(Material m, MaterialPropertyBlock block)
    {
        if (!m.HasProperty(idSrcBlend) || !m.HasProperty(idDstBlend)) { return false; }
        BlendMode src = (BlendMode)Mathf.RoundToInt(Read(m, block, idSrcBlend, 1f)), dst = (BlendMode)Mathf.RoundToInt(Read(m, block, idDstBlend, 0f));
        return dst != BlendMode.Zero || src == BlendMode.DstColor || src == BlendMode.OneMinusDstColor || src == BlendMode.DstAlpha || src == BlendMode.OneMinusDstAlpha;
    }

    public static bool LightPassesThrough(Material m, MaterialPropertyBlock block)
    {
        if (m.HasProperty(idSrcBlend) && m.HasProperty(idDstBlend))
        {
            BlendMode src = (BlendMode)Mathf.RoundToInt(Read(m, block, idSrcBlend, 5f)), dst = (BlendMode)Mathf.RoundToInt(Read(m, block, idDstBlend, 10f));
            return dst == BlendMode.One || src == BlendMode.DstColor || dst == BlendMode.SrcColor;
        }
        return m.HasProperty(idBlend) && Mathf.RoundToInt(Read(m, block, idBlend, 0f)) >= 2;
    }

    private static float Read(Material m, MaterialPropertyBlock block, int id, float fallback)
    {
        if (block != null && block.HasFloat(id)) { return block.GetFloat(id); }
        return m.HasFloat(id) ? m.GetFloat(id) : fallback;
    }
}
