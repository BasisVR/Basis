using System.Collections.Generic;
using System.Text.RegularExpressions;
using Basis.BasisUI;
using NUnit.Framework;
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
            BasisLocalization.SetLanguage(_originalLanguage);
            BasisLocalization.TrackMissingKeys = _originalTrackMissingKeys;
            BasisLocalization.ClearMissingKeys();
        }

        [Test]
        public void Get_OnAKnownKeyReturnsNonEmptyText()
        {
            BasisLocalization.SetLanguage("en");
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
                BasisLocalization.SetLanguage("ja");
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
            BasisLocalization.SetLanguage("en");
            string english = BasisLocalization.Get("settings.title");

            BasisLocalization.SetLanguage("ja");
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

            BasisLocalization.SetLanguage("not-a-real-language-code");
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
            BasisLocalization.SetLanguage("en");
            Assert.That(BasisLocalization.Get(key), Is.Not.EqualTo(key),
                $"'{key}' fell through to the raw key — it is missing from en.json");
        }

        [Test]
        public void NewDropdownAndDialogKeys_ResolveInEnglish([ValueSource(nameof(NewDropdownAndDialogKeys))] string key)
        {
            BasisLocalization.SetLanguage("en");
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
            BasisLocalization.SetLanguage(language);
            Assert.That(BasisLocalization.Get(key), Is.Not.EqualTo(key),
                $"'{key}' fell through to the raw key under '{language}'");
        }

        [Test]
        public void NewDropdownAndDialogKeys_ResolveInEverySpotCheckLanguage(
            [ValueSource(nameof(NewDropdownAndDialogKeys))] string key,
            [ValueSource(nameof(SpotCheckLanguages))] string language)
        {
            BasisLocalization.SetLanguage(language);
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
            BasisLocalization.SetLanguage("en");
            string englishAddTitle = BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.title");
            string englishAddBody = BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.body");
            string englishRemoveBody = BasisLocalization.Get("settings.admin.confirm.removeDefaultLibrary.body");

            BasisLocalization.SetLanguage(language);
            Assert.That(BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.title"), Is.Not.EqualTo(englishAddTitle));
            Assert.That(BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.body"), Is.Not.EqualTo(englishAddBody));
            Assert.That(BasisLocalization.Get("settings.admin.confirm.removeDefaultLibrary.body"), Is.Not.EqualTo(englishRemoveBody));
        }

        [Test]
        public void AdminConfirmDialogBodies_KeepTheirFormatPlaceholderInEveryLanguage(
            [ValueSource(nameof(SpotCheckLanguages))] string language)
        {
            BasisLocalization.SetLanguage(language);
            Assert.That(BasisLocalization.Get("settings.admin.confirm.addDefaultLibrary.body"), Does.Contain("{0}"));
            Assert.That(BasisLocalization.Get("settings.admin.confirm.removeDefaultLibrary.body"), Does.Contain("{0}"));
        }
    }
}
