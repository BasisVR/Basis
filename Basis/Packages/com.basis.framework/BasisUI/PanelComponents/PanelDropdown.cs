using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelDropdown : PanelDataComponent<string>
    {

        public static class DropdownStyles
        {
            public static string Default => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Dropdown.prefab";
            public static string Entry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Dropdown - Entry Variant.prefab";
            public static string OverlayEntry => "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Dropdown - Entry Variant - Overlay.prefab";
        }

        public TMP_Dropdown DropdownComponent;

        public int Index
        {
            get
            {
                if (Entries == null || Entries.Count == -1)
                {
                    return -1;
                }

                return Entries.IndexOf(Value);
            }
        }

        private PanelDropdown() { }

        public static PanelDropdown CreateNew(Component parent)
            => CreateNew<PanelDropdown>(DropdownStyles.Default, parent);
        public static PanelDropdown CreateNewEntry(Component parent)
            => CreateNew<PanelDropdown>(DropdownStyles.Entry, parent);

        public static PanelDropdown CreateNew(string style, Component parent)
            => CreateNew<PanelDropdown>(style, parent);

        public List<string> Entries { get; protected set; }

        public void AssignEntries(List<string> entries)
        {
            Entries = entries;
            DropdownComponent.ClearOptions();
            DropdownComponent.AddOptions(Entries);
            SetValueWithoutNotify(Value);
        }

        public override void OnComponentUsed()
        {
            base.OnComponentUsed();
            if (DropdownComponent.value == -1) SetValue(string.Empty);
            else SetValue(Entries[DropdownComponent.value]);
        }

        public override void SetValueWithoutNotify(string value)
        {
            base.SetValueWithoutNotify(value);
            DropdownComponent.SetValueWithoutNotify(Index);
        }
        public int StringValueToIndex(string Active)
        {
            int Count = DropdownComponent.options.Count;
            for (int Index = 0; Index < Count; Index++)
            {
                TMP_Dropdown.OptionData optionData = DropdownComponent.options[Index];
                if (Active == optionData.text)
                {
                    return Index;
                }
            }
            return 0;
        }
        public string SelectedString
        {
            get
            {
                if (DropdownComponent == null) return string.Empty;
                int index = DropdownComponent.value;
                if (index < 0 || index >= DropdownComponent.options.Count) return string.Empty;
                return DropdownComponent.options[index].text;
            }
        }

    }
}
