using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

public static class BasisVrmMaterials
{
    public const string ShaderAddress = "Packages/com.basis.bundlemanagement/VrmMToon/Shaders/vrmc_materials_mtoon_urp.shader";
    private static Shader shader;
    private static Task<Shader> loading;
    public static Shader MToonShader => shader;
    public static Task<Shader> LoadShaderAsync()
    {
        if (shader != null) return Task.FromResult(shader);
        if (loading == null || loading.IsCompleted) loading = LoadShader();
        return loading;
    }
    private static async Task<Shader> LoadShader()
    {
        try
        {
            AsyncOperationHandle<Shader> handle = Addressables.LoadAssetAsync<Shader>(ShaderAddress);
            shader = Application.isPlaying ? await handle.Task : handle.WaitForCompletion();
            if (handle.Status != AsyncOperationStatus.Succeeded || shader == null) BasisDebug.LogError("VRM MToon shader failed to load from Addressables; VRM avatars keep their glTF materials.");
        }
        catch (Exception exception)
        {
            BasisDebug.LogError($"VRM MToon shader failed to load from Addressables ({exception.Message}); VRM avatars keep their glTF materials.");
        }
        return shader;
    }
    public static void Apply(GltfImport gltf, BasisVrmDocument document, List<(Renderer Renderer, int[] Materials)> slots, List<Object> owned)
    {
        if (document.Materials.Count == 0 || slots.Count == 0 || MToonShader == null) return;
        Dictionary<int, Material> built = new Dictionary<int, Material>();
        foreach (KeyValuePair<int, BasisVrmDocument.MToon> entry in document.Materials)
        {
            Material original = gltf.GetMaterial(entry.Key);
            Material material = Create(gltf, entry.Value, original != null ? original.name : "VRM MToon " + entry.Key);
            built[entry.Key] = material;
            owned.Add(material);
        }
        foreach ((Renderer renderer, int[] indices) in slots)
        {
            if (renderer == null || indices == null) continue;
            Material[] materials = renderer.sharedMaterials;
            bool changed = false;
            for (int Index = 0; Index < materials.Length && Index < indices.Length; Index++)
            {
                if (!built.TryGetValue(indices[Index], out Material material)) continue;
                materials[Index] = material;
                changed = true;
            }
            if (changed) renderer.sharedMaterials = materials;
        }
    }
    private static Material Create(GltfImport gltf, BasisVrmDocument.MToon m, string name)
    {
        Material material = new Material(MToonShader) { name = name };
        bool linearProject = QualitySettings.activeColorSpace == ColorSpace.Linear;
        material.SetColor("_Color", m.BaseColor);
        Texture2D main = Texture(gltf, m.BaseTex);
        material.SetTexture("_MainTex", main);
        material.SetTextureScale("_MainTex", new Vector2(m.MainST.x, m.MainST.y));
        material.SetTextureOffset("_MainTex", new Vector2(m.MainST.z, m.MainST.w));
        material.SetColor("_ShadeColor", m.ShadeColor);
        Texture2D shade = Texture(gltf, m.ShadeTex);
        material.SetTexture("_ShadeTex", shade);
        material.SetFloat("_BasisShadeTexLinear", linearProject && shade != null && !shade.isDataSRGB ? 1f : 0f);
        Texture2D normal = Texture(gltf, m.NormalTex);
        material.SetTexture("_BumpMap", normal);
        material.SetFloat("_BumpScale", m.NormalScale);
        material.SetFloat("_ShadingShiftFactor", Mathf.Clamp(m.ShadingShift, -1f, 1f));
        Texture2D shadingShift = Texture(gltf, m.ShadingShiftTex);
        material.SetTexture("_ShadingShiftTex", shadingShift);
        material.SetFloat("_ShadingShiftTexScale", m.ShadingShiftScale);
        material.SetFloat("_ShadingToonyFactor", Mathf.Clamp01(m.ShadingToony));
        material.SetFloat("_GiEqualization", Mathf.Clamp01(m.GiEqualization));
        material.SetColor("_EmissionColor", m.Emission);
        Texture2D emission = Texture(gltf, m.EmissionTex);
        material.SetTexture("_EmissionMap", emission);
        material.SetColor("_MatcapColor", m.Matcap);
        Texture2D matcap = Texture(gltf, m.MatcapTex);
        material.SetTexture("_MatcapTex", matcap);
        material.SetFloat("_BasisMatcapTexLinear", linearProject && matcap != null && !matcap.isDataSRGB ? 1f : 0f);
        material.SetColor("_RimColor", m.Rim);
        material.SetFloat("_RimFresnelPower", Mathf.Max(0f, m.RimPower));
        material.SetFloat("_RimLift", Mathf.Clamp01(m.RimLift));
        Texture2D rim = Texture(gltf, m.RimTex);
        material.SetTexture("_RimTex", rim);
        material.SetFloat("_BasisRimTexLinear", linearProject && rim != null && !rim.isDataSRGB ? 1f : 0f);
        material.SetFloat("_RimLightingMix", Mathf.Clamp01(m.RimMix));
        bool outline = m.OutlineMode != 0 && m.OutlineWidth > 0f;
        material.SetInt("_OutlineWidthMode", outline ? m.OutlineMode : 0);
        material.SetFloat("_OutlineWidth", Mathf.Max(0f, m.OutlineWidth));
        Texture2D outlineWidth = Texture(gltf, m.OutlineWidthTex);
        material.SetTexture("_OutlineWidthTex", outlineWidth);
        material.SetColor("_OutlineColor", m.OutlineColor);
        material.SetFloat("_OutlineLightingMix", Mathf.Clamp01(m.OutlineMix));
        Texture2D uvMask = Texture(gltf, m.UvMaskTex);
        material.SetTexture("_UvAnimMaskTex", uvMask);
        material.SetFloat("_UvAnimScrollXSpeed", m.ScrollX);
        material.SetFloat("_UvAnimScrollYSpeed", m.ScrollY);
        material.SetFloat("_UvAnimRotationSpeed", m.Rotation);
        material.SetFloat("_Cutoff", Mathf.Clamp01(m.Cutoff));
        material.SetInt("_DoubleSided", m.DoubleSided ? 1 : 0);
        material.SetFloat("_M_CullMode", m.DoubleSided ? (float)CullMode.Off : (float)CullMode.Back);
        int offset;
        switch (m.AlphaMode)
        {
            case "MASK":
                material.SetInt("_AlphaMode", 1);
                material.SetOverrideTag("RenderType", "TransparentCutout");
                SetBlend(material, BlendMode.One, BlendMode.Zero, true, true);
                material.renderQueue = (int)RenderQueue.AlphaTest;
                offset = 0;
                break;
            case "BLEND":
                material.SetInt("_AlphaMode", 2);
                material.SetOverrideTag("RenderType", "Transparent");
                SetBlend(material, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, m.TransparentWithZWrite, false);
                if (m.TransparentWithZWrite)
                {
                    offset = Mathf.Clamp(m.QueueOffset, 0, 9);
                    material.renderQueue = (int)RenderQueue.GeometryLast + 1 + offset;
                }
                else
                {
                    offset = Mathf.Clamp(m.QueueOffset, -9, 0);
                    material.renderQueue = (int)RenderQueue.Transparent + offset;
                }
                break;
            default:
                material.SetInt("_AlphaMode", 0);
                material.SetOverrideTag("RenderType", "Opaque");
                SetBlend(material, BlendMode.One, BlendMode.Zero, true, false);
                material.renderQueue = (int)RenderQueue.Geometry;
                offset = 0;
                break;
        }
        material.SetInt("_TransparentWithZWrite", m.TransparentWithZWrite ? 1 : 0);
        material.SetInt("_RenderQueueOffset", offset);
        Keyword(material, "_ALPHATEST_ON", m.AlphaMode == "MASK");
        Keyword(material, "_ALPHABLEND_ON", m.AlphaMode == "BLEND");
        Keyword(material, "_NORMALMAP", normal != null);
        Keyword(material, "_MTOON_EMISSIVEMAP", emission != null);
        Keyword(material, "_MTOON_RIMMAP", matcap != null || rim != null);
        Keyword(material, "_MTOON_PARAMETERMAP", shadingShift != null || outlineWidth != null || uvMask != null);
        Keyword(material, "_MTOON_OUTLINE_WORLD", outline && m.OutlineMode == 1);
        Keyword(material, "_MTOON_OUTLINE_SCREEN", outline && m.OutlineMode == 2);
        material.SetShaderPassEnabled("SRPDefaultUnlit", outline);
        return material;
    }
    private static void SetBlend(Material material, BlendMode source, BlendMode destination, bool zWrite, bool alphaToMask)
    {
        material.SetFloat("_M_SrcBlend", (float)source);
        material.SetFloat("_M_DstBlend", (float)destination);
        material.SetFloat("_M_ZWrite", zWrite ? 1f : 0f);
        material.SetFloat("_M_AlphaToMask", alphaToMask ? 1f : 0f);
    }
    private static void Keyword(Material material, string keyword, bool enabled)
    {
        if (enabled) material.EnableKeyword(keyword);
        else material.DisableKeyword(keyword);
    }
    private static Texture2D Texture(GltfImport gltf, int index) => index >= 0 ? gltf.GetTexture(index) : null;
}
