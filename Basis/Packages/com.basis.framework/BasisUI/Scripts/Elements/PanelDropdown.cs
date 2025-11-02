using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelDropdown : PanelElement
    {
        public static class DropdownStyles
        {
            public static string Default => "Packages/com.basis.framework/BasisUI/Prefabs/Elements/Panel Dropdown.prefab";
        }

        public TMP_Dropdown DropdownComponent;


        public static PanelDropdown CreateNew(Component parent)
            => CreateNew<PanelDropdown>(DropdownStyles.Default, parent);

        public static PanelDropdown CreateNew(string style, Component parent)
            => CreateNew<PanelDropdown>(style, parent);

    }
}
