using Unity.Scripting.LifecycleManagement;
using System;
using Basis.BasisUI.Styling;
using Basis.Scripts.UI;
using Basis.Scripts.UI.UI_Panels;
using TMPro;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisWorldPanelKit
    {
        public const string StyleWindow = "Background";
        public const string StyleEdge = "Edge";
        public const string StyleCard = "Menu Element";
        public const string StyleButton = "Button";
        public const string StyleButtonDanger = "Button Danger";
        public const string StyleButtonAccent = "Button Standard";
        public const string StyleIcon = "Icon";
        public const string StyleIconAccent = "Icon Accent";
        public const string StyleTrack = "Input Field";
        public const string StyleFill = "Slider Fill";
        public const string StyleKnob = "Knob";
        public const string StyleHeader = "Header";
        public const string StyleSubtitle = "Subtitle";
        public const string StyleButtonLabel = "Button Label";
        public const string StyleHover = "Highlight On Hover";

        private const int IconSize = 64;
        private const int ShapePlay = 0;
        private const int ShapePause = 1;
        private const int ShapeSpeaker = 2;
        private const int ShapeSpeakerMuted = 3;
        private const int ShapeCopy = 4;
        private const int ShapeMusic = 5;
        private const int ShapeCount = 6;

        private static Sprite[] icons;
        private static Sprite rounded;
        private static Sprite fallbackRounded;
        private static Shader distanceFieldShader;

        public static Sprite PlayIcon => GetIcon(ShapePlay);
        public static Sprite PauseIcon => GetIcon(ShapePause);
        public static Sprite SpeakerIcon => GetIcon(ShapeSpeaker);
        public static Sprite SpeakerMutedIcon => GetIcon(ShapeSpeakerMuted);
        public static Sprite CopyIcon => GetIcon(ShapeCopy);
        public static Sprite MusicIcon => GetIcon(ShapeMusic);

        public static Sprite AddressableIcon(string address)
        {
            try
            {
                return AddressableAssets.GetSprite(address);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Sprite RoundedSprite
        {
            get
            {
                if (rounded != null) return rounded;
                try
                {
                    UiStyleLibrary library = UiStyleSettings.GetActiveStyles();
                    ImageStyle style = library != null ? library.ImageStyles.GetStyle(StyleButton) : null;
                    if (style != null && style.Sprite != null) return rounded = style.Sprite;
                }
                catch (Exception)
                {
                }
                return fallbackRounded != null ? fallbackRounded : fallbackRounded = BuildFallbackRounded();
            }
        }

        public static RectTransform CreateCanvas(string name, Transform parent, Vector2 pixelSize, out GameObject root)
        {
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0) uiLayer = 5;
            root = new GameObject(name, typeof(RectTransform));
            root.layer = uiLayer;
            root.SetActive(false);
            var rect = (RectTransform)root.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = pixelSize;
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 2f;
            var rayCaster = root.AddComponent<BasisGraphicUIRayCaster>();
            rayCaster.Canvas = canvas;
            var component = root.AddComponent<BasisUIComponent>();
            component.Canvas = canvas;
            component.CanvasScaler = scaler;
            component.GraphicUIRayCaster = rayCaster;
            return rect;
        }

        public static RectTransform CreateRect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        public static void Stretch(RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        public static void AnchorTop(RectTransform rect, float fromTop)
        {
            float x = rect.anchoredPosition.x;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(x, -fromTop);
        }

        public static void AnchorBottom(RectTransform rect, float fromBottom)
        {
            float x = rect.anchoredPosition.x;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(x, fromBottom);
        }

        public static Image CreateStyledImage(string name, Transform parent, Vector2 position, Vector2 size, string styleId, Color fallback, bool raycast = false)
        {
            RectTransform rect = CreateRect(name, parent, position, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = RoundedSprite;
            image.type = Image.Type.Sliced;
            image.color = fallback;
            image.raycastTarget = raycast;
            Style(image, styleId);
            return image;
        }

        public static void Style(Image image, string styleId)
        {
            if (image == null) return;
            if (!image.TryGetComponent(out UiStyleImage styled)) styled = image.gameObject.AddComponent<UiStyleImage>();
            try
            {
                styled.SetStyle(styleId);
            }
            catch (Exception exception)
            {
                BasisDebug.LogWarning($"[BasisUI] style '{styleId}' could not be applied: {exception.Message}");
            }
        }

        public static void Style(TextMeshProUGUI label, string styleId)
        {
            if (label == null) return;
            if (!label.TryGetComponent(out UiStyleLabel styled)) styled = label.gameObject.AddComponent<UiStyleLabel>();
            try
            {
                styled.SetStyle(styleId);
            }
            catch (Exception exception)
            {
                BasisDebug.LogWarning($"[BasisUI] style '{styleId}' could not be applied: {exception.Message}");
            }
        }

        public static RectTransform CreateWindow(Transform parent, float edge = 6f)
        {
            ResolveMenuTemplate();
            Image edgeImage = CreateStyledImage("Edge", parent, Vector2.zero, Vector2.zero, StyleEdge, new Color(0f, 0f, 0f, 0.15f));
            CopyLook(templateEdge, edgeImage);
            Stretch(edgeImage.rectTransform, -edge);
            Image background = CreateStyledImage("Window", parent, Vector2.zero, Vector2.zero, StyleWindow, new Color(0.11f, 0.1f, 0.15f, 0.97f));
            CopyLook(templateBackground, background);
            Stretch(background.rectTransform);
            Texture2D backdrop = GetBackdrop();
            if (backdrop != null)
            {
                background.gameObject.AddComponent<Mask>().showMaskGraphic = true;
                RectTransform layer = CreateRect("Backdrop", background.rectTransform, Vector2.zero, Vector2.zero);
                Stretch(layer);
                var image = layer.gameObject.AddComponent<BasisWorldPanelBackdrop>();
                image.texture = backdrop;
                image.raycastTarget = false;
                image.FitTexture();
            }
            return background.rectTransform;
        }

        private const int BackdropSize = 1024;
        private static Texture2D backdropTexture;
        private static bool backdropFailed;
        private static Mesh backdropQuad;

        private static Texture2D GetBackdrop()
        {
            if (backdropTexture != null) return backdropTexture;
            if (backdropFailed) return null;
            Material source = Scripts.UI.BasisUIBackgroundCustomization.SharedMaterial;
            Material owned = null;
            if (source == null && templateBackgroundImage != null && templateBackgroundImage.material != null)
            {
                owned = new Material(templateBackgroundImage.material) { hideFlags = HideFlags.HideAndDontSave };
                source = owned;
            }
            if (source == null)
            {
                backdropFailed = true;
                return null;
            }
            RenderTexture target = null;
            try
            {
                target = RenderTexture.GetTemporary(new RenderTextureDescriptor(BackdropSize, BackdropSize, RenderTextureFormat.ARGB32, 0) { sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear });
                var commands = new UnityEngine.Rendering.CommandBuffer { name = "Basis world panel backdrop" };
                commands.SetRenderTarget(target);
                commands.ClearRenderTarget(true, true, WindowColor());
                commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                commands.DrawMesh(GetBackdropQuad(), Matrix4x4.identity, source, 0, 0);
                Graphics.ExecuteCommandBuffer(commands);
                commands.Release();
                var texture = new Texture2D(BackdropSize, BackdropSize, target.graphicsFormat, UnityEngine.Experimental.Rendering.TextureCreationFlags.None)
                {
                    name = "Basis World Panel Backdrop",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                };
                if ((SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.RTToTexture) != 0)
                {
                    Graphics.CopyTexture(target, texture);
                }
                else
                {
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = target;
                    texture.ReadPixels(new Rect(0, 0, BackdropSize, BackdropSize), 0, 0, false);
                    texture.Apply(false, true);
                    RenderTexture.active = previous;
                }
                backdropTexture = texture;
            }
            catch (Exception exception)
            {
                backdropFailed = true;
                BasisDebug.LogWarning($"[BasisUI] world panel backdrop could not be baked: {exception.Message}");
            }
            finally
            {
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (owned != null) UnityEngine.Object.Destroy(owned);
            }
            return backdropTexture;
        }

        private static Color WindowColor()
        {
            try
            {
                ImageStyle style = UiStyleSettings.GetActiveStyles()?.ImageStyles.GetStyle(StyleWindow);
                if (style != null)
                {
                    Color color = style.Color;
                    color.a = 1f;
                    return color;
                }
            }
            catch (Exception)
            {
            }
            return new Color(0.11f, 0.1f, 0.15f, 1f);
        }

        private static Mesh GetBackdropQuad()
        {
            if (backdropQuad != null) return backdropQuad;
            backdropQuad = new Mesh { name = "Basis World Panel Backdrop Quad", hideFlags = HideFlags.DontSave };
            backdropQuad.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f) };
            Vector4[] uv = { new Vector4(0f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(1f, 1f, 0f, 0f) };
            backdropQuad.SetUVs(0, uv);
            backdropQuad.SetUVs(1, uv);
            backdropQuad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            backdropQuad.triangles = new[] { 0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3 };
            backdropQuad.bounds = new Bounds(Vector3.zero, Vector3.one * 4f);
            return backdropQuad;
        }

        private static bool templateResolved;
        private static Image templateEdge;
        private static Image templateBackground;
        private static Image templateBackgroundImage;

        private static void ResolveMenuTemplate()
        {
            if (templateResolved) return;
            templateResolved = true;
            try
            {
                GameObject prefab = BasisAddressablePrefabCache.GetPrefab(BasisMenuPanel.PanelStyles.Default);
                if (prefab == null) return;
                foreach (Image image in prefab.GetComponentsInChildren<Image>(true))
                {
                    switch (image.gameObject.name)
                    {
                        case "Edge": if (templateEdge == null) templateEdge = image; break;
                        case "Background": if (templateBackground == null) templateBackground = image; break;
                        case "Background Image": if (templateBackgroundImage == null) templateBackgroundImage = image; break;
                    }
                }
            }
            catch (Exception exception)
            {
                BasisDebug.LogWarning($"[BasisUI] menu panel look could not be loaded: {exception.Message}");
            }
        }

        private static void CopyLook(Image template, Image target)
        {
            if (template == null || target == null) return;
            if (template.sprite != null) target.sprite = template.sprite;
            target.type = template.type;
            target.pixelsPerUnitMultiplier = template.pixelsPerUnitMultiplier;
        }

        public static TextMeshProUGUI CreateLabel(Transform parent, Vector2 position, Vector2 size, string styleId, TextAlignmentOptions alignment, float sizeScale = 1f)
        {
            RectTransform rect = CreateRect("Label", parent, position, size);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null) text.font = TMP_Settings.defaultFontAsset;
            text.color = Color.white;
            text.fontSize = 26f;
            Style(text, styleId);
            text.richText = false;
            text.parseCtrlCharacters = false;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            float fontSize = text.fontSize * sizeScale;
            text.fontSizeMax = fontSize;
            text.fontSizeMin = fontSize * 0.6f;
            text.enableAutoSizing = true;
            if (distanceFieldShader == null) distanceFieldShader = Shader.Find("TextMeshPro/Distance Field");
            if (distanceFieldShader != null && text.font != null) text.fontMaterial.shader = distanceFieldShader;
            return text;
        }

        public sealed class WorldButton
        {
            public Button Button;
            public Image Background;
            public Image Icon;
            public TextMeshProUGUI Label;
            private string backgroundStyle;

            public void SetIcon(Sprite sprite)
            {
                if (Icon == null) return;
                if (Icon.sprite != sprite) Icon.sprite = sprite;
                Icon.enabled = sprite != null;
            }

            public void SetText(string text)
            {
                if (Label != null && Label.text != text) Label.text = text;
            }

            public void SetStyle(string styleId)
            {
                if (styleId == backgroundStyle) return;
                backgroundStyle = styleId;
                Style(Background, styleId);
            }

            public void SetInteractable(bool value)
            {
                if (Button != null && Button.interactable != value) Button.interactable = value;
                if (Icon != null) Icon.canvasRenderer.SetAlpha(value ? 1f : 0.45f);
                if (Label != null) Label.alpha = value ? 1f : 0.45f;
            }
        }

        public static WorldButton CreateButton(Transform parent, Vector2 position, Vector2 size, Sprite icon, string text, string styleId, UnityAction onClick)
        {
            var result = new WorldButton();
            result.Background = CreateStyledImage("Button", parent, position, size, null, new Color(0.43f, 0.41f, 0.57f, 1f), true);
            result.Background.pixelsPerUnitMultiplier = 4f;
            result.SetStyle(styleId);
            GameObject go = result.Background.gameObject;
            var button = go.AddComponent<Button>();
            button.targetGraphic = result.Background;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock colors = button.colors;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            go.AddComponent<UiStyleSelectable>().SetStyle(StyleHover);
            button.onClick.AddListener(onClick);
            result.Button = button;

            bool hasText = text != null;
            float iconSize = Mathf.Min(size.y * 0.55f, 40f);
            if (icon != null || !hasText)
            {
                float x = hasText ? -size.x * 0.5f + size.y * 0.5f : 0f;
                RectTransform iconRect = CreateRect("Icon", go.transform, new Vector2(x, 0f), new Vector2(iconSize, iconSize));
                result.Icon = iconRect.gameObject.AddComponent<Image>();
                result.Icon.raycastTarget = false;
                result.Icon.preserveAspect = true;
                result.Icon.color = Color.white;
                Style(result.Icon, StyleIcon);
                result.Icon.sprite = icon;
                result.Icon.type = Image.Type.Simple;
                result.Icon.enabled = icon != null;
            }
            if (hasText || icon == null)
            {
                float left = result.Icon != null && hasText ? size.y * 0.85f : 10f;
                result.Label = CreateLabel(go.transform, new Vector2((left - 10f) * 0.5f, 0f), new Vector2(size.x - left - 10f, size.y - 6f), StyleButtonLabel, TextAlignmentOptions.Center);
                result.Label.text = text ?? string.Empty;
            }
            return result;
        }

        public static Slider CreateSlider(Transform parent, Vector2 position, Vector2 size, float trackHeight, float handleSize, string fillStyle)
        {
            RectTransform rect = CreateRect("Slider", parent, position, size);
            var hit = rect.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            hit.raycastTarget = true;

            Image track = CreateStyledImage("Track", rect, Vector2.zero, Vector2.zero, StyleTrack, new Color(0.09f, 0.09f, 0.11f, 1f));
            track.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            track.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            track.rectTransform.sizeDelta = new Vector2(0f, trackHeight);
            track.pixelsPerUnitMultiplier = 512f / trackHeight * 0.5f;

            RectTransform fillArea = CreateRect("Fill Area", rect, Vector2.zero, Vector2.zero);
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.sizeDelta = new Vector2(0f, trackHeight);
            Image fill = CreateStyledImage("Fill", fillArea, Vector2.zero, Vector2.zero, fillStyle, Color.white);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            fill.rectTransform.sizeDelta = new Vector2(trackHeight, 0f);
            fill.sprite = RoundedSprite;
            fill.type = Image.Type.Sliced;
            fill.pixelsPerUnitMultiplier = 512f / trackHeight * 0.5f;

            RectTransform handleArea = CreateRect("Handle Area", rect, Vector2.zero, Vector2.zero);
            handleArea.anchorMin = Vector2.zero;
            handleArea.anchorMax = Vector2.one;
            handleArea.sizeDelta = new Vector2(-handleSize, 0f);
            Image handle = CreateStyledImage("Handle", handleArea, Vector2.zero, new Vector2(handleSize, handleSize), StyleKnob, Color.white, true);
            handle.type = Image.Type.Simple;
            handle.preserveAspect = true;
            handle.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            handle.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            handle.rectTransform.sizeDelta = new Vector2(handleSize, handleSize);

            var slider = rect.gameObject.AddComponent<Slider>();
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            rect.gameObject.AddComponent<UiStyleSelectable>().SetStyle(StyleHover);
            return slider;
        }

        private static Sprite GetIcon(int shape)
        {
            if (icons == null || icons[shape] == null) BuildIcons();
            return icons[shape];
        }

        private static void BuildIcons()
        {
            icons ??= new Sprite[ShapeCount];
            var textures = new Texture2D[ShapeCount];
            var handles = new NativeArray<JobHandle>(ShapeCount, Allocator.Temp);
            for (int shape = 0; shape < ShapeCount; shape++)
            {
                if (icons[shape] != null) continue;
                var texture = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false)
                {
                    name = "Basis World Panel Icon",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                };
                textures[shape] = texture;
                handles[shape] = new IconRasterJob
                {
                    Shape = shape,
                    Pixels = texture.GetRawTextureData<Color32>(),
                }.Schedule(IconSize * IconSize, IconSize);
            }
            JobHandle.CompleteAll(handles);
            handles.Dispose();
            for (int shape = 0; shape < ShapeCount; shape++)
            {
                Texture2D texture = textures[shape];
                if (texture == null) continue;
                texture.Apply(false, true);
                Sprite sprite = Sprite.Create(texture, new Rect(0, 0, IconSize, IconSize), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                sprite.name = texture.name;
                sprite.hideFlags = HideFlags.DontSave;
                icons[shape] = sprite;
            }
        }

        private static Sprite BuildFallbackRounded()
        {
            const int size = 64, radius = 30;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Basis World Panel Rounded",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                    float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }

        [BurstCompile]
        private struct IconRasterJob : IJobParallelFor
        {
            public int Shape;
            [WriteOnly] public NativeArray<Color32> Pixels;

            public void Execute(int index)
            {
                int x = index % IconSize;
                int y = index / IconSize;
                const int samples = 4;
                float sum = 0f;
                for (int sy = 0; sy < samples; sy++)
                {
                    for (int sx = 0; sx < samples; sx++)
                    {
                        sum += Coverage(Shape, x + (sx + 0.5f) / samples, y + (sy + 0.5f) / samples);
                    }
                }
                Pixels[index] = new Color32(255, 255, 255, (byte)(sum / (samples * samples) * 255f + 0.5f));
            }
        }

        private static float Coverage(int shape, float x, float y)
        {
            switch (shape)
            {
                case ShapePlay: return InTriangle(x, y, 18f, 8f, 18f, 56f, 56f, 32f) ? 1f : 0f;
                case ShapePause: return (x >= 14f && x <= 27f || x >= 37f && x <= 50f) && y >= 9f && y <= 55f ? 1f : 0f;
                case ShapeSpeaker: return SpeakerCoverage(x, y);
                case ShapeSpeakerMuted: return SpeakerMutedCoverage(x, y);
                case ShapeCopy: return CopyCoverage(x, y);
                case ShapeMusic: return MusicCoverage(x, y);
            }
            return 0f;
        }

        private static float MusicCoverage(float x, float y)
        {
            float ax = (x - 18f) / 9.5f, ay = (y - 15f) / 7f;
            float bx = (x - 46f) / 9.5f, by = (y - 11f) / 7f;
            if (ax * ax + ay * ay <= 1f || bx * bx + by * by <= 1f) return 1f;
            if (x >= 23.5f && x <= 27.5f && y >= 15f && y <= 52f) return 1f;
            if (x >= 51.5f && x <= 55.5f && y >= 11f && y <= 48f) return 1f;
            float top = 58f - (x - 23.5f) * (4f / 32f);
            return x >= 23.5f && x <= 55.5f && y <= top && y >= top - 9f ? 1f : 0f;
        }

        private static bool SpeakerBody(float x, float y)
        {
            bool box = x >= 6f && x <= 18f && y >= 23f && y <= 41f;
            bool cone = x >= 16f && x <= 32f && Mathf.Abs(y - 32f) <= 9f + (x - 16f) * (14f / 16f);
            return box || cone;
        }

        private static float SpeakerCoverage(float x, float y)
        {
            if (SpeakerBody(x, y)) return 1f;
            float dx = x - 32f, dy = y - 32f;
            if (dx <= 0f || Mathf.Abs(dy) > dx * 1.1f) return 0f;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            return (d >= 9f && d <= 14f) || (d >= 19f && d <= 24f) ? 1f : 0f;
        }

        private static float SpeakerMutedCoverage(float x, float y)
        {
            if (SpeakerBody(x, y)) return 1f;
            float u = x - 48f, v = y - 32f;
            if (Mathf.Abs(u) > 11f || Mathf.Abs(v) > 11f) return 0f;
            return Mathf.Abs(u - v) <= 3.5f || Mathf.Abs(u + v) <= 3.5f ? 1f : 0f;
        }

        private static bool InRoundedOutline(float x, float y, float minX, float minY, float maxX, float maxY, float radius, float thickness)
        {
            float cx = Mathf.Clamp(x, minX + radius, maxX - radius);
            float cy = Mathf.Clamp(y, minY + radius, maxY - radius);
            float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            return d <= radius && d >= radius - thickness;
        }

        private static float CopyCoverage(float x, float y)
        {
            if (InRoundedOutline(x, y, 22f, 6f, 58f, 42f, 7f, 5f)) return 1f;
            bool insideFront = x > 17f && x < 63f && y > 1f && y < 47f;
            return !insideFront && InRoundedOutline(x, y, 6f, 22f, 42f, 58f, 7f, 5f) ? 1f : 0f;
        }

        private static bool InTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            float d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            float d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
            bool negative = d1 < 0f || d2 < 0f || d3 < 0f;
            bool positive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(negative && positive);
        }
    }
}
