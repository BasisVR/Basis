using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisTMPCjkStyleTests
    {
        private const string JapaneseFontPath = "Packages/com.basis.sdk/Fonts/NotoSansJP-Regular.ttf";
        private const string LatinFontPath = "Packages/com.basis.sdk/Fonts/Inter_24pt-Regular.ttf";
        private const string JaLabel = "Basis Fallback - ja-JP";
        private const string ZhHansLabel = "Basis Fallback - zh-Hans";

        [TestCase("言語", null)]
        [TestCase("hello", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        [TestCase("言語を切り替える", BasisTMPFontFallbacks.StyleJapanese)]
        [TestCase("カタカナ", BasisTMPFontFallbacks.StyleJapanese)]
        [TestCase("ｶﾀｶﾅ", BasisTMPFontFallbacks.StyleJapanese)]
        [TestCase("한국어 漢字", BasisTMPFontFallbacks.StyleKorean)]
        [TestCase("中文・名字", null)]
        public void DetectStyle_UsesKanaAndHangulOnly(string text, string expected)
        {
            Assert.That(BasisTMPFontFallbacks.DetectStyle(text), Is.EqualTo(expected));
        }

        [TestCase("ja", BasisTMPFontFallbacks.StyleJapanese)]
        [TestCase("ja-JP", BasisTMPFontFallbacks.StyleJapanese)]
        [TestCase("ko-KR", BasisTMPFontFallbacks.StyleKorean)]
        [TestCase("zh", BasisTMPFontFallbacks.StyleSimplifiedChinese)]
        [TestCase("zh-CN", BasisTMPFontFallbacks.StyleSimplifiedChinese)]
        [TestCase("zh-Hans", BasisTMPFontFallbacks.StyleSimplifiedChinese)]
        [TestCase("zh-SG", BasisTMPFontFallbacks.StyleSimplifiedChinese)]
        [TestCase("zh-Hant", BasisTMPFontFallbacks.StyleTraditionalChinese)]
        [TestCase("zh-TW", BasisTMPFontFallbacks.StyleTraditionalChinese)]
        [TestCase("zh-HK", BasisTMPFontFallbacks.StyleHongKongChinese)]
        [TestCase("zh-Hant-HK", BasisTMPFontFallbacks.StyleHongKongChinese)]
        [TestCase("zh-MO", BasisTMPFontFallbacks.StyleHongKongChinese)]
        [TestCase("en-US", null)]
        [TestCase("jam", null)]
        [TestCase("", null)]
        public void StyleForCulture_MapsCjkCultures(string culture, string expected)
        {
            Assert.That(BasisTMPFontFallbacks.StyleForCulture(culture), Is.EqualTo(expected));
        }

        [Test]
        public void ResolveStyle_ExplicitSettingWins()
        {
            string style = BasisTMPFontFallbacks.ResolveStyle(BasisTMPFontFallbacks.StyleKorean, "ja", "ja-JP", "ja-JP", SystemLanguage.Japanese);
            Assert.That(style, Is.EqualTo(BasisTMPFontFallbacks.StyleKorean));
        }

        [Test]
        public void ResolveStyle_TraditionalMenuInHongKongUsesHongKongForms()
        {
            string style = BasisTMPFontFallbacks.ResolveStyle(BasisTMPFontFallbacks.StyleAuto, "zh-Hant", "en-US", "en-HK", SystemLanguage.English);
            Assert.That(style, Is.EqualTo(BasisTMPFontFallbacks.StyleHongKongChinese));
        }

        [Test]
        public void ResolveStyle_TraditionalMenuInTaiwanStaysTaiwan()
        {
            string style = BasisTMPFontFallbacks.ResolveStyle(BasisTMPFontFallbacks.StyleAuto, "zh-Hant", "zh-TW", "zh-TW", SystemLanguage.ChineseTraditional);
            Assert.That(style, Is.EqualTo(BasisTMPFontFallbacks.StyleTraditionalChinese));
        }

        [Test]
        public void ResolveStyle_NonCjkMenuFallsBackToOsLanguage()
        {
            string style = BasisTMPFontFallbacks.ResolveStyle(BasisTMPFontFallbacks.StyleAuto, "en", "ja-JP", "en-US", SystemLanguage.English);
            Assert.That(style, Is.EqualTo(BasisTMPFontFallbacks.StyleJapanese));
        }

        [Test]
        public void ResolveStyle_NothingCjkGivesNull()
        {
            string style = BasisTMPFontFallbacks.ResolveStyle(BasisTMPFontFallbacks.StyleAuto, "de", "de-DE", "de-DE", SystemLanguage.German);
            Assert.That(style, Is.Null);
        }

        [Test]
        public void CjkOrder_PreferredStyleComesFirst()
        {
            Assert.That(BasisTMPFontFallbacks.CjkOrderFor(null)[0], Is.EqualTo(JaLabel));
            Assert.That(BasisTMPFontFallbacks.CjkOrderFor(BasisTMPFontFallbacks.StyleSimplifiedChinese)[0], Is.EqualTo(ZhHansLabel));
            Assert.That(BasisTMPFontFallbacks.CjkOrderFor(BasisTMPFontFallbacks.StyleHongKongChinese), Is.EqualTo(new[]
            {
                "Basis Fallback - zh-HK", "Basis Fallback - zh-Hant", ZhHansLabel, JaLabel, "Basis Fallback - ko-KR",
            }));
        }

        [Test]
        public void SharedHanCodePoints_FollowTheReorderAndTheTextLanguage()
        {
            Font japanese = AssetDatabase.LoadAssetAtPath<Font>(JapaneseFontPath);
            Font latin = AssetDatabase.LoadAssetAtPath<Font>(LatinFontPath);
            Assert.That(japanese, Is.Not.Null, JapaneseFontPath);
            Assert.That(latin, Is.Not.Null, LatinFontPath);

            List<TMP_FontAsset> fallbacks = TMP_Settings.fallbackFontAssets;
            Assert.That(fallbacks, Is.Not.Null);
            List<TMP_FontAsset> saved = new List<TMP_FontAsset>(fallbacks);
            string savedStyle = BasisTMPFontFallbacks.PreferredStyle;

            TMP_FontAsset jaSlot = TMP_FontAsset.CreateFontAsset(japanese);
            TMP_FontAsset zhSlot = TMP_FontAsset.CreateFontAsset(japanese);
            TMP_FontAsset baseFont = TMP_FontAsset.CreateFontAsset(latin);
            jaSlot.name = JaLabel;
            zhSlot.name = ZhHansLabel;
            GameObject owner = new GameObject("CJK style test", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            TMP_Text label = owner.GetComponent<TMP_Text>();
            try
            {
                fallbacks.Clear();
                fallbacks.Add(zhSlot);
                fallbacks.Add(jaSlot);
                BasisTMPFontFallbacks.ApplyCjkPriority(BasisTMPFontFallbacks.StyleSimplifiedChinese);
                Assert.That(fallbacks[0], Is.SameAs(zhSlot));

                label.font = baseFont;
                label.text = "言語";
                label.ForceMeshUpdate(true, true);
                Assert.That(label.textInfo.characterInfo[0].fontAsset, Is.SameAs(zhSlot));

                BasisTMPFontFallbacks.ApplyCjkPriority(BasisTMPFontFallbacks.StyleJapanese);
                Assert.That(fallbacks[0], Is.SameAs(jaSlot));
                BasisTMPFontFallbacks.ClearFallbackLookupCaches(new[] { label });
                label.ForceMeshUpdate(true, true);
                Assert.That(label.textInfo.characterInfo[0].fontAsset, Is.SameAs(jaSlot),
                    "the base font kept the Han glyph it cached from the old first fallback");

                BasisTMPFontFallbacks.ApplyCjkPriority(BasisTMPFontFallbacks.StyleSimplifiedChinese);
                BasisTMPFontFallbacks.ClearFallbackLookupCaches(new[] { label });
                BasisTMPFontFallbacks.ApplyFontForText(label, "A言語です");
                Assert.That(label.font, Is.Not.SameAs(baseFont), "Japanese text under a Chinese preference needs a variant");
                label.text = "A言語です";
                label.ForceMeshUpdate(true, true);
                TMP_CharacterInfo[] characters = label.textInfo.characterInfo;
                Assert.That(characters[0].fontAsset, Is.SameAs(baseFont).Or.SameAs(label.font), "Latin must stay on the base font");
                Assert.That(characters[1].fontAsset, Is.SameAs(jaSlot));
                Assert.That(characters[3].fontAsset, Is.SameAs(jaSlot));

                BasisTMPFontFallbacks.ApplyFontForText(label, "言語");
                Assert.That(label.font, Is.SameAs(baseFont), "Han-only text goes back to the base font and the global order");
                label.text = "言語";
                label.ForceMeshUpdate(true, true);
                Assert.That(label.textInfo.characterInfo[0].fontAsset, Is.SameAs(zhSlot));
            }
            finally
            {
                Object.DestroyImmediate(owner);
                BasisTMPFontFallbacks.ReleaseVariants();
                fallbacks.Clear();
                fallbacks.AddRange(saved);
                BasisTMPFontFallbacks.ApplyCjkPriority(savedStyle);
                foreach (TMP_FontAsset font in new[] { jaSlot, zhSlot, baseFont })
                {
                    Object.DestroyImmediate(font.material);
                    foreach (Texture2D atlas in font.atlasTextures)
                    {
                        Object.DestroyImmediate(atlas);
                    }
                    Object.DestroyImmediate(font);
                }
            }
        }
    }
}
