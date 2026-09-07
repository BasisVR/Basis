using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public sealed class BasisPerformanceBarView : MonoBehaviour
    {
        private const string ShaderName = "Basis/UI/PerformanceBar";
        private const float DefaultHeight = 22f;
        private const int TextureWidth = 512;
        private const int TextureHeight = 32;
        private const int MaxSegments = 9;

        private static readonly int idSegments = Shader.PropertyToID("_Segments");
        private static readonly int idColors = Shader.PropertyToID("_Colors");
        private static readonly int idFillFraction = Shader.PropertyToID("_FillFraction");
        private static readonly int idOverBudget = Shader.PropertyToID("_OverBudget");
        private static readonly int idGap = Shader.PropertyToID("_Gap");
        private static readonly int idBackground = Shader.PropertyToID("_Background");

        // com.claude.dataviz categorical palette (dark-mode steps), validated adjacent-pair CVD/contrast
        // via scripts/validate_palette.js. "Other" deliberately does not take a categorical slot - it is
        // a catch-all, not an identity, so it gets the muted ink color instead of a generated hue.
        public static readonly Color[] GpuPalette =
        {
            new Color32(0x39, 0x87, 0xe5, 255), new Color32(0xd9, 0x59, 0x26, 255), new Color32(0x19, 0x9e, 0x70, 255),
            new Color32(0xc9, 0x85, 0x00, 255), new Color32(0xd5, 0x51, 0x81, 255), new Color32(0x00, 0x83, 0x00, 255),
            new Color32(0x89, 0x87, 0x81, 255),
        };
        public static readonly Color[] CpuPalette =
        {
            new Color32(0x39, 0x87, 0xe5, 255), new Color32(0xd9, 0x59, 0x26, 255), new Color32(0x19, 0x9e, 0x70, 255),
            new Color32(0xc9, 0x85, 0x00, 255), new Color32(0xd5, 0x51, 0x81, 255), new Color32(0x00, 0x83, 0x00, 255),
            new Color32(0x90, 0x85, 0xe9, 255), new Color32(0xe6, 0x67, 0x67, 255), new Color32(0x89, 0x87, 0x81, 255),
        };

        private RawImage image;
        private Material material;
        private RenderTexture target;
        private bool forGpu;
        private long lastPublished = -1;
        private readonly Vector4[] segmentScratch = new Vector4[MaxSegments];
        private readonly Vector4[] colorScratch = new Vector4[MaxSegments];

        public static BasisPerformanceBarView Create(RectTransform parent, bool forGpu, float height = DefaultHeight)
        {
            GameObject hosting = new GameObject(forGpu ? "GPU Bar" : "CPU Bar",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            // Stays inactive until forGpu/image are set below - AddComponent invokes OnEnable
            // synchronously if the object is already active, which would run EnsureResources
            // before those fields exist.
            hosting.SetActive(false);
            hosting.layer = parent != null ? parent.gameObject.layer : hosting.layer;

            RectTransform rect = (RectTransform)hosting.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);

            RawImage raw = hosting.GetComponent<RawImage>();
            raw.raycastTarget = false;
            raw.color = Color.white;
            raw.material = BasisWaveformGraphic.ResolveOverlayMaterial();

            BasisPerformanceBarView view = hosting.AddComponent<BasisPerformanceBarView>();
            view.image = raw;
            view.forGpu = forGpu;

            hosting.SetActive(true);
            return view;
        }

        private void OnEnable()
        {
            EnsureResources();
            BasisPerformanceBarData.AddSubscriber();
            BasisFrameClock.AddRequest();
            BasisFrameClock.OnTick += OnFrameTick;
            lastPublished = -1;
            Redraw();
        }

        private void OnDisable()
        {
            BasisFrameClock.OnTick -= OnFrameTick;
            BasisFrameClock.RemoveRequest();
            BasisPerformanceBarData.RemoveSubscriber();
        }

        private void OnDestroy()
        {
            if (target != null) { target.Release(); Destroy(target); target = null; }
            if (material != null) { Destroy(material); material = null; }
            if (image != null) image.texture = null;
        }

        private bool EnsureResources()
        {
            if (material != null && target != null) return true;
            if (image == null) image = GetComponent<RawImage>();
            if (image == null) return false;

            Shader shader = Shader.Find(ShaderName);
            if (shader == null) return false;

            material = new Material(shader) { name = forGpu ? "Performance Bar GPU" : "Performance Bar CPU", hideFlags = HideFlags.HideAndDontSave };
            target = new RenderTexture(TextureWidth, TextureHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = material.name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave,
            };
            target.Create();
            image.texture = target;
            PushPalette();
            return true;
        }

        private void PushPalette()
        {
            Color[] palette = forGpu ? GpuPalette : CpuPalette;
            for (int i = 0; i < MaxSegments; i++)
            {
                colorScratch[i] = i < palette.Length ? (Vector4)palette[i] : Vector4.zero;
            }
            material.SetVectorArray(idColors, colorScratch);
            material.SetColor(idBackground, new Color(0f, 0f, 0f, 0.25f));
            material.SetFloat(idGap, 2f / TextureWidth);
        }

        private void OnFrameTick()
        {
            BasisPerformanceBarData.Sample();
            long published = BasisPerformanceBarData.Published;
            if (published == lastPublished) return;
            lastPublished = published;
            Redraw();
        }

        private void Redraw()
        {
            if (!EnsureResources()) return;

            float[] ms = forGpu ? BasisPerformanceBarData.GpuMs : BasisPerformanceBarData.CpuMs;
            float targetMs = BasisPerformanceBarData.TargetMs;
            float total = 0f;
            for (int i = 0; i < ms.Length; i++) total += ms[i];

            float cursor = 0f;
            for (int i = 0; i < MaxSegments; i++)
            {
                if (i >= ms.Length || total <= 0f)
                {
                    segmentScratch[i] = new Vector4(2f, 2f, 0f, 0f);
                    continue;
                }
                float start = cursor;
                cursor = i == ms.Length - 1 ? 1f : cursor + ms[i] / total;
                segmentScratch[i] = new Vector4(start, cursor, 0f, 0f);
            }

            material.SetVectorArray(idSegments, segmentScratch);
            float fill = targetMs > 0f ? Mathf.Clamp01(total / targetMs) : 0f;
            material.SetFloat(idFillFraction, fill);
            material.SetFloat(idOverBudget, targetMs > 0f && total > targetMs ? 1f : 0f);

            Graphics.Blit(Texture2D.whiteTexture, target, material);
        }
    }
}
