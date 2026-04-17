using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

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
    public static class BasisTMPFontFallbacks
    {
        private const int DefaultSamplingPointSize = 90;

        private static bool _installed;

        /// <summary>
        /// Ordered candidates for each fallback slot. Names are OS font
        /// family names and are fed one-by-one to
        /// <see cref="TMP_FontAsset.CreateFontAsset(string, string, int)"/>,
        /// which asks the underlying FontEngine to resolve the font file on
        /// the host OS. The first name that resolves wins. The list
        /// intentionally overlaps Windows / macOS / Linux / Android so a
        /// single array serves every platform.
        /// </summary>
        private static readonly (string Label, string[] Candidates)[] FallbackGroups = new[]
        {
            ("Basis Fallback - CJK", new[]
            {
                // Windows 10/11 (ships by default, including English SKUs)
                "Yu Gothic UI",
                "Yu Gothic",
                "Meiryo UI",
                "Meiryo",
                "MS UI Gothic",
                "MS Gothic",
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "Microsoft JhengHei UI",
                "Microsoft JhengHei",
                "Malgun Gothic",
                "SimSun",
                "SimHei",
                // macOS
                "Hiragino Sans",
                "Hiragino Kaku Gothic ProN",
                "PingFang SC",
                "PingFang TC",
                "Apple SD Gothic Neo",
                // Linux / Noto
                "Noto Sans CJK JP",
                "Noto Sans CJK SC",
                "Noto Sans CJK KR",
                "Noto Sans JP",
                "Noto Sans SC",
                "Noto Sans KR",
                "Source Han Sans",
                // Android
                "Droid Sans Fallback",
                // Legacy / universal
                "Arial Unicode MS",
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

                TMP_FontAsset tmpFont = TryCreateDynamicOSFallback(group.Label, group.Candidates);
                if (tmpFont != null)
                {
                    fallbacks.Add(tmpFont);
                    BasisDebug.Log($"[BasisTMPFontFallbacks] Installed {group.Label} using OS font family '{tmpFont.faceInfo.familyName}'.");
                }
                else
                {
                    BasisDebug.LogError($"[BasisTMPFontFallbacks] None of the candidates for {group.Label} could be resolved on this OS. Candidates tried: {string.Join(", ", group.Candidates)}");
                }
            }
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
