using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelTabPage : PanelComponent
    {
        public static class TabPageStyles
        {
            public static string Default =>
                "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/Page Group Vertical.prefab";
        }

        public static PanelTabPage CreateNew(Component parent) =>
            CreateNew<PanelTabPage>(TabPageStyles.Default, parent);

        public static PanelTabPage CreateNew(string style, Component parent) =>
            CreateNew<PanelTabPage>(style, parent);

        /// <summary>
        /// Create a TabPage with a vertical layout predefined for the content parent.
        /// </summary>
        public static PanelTabPage CreateVertical(Component component)
        {
            PanelTabPage page = CreateNew(component);
            PanelElementDescriptor descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVertical, page.Descriptor.ContentParent);
            page.Descriptor.ContentParent = descriptor.ContentParent;
            return page;
        }

        public void ShowPage(bool value)
        {
            gameObject.SetActive(value);
        }
    }
}
