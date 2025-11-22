using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelDropdown : PanelDataComponent<string>
    {

        public static class DropdownStyles
        {
            public static string Default => "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/PE Dropdown.prefab";
            public static string Entry => "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/PE Dropdown - Entry Variant.prefab";
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
    }
}
