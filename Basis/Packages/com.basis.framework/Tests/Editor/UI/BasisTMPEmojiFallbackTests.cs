using System.IO;
using Basis.BasisUI;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisTMPCjkFallbackTests
    {
        [Test]
        public void InvalidateTextMeshes_ReparsesLabelsWhoseUnicodeTextDidNotChange()
        {
            GameObject owner = new GameObject("CJK fallback invalidation test", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            TMP_Text label = owner.GetComponent<TMP_Text>();
            try
            {
                // These are the shared-code-point strings from issue #1086. Their text can stay
                // byte-for-byte identical while the correct Japanese/Chinese glyph shape changes.
                label.text = "切替 言語";
                label.ForceMeshUpdate();
                label.havePropertiesChanged = false;

                int invalidated = BasisTMPFontFallbacks.InvalidateTextMeshes(new[] { label });

                Assert.That(invalidated, Is.EqualTo(1));
                Assert.That(label.havePropertiesChanged, Is.True,
                    "the label would retain the fallback font selected before the language change");
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }
    }

    [TestFixture]
    public class BasisTMPEmojiFallbackTests
    {
        private const string FontPath = "Packages/com.basis.sdk/Fonts/NotoEmoji-Regular.ttf";
        private const string GroupPath = "Assets/AddressableAssetsData/AssetGroups/Basis Foundation Assets.asset";

        [Test]
        public void ShippedEmojiFont_IsRegisteredInTheFoundationAddressablesGroup()
        {
            string guid = AssetDatabase.AssetPathToGUID(FontPath);
            Assert.That(guid, Is.Not.Empty, FontPath + " is not imported");
            string group = File.ReadAllText(GroupPath);
            Assert.That(group, Does.Contain("m_GUID: " + guid));
            Assert.That(group, Does.Contain("m_Address: " + FontPath));
        }

        [Test]
        public void ShippedEmojiFont_RasterizesEmojiOutsideTheBasicMultilingualPlane()
        {
            Font font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            Assert.That(font, Is.Not.Null, FontPath + " did not import as a Font");
            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(font);
            Assert.That(asset, Is.Not.Null);
            try
            {
                uint[] wanted = { 0x1F427, 0x1F600, 0x2764, 0x1F680, 0x1FAE0 };
                foreach (uint unicode in wanted)
                {
                    string label = "U+" + unicode.ToString("X");
                    TMP_Character character = TMP_FontAssetUtilities.GetCharacterFromFontAsset(unicode, asset, false, FontStyles.Normal, FontWeight.Regular, out _);
                    Assert.That(character, Is.Not.Null, label);
                    Assert.That(character.unicode, Is.EqualTo(unicode), label);
                    Assert.That(character.glyph.glyphRect.width, Is.GreaterThan(0), label + " has no atlas rect");
                    Assert.That(asset.characterLookupTable.ContainsKey(unicode), Is.True, label);
                }
            }
            finally
            {
                Object.DestroyImmediate(asset.material);
                foreach (Texture2D atlas in asset.atlasTextures)
                {
                    Object.DestroyImmediate(atlas);
                }
                Object.DestroyImmediate(asset);
            }
        }
    }
}
