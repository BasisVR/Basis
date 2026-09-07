using System.Collections.Generic;
using Basis.Localization;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.UI
{
    /// <summary>
    /// BasisLocalizationCore is the stateless half of localization shared by the runtime
    /// (BasisLocalization) and editor (BasisEditorLocalization) stacks, so a regression here
    /// breaks both at once. All pure functions — no Addressables, no static table state.
    /// </summary>
    [TestFixture]
    public class BasisLocalizationCoreTests
    {
        [Test]
        public void NormalizeLanguageCode_RewritesRetiredZhCodesToZhHans()
        {
            Assert.That(BasisLocalizationCore.NormalizeLanguageCode("zh"), Is.EqualTo("zh-Hans"));
            Assert.That(BasisLocalizationCore.NormalizeLanguageCode("zh-CN"), Is.EqualTo("zh-Hans"));
            Assert.That(BasisLocalizationCore.NormalizeLanguageCode("ZH-cn"), Is.EqualTo("zh-Hans"),
                "a persisted code from an older build may not match the stored casing");
        }

        [Test]
        public void NormalizeLanguageCode_LeavesLiveCodesUnchanged()
        {
            Assert.That(BasisLocalizationCore.NormalizeLanguageCode("zh-Hans"), Is.EqualTo("zh-Hans"));
            Assert.That(BasisLocalizationCore.NormalizeLanguageCode("zh-Hant"), Is.EqualTo("zh-Hant"));
            Assert.That(BasisLocalizationCore.NormalizeLanguageCode("en"), Is.EqualTo("en"));
            Assert.That(BasisLocalizationCore.NormalizeLanguageCode("ja"), Is.EqualTo("ja"));
        }

        [Test]
        public void NormalizeLanguageCode_PassesNullAndEmptyThrough()
        {
            Assert.That(BasisLocalizationCore.NormalizeLanguageCode(null), Is.Null);
            Assert.That(BasisLocalizationCore.NormalizeLanguageCode(string.Empty), Is.Empty);
        }

        [Test]
        public void Format_AppliesArgsWithInvariantCulture()
        {
            string result = BasisLocalizationCore.Format("{0} of {1}", new object[] { 3, 10 });
            Assert.That(result, Is.EqualTo("3 of 10"));
        }

        [Test]
        public void Format_ReturnsTemplateUnchangedWhenNoArgsGiven()
        {
            Assert.That(BasisLocalizationCore.Format("{0} unresolved", null), Is.EqualTo("{0} unresolved"));
            Assert.That(BasisLocalizationCore.Format("{0} unresolved", new object[0]), Is.EqualTo("{0} unresolved"));
        }

        [Test]
        public void Format_ReturnsTemplateUnchangedRatherThanThrowingOnAMalformedTemplate()
        {
            // en.json is hand-edited text; a stray brace in a translation should degrade gracefully
            // instead of crashing whatever UI code called Get(key, args).
            string result = BasisLocalizationCore.Format("{unbalanced", new object[] { "x" });
            Assert.That(result, Is.EqualTo("{unbalanced"));
        }

        [Test]
        public void ResolveSystemLanguage_FallsBackToDefaultForEnglish()
        {
            List<string> available = new List<string> { "en", "ja", "fr" };
            string resolved = BasisLocalizationCore.ResolveSystemLanguage(SystemLanguage.English, available, "en");
            Assert.That(resolved, Is.EqualTo("en"));
        }

        [Test]
        public void ResolveSystemLanguage_PicksTheMatchingAvailableCode()
        {
            List<string> available = new List<string> { "en", "ja", "fr" };
            string resolved = BasisLocalizationCore.ResolveSystemLanguage(SystemLanguage.Japanese, available, "en");
            Assert.That(resolved, Is.EqualTo("ja"));
        }

        [Test]
        public void ResolveSystemLanguage_FallsBackToDefaultWhenTheOsLanguageIsntShipped()
        {
            // The OS reports Korean but no ko.json exists in this project.
            List<string> available = new List<string> { "en", "ja", "fr" };
            string resolved = BasisLocalizationCore.ResolveSystemLanguage(SystemLanguage.Korean, available, "en");
            Assert.That(resolved, Is.EqualTo("en"));
        }

        [Test]
        public void ResolveSystemLanguage_MatchesARegionSpecificCandidateToItsBaseLanguageFile()
        {
            // ChineseSimplified maps to candidate "zh-Hans"; a catalog that only ships base "zh"
            // must still match on the language part rather than falling back to default.
            List<string> available = new List<string> { "en", "zh" };
            string resolved = BasisLocalizationCore.ResolveSystemLanguage(SystemLanguage.ChineseSimplified, available, "en");
            Assert.That(resolved, Is.EqualTo("zh"));
        }
    }
}
