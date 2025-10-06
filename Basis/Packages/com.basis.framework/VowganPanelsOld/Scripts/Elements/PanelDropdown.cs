using System.Collections.Generic;
using System.Linq;
using Basis.VowganUI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace Basis.VowganUIOld
{
    public class PanelDropdown : AddressableUIInstanceBase
    {
        public static string ReferencePath => "BasisUI/CanvasElement/Dropdown";


        [Header("References")]
        public TextMeshProUGUI TitleLabel;
        public TMP_Dropdown TmpDropdownComponent;
        public List<string> OptionEntries = new();
        public UnityEvent<int> OnValueChanged;

        public void SetValueWithoutNotify(int value)
        {
            TmpDropdownComponent.SetValueWithoutNotify(value);
        }

        public static PanelDropdown CreateNew(Component parent)
        {
            PanelDropdown dropdown = CreateNew<PanelDropdown>(ReferencePath, parent);
            return dropdown;
        }

        public static PanelDropdown CreateNew(Component parent, string label)
        {
            PanelDropdown dropdown = CreateNew(parent);
            dropdown.TitleLabel.text = label;
            return dropdown;
        }

        public static PanelDropdown CreateNew(Component parent, string label, List<string> options)
        {
            PanelDropdown dropdown = CreateNew(parent);
            dropdown.TitleLabel.text = label;
            dropdown.SetOptions(options);
            return dropdown;
        }

        public static PanelDropdown CreateNew(Component parent, string label, string[] options)
        {
            PanelDropdown dropdown = CreateNew(parent);
            dropdown.TitleLabel.text = label;
            dropdown.SetOptions(options);
            return dropdown;
        }

        public void SetOptions(string[] options)
        {
            OptionEntries = options.ToList();
            TmpDropdownComponent.ClearOptions();
            TmpDropdownComponent.AddOptions(OptionEntries);
        }

        public void SetOptions(List<string> options)
        {
            OptionEntries = options;
            TmpDropdownComponent.ClearOptions();
            TmpDropdownComponent.AddOptions(OptionEntries);
        }

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            TmpDropdownComponent.onValueChanged.AddListener(OnDropdownValueChanged);
        }

        protected virtual void OnDropdownValueChanged(int index)
        {
            OnValueChanged?.Invoke(index);
        }

    }
}
