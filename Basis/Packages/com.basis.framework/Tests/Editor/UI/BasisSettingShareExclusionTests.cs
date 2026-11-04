using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisSettingShareExclusionTests
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
        public void ARowThatOnlyMeansSomethingOnThisMachineIsNeverShared()
        {
            PanelToggle toggle = BuildToggle();
            toggle.ShareTitleKey = "camera.selector";
            Assume.That(toggle.CanShareSetting, Is.True);

            toggle.ExcludeFromShare = true;

            Assert.That(toggle.CanShareSetting, Is.False);
            Assert.That(toggle.TryGetShareValue(out _, out _, out _), Is.False);
            Assert.That(toggle.TryReadShareValue("bool", "true", out _, out _, out _), Is.False);
            Assert.That(toggle.TryApplyShareValue("bool", "true"), Is.False);
            Assert.That(toggle.Value, Is.False);
        }

        [Test]
        public void AnExcludedRowKeepsItsReset()
        {
            PanelToggle toggle = BuildToggle();
            toggle.ShareTitleKey = "camera.selector";
            toggle.SetResetDefault(true);
            toggle.ExcludeFromShare = true;

            Assert.That(toggle.HasResetDefault, Is.True);
            Assert.That(toggle.HasPanelOptions, Is.True);
            Assert.That(toggle.CanShareSetting, Is.False);
        }

        [Test]
        public void AnExcludedDropdownIgnoresASharedEntry()
        {
            PanelDropdown dropdown = BuildDropdown("Auto", "Hips");
            dropdown.ShareTitleKey = "camera.selector";
            dropdown.SetValueWithoutNotify("Auto");
            dropdown.ExcludeFromShare = true;

            Assert.That(dropdown.TryApplyShareValue("text", "Hips"), Is.False);
            Assert.That(dropdown.Value, Is.EqualTo("Auto"));
        }

        private PanelToggle BuildToggle()
        {
            GameObject toggleObject = NewObject("Toggle");
            PanelToggle toggle = toggleObject.AddComponent<PanelToggle>();
            toggle.ToggleComponent = toggleObject.AddComponent<Toggle>();
            return toggle;
        }

        private PanelDropdown BuildDropdown(params string[] entries)
        {
            GameObject dropdownObject = NewObject("Dropdown");
            PanelDropdown dropdown = dropdownObject.AddComponent<PanelDropdown>();
            dropdown.DropdownComponent = dropdownObject.AddComponent<TMP_Dropdown>();
            dropdown.AssignEntries(new List<string>(entries));
            return dropdown;
        }

        private GameObject NewObject(string name)
        {
            GameObject created = new GameObject(name, typeof(RectTransform));
            created.SetActive(false);
            _roots.Add(created);
            return created;
        }
    }
}
