using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    /// <summary>
    /// The options gesture on a section header stands for every control filed under it: one press on
    /// "Film Look" is meant to put the whole section back, not to open a window about the header.
    /// The two ways that quietly fails are a header that reaches nothing — the camera panel's
    /// controls are callback-driven, so one with no explicit default has nowhere to go back to and
    /// is skipped without a word — and a header that reaches too much, counting a nested header as a
    /// row or treating a section's own open/closed state as one of the values being reset.
    /// </summary>
    [TestFixture]
    public class BasisPanelSectionResetTests
    {
        private readonly List<GameObject> _roots = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int Index = 0; Index < _roots.Count; Index++)
            {
                if (_roots[Index]) Object.DestroyImmediate(_roots[Index]);
            }
            _roots.Clear();
        }

        [Test]
        public void AControlWithAnExplicitDefaultOffersTheGestureAndTakesIt()
        {
            PanelToggle toggle = BuildToggle(true, false);

            Assert.That(toggle.HasResetDefault, Is.True,
                "an explicit default is the only thing a control with no settings binding can be reset to");

            toggle.ApplyResetToDefault();

            Assert.That(toggle.Value, Is.False);
            Assert.That(toggle.ToggleComponent.isOn, Is.False,
                "a reset that never moves the control leaves the panel showing the value it just replaced");
        }

        [Test]
        public void AResetRunsTheControlsCallbackSoWhateverItDrivesFollows()
        {
            PanelToggle toggle = BuildToggle(true, false);
            bool? pushed = null;
            toggle.OnValueChanged = value => pushed = value;

            toggle.ApplyResetToDefault();

            Assert.That(pushed, Is.False,
                "the camera panel writes every value through this callback, so without it a reset moves the handle and grades nothing");
        }

        [Test]
        public void AControlWithNoDefaultIsLeftWhereItIs()
        {
            PanelToggle toggle = BuildToggle(true, null);

            Assert.That(toggle.HasResetDefault, Is.False);

            toggle.ApplyResetToDefault();

            Assert.That(toggle.Value, Is.True);
        }

        [Test]
        public void AHeaderResetsEveryRowFiledUnderIt()
        {
            PanelSectionToggle section = BuildSection(out RectTransform content);
            PanelToggle first = BuildToggle(true, false, content);
            PanelToggle second = BuildToggle(false, true, content);

            Assert.That(section.HasResetDefault, Is.True);

            section.ApplyResetToDefault();

            Assert.That(first.Value, Is.False);
            Assert.That(second.Value, Is.True);
        }

        [Test]
        public void AHeaderOverNothingResettableDoesNotOfferTheGesture()
        {
            PanelSectionToggle section = BuildSection(out RectTransform content);
            BuildToggle(true, null, content);

            Assert.That(section.HasResetDefault, Is.False,
                "offering it here opens a window whose Reset button does nothing at all");
        }

        [Test]
        public void AHeaderWithNothingBuiltUnderItDoesNotOfferTheGesture()
        {
            PanelSectionToggle section = BuildSection(out RectTransform _);

            Assert.That(section.HasResetDefault, Is.False,
                "a collapsed lazy section has destroyed its rows and has nothing there to reset");
        }

        [Test]
        public void AHeaderReachesTheRowsOfSectionsNestedInsideIt()
        {
            PanelSectionToggle outer = BuildSection(out RectTransform outerContent);
            PanelSectionToggle inner = BuildSection(out RectTransform innerContent, outerContent);
            PanelToggle nested = BuildToggle(true, false, innerContent);
            inner.SetExpandedWithoutNotify(true);

            outer.ApplyResetToDefault();

            Assert.That(nested.Value, Is.False);
            Assert.That(inner.Expanded, Is.True, "the nested header is a way of reading the page, not a row to be reset");
        }

        [Test]
        public void ResettingASectionLeavesItOpenOrClosedAsTheUserLeftIt()
        {
            PanelSectionToggle section = BuildSection(out RectTransform content);
            BuildToggle(true, false, content);
            section.SetExpandedWithoutNotify(true);

            section.ApplyResetToDefault();

            Assert.That(section.Expanded, Is.True,
                "whether a section is open is how the user is reading the page, not one of the values a reset is about");
        }

        // ---- Rig -----------------------------------------------------------------------------
        // Panel controls normally arrive as addressable prefabs. They are built by hand here and
        // left inactive, so UIBehaviour.OnEnable never fires the creation event looking for prefab
        // wiring: the reset paths under test read the value fields and the section's registered
        // content, neither of which that event supplies.

        private PanelSectionToggle BuildSection(out RectTransform content, RectTransform parent = null)
        {
            PanelSectionToggle section = NewObject("Section", parent).AddComponent<PanelSectionToggle>();

            content = (RectTransform)NewObject("Section Content", parent).transform;
            section.RegisterContentContainer(content);

            return section;
        }

        private PanelToggle BuildToggle(bool on, bool? resetDefault, RectTransform parent = null)
        {
            GameObject toggleObject = NewObject("Toggle", parent);
            PanelToggle toggle = toggleObject.AddComponent<PanelToggle>();
            toggle.ToggleComponent = toggleObject.AddComponent<Toggle>();
            toggle.SetValueWithoutNotify(on);

            if (resetDefault.HasValue) toggle.SetResetDefault(resetDefault.Value);
            return toggle;
        }

        private GameObject NewObject(string name, RectTransform parent)
        {
            GameObject created = new GameObject(name, typeof(RectTransform));
            created.SetActive(false);
            created.transform.SetParent(parent, false);

            if (parent == null) _roots.Add(created);
            return created;
        }
    }
}
