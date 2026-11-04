#if !BASIS_DISABLE_MICROPHONE
using System.Globalization;
using System.Text;
using Basis.BasisUI.Styling;
using Basis.Scripts.Drivers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class BasisMicrophoneWaveformView : MonoBehaviour
    {
        public const string ShaderName = "Basis/UI/MicrophoneWaveform";
        public const float DefaultHeight = 240f;
        public const float LegendHeight = 44f;

        private const float StallSeconds = 0.3f;
        private const float LegendInterval = 0.2f;
        private const float FocusSeconds = 2.5f;
        private const float FocusDim = 0.3f;
        private const float FocusRate = 8f;
        private const float EdgePixels = 4f;
        private const float LegendFontScale = 0.9f;
        private const float LabelFontScale = 0.8f;
        private const float LabelWidth = 520f;
        private const float LabelHeight = 24f;
        private const float LabelGap = 2f;
        private const float LabelInset = 10f;
        private const string LegendGap = "     ";
        private const string LabelBackdrop = "<mark=#0000008C>";
        private const int MinimumTextureWidth = 256;
        private const int MaximumTextureWidth = 2048;
        private const int MinimumTextureHeight = 64;
        private const int MaximumTextureHeight = 512;
        private const int StageDenoiser = 0;
        private const int StageGate = 1;
        private const int StageAgc = 2;
        private const int StageLimiter = 3;

        private static readonly Color DenoiserColour = new Color(0.68f, 0.45f, 0.95f);

        private static readonly int DataProperty = Shader.PropertyToID("_Data");
        private static readonly int BackgroundProperty = Shader.PropertyToID("_Background");
        private static readonly int GuideProperty = Shader.PropertyToID("_Guide");
        private static readonly int SentProperty = Shader.PropertyToID("_Sent");
        private static readonly int DenoiserProperty = Shader.PropertyToID("_Denoiser");
        private static readonly int GateProperty = Shader.PropertyToID("_Gate");
        private static readonly int AgcProperty = Shader.PropertyToID("_Agc");
        private static readonly int LimiterProperty = Shader.PropertyToID("_Limiter");
        private static readonly int FocusProperty = Shader.PropertyToID("_Focus");
        private static readonly int BoostProperty = Shader.PropertyToID("_Boost");
        private static readonly int PixelProperty = Shader.PropertyToID("_Pixel");
        private static readonly int OldestProperty = Shader.PropertyToID("_Oldest");
        private static readonly int PadProperty = Shader.PropertyToID("_Pad");
        private static readonly int GainSpanProperty = Shader.PropertyToID("_GainSpan");
        private static readonly int GateModeProperty = Shader.PropertyToID("_GateMode");
        private static readonly int GateLevelProperty = Shader.PropertyToID("_GateLevel");
        private static readonly int AgcOnProperty = Shader.PropertyToID("_AgcOn");
        private static readonly int AgcTargetProperty = Shader.PropertyToID("_AgcTarget");
        private static readonly int LimitLevelProperty = Shader.PropertyToID("_LimitLevel");

        private readonly StringBuilder legendText = new StringBuilder(256);
        private readonly TextMeshProUGUI[] labelOrder = new TextMeshProUGUI[3];
        private readonly float[] levelOrder = new float[3];
        private RawImage image;
        private TextMeshProUGUI legend;
        private TextMeshProUGUI gateLabel;
        private TextMeshProUGUI targetLabel;
        private TextMeshProUGUI limitLabel;
        private RenderTexture target;
        private Texture2D dataTexture;
        private ushort[] halfData;
        private Material material;
        private SMDMicrophone.MicSettings settings;
        private Vector4 focus = Vector4.one;
        private Vector4 boost = Vector4.zero;
        private Color lastBackground;
        private Color lastGuide;
        private Color lastAccent;
        private Color lastCaution;
        private Color lastSuccess;
        private Color lastDanger;
        private string sentHex;
        private string heldHex;
        private string denoiserHex;
        private string gateHex;
        private string agcHex;
        private string limiterHex;
        private string lastLegend;
        private long lastPublished = -1;
        private long lastCaptured = -1;
        private float stalledFor;
        private float legendTimer;
        private float focusUntil;
        private int focusStage = -1;
        private bool settingsDirty = true;
        private bool coloursPushed;
        private bool sizeDirty = true;
        private bool holdingCapture;
        private bool shaderMissingReported;

        public static BasisMicrophoneWaveformView Create(RectTransform parent, float height = DefaultHeight)
        {
            GameObject hosting = new GameObject("Microphone Waveform", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            RectTransform rect = Place(hosting, parent, height);

            RawImage raw = hosting.GetComponent<RawImage>();
            raw.raycastTarget = false;
            raw.color = Color.white;
            raw.material = BasisWaveformGraphic.ResolveOverlayMaterial();

            TMP_Text reference = FindReference(parent);
            RectTransform labelLayer = CreateIsolatedLayer("Microphone Waveform Labels", rect, parent);
            TextMeshProUGUI gate = CreateLabel(labelLayer, reference, "Gate Label");
            TextMeshProUGUI aim = CreateLabel(labelLayer, reference, "Target Label");
            TextMeshProUGUI limit = CreateLabel(labelLayer, reference, "Limit Label");
            TextMeshProUGUI legendLabel = CreateLegend(parent, reference);

            BasisMicrophoneWaveformView view = hosting.AddComponent<BasisMicrophoneWaveformView>();
            view.gateLabel = gate;
            view.targetLabel = aim;
            view.limitLabel = limit;
            view.legend = legendLabel;
            view.UpdateText();
            return view;
        }

        public void Preview(SMDMicrophone.MicSettings preview) => OnSettingsChanged(preview);

        private static RectTransform Place(GameObject hosting, RectTransform parent, float height)
        {
            hosting.layer = parent != null ? parent.gameObject.layer : hosting.layer;

            RectTransform rect = (RectTransform)hosting.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(parent != null ? parent.rect.width : 0f, height);

            LayoutElement layout = hosting.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;
            layout.flexibleWidth = 1f;
            return rect;
        }

        private static TMP_Text FindReference(RectTransform parent)
        {
            PanelElementDescriptor host = parent != null ? parent.GetComponentInParent<PanelElementDescriptor>(true) : null;
            if (host == null) return null;
            return host.HasDescription ? host.DescriptionLabel : host.HasTitle ? host.TitleLabel : null;
        }

        private static RectTransform CreateIsolatedLayer(string name, RectTransform host, RectTransform canvasSource)
        {
            GameObject layer = new GameObject(name, typeof(RectTransform));
            layer.layer = host.gameObject.layer;

            RectTransform rect = (RectTransform)layer.transform;
            rect.SetParent(host, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Canvas parentCanvas = canvasSource != null ? canvasSource.GetComponentInParent<Canvas>() : null;
            Canvas isolation = layer.AddComponent<Canvas>();
            if (parentCanvas != null) isolation.additionalShaderChannels = parentCanvas.additionalShaderChannels;
            return rect;
        }

        private static TextMeshProUGUI CreateText(RectTransform parent, TMP_Text reference, string name, float scale)
        {
            GameObject hosting = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            hosting.layer = parent.gameObject.layer;
            ((RectTransform)hosting.transform).SetParent(parent, false);

            TextMeshProUGUI text = hosting.GetComponent<TextMeshProUGUI>();
            if (reference != null)
            {
                text.font = reference.font;
                text.fontSharedMaterial = reference.fontSharedMaterial;
                text.fontSize = reference.fontSize * scale;
                text.color = reference.color;
            }

            text.richText = true;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            return text;
        }

        private static TextMeshProUGUI CreateLabel(RectTransform layer, TMP_Text reference, string name)
        {
            TextMeshProUGUI text = CreateText(layer, reference, name, LabelFontScale);
            RectTransform rect = text.rectTransform;
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0f);
            rect.sizeDelta = new Vector2(LabelWidth, LabelHeight);
            text.alignment = TextAlignmentOptions.BottomRight;
            text.overflowMode = TextOverflowModes.Overflow;
            text.gameObject.SetActive(false);
            return text;
        }

        private static TextMeshProUGUI CreateLegend(RectTransform parent, TMP_Text reference)
        {
            GameObject holder = new GameObject("Microphone Waveform Legend", typeof(RectTransform));
            RectTransform holderRect = Place(holder, parent, LegendHeight);
            Canvas parentCanvas = parent != null ? parent.GetComponentInParent<Canvas>() : null;
            Canvas isolation = holder.AddComponent<Canvas>();
            if (parentCanvas != null) isolation.additionalShaderChannels = parentCanvas.additionalShaderChannels;

            TextMeshProUGUI text = CreateText(holderRect, reference, "Text", LegendFontScale);
            RectTransform rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            text.alignment = TextAlignmentOptions.Left;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.margin = new Vector4(12f, 0f, 12f, 0f);
            return text;
        }

        private void OnEnable()
        {
            lastPublished = -1;
            lastCaptured = -1;
            stalledFor = 0f;
            legendTimer = LegendInterval;
            settings = SMDMicrophone.Current;
            settingsDirty = true;

            BasisMicrophoneWaveform.AddSubscriber();
            if (!holdingCapture)
            {
                holdingCapture = true;
                BasisLocalMicrophoneDriver.AddCaptureHold();
            }

            SMDMicrophone.OnMicrophoneSettingsChanged += OnSettingsChanged;
            BasisFrameClock.AddRequest();
            BasisFrameClock.OnTick += OnFrameTick;
            Redraw();
        }

        private void OnDisable()
        {
            BasisFrameClock.OnTick -= OnFrameTick;
            BasisFrameClock.RemoveRequest();
            SMDMicrophone.OnMicrophoneSettingsChanged -= OnSettingsChanged;
            BasisMicrophoneWaveform.RemoveSubscriber();

            if (!holdingCapture) return;
            holdingCapture = false;
            BasisLocalMicrophoneDriver.ReleaseCaptureHold();
        }

        private void OnDestroy()
        {
            if (image != null) image.texture = null;
            ReleaseTarget();

            if (dataTexture != null)
            {
                Destroy(dataTexture);
                dataTexture = null;
            }

            if (material != null)
            {
                Destroy(material);
                material = null;
            }
        }

        private void OnRectTransformDimensionsChange()
        {
            sizeDirty = true;
        }

        private void OnSettingsChanged(SMDMicrophone.MicSettings next)
        {
            int stage = ChangedStage(settings, next);
            settings = next;
            settingsDirty = true;
            lastLegend = null;
            if (stage < 0) return;

            focusStage = stage;
            focusUntil = Time.unscaledTime + FocusSeconds;
        }

        private static int ChangedStage(SMDMicrophone.MicSettings a, SMDMicrophone.MicSettings b)
        {
            if (a.UseDenoiser != b.UseDenoiser || a.DenoiseWet != b.DenoiseWet || a.DenoiseMakeupDb != b.DenoiseMakeupDb) return StageDenoiser;
            if (a.UseNoiseGate != b.UseNoiseGate || a.AutoNoiseGate != b.AutoNoiseGate || a.NoiseGateThreshold != b.NoiseGateThreshold || a.NoiseGateAttack != b.NoiseGateAttack || a.NoiseGateRelease != b.NoiseGateRelease) return StageGate;
            if (a.UseAGC != b.UseAGC || a.AgcMaxGainDb != b.AgcMaxGainDb || a.AgcAttack != b.AgcAttack || a.AgcRelease != b.AgcRelease) return StageAgc;
            if (a.LimitThreshold != b.LimitThreshold || a.LimitKnee != b.LimitKnee) return StageLimiter;
            return -1;
        }

        private void OnFrameTick()
        {
            float delta = Time.unscaledDeltaTime;

            long capturedNow = BasisMicrophoneWaveform.Captured;
            if (capturedNow != lastCaptured)
            {
                lastCaptured = capturedNow;
                stalledFor = 0f;
            }
            else
            {
                stalledFor += delta;
                if (stalledFor > StallSeconds) BasisMicrophoneWaveform.PushIdle(delta);
            }

            legendTimer += delta;
            if (legendTimer >= LegendInterval)
            {
                legendTimer = 0f;
                UpdateText();
            }

            bool focusMoved = StepFocus(delta);
            long publishedNow = BasisMicrophoneWaveform.Published;
            if (publishedNow == lastPublished && !focusMoved && !settingsDirty) return;

            lastPublished = publishedNow;
            Redraw();
        }

        private bool StepFocus(float delta)
        {
            Vector4 focusGoal = Vector4.one;
            Vector4 boostGoal = Vector4.zero;
            if (focusStage >= 0 && Time.unscaledTime < focusUntil)
            {
                focusGoal = new Vector4(FocusDim, FocusDim, FocusDim, FocusDim);
                focusGoal[focusStage] = 1f;
                boostGoal[focusStage] = 1f;
            }

            if (focus == focusGoal && boost == boostGoal) return false;

            float step = 1f - Mathf.Exp(-FocusRate * delta);
            focus = Vector4.Lerp(focus, focusGoal, step);
            boost = Vector4.Lerp(boost, boostGoal, step);
            if ((focus - focusGoal).sqrMagnitude < 1e-5f && (boost - boostGoal).sqrMagnitude < 1e-5f)
            {
                focus = focusGoal;
                boost = boostGoal;
            }
            return true;
        }

        private bool EnsureResources()
        {
            if (image == null) image = GetComponent<RawImage>();
            if (image == null) return false;

            Material overlay = BasisWaveformGraphic.ResolveOverlayMaterial();
            if (overlay != null && image.material != overlay) image.material = overlay;

            if (material == null)
            {
                Shader shader = Shader.Find(ShaderName);
                if (shader == null)
                {
                    if (!shaderMissingReported)
                    {
                        shaderMissingReported = true;
                        BasisDebug.LogError($"Shader '{ShaderName}' not found, the microphone waveform will not render.");
                    }
                    return false;
                }

                material = new Material(shader) { name = "Microphone Waveform", hideFlags = HideFlags.HideAndDontSave };
                coloursPushed = false;
                settingsDirty = true;
                sizeDirty = true;
                material.SetFloat(GainSpanProperty, 1f / -BasisMicrophoneWaveform.FloorDb);
                if (dataTexture != null) material.SetTexture(DataProperty, dataTexture);
            }

            if (dataTexture == null)
            {
                bool full = SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat);
                dataTexture = new Texture2D(BasisMicrophoneWaveform.Columns, BasisMicrophoneWaveform.Rows, full ? TextureFormat.RGBAFloat : TextureFormat.RGBAHalf, false, true)
                {
                    name = "Microphone Waveform Data",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                halfData = full ? null : new ushort[BasisMicrophoneWaveform.Data.Length];
                material.SetTexture(DataProperty, dataTexture);
            }

            if (sizeDirty || target == null)
            {
                sizeDirty = false;
                ResolveTextureSize(out int width, out int height);
                if (target == null || target.width != width || target.height != height)
                {
                    ReleaseTarget();
                    target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                    {
                        name = "Microphone Waveform",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        useMipMap = false,
                        autoGenerateMips = false,
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    target.Create();
                }

                material.SetVector(PixelProperty, new Vector4(1f / target.width, 1f / target.height, target.width, target.height));
                material.SetFloat(PadProperty, EdgePixels / (target.height * 0.5f));
            }

            if (image.texture != target) image.texture = target;
            return true;
        }

        private void ResolveTextureSize(out int width, out int height)
        {
            Rect rect = ((RectTransform)transform).rect;
            float scale = Application.isMobilePlatform ? 0.5f : 1f;
            float sourceWidth = rect.width > 1f ? rect.width : 1024f;
            float sourceHeight = rect.height > 1f ? rect.height : DefaultHeight;
            width = Mathf.Clamp(Mathf.RoundToInt(sourceWidth * scale), MinimumTextureWidth, MaximumTextureWidth);
            height = Mathf.Clamp(Mathf.RoundToInt(sourceHeight * scale), MinimumTextureHeight, MaximumTextureHeight);
        }

        private void ReleaseTarget()
        {
            if (target == null) return;
            if (image != null && image.texture == target) image.texture = null;
            target.Release();
            Destroy(target);
            target = null;
        }

        private void Redraw()
        {
            if (!EnsureResources()) return;

            UiStylePalette palette = UiStyleSettings.GetActivePalette();
            if (!coloursPushed || PaletteChanged(palette)) PushColours(palette);
            if (settingsDirty) PushSettings(settings);

            material.SetFloat(OldestProperty, BasisMicrophoneWaveform.Oldest);
            UploadData();
            material.SetVector(FocusProperty, focus);
            material.SetVector(BoostProperty, boost);

            Graphics.Blit(Texture2D.whiteTexture, target, material);
            PlaceLabels();
        }

        private void UploadData()
        {
            float[] source = BasisMicrophoneWaveform.Data;
            if (halfData == null)
            {
                dataTexture.SetPixelData(source, 0);
            }
            else
            {
                for (int i = 0; i < source.Length; i++) halfData[i] = Mathf.FloatToHalf(source[i]);
                dataTexture.SetPixelData(halfData, 0);
            }
            dataTexture.Apply(false, false);
        }

        private bool PaletteChanged(UiStylePalette palette)
        {
            if (palette == null) return false;
            return palette.InputFieldColor != lastBackground || palette.FontColor3 != lastGuide || palette.AccentColor != lastAccent
                || palette.CautionColor != lastCaution || palette.SuccessColor != lastSuccess || palette.DangerColor != lastDanger;
        }

        private void PushColours(UiStylePalette palette)
        {
            coloursPushed = true;

            Color background = palette != null ? palette.InputFieldColor : new Color(0.13f, 0.13f, 0.15f);
            Color guide = palette != null ? palette.FontColor3 : new Color(0.65f, 0.67f, 0.69f);
            Color accent = palette != null ? palette.AccentColor : new Color(0.14f, 0.46f, 0.93f);
            Color caution = palette != null ? palette.CautionColor : new Color(1f, 0.82f, 0.34f);
            Color success = palette != null ? palette.SuccessColor : new Color(0.09f, 0.8f, 0.47f);
            Color danger = palette != null ? palette.DangerColor : new Color(0.97f, 0.34f, 0.34f);

            lastBackground = background;
            lastGuide = guide;
            lastAccent = accent;
            lastCaution = caution;
            lastSuccess = success;
            lastDanger = danger;

            material.SetColor(BackgroundProperty, Color.Lerp(background, Color.black, 0.2f));
            material.SetColor(GuideProperty, guide);
            material.SetColor(SentProperty, accent);
            material.SetColor(DenoiserProperty, DenoiserColour);
            material.SetColor(GateProperty, caution);
            material.SetColor(AgcProperty, success);
            material.SetColor(LimiterProperty, danger);

            sentHex = ColorUtility.ToHtmlStringRGB(accent);
            heldHex = ColorUtility.ToHtmlStringRGB(guide);
            denoiserHex = ColorUtility.ToHtmlStringRGB(DenoiserColour);
            gateHex = ColorUtility.ToHtmlStringRGB(caution);
            agcHex = ColorUtility.ToHtmlStringRGB(success);
            limiterHex = ColorUtility.ToHtmlStringRGB(danger);
            lastLegend = null;
            UpdateText();
        }

        private void PushSettings(SMDMicrophone.MicSettings s)
        {
            settingsDirty = false;
            material.SetFloat(GateModeProperty, !s.UseNoiseGate ? 0f : s.AutoNoiseGate ? 2f : 1f);
            material.SetFloat(GateLevelProperty, BasisMicrophoneWaveform.ToUnit(s.NoiseGateThreshold));
            material.SetFloat(AgcOnProperty, s.UseAGC ? 1f : 0f);
            material.SetFloat(AgcTargetProperty, BasisMicrophoneWaveform.ToUnit(s.AgcTargetRms));
            material.SetFloat(LimitLevelProperty, BasisMicrophoneWaveform.ToUnit(s.LimitThreshold));
        }

        private static int NewestOffset()
        {
            int columns = BasisMicrophoneWaveform.Columns;
            return (BasisMicrophoneWaveform.Oldest + columns - 1) % columns * 4;
        }

        private static float Shift(float level, float gain) => level > 0.0005f ? Mathf.Clamp01(level + gain) : 0f;

        private void PlaceLabels()
        {
            if (gateLabel == null) return;

            float[] data = BasisMicrophoneWaveform.Data;
            int offset = NewestOffset();
            int stages = BasisMicrophoneWaveform.RowStride + offset;
            int meta = BasisMicrophoneWaveform.RowStride * 2 + offset;
            float gain = data[meta + 3] > 0f ? data[meta] / -BasisMicrophoneWaveform.FloorDb : 0f;

            SMDMicrophone.MicSettings s = settings;
            float gateLevel = s.AutoNoiseGate ? Shift(data[stages + 2], gain) : Shift(BasisMicrophoneWaveform.ToUnit(s.NoiseGateThreshold), gain);

            int count = 0;
            AddLabel(limitLabel, BasisMicrophoneWaveform.ToUnit(s.LimitThreshold), true, ref count);
            AddLabel(targetLabel, BasisMicrophoneWaveform.ToUnit(s.AgcTargetRms), s.UseAGC, ref count);
            AddLabel(gateLabel, gateLevel, s.UseNoiseGate && gateLevel > 0f, ref count);

            for (int i = 1; i < count; i++)
            {
                for (int j = i; j > 0 && levelOrder[j] > levelOrder[j - 1]; j--)
                {
                    (levelOrder[j], levelOrder[j - 1]) = (levelOrder[j - 1], levelOrder[j]);
                    (labelOrder[j], labelOrder[j - 1]) = (labelOrder[j - 1], labelOrder[j]);
                }
            }

            Rect rect = ((RectTransform)transform).rect;
            float half = rect.height * 0.5f;
            float span = half > EdgePixels ? 1f - EdgePixels / half : 1f;
            float rightCeiling = half;
            float leftCeiling = half;
            for (int i = 0; i < count; i++)
            {
                float line = levelOrder[i] * span * half;
                bool left = line + LabelGap + LabelHeight > half;
                float bottom = left ? line - LabelGap - LabelHeight : line + LabelGap;
                float ceiling = left ? leftCeiling : rightCeiling;
                if (bottom + LabelHeight > ceiling) bottom = ceiling - LabelHeight;
                if (left) leftCeiling = bottom;
                else rightCeiling = bottom;

                SetSide(labelOrder[i], left);
                RectTransform label = labelOrder[i].rectTransform;
                Vector2 position = new Vector2(left ? LabelInset : -LabelInset, bottom);
                if ((label.anchoredPosition - position).sqrMagnitude > 0.25f) label.anchoredPosition = position;
            }
        }

        private static void SetSide(TextMeshProUGUI label, bool left)
        {
            float side = left ? 0f : 1f;
            RectTransform rect = label.rectTransform;
            if (rect.anchorMin.x == side) return;
            rect.anchorMin = new Vector2(side, 0.5f);
            rect.anchorMax = new Vector2(side, 0.5f);
            rect.pivot = new Vector2(side, 0f);
            label.alignment = left ? TextAlignmentOptions.BottomLeft : TextAlignmentOptions.BottomRight;
        }

        private void AddLabel(TextMeshProUGUI label, float level, bool shown, ref int count)
        {
            if (label.gameObject.activeSelf != shown) label.gameObject.SetActive(shown);
            if (!shown) return;
            labelOrder[count] = label;
            levelOrder[count] = level;
            count++;
        }

        private void UpdateText()
        {
            if (legend == null || sentHex == null) return;

            SMDMicrophone.MicSettings s = settings;
            float[] data = BasisMicrophoneWaveform.Data;
            int offset = NewestOffset();
            int stages = BasisMicrophoneWaveform.RowStride + offset;
            int meta = BasisMicrophoneWaveform.RowStride * 2 + offset;
            bool valid = data[meta + 3] > 0f;
            int flags = Mathf.RoundToInt(data[meta + 1]);
            bool sentNow = valid && (flags & BasisMicrophoneWaveform.FlagSent) != 0;
            bool gateNow = valid && (flags & BasisMicrophoneWaveform.FlagGate) != 0 && data[meta + 2] < 0.95f && data[stages] > 0f;
            bool denoiseNow = valid && (flags & BasisMicrophoneWaveform.FlagDenoiser) != 0 && data[offset + 1] - data[stages] > 3f / -BasisMicrophoneWaveform.FloorDb;
            bool limitNow = valid && data[stages + 1] > data[offset + 2] + 0.003f;
            bool muted = BasisLocalMicrophoneDriver.isPaused && !BasisLocalMicrophoneDriver.MuteBypassed;

            legendText.Clear();
            AppendLegend(sentHex, BasisLocalization.Get("settings.microphone.scope.sent"), sentNow);
            AppendLegend(heldHex, BasisLocalization.Get(muted ? "settings.microphone.scope.muted" : "settings.microphone.scope.notSent"), valid && !sentNow);
            if (s.UseNoiseGate) AppendLegend(gateHex, BasisLocalization.Get("settings.microphone.scope.cutGate"), gateNow);
            if (s.UseDenoiser) AppendLegend(denoiserHex, BasisLocalization.Get("settings.microphone.scope.cutDenoiser"), denoiseNow);
            AppendLegend(limiterHex, BasisLocalization.Get("settings.microphone.scope.cutLimiter"), limitNow);

            string text = legendText.ToString();
            if (text != lastLegend)
            {
                lastLegend = text;
                legend.SetText(text);
            }

            float gateThreshold = s.AutoNoiseGate ? BasisLocalMicrophoneDriver.GateThreshold : s.NoiseGateThreshold;
            SetLabel(gateLabel, gateHex, BasisLocalization.Get("settings.microphone.noiseGate.title"), Percent(gateThreshold / SMDMicrophone.NoiseGateThresholdMax));
            SetLabel(targetLabel, agcHex, BasisLocalization.Get("settings.microphone.scope.target"), FormatGain(BasisLocalMicrophoneDriver.AgcGainDb));
            SetLabel(limitLabel, limiterHex, BasisLocalization.Get("settings.microphone.limiter.title"), Percent(s.LimitThreshold));
        }

        private void AppendLegend(string hex, string label, bool active)
        {
            if (legendText.Length > 0) legendText.Append(LegendGap);
            legendText.Append("<color=#").Append(hex).Append('>');
            if (active) legendText.Append("<b>");
            legendText.Append(label);
            if (active) legendText.Append("</b>");
            legendText.Append("</color>");
        }

        private static void SetLabel(TextMeshProUGUI label, string hex, string name, string value)
        {
            if (label == null) return;
            string text = LabelBackdrop + "<color=#" + hex + ">" + name + " " + value + "</color></mark>";
            if (label.text != text) label.SetText(text);
        }

        private static string Percent(float fraction)
        {
            return Mathf.RoundToInt(fraction * 100f).ToString(CultureInfo.InvariantCulture) + "%";
        }

        private static string FormatGain(float db)
        {
            return db.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";
        }
    }
}
#endif
