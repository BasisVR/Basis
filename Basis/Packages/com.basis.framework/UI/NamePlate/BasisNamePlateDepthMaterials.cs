using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.UI.NamePlate
{
    [AutoStaticsCleanup]
    public static partial class BasisNamePlateDepthMaterials
    {
        public const string ObjectKeyword = "BASIS_NAMEPLATE_OBJECT";
        public const string PanelShaderName = "Basis/NamePlate/Panel";
        public const string TextShaderName = "Basis/NamePlate/Text";
        public static float PullUnits = 50f;
        private static readonly int DepthPullId = Shader.PropertyToID("_BasisNamePlateDepthPull");
        private static float appliedPull = float.NaN;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int SheenId = Shader.PropertyToID("_Sheen");
        [NoAutoStaticsCleanup] private static readonly int[] BlendStateIds =
        {
            Shader.PropertyToID("_SrcBlend"),
            Shader.PropertyToID("_DstBlend"),
            Shader.PropertyToID("_SrcBlendAlpha"),
            Shader.PropertyToID("_DstBlendAlpha"),
            Shader.PropertyToID("_ZWrite"),
        };
        private static readonly Dictionary<Material, Material> surfaces = new();
        private static readonly Dictionary<Material, Material> texts = new();
        private static Shader panelShader, textShader;
        private static bool shadersResolved;
        public static void UpdateDepthPull(float plateWorldScale)
        {
            float pull = PullUnits * plateWorldScale;
            if (pull == appliedPull) return;
            appliedPull = pull;
            Shader.SetGlobalFloat(DepthPullId, pull);
        }
        public static Material Surface(Material source)
        {
            if (source == null || IsDepthMaterial(source)) return source;
            if (surfaces.TryGetValue(source, out Material existing) && existing != null) return existing;
            ResolveShaders();
            if (panelShader == null) return source;
            Material created = CreateSurface(panelShader, source);
            surfaces[source] = created;
            return created;
        }
        public static Material Text(Material source)
        {
            if (source == null || IsDepthMaterial(source)) return source;
            if (texts.TryGetValue(source, out Material existing) && existing != null) return existing;
            ResolveShaders();
            if (textShader == null) return source;
            Material created = CreateText(textShader, source);
            texts[source] = created;
            return created;
        }
        public static void Apply(TMP_Text text)
        {
            if (text == null) return;
            Material current = text.fontSharedMaterial;
            Material depth = Text(current);
            if (depth != current) text.fontSharedMaterial = depth;
        }
        public static bool IsDepthMaterial(Material material) => material != null && material.IsKeywordEnabled(ObjectKeyword);
        public static Material CreateSurface(Shader shader, Material source)
        {
            bool opaque = source.renderQueue <= (int)RenderQueue.GeometryLast;
            Material material = new Material(shader) { name = source.name + " (Plate Depth)" };
            material.EnableKeyword(ObjectKeyword);
            Color color = source.HasProperty(BaseColorId) ? source.GetColor(BaseColorId) : Color.white;
            if (opaque) color.a = 1f;
            material.SetColor(BaseColorId, color);
            material.SetFloat(SheenId, 0f);
            for (int i = 0; i < BlendStateIds.Length; i++)
            {
                if (source.HasProperty(BlendStateIds[i])) material.SetFloat(BlendStateIds[i], source.GetFloat(BlendStateIds[i]));
            }
            material.renderQueue = opaque ? (int)RenderQueue.GeometryLast + 1 : source.renderQueue;
            return material;
        }
        public static Material CreateText(Shader shader, Material source)
        {
            Material material = new Material(source) { name = source.name + " (Plate Depth)" };
            material.shader = shader;
            material.EnableKeyword(ObjectKeyword);
            return material;
        }
        public static void Dispose()
        {
            foreach (Material material in surfaces.Values)
            {
                if (material != null) Object.Destroy(material);
            }
            foreach (Material material in texts.Values)
            {
                if (material != null) Object.Destroy(material);
            }
            surfaces.Clear();
            texts.Clear();
            appliedPull = float.NaN;
        }
        private static void ResolveShaders()
        {
            if (shadersResolved) return;
            shadersResolved = true;
            panelShader = Shader.Find(PanelShaderName);
            textShader = Shader.Find(TextShaderName);
        }
    }
}
