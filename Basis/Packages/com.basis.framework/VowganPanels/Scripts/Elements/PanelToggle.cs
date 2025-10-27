using UnityEngine;
using UnityEngine.UI;

namespace Basis.VowganUI
{
    public class PanelToggle : PanelElement
    {
        public static class Styles
        {
            public static string Default => "VowganUI/Elements/Toggle";
        }

        public Toggle ToggleComponent;


        public static PanelToggle CreateNew(Component parent)
        {
            PanelToggle element = CreateNew<PanelToggle>(Styles.Default, parent);
            return element;
        }

        public static PanelToggle CreateNew(Component parent, string style)
        {
            PanelToggle element = CreateNew<PanelToggle>(style, parent);
            return element;
        }
    }
}
