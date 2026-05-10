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

    public static void MaterialCorrection(IList<Renderer> renderers, Shader fallbackUrpShader)
    {
        if (renderers == null) return;
        int count = renderers.Count;
        if (count == 0) return;
        if (fallbackUrpShader == null)
        {
            Debug.LogWarning("MaterialCorrection: fallbackUrpShader is null, cannot swap shaders.");
            return;
        }
        var fixedMaterials = new Dictionary<Material, Material>(count);
        var shaderBroken = new Dictionary<Shader, bool>(count);
        for (int i = 0; i < count; i++)
        {
            CorrectRenderer(renderers[i], fallbackUrpShader, fixedMaterials, shaderBroken);
        }
    }

    public static void MaterialCorrection(Renderer renderer, Shader fallbackUrpShader)
    {
        if (renderer == null) return;
        if (fallbackUrpShader == null)
        {
            Debug.LogWarning("MaterialCorrection: fallbackUrpShader is null, cannot swap shaders.");
            return;
        }
        var fixedMaterials = new Dictionary<Material, Material>();
        var shaderBroken = new Dictionary<Shader, bool>();
        CorrectRenderer(renderer, fallbackUrpShader, fixedMaterials, shaderBroken);
    }

    private static void CorrectRenderer(Renderer renderer, Shader fallbackUrpShader, Dictionary<Material, Material> fixedMaterials, Dictionary<Shader, bool> shaderBroken)
    {
        if (renderer == null) return;
        var materials = renderer.sharedMaterials;
        if (materials == null || materials.Length == 0) return;

        bool anyChanged = false;

        for (int mi = 0; mi < materials.Length; mi++)
        {
            var mat = materials[mi];
            if (mat == null) continue;

            if (fixedMaterials.TryGetValue(mat, out Material cachedFixed))
            {
                materials[mi] = cachedFixed;
                anyChanged = true;
                continue;
            }

            var shader = mat.shader;
            if (shader == null) continue;

            if (!shaderBroken.TryGetValue(shader, out bool isBroken))
            {
                isBroken = !shader.isSupported || (!string.IsNullOrEmpty(shader.name) && shader.name.Contains("InternalErrorShader"));
                shaderBroken[shader] = isBroken;
            }
            if (!isBroken) continue;

            var fixedMat = BuildFallbackMaterial(mat, fallbackUrpShader);
            fixedMaterials[mat] = fixedMat;
            materials[mi] = fixedMat;
            anyChanged = true;
        }
        if (anyChanged)
        {
            renderer.sharedMaterials = materials;
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
