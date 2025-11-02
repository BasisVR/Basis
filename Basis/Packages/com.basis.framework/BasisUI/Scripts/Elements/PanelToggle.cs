using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class PanelToggle : PanelElement
    {
        public static class Styles
        {
            public static string Default => "Packages/com.basis.framework/BasisUI/Prefabs/Elements/Panel Toggle.prefab";
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
