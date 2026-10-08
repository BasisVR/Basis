using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
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
        private const string ShippedJapaneseFontAddress = "Packages/com.basis.sdk/Fonts/NotoSansJP-Regular.ttf";
        private const string EmojiLabel = "Basis Fallback - Emoji";
        private const string ShippedEmojiFontAddress = "Packages/com.basis.sdk/Fonts/NotoEmoji-Regular.ttf";

        [NoAutoStaticsCleanup] private static readonly string[] CjkLabels = { JaJpLabel, KoKrLabel, ZhHansLabel, ZhHantLabel };

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
                // macOS
                "PingFang TC",
                // Linux / Noto
                "Noto Sans CJK TC",
                "Noto Sans TC",
            }),
            ("Basis Fallback - zh-Hans", new[]
            {
                // Windows
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                // macOS
                "PingFang SC",
                // Linux / Noto
                "Noto Sans CJK SC",
                "Noto Sans SC",
                "Source Han Sans",
            }),
            ("Basis Fallback - ko-KR", new[]
            {
                // Windows
                "Malgun Gothic",
                // macOS
                "Apple SD Gothic Neo",
                // Linux / Noto
                "Noto Sans CJK KR",
                "Noto Sans KR",
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
        private static void AutoInstall()
        {
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

                TMP_FontAsset shipped = group.Label == JaJpLabel ? GetShippedJapaneseFallback() : group.Label == EmojiLabel ? GetShippedEmojiFallback() : null;
                TMP_FontAsset tmpFont = shipped ?? TryCreateDynamicOSFallback(group.Label, group.Candidates);
                if (tmpFont != null)
                {
                    fallbacks.Add(tmpFont);
                    if (group.Label == EmojiLabel)
                    {
                        RegisterEmojiFallback(tmpFont);
                    }
                    BasisDebug.Log($"[BasisTMPFontFallbacks] Installed {group.Label} using OS font family '{tmpFont.faceInfo.familyName}'.");
                }
                else
                {
                    BasisDebug.LogError($"[BasisTMPFontFallbacks] None of the candidates for {group.Label} could be resolved on this OS. Candidates tried: {string.Join(", ", group.Candidates)}");
                }
            }

            // Localization isn't up this early; seed from the OS locale and let
            // RefreshJapanesePriority re-apply from the UI language before menus render.
            ApplyCjkPriority(Application.systemLanguage == SystemLanguage.Japanese ? JaJpLabel : null);
            BasisLocalization.OnLanguageChanged += RefreshJapanesePriority;
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
        /// and re-applies language-aware CJK ordering from the resolved UI language.
        /// Call once <see cref="BasisLocalization"/> is initialized and on every
        /// language change.
        /// </summary>
        public static void RefreshJapanesePriority()
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

            ApplyCjkPriority(CjkLabelForLanguage(BasisLocalization.CurrentLanguage));
        }

        /// <summary>
        /// Moves the given CJK fallback ahead of the other CJK fallbacks in the
        /// global list so shared Han glyphs resolve to that language's font. No-op
        /// when the language has no CJK fallback (non-CJK locales keep the declared,
        /// Chinese-first order). <paramref name="targetLabel"/> is one of
        /// <see cref="CjkLabels"/> or null.
        /// </summary>
        private static void ApplyCjkPriority(string targetLabel)
        {
            if (targetLabel == null)
            {
                return;
            }

            List<TMP_FontAsset> fallbacks = TMP_Settings.fallbackFontAssets;
            if (fallbacks == null)
            {
                return;
            }

            int target = IndexOfByName(fallbacks, targetLabel);
            if (target < 0)
            {
                return;
            }

            int firstCjk = target;
            for (int i = 0; i < CjkLabels.Length; i++)
            {
                int idx = IndexOfByName(fallbacks, CjkLabels[i]);
                if (idx >= 0 && idx < firstCjk)
                {
                    firstCjk = idx;
                }
            }

            if (target > firstCjk)
            {
                TMP_FontAsset entry = fallbacks[target];
                fallbacks.RemoveAt(target);
                fallbacks.Insert(firstCjk, entry);
            }
        }

        private static string CjkLabelForLanguage(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                return null;
            }
            if (language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return JaJpLabel;
            if (language.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return KoKrLabel;
            if (language.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)) return ZhHantLabel;
            if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return ZhHansLabel;
            return null;
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
        private static TMP_FontAsset TryCreateDynamicOSFallback(string label, string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                string family = candidates[i];
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
    }
}
