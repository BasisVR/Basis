using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisSettingShareValueTests
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
        public void AToggleReadsOnlyTheTwoBoolWords()
        {
            PanelToggle toggle = BuildToggle();

            Assert.That(TryParse(toggle, "bool", "true", out bool on), Is.True);
            Assert.That(on, Is.True);
            Assert.That(TryParse(toggle, "bool", "false", out bool off), Is.True);
            Assert.That(off, Is.False);
            Assert.That(TryParse(toggle, "bool", "True", out _), Is.False);
            Assert.That(TryParse(toggle, "number", "1", out _), Is.False);
            Assert.That(TryParse(toggle, "text", "true", out _), Is.False);
        }

        [Test]
        public void ASliderClampsToItsOwnRangeAndRoundsWholeNumbers()
        {
            PanelSlider slider = BuildSlider(0f, 10f, true);

            Assert.That(TryParse(slider, "number", "12.6", out float high), Is.True);
            Assert.That(high, Is.EqualTo(10f));
            Assert.That(TryParse(slider, "number", "-4", out float low), Is.True);
            Assert.That(low, Is.EqualTo(0f));
            Assert.That(TryParse(slider, "number", "3.4", out float rounded), Is.True);
            Assert.That(rounded, Is.EqualTo(3f));
            Assert.That(TryParse(slider, "number", "NaN", out _), Is.False);
            Assert.That(TryParse(slider, "number", "1e39", out _), Is.False);
            Assert.That(TryParse(slider, "text", "3", out _), Is.False);
            Assert.That(TryParse(slider, "bool", "true", out _), Is.False);
        }

        [Test]
        public void AFractionalSliderKeepsTheSharedValue()
        {
            PanelSlider slider = BuildSlider(0f, 1f, false);

            Assert.That(TryParse(slider, "number", "0.25", out float value), Is.True);
            Assert.That(value, Is.EqualTo(0.25f));
        }

        [Test]
        public void ADropdownTakesOnlyOneOfItsOwnEntries()
        {
            PanelDropdown dropdown = BuildDropdown("low", "high");

            Assert.That(TryParse(dropdown, "text", "HIGH", out string entry), Is.True);
            Assert.That(entry, Is.EqualTo("high"));
            Assert.That(TryParse(dropdown, "text", "ultra", out _), Is.False);
            Assert.That(TryParse(dropdown, "text", "", out _), Is.False);
            Assert.That(TryParse(dropdown, "bool", "true", out _), Is.False);
        }

        [Test]
        public void ValuesAreWrittenInTheSharedFormat()
        {
            Assert.That(TryWrite(true, out string boolType, out string boolText), Is.True);
            Assert.That(boolType, Is.EqualTo("bool"));
            Assert.That(boolText, Is.EqualTo("true"));
            Assert.That(TryWrite(0.25f, out string numberType, out string numberText), Is.True);
            Assert.That(numberType, Is.EqualTo("number"));
            Assert.That(numberText, Is.EqualTo("0.25"));
            Assert.That(TryWrite(-3, out _, out string wholeText), Is.True);
            Assert.That(wholeText, Is.EqualTo("-3"));
            Assert.That(TryWrite("high", out string textType, out string text), Is.True);
            Assert.That(textType, Is.EqualTo("text"));
            Assert.That(text, Is.EqualTo("high"));
            Assert.That(TryWrite("line\nbreak", out _, out _), Is.False);
            Assert.That(TryWrite(float.NaN, out _, out _), Is.False);
            Assert.That(TryWrite(new Vector3(1f, 2f, 3f), out _, out _), Is.False);
        }

        [Test]
        public void AControlWithNeitherABindingNorATitleCannotBeShared()
        {
            PanelToggle toggle = BuildToggle();
            toggle.SetResetDefault(false);

            Assert.That(toggle.HasResetDefault, Is.True);
            Assert.That(toggle.CanShareSetting, Is.False);
            Assert.That(toggle.TryGetShareValue(out _, out _, out _), Is.False);
            Assert.That(toggle.TryReadShareValue("bool", "true", out _, out _, out _), Is.False);
            Assert.That(toggle.TryApplyShareValue("bool", "true"), Is.False);
            Assert.That(toggle.Value, Is.False);
        }

        private static bool TryParse<T>(PanelDataComponent<T> control, string type, string value, out T parsed)
        {
            MethodInfo method = typeof(PanelDataComponent<T>).GetMethod("TryParseShareValue", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "PanelDataComponent.TryParseShareValue was renamed; this test needs updating.");
            object[] args = { type, value, null };
            bool read = (bool)method.Invoke(control, args);
            parsed = (T)args[2];
            return read;
        }

        private static bool TryWrite<T>(T value, out string type, out string text)
        {
            MethodInfo method = typeof(PanelDataComponent<T>).GetMethod("TryWriteShareValue", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "PanelDataComponent.TryWriteShareValue was renamed; this test needs updating.");
            object[] args = { value, null, null };
            bool written = (bool)method.Invoke(null, args);
            type = (string)args[1];
            text = (string)args[2];
            return written;
        }

        private PanelToggle BuildToggle()
        {
            GameObject toggleObject = NewObject("Toggle");
            PanelToggle toggle = toggleObject.AddComponent<PanelToggle>();
            toggle.ToggleComponent = toggleObject.AddComponent<Toggle>();
            return toggle;
        }

        private PanelSlider BuildSlider(float min, float max, bool wholeNumbers)
        {
            GameObject sliderObject = NewObject("Slider");
            PanelSlider slider = sliderObject.AddComponent<PanelSlider>();
            slider.SliderComponent = sliderObject.AddComponent<Slider>();
            slider.SliderComponent.minValue = min;
            slider.SliderComponent.maxValue = max;
            slider.SliderComponent.wholeNumbers = wholeNumbers;
            return slider;
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
