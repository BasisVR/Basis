using System;
using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisMenuLocationTests
    {
        private const string SectionKey = "camera.tab.image";
        private const string RowKey = "camera.gif.dither";
        private const string OtherKey = "settings.tab.graphics";

        private readonly List<GameObject> _roots = new List<GameObject>();
        private RectTransform _content;
        private string _originalLanguage;

        private sealed class BasisSampleFeaturePanelProvider
        {
        }

        [SetUp]
        public void SetUp()
        {
            BasisAddressablePrefabCache.ResetForTests(path => AssetDatabase.LoadAssetAtPath<GameObject>(path));
            _originalLanguage = BasisLocalization.CurrentLanguage;
            BasisLocalization.LoadLanguage("en");
            Assume.That(BasisLocalization.TryGet(SectionKey, out _) && BasisLocalization.TryGet(RowKey, out _) && BasisLocalization.TryGet(OtherKey, out _), "the language tables in this editor do not have the keys these tests name");

            GameObject canvasObject = new GameObject("CanvasRoot", typeof(Canvas));
            _roots.Add(canvasObject);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.sizeDelta = new Vector2(1920, 1080);

            _content = (RectTransform)new GameObject("Content", typeof(RectTransform)).transform;
            _content.SetParent(canvasRect, false);
            _content.sizeDelta = new Vector2(1200, 1000);
        }

        [TearDown]
        public void TearDown()
        {
            BasisAddressablePrefabCache.ResetForTests(null);
            BasisLocalization.LoadLanguage(_originalLanguage);
            for (int Index = 0; Index < _roots.Count; Index++)
            {
                if (_roots[Index]) Object.DestroyImmediate(_roots[Index]);
            }
            _roots.Clear();
        }

        [Test]
        public void AProviderIsNamedByItsTypeSoTheNameSurvivesALanguageChange()
        {
            Assert.That(BasisMenuLocation.MenuKey(typeof(Basis.BasisUI.SettingsProvider)), Is.EqualTo("settings"));
            Assert.That(BasisMenuLocation.MenuKey(typeof(CalibrationProvider)), Is.EqualTo("calibration"));
            Assert.That(BasisMenuLocation.MenuKey(typeof(IndividualPlayerProvider)), Is.EqualTo("individualplayer"));
            Assert.That(BasisMenuLocation.MenuKey(typeof(BasisSampleFeaturePanelProvider)), Is.EqualTo("samplefeature"));
            Assert.That(BasisMenuLocation.MenuKey(null), Is.Null);
        }

        [Test]
        public void ARowTitledFromAKeyIsNamedByThatKeyAndTheSectionOverIt()
        {
            PanelToggle row = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(_content, BasisLocalization.Get(SectionKey), () => row = Toggle(_content, BasisLocalization.Get(RowKey)));

            Assert.That(row.CanShareSetting, Is.True);
            Assert.That(BasisMenuLocation.ControlKey(row), Is.EqualTo("#" + SectionKey + ">" + RowKey));
            Assert.That(BasisMenuLocation.ControlKey(Toggle(_content, BasisLocalization.Get(RowKey))), Is.EqualTo("#" + RowKey));
        }

        [Test]
        public void ARowWithAnUntranslatedTitleIsNamedByItsText()
        {
            PanelToggle row = Toggle(_content, "Plain Row zz");

            Assert.That(BasisMenuLocation.ControlKey(row), Is.EqualTo("=Plain Row zz"));
            Assert.That(BasisMenuLocation.Matcher("=Plain Row zz")(row), Is.True);
            Assert.That(BasisMenuLocation.Matcher("=Other Row zz")(row), Is.False);
        }

        [Test]
        public void TheKeyFindsTheSameRowWhenThePageIsBuiltAgainButNotItsTwinInAnotherSection()
        {
            PanelToggle twin = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(_content, BasisLocalization.Get(OtherKey), () => twin = Toggle(_content, BasisLocalization.Get(RowKey)));
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(_content, BasisLocalization.Get(SectionKey), () => Toggle(_content, BasisLocalization.Get(RowKey)));

            Predicate<PanelComponent> match = BasisMenuLocation.Matcher("#" + SectionKey + ">" + RowKey);
            Assert.That(match, Is.Not.Null);
            Assert.That(match(twin), Is.False);

            PanelComponent found = BasisPanelSearch.RevealControl(_content, match);
            Assert.That(found, Is.Not.Null);
            Assert.That(found, Is.Not.SameAs(twin));
            Assert.That(BasisMenuLocation.ControlKey(found), Is.EqualTo("#" + SectionKey + ">" + RowKey));
            Assert.That(BasisMenuLocation.Matcher("#" + RowKey)(twin), Is.True, "with no section named, any row with the title will do");
            Assert.That(BasisMenuLocation.Matcher("#no.such.key.zz"), Is.Null);
        }

        [Test]
        public void TwoKeysWithTheSameTextAreToldApartByWhichOneBuiltTheTitle()
        {
            string camera = BasisLocalization.Get("camera.modePreset");
            string mirror = BasisLocalization.Get("mirror.preset");
            Assume.That(camera, Is.EqualTo(mirror));

            Assert.That(BasisLocalization.TryFindKey(camera, out string cameraKey), Is.True);
            Assert.That(cameraKey, Is.EqualTo("camera.modePreset"));
            Assert.That(BasisLocalization.TryFindKey(mirror, out string mirrorKey), Is.True);
            Assert.That(mirrorKey, Is.EqualTo("mirror.preset"));
            Assert.That(BasisLocalization.TryFindKey(new string(mirror.ToCharArray()), out string anyKey), Is.True);
            Assert.That(BasisLocalization.Get(anyKey), Is.EqualTo(mirror));
            Assert.That(BasisLocalization.TryFindKey("Not a translated string zz", out _), Is.False);
            Assert.That(BasisLocalization.TryFindKey(null, out _), Is.False);
        }

        [Test]
        public void ATabIsRecordedByItsKeyAndOpenedByIt()
        {
            PanelTabGroup group = PanelTabGroup.CreateNew(_content, LayoutDirection.Vertical);
            PanelTabPage first = PanelTabPage.CreateVertical(group.Descriptor.ContentParent);
            PanelTabPage second = PanelTabPage.CreateVertical(group.Descriptor.ContentParent);
            group.AddTab(BasisLocalization.Get(OtherKey), () => { }, first);
            group.AddTab(BasisLocalization.Get(SectionKey), () => { }, second);
            PanelToggle row = Toggle(second.Descriptor.ContentParent, BasisLocalization.Get(RowKey));

            List<string> titles = new List<string> { "Menu" };
            Assert.That(BasisMenuLocation.DescribeTabs(row.transform, _content, titles), Is.EqualTo(SectionKey));
            Assert.That(titles, Is.EqualTo(new[] { "Menu", BasisLocalization.Get(SectionKey) }));

            group.SetValue(0);
            Assert.That(BasisMenuLocation.SelectTabs(_content, SectionKey), Is.SameAs(second.Descriptor.ContentParent));
            Assert.That(group.Value, Is.EqualTo(1));
            Assert.That(BasisMenuLocation.SelectTabs(_content, "0"), Is.SameAs(first.Descriptor.ContentParent));
            Assert.That(group.Value, Is.EqualTo(0));
            Assert.That(BasisMenuLocation.SelectTabs(_content, BasisMenuLocation.NoTab), Is.SameAs(_content));
            Assert.That(BasisMenuLocation.SelectTabs(_content, "no.such.tab.zz"), Is.Null);
            Assert.That(BasisMenuLocation.DescribeTabs(Toggle(_content, "Outside zz").transform, _content, new List<string>()), Is.EqualTo(BasisMenuLocation.NoTab));
        }

        [Test]
        public void AnUntranslatedTabIsRecordedByItsPlaceAndAHiddenTabIsNotOpened()
        {
            PanelTabGroup group = PanelTabGroup.CreateNew(_content, LayoutDirection.Vertical);
            PanelTabPage first = PanelTabPage.CreateVertical(group.Descriptor.ContentParent);
            PanelTabPage second = PanelTabPage.CreateVertical(group.Descriptor.ContentParent);
            group.AddTab("First zz", () => { }, first);
            group.AddTab("Second zz", () => { }, second);
            PanelToggle row = Toggle(second.Descriptor.ContentParent, BasisLocalization.Get(RowKey));

            Assert.That(BasisMenuLocation.DescribeTabs(row.transform, _content, new List<string>()), Is.EqualTo("1"));
            Assert.That(BasisMenuLocation.SelectTabs(_content, "1"), Is.SameAs(second.Descriptor.ContentParent));

            group.SetValue(0);
            group.SelectionButtons[1].gameObject.SetActive(false);
            Assert.That(BasisMenuLocation.SelectTabs(_content, "1"), Is.Null);
            Assert.That(group.Value, Is.EqualTo(0));
        }

        [Test]
        public void AnUntitledPickerIsNamedByTheKeyItWasGiven()
        {
            Assume.That(BasisLocalization.TryGet("camera.selector", out string name), Is.True);
            PanelDropdown picker = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, _content);
            Assume.That(picker.Descriptor.Title, Is.Null.Or.Empty);
            Assert.That(picker.CanShareSetting, Is.False, "with no title and no key there is nothing to call it");

            picker.ShareTitleKey = "camera.selector";

            Assert.That(picker.CanShareSetting, Is.True);
            Assert.That(BasisMenuLocation.TitleOf(picker), Is.EqualTo(name));
            Assert.That(BasisMenuLocation.ControlKey(picker), Is.EqualTo("#camera.selector"));
            Assert.That(BasisMenuLocation.Matcher("#camera.selector")(picker), Is.True);
        }

        [Test]
        public void APlaceRidesAtTheFrontOfTheTabPath()
        {
            Assert.That(BasisMenuLocation.JoinPlace("0f2c", BasisMenuLocation.NoTab), Is.EqualTo("@0f2c"));
            Assert.That(BasisMenuLocation.JoinPlace("0f2c", OtherKey + ">1"), Is.EqualTo("@0f2c>" + OtherKey + ">1"));
            Assert.That(BasisMenuLocation.JoinPlace(null, OtherKey), Is.EqualTo(OtherKey));

            Assert.That(BasisMenuLocation.TakePlace("@0f2c>" + OtherKey + ">1", out string tabs), Is.EqualTo("0f2c"));
            Assert.That(tabs, Is.EqualTo(OtherKey + ">1"));
            Assert.That(BasisMenuLocation.TakePlace("@0f2c", out string none), Is.EqualTo("0f2c"));
            Assert.That(none, Is.EqualTo(BasisMenuLocation.NoTab));
            Assert.That(BasisMenuLocation.TakePlace(OtherKey, out string plain), Is.Null);
            Assert.That(plain, Is.EqualTo(OtherKey));
            Assert.That(BasisMenuLocation.TakePlace(null, out _), Is.Null);
        }

        [Test]
        public void ATabThatIsAlreadyOpenIsNotClickedAgain()
        {
            PanelTabGroup group = PanelTabGroup.CreateNew(_content, LayoutDirection.Vertical);
            PanelTabPage first = PanelTabPage.CreateVertical(group.Descriptor.ContentParent);
            PanelTabPage second = PanelTabPage.CreateVertical(group.Descriptor.ContentParent);
            int clicks = 0;
            group.AddTab(BasisLocalization.Get(OtherKey), () => { }, first);
            group.AddTab(BasisLocalization.Get(SectionKey), () => clicks++, second);

            group.SetValue(1);
            Assert.That(BasisMenuLocation.SelectTabs(_content, SectionKey), Is.SameAs(second.Descriptor.ContentParent));
            Assert.That(clicks, Is.EqualTo(0));

            group.SetValue(0);
            Assert.That(BasisMenuLocation.SelectTabs(_content, SectionKey), Is.SameAs(second.Descriptor.ContentParent));
            Assert.That(clicks, Is.EqualTo(1));
        }

        [Test]
        public void ARowDrivenByItsCallbackSharesTheValueItShowsAndTakesOneBack()
        {
            PanelToggle row = Toggle(_content, BasisLocalization.Get(RowKey));
            bool? applied = null;
            row.SetValueWithoutNotify(false);
            row.OnValueChanged = value => applied = value;

            Assert.That(row.TryGetShareValue(out string type, out string text, out string display), Is.True);
            Assert.That(type, Is.EqualTo("bool"));
            Assert.That(text, Is.EqualTo("false"));
            Assert.That(display, Is.EqualTo(BasisLocalization.Get("ui.off")));

            Assert.That(row.TryReadShareValue("bool", "true", out string shared, out string current, out bool unchanged), Is.True);
            Assert.That(unchanged, Is.False);
            Assert.That(shared, Is.EqualTo(BasisLocalization.Get("ui.on")));
            Assert.That(current, Is.EqualTo(BasisLocalization.Get("ui.off")));

            Assert.That(row.TryApplyShareValue("bool", "true"), Is.True);
            Assert.That(applied, Is.True);
            Assert.That(row.Value, Is.True);
        }

        private PanelToggle Toggle(RectTransform parent, string title)
        {
            PanelToggle toggle = PanelToggle.CreateNewEntry(parent);
            toggle.Descriptor.SetTitle(title);
            return toggle;
        }
    }
}
