using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisLazyFlatSectionRevealTests
    {
        private readonly List<GameObject> _roots = new List<GameObject>();
        private RectTransform _content;
        private PanelSectionToggle _outer;
        private PanelSectionToggle _first;
        private PanelSectionToggle _second;
        private PanelSectionToggle _nested;
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

            _outer = PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(_content, "Outer", () =>
            {
                _first = PanelSectionToggleHelpers.CreateLazyFlatSection(_content, "First", () => Row("First Row"));
                _second = PanelSectionToggleHelpers.CreateLazyFlatSection(_content, "Second", () =>
                {
                    Row("Second Row");
                    Row("Second Row Two");
                    _nested = PanelSectionToggle.CreateNewEntry(_content);
                    _nested.SetTitle("Nested");
                    int start = _content.childCount;
                    Row("Nested Row");
                    PanelSectionToggleHelpers.FinalizeFlatSectionFromIndex(_nested, _content, start, false);
                });
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
        public void ARowInALazyFlatSectionIsRevealedAndItsSectionLeftOpen()
        {
            LogAssert.ignoreFailingMessages = true;
            PanelComponent found = BasisPanelSearch.RevealControl(_content, Titled("Second Row"));

            Assert.That(found, Is.Not.Null);
            Assert.That(found.gameObject.activeInHierarchy, Is.True);
            Assert.That(PanelSectionToggle.GetOwningSection(found.transform), Is.SameAs(_second));
            Assert.That(_second.Expanded, Is.True);
            Assert.That(_outer.Expanded, Is.True);
            Assert.That(_first.Expanded, Is.False, "a section opened only to look is closed again");
        }

        [Test]
        public void TheRowsOfAnOpenLazyFlatSectionAreFiledUnderItAndLetGoWhenItCloses()
        {
            LogAssert.ignoreFailingMessages = true;
            List<Transform> contents = new List<Transform>();

            _second.SetExpanded(true);
            _second.GetContentContainers(contents);
            Assert.That(contents.Count, Is.EqualTo(3), "both rows and the nested header");
            Assert.That(PanelSectionToggle.GetOwningSection(Find("Second Row Two").transform), Is.SameAs(_second));

            _second.SetExpanded(false);
            _second.GetContentContainers(contents);
            Assert.That(contents, Is.Empty, "a closed lazy section has nothing built under it");
        }

        [Test]
        public void ARowANestedSectionAlreadyOwnsKeepsItsOwner()
        {
            LogAssert.ignoreFailingMessages = true;
            _second.SetExpanded(true);

            Assert.That(PanelSectionToggle.GetOwningSection(Find("Nested Row").transform), Is.SameAs(_nested));
            Assert.That(PanelSectionToggle.GetOwningSection(_nested.transform), Is.SameAs(_second));

            List<PanelSectionToggle> chain = new List<PanelSectionToggle>();
            PanelSectionToggle.GetSectionChain(_nested, chain);
            Assert.That(chain, Is.EqualTo(new[] { _outer, _second, _nested }));
        }

        private void Row(string title)
        {
            PanelToggle toggle = PanelToggle.CreateNewEntry(_content);
            toggle.Descriptor.SetTitle(title);
        }

        private static System.Predicate<PanelComponent> Titled(string title) =>
            control => control is PanelToggle && control.Descriptor != null && control.Descriptor.Title == title;

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
