using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisPanelRevealControlTests
    {
        private readonly List<GameObject> _roots = new List<GameObject>();
        private RectTransform _content;
        private PanelSectionToggle _plain;
        private PanelSectionToggle _lazy;
        private PanelSectionToggle _outer;
        private PanelSectionToggle _inner;
        private bool _suppressed;

        [SetUp]
        public void SetUp()
        {
            BasisAddressablePrefabCache.ResetForTests(path => AssetDatabase.LoadAssetAtPath<GameObject>(path));
            _suppressed = BasisMenuStateMemory.SuppressSectionWrites;

            GameObject canvasObject = new GameObject("CanvasRoot", typeof(Canvas));
            _roots.Add(canvasObject);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.sizeDelta = new Vector2(1920, 1080);

            RectTransform host = (RectTransform)new GameObject("Host", typeof(RectTransform)).transform;
            host.SetParent(canvasRect, false);
            host.sizeDelta = new Vector2(1200, 1000);

            PanelTabPage page = PanelTabPage.CreateVertical(host);
            Assert.That(page, Is.Not.Null, "the tab page prefab did not load");
            _content = page.Descriptor.ContentParent;

            _plain = PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(_content, "Plain", () => Row("Plain Row"));
            _lazy = PanelSectionToggleHelpers.CreateLazyBoxedSection(_content, "Lazy", () => Row("Lazy Row"));
            _outer = PanelSectionToggleHelpers.CreateLazyBoxedSection(_content, "Outer", () =>
            {
                Row("Outer Row");
                _inner = PanelSectionToggleHelpers.CreateLazyBoxedSection(_content, "Inner", () => Row("Deep Row"));
            });
        }

        [TearDown]
        public void TearDown()
        {
            BasisMenuStateMemory.SuppressSectionWrites = _suppressed;
            LogAssert.ignoreFailingMessages = false;
            BasisAddressablePrefabCache.ResetForTests(null);
            for (int Index = 0; Index < _roots.Count; Index++)
            {
                if (_roots[Index]) Object.DestroyImmediate(_roots[Index]);
            }
            _roots.Clear();
        }

        [Test]
        public void TheRigStartsWithEverySectionClosedAndTheLazyRowsUnbuilt()
        {
            Assert.That(_plain.Expanded || _lazy.Expanded || _outer.Expanded, Is.False);
            Assert.That(Find("Plain Row"), Is.Not.Null, "an ordinary section keeps its rows while closed");
            Assert.That(Find("Lazy Row"), Is.Null, "a lazy section builds its rows only when opened");
            Assert.That(Find("Deep Row"), Is.Null);
        }

        [Test]
        public void ARowThatIsAlreadyBuiltIsRevealedWithoutBuildingAnythingElse()
        {
            PanelComponent found = BasisPanelSearch.RevealControl(_content, Titled("Plain Row"));

            Assert.That(found, Is.Not.Null);
            Assert.That(found.gameObject.activeInHierarchy, Is.True);
            Assert.That(_plain.Expanded, Is.True);
            Assert.That(_lazy.Expanded || _outer.Expanded, Is.False, "nothing else had to be opened to find it");
            Assert.That(Find("Lazy Row"), Is.Null);
        }

        [Test]
        public void ARowInALazySectionIsBuiltAndItsSectionLeftOpen()
        {
            PanelComponent found = BasisPanelSearch.RevealControl(_content, Titled("Lazy Row"));

            Assert.That(found, Is.Not.Null);
            Assert.That(found.gameObject.activeInHierarchy, Is.True);
            Assert.That(_lazy.Expanded, Is.True);
            Assert.That(_plain.Expanded, Is.False);
            Assert.That(_outer.Expanded, Is.False, "a section opened only to look is closed again");
        }

        [Test]
        public void ARowTwoLazySectionsDeepIsReachedAndBothStayOpen()
        {
            LogAssert.ignoreFailingMessages = true;
            PanelComponent found = BasisPanelSearch.RevealControl(_content, Titled("Deep Row"));

            Assert.That(found, Is.Not.Null);
            Assert.That(found.gameObject.activeInHierarchy, Is.True);
            Assert.That(_outer.Expanded, Is.True);
            Assert.That(_inner, Is.Not.Null);
            Assert.That(_inner.Expanded, Is.True);
            Assert.That(_lazy.Expanded, Is.False, "a section opened only to look is closed again");
            Assert.That(IsShown("Lazy Row"), Is.False);
        }

        [Test]
        public void WhenNothingMatchesThePageIsLeftAsItWasFound()
        {
            LogAssert.ignoreFailingMessages = true;
            BasisMenuStateMemory.SuppressSectionWrites = false;

            PanelComponent found = BasisPanelSearch.RevealControl(_content, Titled("Missing Row"));

            Assert.That(found, Is.Null);
            Assert.That(_plain.Expanded || _lazy.Expanded || _outer.Expanded, Is.False);
            Assert.That(IsShown("Lazy Row"), Is.False, "rows built only to look are dropped again");
            Assert.That(IsShown("Deep Row"), Is.False);
            Assert.That(BasisMenuStateMemory.SuppressSectionWrites, Is.False, "the remembered layout is only shielded while looking");
        }

        private void Row(string title)
        {
            PanelToggle toggle = PanelToggle.CreateNewEntry(_content);
            toggle.Descriptor.SetTitle(title);
        }

        private static System.Predicate<PanelComponent> Titled(string title) =>
            control => control is PanelToggle && control.Descriptor != null && control.Descriptor.Title == title;

        private bool IsShown(string title)
        {
            PanelComponent row = Find(title);
            return row != null && row.gameObject.activeInHierarchy;
        }

        private PanelComponent Find(string title)
        {
            PanelComponent[] controls = _content.GetComponentsInChildren<PanelComponent>(true);
            for (int Index = 0; Index < controls.Length; Index++)
            {
                if (controls[Index] is PanelToggle && controls[Index].Descriptor.Title == title) return controls[Index];
            }
            return null;
        }
    }
}
