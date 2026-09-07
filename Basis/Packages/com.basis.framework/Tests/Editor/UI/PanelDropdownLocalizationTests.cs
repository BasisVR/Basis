using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace Basis.Tests.UI
{
    /// <summary>
    /// PanelDropdown.AssignEntries/AssignLocalizedEntries is the exact mechanism behind every
    /// dropdown localization bug fixed this session (Dominant Hand, Library Sort/Filter): Entries
    /// is the stable, persisted/compared value (bindings and Enum.TryParse round-trip it) and must
    /// never change with language — only the separately-resolved display label may. A regression
    /// here reopens all of them at once, since every fix funnels through this one API.
    /// </summary>
    [TestFixture]
    public class PanelDropdownLocalizationTests
    {
        private readonly List<GameObject> _roots = new List<GameObject>();
        private string _originalLanguage;

        [SetUp]
        public void SetUp()
        {
            _originalLanguage = BasisLocalization.CurrentLanguage;
        }

        [TearDown]
        public void TearDown()
        {
            BasisLocalization.SetLanguage(_originalLanguage);
            for (int Index = 0; Index < _roots.Count; Index++)
            {
                if (_roots[Index]) Object.DestroyImmediate(_roots[Index]);
            }
            _roots.Clear();
        }

        // Left inactive, matching BasisPanelSectionResetTests' rig: UIBehaviour.OnEnable never
        // fires the creation event looking for prefab wiring this bare rig doesn't have.
        private PanelDropdown BuildDropdown()
        {
            GameObject dropdownObject = new GameObject("Dropdown", typeof(RectTransform));
            dropdownObject.SetActive(false);
            _roots.Add(dropdownObject);

            PanelDropdown dropdown = dropdownObject.AddComponent<PanelDropdown>();
            dropdown.DropdownComponent = dropdownObject.AddComponent<TMP_Dropdown>();
            return dropdown;
        }

        [Test]
        public void AssignEntries_WithDisplayLabelsKeepsEntriesAsTheStableStoredValue()
        {
            PanelDropdown dropdown = BuildDropdown();

            dropdown.AssignEntries(
                new List<string> { "right", "left" },
                new List<string> { "Droite", "Gauche" });

            // Same binding-safety invariant SettingsProviderControllerConfig's Dominant Hand
            // dropdown depends on: whatever the display says, Entries stays the raw id a persisted
            // AssignBinding round-trips.
            Assert.That(dropdown.Entries, Is.EqualTo(new List<string> { "right", "left" }));
        }

        [Test]
        public void AssignEntries_MismatchedLabelCountFallsBackToShowingEntriesRatherThanThrowing()
        {
            PanelDropdown dropdown = BuildDropdown();

            Assert.DoesNotThrow(() => dropdown.AssignEntries(
                new List<string> { "a", "b", "c" },
                new List<string> { "only one label" }));
            Assert.That(dropdown.Entries, Has.Count.EqualTo(3));
        }

        [Test]
        public void AssignLocalizedEntries_ResolvesDisplayFromTheGivenKeysAgainstRealLocalizationData()
        {
            PanelDropdown dropdown = BuildDropdown();
            BasisLocalization.SetLanguage("en");

            // Exactly the shape of this session's Dominant Hand fix.
            dropdown.AssignLocalizedEntries(
                new List<string> { "right", "left" },
                new List<string> { "settings.controls.dominantHand.right", "settings.controls.dominantHand.left" });

            Assert.That(dropdown.Entries, Is.EqualTo(new List<string> { "right", "left" }),
                "entries must stay the persisted-binding values, not the localized text");
            Assert.That(dropdown.DropdownComponent.options.Count, Is.EqualTo(2));
            Assert.That(dropdown.DropdownComponent.options[0].text,
                Is.EqualTo(BasisLocalization.Get("settings.controls.dominantHand.right")));
            Assert.That(dropdown.DropdownComponent.options[1].text,
                Is.EqualTo(BasisLocalization.Get("settings.controls.dominantHand.left")));
        }

        [Test]
        public void AssignLocalizedEntries_TooltipAutoDerivesFromKeyPlusDotTooltipSuffix()
        {
            PanelDropdown dropdown = BuildDropdown();
            BasisLocalization.SetLanguage("en");

            // Exactly the shape of this session's Library Sort/Filter fix: the tooltip key already
            // existed in en.json before the label key did, and must still resolve via the same
            // <key>.tooltip derivation once the label key is added alongside it.
            dropdown.AssignLocalizedEntries(
                new List<string> { "All" },
                new List<string> { "library.filter.all" });

            Assert.That(dropdown.GetOptionTooltip(0), Is.EqualTo(BasisLocalization.Get("library.filter.all.tooltip")));
        }

        [Test]
        public void AssignLocalizedEntries_ExplicitTooltipKeysOverrideTheDerivedOne()
        {
            PanelDropdown dropdown = BuildDropdown();
            BasisLocalization.SetLanguage("en");

            dropdown.AssignLocalizedEntries(
                new List<string> { "on" },
                new List<string> { "ui.option.on" },
                new List<string> { "settings.controls.dominantHand.right.tooltip" });

            Assert.That(dropdown.GetOptionTooltip(0),
                Is.EqualTo(BasisLocalization.Get("settings.controls.dominantHand.right.tooltip")));
        }

        [Test]
        public void AssignLocalizedEntries_ReResolvesDisplayLiveWhenTheLanguageChangesAfterward()
        {
            // The exact bug shape this whole session chased: rebuilding a dropdown must not freeze
            // its display text at whatever language was active the first time it was built.
            PanelDropdown dropdown = BuildDropdown();

            BasisLocalization.SetLanguage("en");
            dropdown.AssignLocalizedEntries(new List<string> { "Name" }, new List<string> { "library.sort.name" });
            string english = dropdown.DropdownComponent.options[0].text;

            BasisLocalization.SetLanguage("ja");
            dropdown.AssignLocalizedEntries(new List<string> { "Name" }, new List<string> { "library.sort.name" });
            string japanese = dropdown.DropdownComponent.options[0].text;

            Assert.That(japanese, Is.Not.EqualTo(english),
                "library.sort.name has a real Japanese translation; a caching bug would show English both times");
        }

        [Test]
        public void AssignLocalizedEntries_EntriesStayStableAcrossALanguageChangeSoEnumTryParseStillWorks()
        {
            // Exactly what LibraryProvider's Sort/Filter dropdowns depend on: their OnValueChanged
            // handler does Enum.TryParse<TEnum>(dropdown.Value, ...), so Entries must always be the
            // raw enum member names, in any language.
            PanelDropdown dropdown = BuildDropdown();
            List<string> rawNames = new List<string> { "All", "Local", "Networked" };
            List<string> keys = new List<string> { "library.filter.all", "library.filter.local", "library.filter.networked" };

            BasisLocalization.SetLanguage("en");
            dropdown.AssignLocalizedEntries(rawNames, keys);
            dropdown.SetValueWithoutNotify("Local");

            BasisLocalization.SetLanguage("ja");
            dropdown.AssignLocalizedEntries(rawNames, keys);

            Assert.That(dropdown.Entries, Is.EqualTo(rawNames));
            Assert.That(dropdown.Value, Is.EqualTo("Local"),
                "the stored value must survive a rebuild under a different language unchanged");
        }
    }
}
