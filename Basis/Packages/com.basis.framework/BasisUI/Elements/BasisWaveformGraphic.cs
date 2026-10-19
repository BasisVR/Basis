using Unity.Scripting.LifecycleManagement;
using System;
using Basis.BasisUI.Styling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    [RequireComponent(typeof(CanvasRenderer))]
    public partial class BasisWaveformGraphic : MaskableGraphic
    {
        public const float DefaultHeight = 96f;

        private const float WriteStep = 1f / 128f;
        private const float ColumnGap = 1f;
        private const float VerticalPadding = 6f;
        private const float MinimumBarHeight = 1f;
        private const float ClippingLevel = 0.96f;
        private const float HotLevel = 0.88f;
        private const float QuietLevel = 0.30f;

        private float[] peaks = Array.Empty<float>();
        private float[] bodies = Array.Empty<float>();
        private int columns;
        private float playhead = -1f;

        public const string UiShaderName = "Basis/UI/Main";

        private static readonly int ZTestProperty = Shader.PropertyToID("_ZTest");
        private static Material overlayMaterial;

        /// <summary>
        /// The menu's own UI material (Queue=Overlay, ZTest Always). A runtime Graphic left with a
        /// null material falls back to UI/Default, which depth-tests and so does not draw on top.
        /// </summary>
        public static Material ResolveOverlayMaterial()
        {
            if (overlayMaterial != null) return overlayMaterial;

            Shader shader = Shader.Find(UiShaderName);
            bool fallback = shader == null;
            if (fallback) shader = Shader.Find("UI/Default");
            if (shader == null) return null;

            overlayMaterial = new Material(shader) { name = "Basis Waveform UI", hideFlags = HideFlags.HideAndDontSave };
            if (fallback) overlayMaterial.SetInt(ZTestProperty, (int)CompareFunction.Always);
            return overlayMaterial;
        }

        public static BasisWaveformGraphic Create(RectTransform parent, float height = DefaultHeight)
        {
            GameObject hosting = new GameObject("Waveform", typeof(RectTransform), typeof(CanvasRenderer));
            hosting.layer = parent != null ? parent.gameObject.layer : hosting.layer;
            RectTransform rect = (RectTransform)hosting.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(parent != null ? parent.rect.width : 0f, height);

            LayoutElement layout = hosting.AddComponent<LayoutElement>();
            layout.preferredHeight = height;
            layout.minHeight = height;
            layout.flexibleWidth = 1f;

            Canvas parentCanvas = parent != null ? parent.GetComponentInParent<Canvas>() : null;
            Canvas isolation = hosting.AddComponent<Canvas>();
            if (parentCanvas != null) isolation.additionalShaderChannels = parentCanvas.additionalShaderChannels;

            BasisWaveformGraphic graphic = hosting.AddComponent<BasisWaveformGraphic>();
            graphic.raycastTarget = false;
            graphic.material = ResolveOverlayMaterial();
            return graphic;
        }

        public void SetBars(float[] peakValues, float[] bodyValues, int count)
        {
            if (peakValues == null || bodyValues == null || count <= 0)
            {
                if (columns == 0) return;
                columns = 0;
                SetVerticesDirty();
                return;
            }

            if (count > peakValues.Length) count = peakValues.Length;
            if (count > bodyValues.Length) count = bodyValues.Length;

            if (peaks.Length < count)
            {
                peaks = new float[count];
                bodies = new float[count];
            }

            bool changed = columns != count;
            columns = count;

            for (int i = 0; i < count; i++)
            {
                float peak = Quantize(peakValues[i]);
                float body = Quantize(bodyValues[i]);
                if (peaks[i] != peak)
                {
                    peaks[i] = peak;
                    changed = true;
                }
                if (bodies[i] != body)
                {
                    bodies[i] = body;
                    changed = true;
                }
            }

            if (changed) SetVerticesDirty();
        }

        public void SetPlayhead(float normalized)
        {
            float value = normalized < 0f ? -1f : Quantize(normalized);
            if (value == playhead) return;
            playhead = value;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper helper)
        {
            helper.Clear();

            Rect rect = GetPixelAdjustedRect();
            if (rect.width <= 0f || rect.height <= 0f) return;

            UiStylePalette palette = UiStyleSettings.GetActivePalette();
            Color background = palette != null ? palette.InputFieldColor : new Color(0.13f, 0.13f, 0.15f);
            Color guide = palette != null ? palette.FontColor3 : new Color(0.65f, 0.67f, 0.69f);
            Color marker = palette != null ? palette.CautionColor : new Color(1f, 0.82f, 0.34f);

            AddQuad(helper, rect.xMin, rect.yMin, rect.xMax, rect.yMax, background);

            float centre = rect.center.y;
            float half = Mathf.Max(1f, rect.height * 0.5f - VerticalPadding);

            Color centreLine = guide;
            centreLine.a *= 0.35f;
            AddQuad(helper, rect.xMin, centre - 0.5f, rect.xMax, centre + 0.5f, centreLine);

            if (columns > 0)
            {
                float columnWidth = rect.width / columns;
                float barWidth = Mathf.Max(1f, columnWidth - ColumnGap);

                for (int i = 0; i < columns; i++)
                {
                    float left = rect.xMin + i * columnWidth;
                    float right = left + barWidth;

                    float peak = peaks[i] * half;
                    float body = bodies[i] * half;

                    Color level = ResolveLevelColour(peaks[i], palette, guide);
                    Color envelope = level;
                    envelope.a *= 0.35f;

                    if (peak >= MinimumBarHeight) AddQuad(helper, left, centre - peak, right, centre + peak, envelope);
                    if (body >= MinimumBarHeight) AddQuad(helper, left, centre - body, right, centre + body, level);
                }
            }

            if (playhead >= 0f)
            {
                float x = Mathf.Lerp(rect.xMin, rect.xMax, playhead);
                AddQuad(helper, x - 1f, rect.yMin, x + 1f, rect.yMax, marker);
            }
        }

        // Colour carries the verdict on each column so a bad take is visible without reading numbers:
        // red = peaking, amber = close to it, green = healthy, dim grey = too quiet to hear well.
        private static Color ResolveLevelColour(float peak, UiStylePalette palette, Color guide)
        {
            if (peak >= ClippingLevel) return palette != null ? palette.DangerColor : new Color(0.97f, 0.34f, 0.34f);
            if (peak >= HotLevel) return palette != null ? palette.CautionColor : new Color(1f, 0.82f, 0.34f);

            if (peak <= QuietLevel)
            {
                Color dim = guide;
                dim.a *= 0.7f;
                return dim;
            }

            return palette != null ? palette.SuccessColor : new Color(0.09f, 0.8f, 0.47f);
        }

        private static void AddQuad(VertexHelper helper, float left, float bottom, float right, float top, Color32 tint)
        {
            int index = helper.currentVertCount;
            helper.AddVert(new Vector3(left, bottom), tint, Vector2.zero);
            helper.AddVert(new Vector3(left, top), tint, Vector2.zero);
            helper.AddVert(new Vector3(right, top), tint, Vector2.zero);
            helper.AddVert(new Vector3(right, bottom), tint, Vector2.zero);
            helper.AddTriangle(index, index + 1, index + 2);
            helper.AddTriangle(index + 2, index + 3, index);
        }

        private static float Quantize(float value)
        {
            if (value <= 0f) return 0f;
            if (value >= 1f) return 1f;
            return Mathf.Round(value / WriteStep) * WriteStep;
        }
    }
}
