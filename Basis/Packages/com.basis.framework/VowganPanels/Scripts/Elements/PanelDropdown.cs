using System;
using TMPro;
using UnityEngine;

namespace Basis.VowganUI
{
    public class PanelDropdown : PanelElement
    {
        public static class Styles
        {
            public static string Default => "VowganUI/Elements/Dropdown";
        }

        public TMP_Dropdown DropdownComponent;

        public static PanelDropdown CreateNew(Component parent)
        {
            PanelDropdown element = CreateNew<PanelDropdown>(Styles.Default, parent);
            return element;
        }
    }
}
