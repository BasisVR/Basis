using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisBoxedSectionRowGateTests
    {
        private const float Tolerance = 0.5f;
        private const int SettlePasses = 8;

        private readonly List<GameObject> _roots = new List<GameObject>();
        private readonly Vector3[] _corners = new Vector3[4];
        private RectTransform _content;
        private PanelSectionToggle _section;
        private PanelToggle _gate;
        private PanelTextField _field;
        private PanelSlider _size;
        private PanelSlider _duration;

        [SetUp]
        public void SetUp()
        {
            BasisAddressablePrefabCache.ResetForTests(path => AssetDatabase.LoadAssetAtPath<GameObject>(path));

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

            _section = PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(_content, "Chat", () =>
            {
                _gate = PanelToggle.CreateNewEntry(_content);
                _field = PanelTextField.CreateNewEntry(_content);
                _size = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, _content);
                _duration = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, _content);
            }, true, visible =>
            {
                if (visible) ApplyGate();
                RebuildRows();
            });
            _gate.OnValueChanged += _ =>
            {
                ApplyGate();
                RebuildRows();
            };

            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(_content, "Notifications", () => PanelToggle.CreateNewEntry(_content));
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(_content, "Menu Styles", () => PanelToggle.CreateNewEntry(_content));

            RectTransform scroll = (RectTransform)_content.GetComponentInParent<ScrollRect>().transform;
            for (int pass = 0; pass < SettlePasses; pass++)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(scroll);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            }
        }

        [TearDown]
        public void TearDown()
        {
            BasisAddressablePrefabCache.ResetForTests(null);
            for (int Index = 0; Index < _roots.Count; Index++)
            {
                if (_roots[Index]) Object.DestroyImmediate(_roots[Index]);
            }
            _roots.Clear();
        }

        [Test]
        public void TheRigBuildsTheSectionTheWayASettingsPageDoes()
        {
            Assert.That(_field.transform.parent, Is.Not.SameAs(_content), "a boxed section lifts its rows into a card of their own");
            Assert.That(_field.transform.parent.parent.parent, Is.SameAs(_content), "the card sits directly on the page");
            Assert.That(_content.rect.width, Is.GreaterThan(300f), "the scroll view never gave the page a width, so the rows have nothing to lay out against");
            AssertSettled("as built");
        }

        [Test]
        public void HidingRowsInsideTheCardPullsEverythingBelowItUp()
        {
            _gate.SetValue(true);
            AssertSettled("after hiding the rows");
        }

        [Test]
        public void ShowingRowsInsideTheCardPushesEverythingBelowItDown()
        {
            _gate.SetValue(true);
            AssertSettled("after hiding the rows");
            _gate.SetValue(false);
            AssertSettled("after showing the rows again");
        }

        [Test]
        public void ReopeningTheSectionAndShowingTheRowsAgainLeavesNothingOverlapping()
        {
            _gate.SetValue(true);
            AssertSettled("after hiding the rows");
            _section.SetExpanded(false);
            AssertSettled("after closing the section");
            _section.SetExpanded(true);
            AssertSettled("after reopening the section");
            _gate.SetValue(false);
            AssertSettled("after showing the rows again");
        }

        [Test]
        public void RowsHiddenWhileTheSectionWasClosedStillFitWhenItReopens()
        {
            _section.SetExpanded(false);
            AssertSettled("after closing the section");
            _gate.SetValue(true);
            AssertSettled("after hiding the rows behind the closed section");
            _section.SetExpanded(true);
            AssertSettled("after reopening the section");
        }

        private void ApplyGate()
        {
            bool shown = !_gate.Value;
            _field.Descriptor.SetActive(shown);
            _size.Descriptor.SetActive(shown);
            _duration.Descriptor.SetActive(shown);
        }

        private void RebuildRows() => PanelElementDescriptor.RebuildLayoutChain(_field.transform.parent as RectTransform, _content);

        private void AssertSettled(string when)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);

            List<RectTransform> tracked = new List<RectTransform>();
            AddLaidOut(_content, tracked);
            AddLaidOut((RectTransform)_field.transform.parent, tracked);
            Vector2[] shown = new Vector2[tracked.Count];
            for (int Index = 0; Index < tracked.Count; Index++) shown[Index] = Extent(tracked[Index]);

            for (int pass = 0; pass < SettlePasses; pass++) LayoutRebuilder.ForceRebuildLayoutImmediate(_content);

            for (int Index = 0; Index < tracked.Count; Index++)
            {
                Vector2 settled = Extent(tracked[Index]);
                Assert.That(shown[Index].x, Is.EqualTo(settled.x).Within(Tolerance),
                    $"{when}: '{tracked[Index].name}' (sibling {tracked[Index].GetSiblingIndex()}) was drawn {shown[Index].x - settled.x:0.#} px off its settled top, so the page showed it measured at its old size");
                Assert.That(shown[Index].y, Is.EqualTo(settled.y).Within(Tolerance),
                    $"{when}: '{tracked[Index].name}' (sibling {tracked[Index].GetSiblingIndex()}) was drawn {shown[Index].y - settled.y:0.#} px off its settled bottom, so the page showed it measured at its old size");
            }

            float spacing = _content.GetComponent<VerticalLayoutGroup>().spacing;
            RectTransform previous = null;
            for (int Index = 0; Index < tracked.Count; Index++)
            {
                if (tracked[Index].parent != _content) continue;
                if (previous != null)
                {
                    float gap = Extent(previous).y - Extent(tracked[Index]).x;
                    Assert.That(gap, Is.EqualTo(spacing).Within(Tolerance),
                        $"{when}: '{tracked[Index].name}' (sibling {tracked[Index].GetSiblingIndex()}) starts {gap:0.#} px below '{previous.name}' instead of {spacing}");
                }
                previous = tracked[Index];
            }
        }

        private Vector2 Extent(RectTransform rect)
        {
            rect.GetWorldCorners(_corners);
            return new Vector2(_content.InverseTransformPoint(_corners[1]).y, _content.InverseTransformPoint(_corners[0]).y);
        }

        private static void AddLaidOut(RectTransform parent, List<RectTransform> into)
        {
            for (int Index = 0; Index < parent.childCount; Index++)
            {
                RectTransform child = (RectTransform)parent.GetChild(Index);
                if (IsLaidOut(child)) into.Add(child);
            }
        }

        private static bool IsLaidOut(RectTransform child)
        {
            if (!child.gameObject.activeInHierarchy) return false;
            ILayoutIgnorer[] ignorers = child.GetComponents<ILayoutIgnorer>();
            if (ignorers.Length == 0) return true;
            for (int Index = 0; Index < ignorers.Length; Index++)
            {
                if (!ignorers[Index].ignoreLayout) return true;
            }
            return false;
        }
    }
}
