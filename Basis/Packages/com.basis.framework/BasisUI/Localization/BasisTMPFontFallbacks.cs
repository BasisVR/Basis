using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Basis.BasisUI
{
    /// <summary>
    /// Builds TMP font assets from OS-installed fonts and wires them up as
    /// global fallbacks so any TextMeshPro label falls through to a system
    /// font when its primary font doesn't have a glyph. This is how we get
    /// broad Unicode coverage (CJK, Cyrillic, Arabic, Devanagari, Thai…)
    /// without having to ship every glyph inside a baked static atlas.
    ///
    /// Runs once before the first scene loads. Define
    /// <c>BASIS_DISABLE_TMP_FALLBACKS</c> to opt out.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisTMPFontFallbacks
    {
        private const int DefaultSamplingPointSize = 90;
        private const string JaJpLabel = "Basis Fallback - ja-JP";
        private const string KoKrLabel = "Basis Fallback - ko-KR";
        private const string ZhHansLabel = "Basis Fallback - zh-Hans";
        private const string ZhHantLabel = "Basis Fallback - zh-Hant";
        private const string ZhHkLabel = "Basis Fallback - zh-HK";
        private const string ShippedJapaneseFontAddress = "Packages/com.basis.sdk/Fonts/NotoSansJP-Regular.ttf";
        private const string EmojiLabel = "Basis Fallback - Emoji";
        private const string ShippedEmojiFontAddress = "Packages/com.basis.sdk/Fonts/NotoEmoji-Regular.ttf";

        public const string StyleAuto = "auto";
        public const string StyleJapanese = "ja";
        public const string StyleSimplifiedChinese = "zh-Hans";
        public const string StyleTraditionalChinese = "zh-Hant";
        public const string StyleHongKongChinese = "zh-HK";
        public const string StyleKorean = "ko";

        [NoAutoStaticsCleanup] private static readonly string[] CjkLabels = { JaJpLabel, ZhHansLabel, ZhHantLabel, ZhHkLabel, KoKrLabel };
        [NoAutoStaticsCleanup] private static readonly string[] OptionalLabels = { ZhHkLabel };

        private static string _preferredStyle;
        private static readonly Dictionary<(TMP_FontAsset Base, string Style), TMP_FontAsset> _variants = new();
        private static readonly Dictionary<TMP_FontAsset, TMP_FontAsset> _variantBase = new();
        private static readonly List<uint> _cacheKeyScratch = new();
        private static bool _fontEventHooked;

        private static bool _installed;
        private static TMP_FontAsset _shippedJapaneseFallback;
        private static TMP_FontAsset _shippedEmojiFallback;

#if UNITY_SERVER
        private static readonly bool IsServerBuild = true;
#else
        private static readonly bool IsServerBuild = false;
#endif

        /// <summary>
        /// Ordered candidates for each fallback slot. Names are OS font
        /// family names and are fed one-by-one to
        /// <see cref="TMP_FontAsset.CreateFontAsset(string, string, int)"/>,
        /// which asks the underlying FontEngine to resolve the font file on
        /// the host OS. The first name that resolves wins. The list
        /// intentionally overlaps Windows / macOS / Linux / Android so a
        /// single array serves every platform.
        /// </summary>
        [NoAutoStaticsCleanup] private static readonly (string Label, string[] Candidates)[] FallbackGroups = new[]
        {
            ("Basis Fallback - zh-Hant", new[]
            {
                // Windows
                "Microsoft JhengHei UI",
                "Microsoft JhengHei",
                "PMingLiU",
                "MingLiU",
                // macOS
                "PingFang TC",
                "Heiti TC",
                // Linux / Noto
                "Noto Sans CJK TC",
                "Noto Sans TC",
            }),
            ("Basis Fallback - zh-HK", new[]
            {
                // macOS
                "PingFang HK",
                // Linux / Noto
                "Noto Sans CJK HK",
                "Noto Sans HK",
                "Source Han Sans HC",
                // Windows (Traditional Chinese supplemental fonts)
                "MingLiU_HKSCS",
            }),
            ("Basis Fallback - zh-Hans", new[]
            {
                // Windows
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "SimHei",
                "SimSun",
                // macOS
                "PingFang SC",
                "Hiragino Sans GB",
                "STHeiti",
                // Linux / Noto
                "Noto Sans CJK SC",
                "Noto Sans SC",
                "Source Han Sans SC",
                "Source Han Sans",
                "WenQuanYi Micro Hei",
            }),
            ("Basis Fallback - ko-KR", new[]
            {
                // Windows
                "Malgun Gothic",
                "Gulim",
                "Dotum",
                "Batang",
                // macOS
                "Apple SD Gothic Neo",
                "AppleGothic",
                // Linux / Noto
                "Noto Sans CJK KR",
                "Noto Sans KR",
                "NanumGothic",
            }),
            ("Basis Fallback - ja-JP", new[]
            {
                // Windows 10/11 (ships by default, including English SKUs)
                "Yu Gothic UI",
                "Yu Gothic",
                "Meiryo UI",
                "Meiryo",
                "MS UI Gothic",
                "MS Gothic",
                // macOS
                "Hiragino Sans",
                "Hiragino Kaku Gothic ProN",
                // Linux / Noto
                "Noto Sans CJK JP",
                "Noto Sans JP",
                "Source Han Sans JP",
                "TakaoGothic",
            }),
            ("Basis Fallback - Unicode", new[]
            {
                "Segoe UI",
                "Segoe UI Symbol",
                "Tahoma",
                "Arial",
                "Helvetica",
                "Helvetica Neue",
                "DejaVu Sans",
                "Liberation Sans",
                "FreeSans",
                "Noto Sans",
                "Roboto",
            }),
            ("Basis Fallback - Arabic", new[]
            {
                // Windows 10/11 (Segoe UI ships Arabic, Tahoma/Arial too)
                "Segoe UI",
                "Tahoma",
                "Arial",
                "Traditional Arabic",
                "Simplified Arabic",
                "Sakkal Majalla",
                "Arabic Typesetting",
                // Urdu Nastaliq shaping
                "Urdu Typesetting",
                "Jameel Noori Nastaleeq",
                // macOS
                "Geeza Pro",
                "Al Nile",
                "Damascus",
                "Beirut",
                "Baghdad",
                // Linux / Noto
                "Noto Sans Arabic",
                "Noto Naskh Arabic",
                "Noto Nastaliq Urdu",
                "Amiri",
                "KacstBook",
                "KacstOne",
                // Android
                "Droid Arabic Naskh",
                "Droid Sans Arabic",
            }),
            ("Basis Fallback - Devanagari", new[]
            {
                // Windows 10/11
                "Nirmala UI",
                "Mangal",
                "Aparajita",
                "Kokila",
                "Utsaah",
                // macOS
                "Kohinoor Devanagari",
                "Devanagari MT",
                "Devanagari Sangam MN",
                "Shree Devanagari 714",
                "ITF Devanagari",
                // Linux / Noto
                "Noto Sans Devanagari",
                "Lohit Devanagari",
                "Samyak Devanagari",
                "FreeSans",
                // Android
                "Droid Sans Devanagari",
            }),
            ("Basis Fallback - Bengali", new[]
            {
                // Windows 10/11
                "Nirmala UI",
                "Vrinda",
                "Shonar Bangla",
                // macOS
                "Kohinoor Bangla",
                "Bangla MN",
                "Bangla Sangam MN",
                // Linux / Noto
                "Noto Sans Bengali",
                "Lohit Bengali",
                "Mukti Narrow",
                "FreeSans",
                // Android
                "Droid Sans Bengali",
            }),
            ("Basis Fallback - Thai", new[]
            {
                // Windows 10/11
                "Leelawadee UI",
                "Leelawadee",
                "Tahoma",
                "Microsoft Sans Serif",
                "Angsana New",
                "Cordia New",
                // macOS
                "Thonburi",
                "Ayuthaya",
                "Krungthep",
                "Silom",
                "Sukhumvit Set",
                // Linux / Noto
                "Noto Sans Thai",
                "Garuda",
                "Kinnari",
                "Norasi",
                "Loma",
                // Android
                "Droid Sans Thai",
            }),
            ("Basis Fallback - Hebrew", new[]
            {
                // Windows 10/11
                "Segoe UI",
                "Tahoma",
                "Arial",
                "David",
                "Narkisim",
                "FrankRuehl",
                "Gisha",
                // macOS
                "Arial Hebrew",
                "Lucida Grande",
                "New Peninim MT",
                "Corsiva Hebrew",
                // Linux / Noto
                "Noto Sans Hebrew",
                "DejaVu Sans",
                "FreeSans",
                // Android
                "Droid Sans Hebrew",
            }),
            ("Basis Fallback - Cyrillic", new[]
            {
                // Windows 10/11
                "Segoe UI",
                "Tahoma",
                "Arial",
                "Calibri",
                // macOS
                "Helvetica Neue",
                "Helvetica",
                "Lucida Grande",
                // Linux / Noto
                "DejaVu Sans",
                "Liberation Sans",
                "Noto Sans",
                "FreeSans",
                // Android
                "Roboto",
                "Droid Sans",
            }),
            ("Basis Fallback - Emoji", new[]
            {
                "Segoe UI Emoji",
                "Noto Emoji",
                "Symbola",
            }),
        };

#if !BASIS_DISABLE_TMP_FALLBACKS
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static async void AutoInstall()
        {
            await Basis.Scripts.Platform.BasisWebAddressables.WhenReady();
            InstallFallbacks();
        }
#endif

        /// <summary>
        /// Idempotent. Builds any missing dynamic-OS TMP font assets and
        /// appends them to <see cref="TMP_Settings.fallbackFontAssets"/>.
        /// Safe to call more than once — fallbacks already registered by name
        /// are skipped.
        /// </summary>
        public static void InstallFallbacks()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            if (IsServerBuild)
            {
                return;
            }

            List<TMP_FontAsset> fallbacks = TMP_Settings.fallbackFontAssets;
            if (fallbacks == null)
            {
                BasisDebug.LogError("[BasisTMPFontFallbacks] TMP_Settings.fallbackFontAssets is null — skipping install.");
                return;
            }

            for (int i = 0; i < FallbackGroups.Length; i++)
            {
                var group = FallbackGroups[i];
                if (ContainsByName(fallbacks, group.Label))
                {
                    continue;
                }

                bool optional = Array.IndexOf(OptionalLabels, group.Label) >= 0;
                TMP_FontAsset shipped = group.Label == JaJpLabel ? GetShippedJapaneseFallback() : group.Label == EmojiLabel ? GetShippedEmojiFallback() : null;
                TMP_FontAsset tmpFont = shipped ?? (Application.platform == RuntimePlatform.WebGLPlayer ? null : TryCreateDynamicOSFallback(group.Label, group.Candidates, optional));
                if (tmpFont != null)
                {
                    fallbacks.Add(tmpFont);
                    if (group.Label == EmojiLabel)
                    {
                        RegisterEmojiFallback(tmpFont);
                    }
                    BasisDebug.Log($"[BasisTMPFontFallbacks] Installed {group.Label} using OS font family '{tmpFont.faceInfo.familyName}'.");
                }
                else if (!optional && Application.platform != RuntimePlatform.WebGLPlayer)
                {
                    BasisDebug.LogError($"[BasisTMPFontFallbacks] None of the candidates for {group.Label} could be resolved on this OS. Candidates tried: {string.Join(", ", group.Candidates)}");
                }
            }

            // Localization isn't up this early; seed from the OS locale and let
            // RefreshCjkPriority re-apply from the UI language before menus render.
            ApplyCjkPriority(ResolveCurrentStyle(null));
            BasisLocalization.OnLanguageChanged += RefreshCjkPriority;
        }

        /// <summary>
        /// The project's authoritative Japanese glyph source: a dynamic TMP font
        /// asset built once from the embedded Noto Sans JP and cached, so menus,
        /// settings, nameplates and chat share one face on every platform regardless
        /// of installed OS fonts. Returns null if the addressable can't resolve yet.
        /// </summary>
        public static TMP_FontAsset GetShippedJapaneseFallback()
        {
            if (_shippedJapaneseFallback == null)
            {
                _shippedJapaneseFallback = LoadShippedFallback(ShippedJapaneseFontAddress, JaJpLabel);
            }
            return _shippedJapaneseFallback;
        }

        public static TMP_FontAsset GetShippedEmojiFallback()
        {
            if (_shippedEmojiFallback == null)
            {
                _shippedEmojiFallback = LoadShippedFallback(ShippedEmojiFontAddress, EmojiLabel);
            }
            return _shippedEmojiFallback;
        }

        private static TMP_FontAsset LoadShippedFallback(string address, string label)
        {
            if (IsServerBuild)
            {
                return null;
            }

            Font font;
            try
            {
                font = Addressables.LoadAssetAsync<Font>(address).WaitForCompletion();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisTMPFontFallbacks] Failed to load embedded font '{address}': {e.Message}");
                return null;
            }

            if (font == null)
            {
                BasisDebug.LogError($"[BasisTMPFontFallbacks] Embedded font not found at '{address}'.");
                return null;
            }

            TMP_FontAsset tmpFont;
            try
            {
                tmpFont = TMP_FontAsset.CreateFontAsset(font);
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisTMPFontFallbacks] CreateFontAsset threw for the embedded font '{address}': {e.Message}");
                return null;
            }

            if (tmpFont == null)
            {
                BasisDebug.LogError($"[BasisTMPFontFallbacks] CreateFontAsset returned null for the embedded font '{address}'.");
                return null;
            }

            tmpFont.name = label;
            return tmpFont;
        }

        private static void RegisterEmojiFallback(TMP_FontAsset font)
        {
            List<TMP_Asset> emoji = TMP_Settings.emojiFallbackTextAssets;
            if (emoji == null)
            {
                emoji = new List<TMP_Asset>();
                TMP_Settings.emojiFallbackTextAssets = emoji;
            }
            for (int i = emoji.Count - 1; i >= 0; i--)
            {
                if (emoji[i] == null || emoji[i].name == EmojiLabel)
                {
                    emoji.RemoveAt(i);
                }
            }
            TMP_SpriteAsset sprites = TMP_Settings.defaultSpriteAsset;
            if (sprites != null && !emoji.Contains(sprites))
            {
                emoji.Insert(0, sprites);
            }
            emoji.Add(font);
        }

        private static bool SwapInShipped(List<TMP_FontAsset> fallbacks, string label, TMP_FontAsset shipped)
        {
            if (shipped == null)
            {
                return false;
            }
            int index = IndexOfByName(fallbacks, label);
            if (index >= 0)
            {
                fallbacks[index] = shipped;
            }
            else
            {
                fallbacks.Add(shipped);
            }
            return true;
        }

        /// <summary>
        /// Wires the embedded Noto Sans JP into the global fallback list (swapping
        /// out any OS Japanese font picked up during the early BeforeSceneLoad pass)
        /// and re-applies the CJK glyph style from the setting, UI language and OS
        /// region. Call once <see cref="BasisLocalization"/> is initialized, on every
        /// language change and when the glyph style setting changes.
        /// </summary>
        public static void RefreshCjkPriority()
        {
            InstallFallbacks();

            List<TMP_FontAsset> fallbacks = TMP_Settings.fallbackFontAssets;
            if (fallbacks == null)
            {
                return;
            }

            SwapInShipped(fallbacks, JaJpLabel, GetShippedJapaneseFallback());
            TMP_FontAsset shippedEmoji = GetShippedEmojiFallback();
            if (SwapInShipped(fallbacks, EmojiLabel, shippedEmoji))
            {
                RegisterEmojiFallback(shippedEmoji);
            }

            ApplyCjkPriority(ResolveCurrentStyle(BasisLocalization.CurrentLanguage));
            RefreshVariantFallbacks();
            TMP_Text[] labels = UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include);
            ClearFallbackLookupCaches(labels);
            InvalidateTextMeshes(labels);
            Basis.Scripts.UI.NamePlate.BasisRemoteNamePlateDriver.RebakeAllNamePlates();
        }

        /// <summary>
        /// The CJK glyph style shared Han code points currently resolve to, or null when
        /// nothing points at one and the Japanese-first default order is in use.
        /// </summary>
        public static string PreferredStyle => _preferredStyle;

        private static string EffectiveStyle => _preferredStyle ?? StyleJapanese;

        /// <summary>
        /// Picks the font a label should use for user-written text. Han code points look
        /// different in Japanese, Chinese and Korean, so text that carries kana or Hangul
        /// gets a variant of <paramref name="font"/> that resolves Han through that
        /// language's font first; everything else keeps the base font and the global order.
        /// </summary>
        public static TMP_FontAsset FontForText(TMP_FontAsset font, string text)
        {
            if (font == null)
            {
                return null;
            }
            TMP_FontAsset baseFont = _variantBase.TryGetValue(font, out TMP_FontAsset original) && original != null ? original : font;
            if (IsServerBuild)
            {
                return baseFont;
            }
            string style = DetectStyle(text);
            if (style == null || style == EffectiveStyle)
            {
                return baseFont;
            }
            return GetVariant(baseFont, style) ?? baseFont;
        }

        public static void ApplyFontForText(TMP_Text label, string text)
        {
            if (label == null)
            {
                return;
            }
            TMP_FontAsset font = FontForText(label.font, text);
            if (font != null && label.font != font)
            {
                label.font = font;
            }
        }

        internal static string DetectStyle(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            bool hangul = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (((c >= '぀' && c <= 'ヿ') || (c >= 'ㇰ' && c <= 'ㇿ') || (c >= 'ｦ' && c <= 'ﾟ')) && c != '・')
                {
                    return StyleJapanese;
                }
                if ((c >= '가' && c <= '힯') || (c >= 'ᄀ' && c <= 'ᇿ') || (c >= '㄰' && c <= '㆏') || (c >= 'ꥠ' && c <= '꥿') || (c >= 'ힰ' && c <= '퟿'))
                {
                    hangul = true;
                }
            }
            return hangul ? StyleKorean : null;
        }

        private static TMP_FontAsset GetVariant(TMP_FontAsset baseFont, string style)
        {
            if (_variants.TryGetValue((baseFont, style), out TMP_FontAsset variant) && variant != null)
            {
                return variant;
            }
            TMP_FontAsset cjk = FindFallback(LabelForStyle(style));
            if (cjk == null)
            {
                return null;
            }
            variant = UnityEngine.Object.Instantiate(baseFont);
            variant.name = baseFont.name + " (" + style + ")";
            variant.hideFlags = HideFlags.HideAndDontSave;
            variant.atlasPopulationMode = AtlasPopulationMode.Static;
            variant.fallbackFontAssetTable = new List<TMP_FontAsset> { baseFont, cjk };
            variant.ReadFontAssetDefinition();
            _variants[(baseFont, style)] = variant;
            _variantBase[variant] = baseFont;
            if (!_fontEventHooked)
            {
                _fontEventHooked = true;
                TMPro_EventManager.FONT_PROPERTY_EVENT.Remove(OnFontPropertyChanged);
                TMPro_EventManager.FONT_PROPERTY_EVENT.Add(OnFontPropertyChanged);
            }
            return variant;
        }

        private static void OnFontPropertyChanged(bool changed, UnityEngine.Object obj)
        {
            if (obj is not TMP_FontAsset font || _variantBase.ContainsKey(font))
            {
                return;
            }
            foreach (KeyValuePair<(TMP_FontAsset Base, string Style), TMP_FontAsset> pair in _variants)
            {
                TMP_FontAsset variant = pair.Value;
                if (pair.Key.Base != font || variant == null)
                {
                    continue;
                }
                variant.characterTable.Clear();
                variant.glyphTable.Clear();
                variant.atlasTextures = font.atlasTextures;
                variant.ReadFontAssetDefinition();
                TMPro_EventManager.ON_FONT_PROPERTY_CHANGED(true, variant);
            }
        }

        /// <summary>
        /// Variants share their base font's atlas and material, which a font asset destroys
        /// with itself, so those references are dropped before each variant is destroyed.
        /// </summary>
        internal static void ReleaseVariants()
        {
            foreach (TMP_FontAsset variant in _variantBase.Keys)
            {
                if (variant == null)
                {
                    continue;
                }
                variant.material = null;
                variant.atlasTextures = null;
                variant.fallbackFontAssetTable = null;
                UnityEngine.Object.DestroyImmediate(variant);
            }
            _variants.Clear();
            _variantBase.Clear();
        }

        private static void RefreshVariantFallbacks()
        {
            foreach (KeyValuePair<(TMP_FontAsset Base, string Style), TMP_FontAsset> pair in _variants)
            {
                TMP_FontAsset variant = pair.Value;
                if (variant == null)
                {
                    continue;
                }
                List<TMP_FontAsset> table = variant.fallbackFontAssetTable;
                table.Clear();
                table.Add(pair.Key.Base);
                TMP_FontAsset cjk = FindFallback(LabelForStyle(pair.Key.Style));
                if (cjk != null)
                {
                    table.Add(cjk);
                }
            }
        }

        /// <summary>
        /// TMP caches every character it found through a fallback inside the label's own font,
        /// so once a Han character has resolved to one CJK font it keeps that font after the
        /// order changes. Drop those cached fallback hits so the new order applies.
        /// </summary>
        internal static void ClearFallbackLookupCaches(IEnumerable<TMP_Text> labels)
        {
            HashSet<TMP_FontAsset> seen = new HashSet<TMP_FontAsset>();
            if (labels != null)
            {
                foreach (TMP_Text label in labels)
                {
                    if (label != null)
                    {
                        ClearFallbackLookupCache(label.font, seen);
                    }
                }
            }
            foreach (TMP_FontAsset variant in _variantBase.Keys)
            {
                ClearFallbackLookupCache(variant, seen);
            }
            ClearFallbackLookupCache(TMP_Settings.defaultFontAsset, seen);
        }

        internal static void ClearFallbackLookupCache(TMP_FontAsset font, HashSet<TMP_FontAsset> seen)
        {
            if (font == null || !seen.Add(font))
            {
                return;
            }
            Dictionary<uint, TMP_Character> table = font.characterLookupTable;
            if (table != null)
            {
                _cacheKeyScratch.Clear();
                foreach (KeyValuePair<uint, TMP_Character> entry in table)
                {
                    if (entry.Value == null || entry.Value.textAsset != font)
                    {
                        _cacheKeyScratch.Add(entry.Key);
                    }
                }
                for (int i = 0; i < _cacheKeyScratch.Count; i++)
                {
                    table.Remove(_cacheKeyScratch[i]);
                }
                _cacheKeyScratch.Clear();
            }
            TMP_FontWeightPair[] weights = font.fontWeightTable;
            if (weights != null)
            {
                for (int i = 0; i < weights.Length; i++)
                {
                    ClearFallbackLookupCache(weights[i].regularTypeface, seen);
                    ClearFallbackLookupCache(weights[i].italicTypeface, seen);
                }
            }
        }

        internal static int InvalidateTextMeshes(IEnumerable<TMP_Text> labels)
        {
            if (labels == null)
            {
                return 0;
            }

            int invalidated = 0;
            foreach (TMP_Text label in labels)
            {
                if (label == null)
                {
                    continue;
                }

                label.havePropertiesChanged = true;
                invalidated++;
            }
            return invalidated;
        }

        /// <summary>
        /// Rewrites the CJK slots of the global fallback list so the given style's font
        /// comes first, followed by its nearest neighbours. Null keeps the Japanese-first
        /// default, which is also the only CJK font every platform has (it ships embedded).
        /// </summary>
        internal static void ApplyCjkPriority(string style)
        {
            _preferredStyle = style;

            List<TMP_FontAsset> fallbacks = TMP_Settings.fallbackFontAssets;
            if (fallbacks == null)
            {
                return;
            }

            List<int> slots = new List<int>(CjkLabels.Length);
            for (int i = 0; i < fallbacks.Count; i++)
            {
                if (fallbacks[i] != null && Array.IndexOf(CjkLabels, fallbacks[i].name) >= 0)
                {
                    slots.Add(i);
                }
            }

            string[] order = CjkOrderFor(style);
            List<TMP_FontAsset> ordered = new List<TMP_FontAsset>(slots.Count);
            for (int i = 0; i < order.Length; i++)
            {
                int index = IndexOfByName(fallbacks, order[i]);
                if (index >= 0)
                {
                    ordered.Add(fallbacks[index]);
                }
            }

            if (ordered.Count != slots.Count)
            {
                return;
            }
            for (int i = 0; i < slots.Count; i++)
            {
                fallbacks[slots[i]] = ordered[i];
            }
        }

        [NoAutoStaticsCleanup] private static readonly string[] OrderJapanese = { JaJpLabel, ZhHansLabel, ZhHantLabel, ZhHkLabel, KoKrLabel };
        [NoAutoStaticsCleanup] private static readonly string[] OrderSimplified = { ZhHansLabel, ZhHantLabel, ZhHkLabel, JaJpLabel, KoKrLabel };
        [NoAutoStaticsCleanup] private static readonly string[] OrderTraditional = { ZhHantLabel, ZhHkLabel, ZhHansLabel, JaJpLabel, KoKrLabel };
        [NoAutoStaticsCleanup] private static readonly string[] OrderHongKong = { ZhHkLabel, ZhHantLabel, ZhHansLabel, JaJpLabel, KoKrLabel };
        [NoAutoStaticsCleanup] private static readonly string[] OrderKorean = { KoKrLabel, ZhHantLabel, ZhHkLabel, JaJpLabel, ZhHansLabel };

        internal static string[] CjkOrderFor(string style)
        {
            switch (style)
            {
                case StyleSimplifiedChinese: return OrderSimplified;
                case StyleTraditionalChinese: return OrderTraditional;
                case StyleHongKongChinese: return OrderHongKong;
                case StyleKorean: return OrderKorean;
                default: return OrderJapanese;
            }
        }

        private static string LabelForStyle(string style)
        {
            switch (style)
            {
                case StyleJapanese: return JaJpLabel;
                case StyleSimplifiedChinese: return ZhHansLabel;
                case StyleTraditionalChinese: return ZhHantLabel;
                case StyleHongKongChinese: return ZhHkLabel;
                case StyleKorean: return KoKrLabel;
                default: return null;
            }
        }

        private static TMP_FontAsset FindFallback(string label)
        {
            List<TMP_FontAsset> fallbacks = TMP_Settings.fallbackFontAssets;
            if (label == null || fallbacks == null)
            {
                return null;
            }
            int index = IndexOfByName(fallbacks, label);
            if (index < 0 && label == ZhHkLabel)
            {
                index = IndexOfByName(fallbacks, ZhHantLabel);
            }
            return index >= 0 ? fallbacks[index] : null;
        }

        private static string ResolveCurrentStyle(string uiLanguage)
        {
            string setting = BasisSettingsDefaults.CjkGlyphStyle != null ? BasisSettingsDefaults.CjkGlyphStyle.RawValue : StyleAuto;
            string osUiCulture = null;
            string osCulture = null;
            try
            {
                osUiCulture = CultureInfo.CurrentUICulture.Name;
                osCulture = CultureInfo.CurrentCulture.Name;
            }
            catch (Exception)
            {
            }
            return ResolveStyle(setting, uiLanguage, osUiCulture, osCulture, Application.systemLanguage);
        }

        /// <summary>
        /// An explicit setting wins. Auto takes the menu language, then the OS language, then
        /// <see cref="Application.systemLanguage"/>. Traditional Chinese becomes Hong Kong when
        /// the OS region is Hong Kong or Macau, since one zh-Hant table serves both.
        /// </summary>
        internal static string ResolveStyle(string setting, string uiLanguage, string osUiCulture, string osCulture, SystemLanguage systemLanguage)
        {
            if (IsStyle(setting))
            {
                return setting;
            }
            string style = StyleForCulture(uiLanguage) ?? StyleForCulture(osUiCulture) ?? StyleForCulture(osCulture) ?? StyleForSystemLanguage(systemLanguage);
            if (style == StyleTraditionalChinese && (IsHongKongRegion(osUiCulture) || IsHongKongRegion(osCulture)))
            {
                return StyleHongKongChinese;
            }
            return style;
        }

        internal static bool IsStyle(string value)
        {
            return value == StyleJapanese || value == StyleSimplifiedChinese || value == StyleTraditionalChinese || value == StyleHongKongChinese || value == StyleKorean;
        }

        internal static string StyleForCulture(string culture)
        {
            if (string.IsNullOrEmpty(culture))
            {
                return null;
            }
            string code = culture.Replace('_', '-').ToLowerInvariant();
            if (HasLanguage(code, "ja"))
            {
                return StyleJapanese;
            }
            if (HasLanguage(code, "ko"))
            {
                return StyleKorean;
            }
            if (!HasLanguage(code, "zh"))
            {
                return null;
            }
            if (IsHongKongRegion(code))
            {
                return StyleHongKongChinese;
            }
            if (code.Contains("-hant") || code.EndsWith("-tw") || code.Contains("-tw-"))
            {
                return StyleTraditionalChinese;
            }
            return StyleSimplifiedChinese;
        }

        private static bool HasLanguage(string code, string language)
        {
            return code.StartsWith(language, StringComparison.Ordinal) && (code.Length == language.Length || code[language.Length] == '-');
        }

        private static bool IsHongKongRegion(string culture)
        {
            if (string.IsNullOrEmpty(culture))
            {
                return false;
            }
            string[] parts = culture.Replace('_', '-').Split('-');
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Equals("HK", StringComparison.OrdinalIgnoreCase) || parts[i].Equals("MO", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string StyleForSystemLanguage(SystemLanguage language)
        {
            switch (language)
            {
                case SystemLanguage.Japanese: return StyleJapanese;
                case SystemLanguage.Korean: return StyleKorean;
                case SystemLanguage.ChineseTraditional: return StyleTraditionalChinese;
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified: return StyleSimplifiedChinese;
                default: return null;
            }
        }

        private static int IndexOfByName(List<TMP_FontAsset> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].name == name)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool ContainsByName(List<TMP_FontAsset> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].name == name)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Walks the candidate list and returns the first TMP font asset
        /// that TMP can create from an OS font family. This uses the
        /// family-name overload of <see cref="TMP_FontAsset.CreateFontAsset(string, string, int)"/>,
        /// which internally calls FontEngine.TryGetSystemFontReference and
        /// correctly sets the atlas population mode to DynamicOS — that's
        /// the only path that makes the asset actually pull glyphs from the
        /// host OS font file at runtime.
        /// </summary>
        private static TMP_FontAsset TryCreateDynamicOSFallback(string label, string[] candidates, bool installedOnly)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                string family = candidates[i];
                if (installedOnly && !IsFontInstalled(family))
                {
                    continue;
                }
                TMP_FontAsset tmpFont;
                try
                {
                    tmpFont = TMP_FontAsset.CreateFontAsset(family, "Regular", DefaultSamplingPointSize);
                }
                catch (Exception e)
                {
                    BasisDebug.LogError($"[BasisTMPFontFallbacks] CreateFontAsset threw for '{family}': {e.Message}");
                    continue;
                }

                if (tmpFont == null)
                {
                    continue;
                }

                tmpFont.name = label;
                return tmpFont;
            }

            return null;
        }

        private static HashSet<string> _installedFontNames;

        private static bool IsFontInstalled(string family)
        {
            if (_installedFontNames == null)
            {
                _installedFontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string[] names = Font.GetOSInstalledFontNames();
                    if (names != null)
                    {
                        for (int i = 0; i < names.Length; i++)
                        {
                            if (!string.IsNullOrEmpty(names[i]))
                            {
                                _installedFontNames.Add(names[i]);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return _installedFontNames.Count == 0 || _installedFontNames.Contains(family);
        }
    }
}
