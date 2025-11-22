using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelTabPageGroup : PanelElementDescriptor
    {
        public static class TabPageGroupStyles
        {
            public static string Group => "Packages/com.basis.framework/BasisUI/Prefabs/Elements/Panel Group.prefab";
        }

        public static PanelTabPageGroup CreateNew(Component parent)
            => CreateNew<PanelTabPageGroup>(TabPageGroupStyles.Group, parent);

        public static PanelTabPageGroup CreateNew(string style, Component parent)
            => CreateNew<PanelTabPageGroup>(style, parent);
    }
}
