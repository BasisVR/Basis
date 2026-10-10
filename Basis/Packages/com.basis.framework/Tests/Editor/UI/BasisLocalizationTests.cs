using System.Collections.Generic;
using System.Text.RegularExpressions;
using Basis.BasisUI;
using Basis.Localization;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI.UI_Panels;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.UI
{
    /// <summary>
    /// Exercises BasisLocalization against the project's REAL language tables (loaded once per
    /// Editor session via Addressables) rather than a mock, so these tests double as a regression
    /// guard on the actual en.json/lang-file content — a key that never made it into en.json, or a
    /// translation that silently fell back to English, fails here rather than only in the app.
    ///
    /// BasisLocalization is a static class with process-wide state (current language, missing-key
    /// set), so every test restores what it changed in TearDown rather than relying on test order.
    /// </summary>
    [TestFixture]
    public class BasisLocalizationTests
    {
        private string _originalLanguage;
        private bool _originalTrackMissingKeys;

        [SetUp]
        public void SetUp()
        {
            _originalLanguage = BasisLocalization.CurrentLanguage;
            _originalTrackMissingKeys = BasisLocalization.TrackMissingKeys;
            BasisLocalization.ClearMissingKeys();
        }

        [TearDown]
        public void TearDown()
        {
            BasisLocalization.LoadLanguage(_originalLanguage);
            BasisLocalization.TrackMissingKeys = _originalTrackMissingKeys;
            BasisLocalization.ClearMissingKeys();
        }

        [Test]
        public void Get_OnAKnownKeyReturnsNonEmptyText()
        {
            BasisLocalization.LoadLanguage("en");
            Assert.That(BasisLocalization.Get("settings.title"), Is.Not.Empty.And.Not.EqualTo("settings.title"));
        }

        [Test]
        public void Get_OnAnUnknownKeyFallsBackToTheKeyItselfSoAMissingTranslationIsVisibleNotBlank()
        {
            const string fakeKey = "this.key.does.not.exist.anywhere.zz";
            Assert.That(BasisLocalization.Get(fakeKey), Is.EqualTo(fakeKey));
        }

        [Test]
        public void Get_RecordsAMissOnlyWhenTrackingIsEnabled()
        {
            const string fakeKey = "this.key.also.does.not.exist.zz";

            BasisLocalization.TrackMissingKeys = true;
            BasisLocalization.Get(fakeKey);
            Assert.That(BasisLocalization.MissingKeys, Does.Contain(fakeKey));

            BasisLocalization.ClearMissingKeys();
            BasisLocalization.TrackMissingKeys = false;
            BasisLocalization.Get(fakeKey);
            Assert.That(BasisLocalization.MissingKeys, Does.Not.Contain(fakeKey));
        }

        [Test]
        public void TryGet_ReturnsFalseWithoutRecordingAMissForAKeyThatGenuinelyDoesNotExist()
        {
            BasisLocalization.TrackMissingKeys = true;
            BasisLocalization.ClearMissingKeys();

            bool found = BasisLocalization.TryGet("this.optional.key.zz", out string value);

            Assert.That(found, Is.False);
            Assert.That(value, Is.Null);
            Assert.That(BasisLocalization.MissingKeys, Is.Empty,
                "TryGet is for options where absence is normal (e.g. a per-option tooltip), not a translation gap to flag");
        }

        [Test]
        public void SetLanguage_UpdatesCurrentLanguageAndFiresOnLanguageChanged()
        {
            bool fired = false;
            System.Action handler = () => fired = true;
            BasisLocalization.OnLanguageChanged += handler;
            try
            {
                BasisLocalization.LoadLanguage("ja");
                Assert.That(BasisLocalization.CurrentLanguage, Is.EqualTo("ja"));
                Assert.That(fired, Is.True);
            }
            finally
            {
                BasisLocalization.OnLanguageChanged -= handler;
            }
        }

        [Test]
        public void SetLanguage_ReResolvesGetAgainstTheNewTableLiveNotACachedLeftover()
        {
            // This is the exact mechanism the provider-title bug (menu.provider.mirror etc.) relied
            // on being true: a live Get() call must return DIFFERENT text after SetLanguage, not
            // whatever text was captured the first time something read it.
            BasisLocalization.LoadLanguage("en");
            string english = BasisLocalization.Get("settings.title");

            BasisLocalization.LoadLanguage("ja");
            string japanese = BasisLocalization.Get("settings.title");

            Assert.That(japanese, Is.Not.EqualTo(english),
                "settings.title has a real Japanese translation; if this equals English the table failed to load or switch");
        }

        [Test]
        public void SetLanguage_UnknownCodeFallsBackToEnglishRatherThanThrowing()
        {
            // SetLanguage deliberately logs an error for a code with no loaded table before
            // falling back (see BasisLocalization.cs) — that diagnostic is the intended behavior,
            // not a bug, so it must be expected rather than left to auto-fail the test.
            LogAssert.Expect(LogType.Error, new Regex("Language table not loaded for code"));

            BasisLocalization.LoadLanguage("not-a-real-language-code");
            Assert.That(BasisLocalization.CurrentLanguage, Is.EqualTo("en"));
        }

        // ---- Regression coverage for this session's new keys ------------------------------------
        // Every key added while fixing the "stuck on language switch" bugs. A key referenced by code
        // but missing from en.json is invisible to a build (Get() just returns the raw string), so
        // this is exactly the class of bug the whole session was about.

        private static readonly string[] NewProviderTitleKeys =
        {
            "menu.provider.mirror",
            "menu.provider.cameraSettings",
            "menu.provider.mediaPlayers",
        };

        private static readonly string[] NewDropdownAndDialogKeys =
        {
            "settings.controls.dominantHand.right",
            "settings.controls.dominantHand.left",
            "library.sort.name",
            "library.sort.dateOldestToNewest",
            "library.sort.dateNewestToOldest",
            "library.filter.all",
            "library.filter.embedded",
            "library.filter.local",
            "library.filter.networked",
            "library.filter.gameObject",
            "library.filter.scene",
            "library.filter.avatar",
            "library.filter.adminOnly",
            "library.filter.persistentOnly",
            "library.filter.notPersistent",
            "library.filter.placedByMe",
            "library.filter.notPlacedByMe",
            "settings.admin.title.type.avatar",
            "settings.admin.title.type.world",
            "settings.admin.title.type.prop",
            "settings.admin.confirm.addDefaultLibrary.title",
            "settings.admin.confirm.addDefaultLibrary.body",
            "settings.admin.confirm.addDefaultLibrary.confirm",
            "settings.admin.confirm.removeDefaultLibrary.title",
            "settings.admin.confirm.removeDefaultLibrary.body",
            "settings.admin.confirm.removeDefaultLibrary.confirm",
        };

        private static readonly string[] SpotCheckLanguages = { "ja", "de", "fr", "ar", "zh-Hans" };

        [Test]
        public void NewProviderTitleKeys_ResolveInEnglish([ValueSource(nameof(NewProviderTitleKeys))] string key)
        {
            BasisLocalization.LoadLanguage("en");
            Assert.That(BasisLocalization.Get(key), Is.Not.EqualTo(key),
                $"'{key}' fell through to the raw key — it is missing from en.json");
        }

        [Test]
        public void NewDropdownAndDialogKeys_ResolveInEnglish([ValueSource(nameof(NewDropdownAndDialogKeys))] string key)
        {
            BasisLocalization.LoadLanguage("en");
            Assert.That(BasisLocalization.Get(key), Is.Not.EqualTo(key),
                $"'{key}' fell through to the raw key — it is missing from en.json");
        }

        [Test]
        public void NewProviderTitleKeys_ResolveInEverySpotCheckLanguage(
            [ValueSource(nameof(NewProviderTitleKeys))] string key,
            [ValueSource(nameof(SpotCheckLanguages))] string language)
        {
            // Falling back to English for a missing translation is a lesser bug than the raw-key
            // case above, but still worth catching — a language file that never got this batch's
            // insert would silently show English instead of failing loudly.
            BasisLocalization.LoadLanguage(language);
            Assert.That(BasisLocalization.Get(key), Is.Not.EqualTo(key),
                $"'{key}' fell through to the raw key under '{language}'");
        }

        [Test]
        public void NewDropdownAndDialogKeys_ResolveInEverySpotCheckLanguage(
            [ValueSource(nameof(NewDropdownAndDialogKeys))] string key,
            [ValueSource(nameof(SpotCheckLanguages))] string language)
        {
            BasisLocalization.LoadLanguage(language);
            Assert.That(BasisLocalization.Get(key), Is.Not.EqualTo(key),
                $"'{key}' fell through to the raw key under '{language}'");
        }

        [Test]
        public void AdminConfirmDialogText_IsActuallyTranslatedNotJustEchoingEnglish(
            [ValueSource(nameof(SpotCheckLanguages))] string language)
        {
            // Full sentences, unlike single-word labels, essentially never coincide with English by
            // accident (a "Local"/"Avatar"-style loanword might legitimately match) — a safe place
            // to assert on TRANSLATED, not merely PRESENT.
            BasisLocalization.LoadLanguage("en");
            string englishAddTitle = BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.title");
            string englishAddBody = BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.body");
            string englishRemoveBody = BasisLocalization.Get("settings.admin.confirm.removeDefaultLibrary.body");

            BasisLocalization.LoadLanguage(language);
            Assert.That(BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.title"), Is.Not.EqualTo(englishAddTitle));
            Assert.That(BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.body"), Is.Not.EqualTo(englishAddBody));
            Assert.That(BasisLocalization.Get("settings.admin.confirm.removeDefaultLibrary.body"), Is.Not.EqualTo(englishRemoveBody));
        }

        [Test]
        public void AdminConfirmDialogBodies_KeepTheirFormatPlaceholderInEveryLanguage(
            [ValueSource(nameof(SpotCheckLanguages))] string language)
        {
            BasisLocalization.LoadLanguage(language);
            Assert.That(BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.body"), Does.Contain("{0}"));
            Assert.That(BasisLocalization.Get("settings.admin.confirm.removeDefaultLibrary.body"), Does.Contain("{0}"));
        }

        private const string EmbeddedItemsCatalogPath = "Packages/com.basis.sdk/Settings/EmbeddedItemsCatalog.asset";
        private const string FrameworkLanguagesFolder = "Packages/com.basis.framework/BasisUI/Localization/Languages";

        [Test]
        public void EmbeddedItemDisplayNames_ExistInEveryLanguageFile()
        {
            EmbeddedItemsCatalogAsset catalog = AssetDatabase.LoadAssetAtPath<EmbeddedItemsCatalogAsset>(EmbeddedItemsCatalogPath);
            Assert.That(catalog, Is.Not.Null, $"catalog not found: {EmbeddedItemsCatalogPath}");
            foreach ((string path, HashSet<string> keys) in FrameworkLanguageKeySets())
            {
                foreach (EmbeddedItemDefinition definition in catalog.Entries)
                {
                    if (!definition.Key.EmbeddedSettings.IsEmbedded) continue;
                    Assert.That(definition.HasCustomDisplayName && !string.IsNullOrEmpty(definition.DisplayNameKey), Is.True,
                        $"'{definition.Key.Url}' has no display name, so the library and the hotbar show its address in every language");
                    Assert.That(keys, Does.Contain(definition.DisplayNameKey), $"'{definition.DisplayNameKey}' is missing from {path}");
                }
            }
        }

        [Test]
        public void LabelKeysBuiltFromEnums_ExistInEveryLanguageFile()
        {
            List<string> expected = new List<string>();
            foreach (BasisBoneTrackedRole role in System.Enum.GetValues(typeof(BasisBoneTrackedRole)))
            {
                expected.Add("ui.bodyRole." + LowerFirst(role.ToString()));
            }
            foreach (BasisActionDriver.ActionId action in System.Enum.GetValues(typeof(BasisActionDriver.ActionId)))
            {
                if (action != BasisActionDriver.ActionId.Count) expected.Add("settings.controls.actionId." + LowerFirst(action.ToString()));
            }
            foreach ((string path, HashSet<string> keys) in FrameworkLanguageKeySets())
            {
                foreach (string key in expected)
                {
                    Assert.That(keys, Does.Contain(key), $"'{key}' is missing from {path}, so that label shows its raw key");
                }
            }
        }

        private static List<(string path, HashSet<string> keys)> FrameworkLanguageKeySets()
        {
            string[] languageFiles = AssetDatabase.FindAssets("t:TextAsset", new[] { FrameworkLanguagesFolder });
            Assert.That(languageFiles, Is.Not.Empty);
            List<(string path, HashSet<string> keys)> sets = new List<(string path, HashSet<string> keys)>();
            foreach (string guid in languageFiles)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                BasisLanguageTable table = JsonUtility.FromJson<BasisLanguageTable>(AssetDatabase.LoadAssetAtPath<TextAsset>(path).text);
                HashSet<string> keys = new HashSet<string>();
                foreach (BasisLanguageEntry entry in table.entries)
                {
                    if (!string.IsNullOrEmpty(entry.value)) keys.Add(entry.key);
                }
                sets.Add((path, keys));
            }
            return sets;
        }

        private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value.Substring(1);

        [Test]
        public void CachedEmbeddedItemAndItsPinnedButton_FollowALanguageSwitch()
        {
            BasisDataStoreItemKeys.ItemKey item = null;
            EmbeddedItemDefinition definition = null;
            foreach (BasisDataStoreItemKeys.ItemKey candidate in EmbeddedItems.HardcodedKeys)
            {
                if (EmbeddedItems.TryGetDefinition(candidate, out definition) && definition.HasCustomDisplayName)
                {
                    item = candidate;
                    break;
                }
            }
            Assert.That(item, Is.Not.Null, "no embedded item has a display name to follow");

            CachedMetaData.TryGetMeta(item.Url, out CachedMetaData.CachedContent previous);
            CachedMetaData.CachedContent meta = new CachedMetaData.CachedContent
            {
                BasisBundleConnector = new BasisBundleConnector { BasisBundleDescription = new BasisBundleDescription() }
            };
            try
            {
                CachedMetaData.SetMetaData(item.Url, meta);
                BasisLocalization.LoadLanguage("en");
                Assert.That(meta.Name, Is.EqualTo(BasisLocalization.Get(definition.DisplayNameKey)), "the cached name did not follow a language switch");
                PinnedItemProvider pinned = new PinnedItemProvider(item, meta);
                foreach (string language in SpotCheckLanguages)
                {
                    BasisLocalization.LoadLanguage(language);
                    string expected = BasisLocalization.Get(definition.DisplayNameKey);
                    Assert.That(meta.Name, Is.EqualTo(expected), $"the cached name stayed in the previous language under '{language}'");
                    Assert.That(meta.BasisBundleConnector.BasisBundleDescription.AssetBundleDescription, Is.EqualTo(BasisLocalization.Get("library.embeddedItem")));
                    Assert.That(pinned.Title, Is.EqualTo(LibraryProviderStrUtil.TitleToCase(expected)), $"the pinned hotbar button stayed in the previous language under '{language}'");
                }
            }
            finally
            {
                if (previous != null) CachedMetaData.SetMetaData(item.Url, previous);
                else CachedMetaData.RemoveMetaData(item.Url);
            }
        }
    }
}
