using UnityEngine;

namespace Basis.VowganUI
{
    public class PanelTabPageGroup : PanelElement
    {
        public static class TabPageGroupStyles
        {
            public static string Group => "VowganUI/Elements/Group";
        }

        public static PanelTabPageGroup CreateNew(Component parent)
            => CreateNew<PanelTabPageGroup>(TabPageGroupStyles.Group, parent);

        public static PanelTabPageGroup CreateNew(string style, Component parent)
            => CreateNew<PanelTabPageGroup>(style, parent);
    }
}
