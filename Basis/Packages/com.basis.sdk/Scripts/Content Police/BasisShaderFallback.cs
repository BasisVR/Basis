using System;
using System.Collections.Generic;
using UnityEngine;

public struct BasisTexTransform
{
    public Texture texture;
    public Vector2 scale;
    public Vector2 offset;
    public BasisTexTransform(Texture tex, Vector2 scale, Vector2 offset)
    {
        this.texture = tex;
        this.scale = scale;
        this.offset = offset;
    }
}

public static class BasisShaderFallback
{
    // User-configured blocklist: case-insensitive substrings matched against shader
    // names and material shader keywords. A match forces the fallback material.
    private static string[] BlockedPatterns = Array.Empty<string>();
    private static readonly char[] BlocklistSeparators = { ',', ';', '\n', '\r' };

    public static bool HasBlocklist => BlockedPatterns.Length > 0;

    public static void SetBlocklist(string patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
        {
            BlockedPatterns = Array.Empty<string>();
            return;
        }
        string[] split = patterns.Split(BlocklistSeparators, StringSplitOptions.RemoveEmptyEntries);
        List<string> cleaned = new List<string>(split.Length);
        for (int Index = 0; Index < split.Length; Index++)
        {
            string trimmed = split[Index].Trim();
            if (trimmed.Length > 0)
            {
                cleaned.Add(trimmed);
            }
        }
        BlockedPatterns = cleaned.ToArray();
    }

    private static bool IsBlocked(Material mat, Shader shader, Dictionary<Shader, bool> shaderNameBlocked)
    {
        string[] patterns = BlockedPatterns;
        if (patterns.Length == 0)
        {
            return false;
        }
        if (!shaderNameBlocked.TryGetValue(shader, out bool nameBlocked))
        {
            nameBlocked = MatchesAny(shader.name, patterns);
            shaderNameBlocked[shader] = nameBlocked;
        }
        if (nameBlocked)
        {
            return true;
        }
        string[] keywords = mat.shaderKeywords;
        for (int Index = 0; Index < keywords.Length; Index++)
        {
            if (MatchesAny(keywords[Index], patterns))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesAny(string value, string[] patterns)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        for (int Index = 0; Index < patterns.Length; Index++)
        {
            if (value.IndexOf(patterns[Index], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static readonly string[] AlbedoProps =
    {
        "_MainTex",
        "_BaseMap",
        "_Albedo",
        "_Diffuse",
        "_BaseColorMap",
        "_ColorTexture",
        "_Tex",
        "_Texture"
    };
    private static readonly string[] NormalProps =
    {
        "_BumpMap", "_NormalMap", "_NormalTex", "_NormalTexture"
    };
    private static readonly string[] MetallicProps =
    {
        "_MetallicGlossMap", "_MetallicMap", "_MetalMap", "_MetallicTex"
    };
    private static readonly string[] OcclusionProps =
    {
        "_OcclusionMap", "_Occlusion", "_AOMap", "_AmbientOcclusionMap"
    };
    private static readonly string[] ColorProps =
    {
        "_BaseColor", "_Color", "_Tint", "_MainColor"
    };

    public static bool TryGetFirstColor(Material mat, out Color value, out string foundProp)
    {
        for (int Index = 0; Index < ColorProps.Length; Index++)
        {
            string p = ColorProps[Index];
            if (mat.HasProperty(p))
            {
                value = mat.GetColor(p);
                foundProp = p;
                return true;
            }
        }
        value = Color.white;
        foundProp = string.Empty;
        return false;
    }

    public static bool TryGetFirstTextureWithScaleAndOffset(Material mat, string[] props, out BasisTexTransform result, out string foundProp)
    {
        for (int i = 0; i < props.Length; i++)
        {
            string p = props[i];
            if (TryGetTextureWithScaleAndOffset(mat, p, out result))
            {
                foundProp = p;
                return true;
            }
        }
        result = default;
        foundProp = string.Empty;
        return false;
    }

    public static bool TryGetTextureWithScaleAndOffset(Material mat, string propertyName, out BasisTexTransform result)
    {
        result = default;
        if (mat == null)
        {
            return false;
        }
        Texture tex = mat.GetTexture(propertyName);
        if (tex == null)
        {
            return false;
        }
        Vector2 scale = mat.GetTextureScale(propertyName);
        Vector2 offset = mat.GetTextureOffset(propertyName);
        result = new BasisTexTransform(tex, scale, offset);
        return true;
    }

    // Per-pass caches: every unique material/shader is evaluated once per load, and
    // GetSharedMaterials/SetSharedMaterials against the shared scratch list keeps the
    // per-renderer walk allocation-free. Main-thread only, consumed synchronously.
    private struct CorrectionPass
    {
        public Shader FallbackShader;
        public Dictionary<Material, Material> FixedMaterials;
        public Dictionary<Shader, bool> ShaderBroken;
        public Dictionary<Shader, bool> ShaderNameBlocked;
        public Dictionary<Material, bool> MaterialBlocked;
        public bool CorrectBroken;
    }

    private static readonly List<Material> MaterialScratch = new List<Material>(16);

    public static void MaterialCorrection(IList<Renderer> renderers, Shader fallbackUrpShader, bool correctBroken = true, bool applyBlocklist = false)
    {
        if (renderers == null) return;
        int count = renderers.Count;
        if (count == 0) return;
        if (fallbackUrpShader == null)
        {
            Debug.LogWarning("MaterialCorrection: fallbackUrpShader is null, cannot swap shaders.");
            return;
        }
        CorrectionPass pass = CreatePass(fallbackUrpShader, correctBroken, applyBlocklist, count);
        for (int i = 0; i < count; i++)
        {
            CorrectRenderer(renderers[i], ref pass);
        }
    }

    public static void MaterialCorrection(Renderer renderer, Shader fallbackUrpShader, bool correctBroken = true, bool applyBlocklist = false)
    {
        if (renderer == null) return;
        if (fallbackUrpShader == null)
        {
            Debug.LogWarning("MaterialCorrection: fallbackUrpShader is null, cannot swap shaders.");
            return;
        }
        CorrectionPass pass = CreatePass(fallbackUrpShader, correctBroken, applyBlocklist, 1);
        CorrectRenderer(renderer, ref pass);
    }

    private static CorrectionPass CreatePass(Shader fallbackUrpShader, bool correctBroken, bool applyBlocklist, int capacity)
    {
        CorrectionPass pass = default;
        pass.FallbackShader = fallbackUrpShader;
        pass.CorrectBroken = correctBroken;
        pass.FixedMaterials = new Dictionary<Material, Material>(capacity);
        if (correctBroken)
        {
            pass.ShaderBroken = new Dictionary<Shader, bool>(capacity);
        }
        if (applyBlocklist && HasBlocklist)
        {
            pass.ShaderNameBlocked = new Dictionary<Shader, bool>(capacity);
            pass.MaterialBlocked = new Dictionary<Material, bool>(capacity);
        }
        return pass;
    }

    private static void CorrectRenderer(Renderer renderer, ref CorrectionPass pass)
    {
        if (renderer == null) return;
        List<Material> materials = MaterialScratch;
        renderer.GetSharedMaterials(materials);
        int matCount = materials.Count;
        if (matCount == 0) return;

        bool anyChanged = false;

        for (int mi = 0; mi < matCount; mi++)
        {
            var mat = materials[mi];
            if (mat == null) continue;

            if (pass.FixedMaterials.TryGetValue(mat, out Material cachedFixed))
            {
                materials[mi] = cachedFixed;
                anyChanged = true;
                continue;
            }

            var shader = mat.shader;
            if (shader == null) continue;

            bool replace = false;
            if (pass.CorrectBroken)
            {
                if (!pass.ShaderBroken.TryGetValue(shader, out bool isBroken))
                {
                    isBroken = !shader.isSupported || (!string.IsNullOrEmpty(shader.name) && shader.name.Contains("InternalErrorShader"));
                    pass.ShaderBroken[shader] = isBroken;
                }
                replace = isBroken;
            }
            if (!replace && pass.MaterialBlocked != null)
            {
                if (!pass.MaterialBlocked.TryGetValue(mat, out bool isBlocked))
                {
                    isBlocked = IsBlocked(mat, shader, pass.ShaderNameBlocked);
                    pass.MaterialBlocked[mat] = isBlocked;
                    if (isBlocked)
                    {
                        Debug.Log($"Shader blocklist matched material '{mat.name}' (shader '{shader.name}'), swapping to fallback.");
                    }
                }
                replace = isBlocked;
            }
            if (!replace) continue;

            var fixedMat = BuildFallbackMaterial(mat, pass.FallbackShader);
            pass.FixedMaterials[mat] = fixedMat;
            materials[mi] = fixedMat;
            anyChanged = true;
        }
        if (anyChanged)
        {
            renderer.SetSharedMaterials(materials);
        }
    }

    private static Material BuildFallbackMaterial(Material src, Shader fallbackUrpShader)
    {
        bool hasAlbedo = TryGetFirstTextureWithScaleAndOffset(src, AlbedoProps, out BasisTexTransform albedo, out _);
        bool hasNormal = TryGetFirstTextureWithScaleAndOffset(src, NormalProps, out BasisTexTransform normal, out _);
        bool hasMetal = TryGetFirstTextureWithScaleAndOffset(src, MetallicProps, out BasisTexTransform metal, out _);
        bool hasOcc = TryGetFirstTextureWithScaleAndOffset(src, OcclusionProps, out BasisTexTransform occ, out _);
        bool hasColor = TryGetFirstColor(src, out Color baseColor, out _);
        var fixedMat = new Material(fallbackUrpShader)
        {
            name = src.name + " (Fixed)"
        };
        if (hasAlbedo)
        {
            fixedMat.SetTexture("_BaseMap", albedo.texture);
            fixedMat.SetTextureScale("_BaseMap", albedo.scale);
            fixedMat.SetTextureOffset("_BaseMap", albedo.offset);
        }
        if (hasColor)
        {
            fixedMat.SetColor("_BaseColor", baseColor);
        }
        if (hasNormal)
        {
            fixedMat.EnableKeyword("_NORMALMAP");
            fixedMat.SetTexture("_BumpMap", normal.texture);
            fixedMat.SetTextureScale("_BumpMap", normal.scale);
            fixedMat.SetTextureOffset("_BumpMap", normal.offset);
            fixedMat.SetFloat("_BumpScale", 0.2f);
        }
        if (hasMetal)
        {
            fixedMat.EnableKeyword("_METALLICSPECGLOSSMAP");
            fixedMat.SetTexture("_MetallicGlossMap", metal.texture);
            fixedMat.SetTextureScale("_MetallicGlossMap", metal.scale);
            fixedMat.SetTextureOffset("_MetallicGlossMap", metal.offset);
        }
        fixedMat.SetFloat("_Metallic", 0.2f);
        fixedMat.SetFloat("_Smoothness", 0.2f);
        if (hasOcc)
        {
            fixedMat.SetTexture("_OcclusionMap", occ.texture);
            fixedMat.SetTextureScale("_OcclusionMap", occ.scale);
            fixedMat.SetTextureOffset("_OcclusionMap", occ.offset);

            fixedMat.SetFloat("_OcclusionStrength", 0.2f);
        }
        return fixedMat;
    }
}
