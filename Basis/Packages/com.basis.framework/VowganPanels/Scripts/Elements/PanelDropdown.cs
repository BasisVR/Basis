using TMPro;
using UnityEngine;

namespace Basis.VowganUI
{
    public class PanelDropdown : PanelElement
    {
        public static class DropdownStyles
        {
            public static string Default => "VowganUI/Elements/Dropdown";
        }

        public TMP_Dropdown DropdownComponent;


        public static PanelDropdown CreateNew(Component parent)
            => CreateNew<PanelDropdown>(DropdownStyles.Default, parent);

        public static PanelDropdown CreateNew(string style, Component parent)
            => CreateNew<PanelDropdown>(style, parent);

    }
}
