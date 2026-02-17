using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelTabPage : PanelComponent
    {
        public static class TabPageStyles
        {
            public static string Default =>
                "Packages/com.basis.sdk/Prefabs/Panel Elements/Tab Page.prefab";
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
        /// <summary>
        /// Create a TabPage with a Horizontal layout predefined for the content parent.
        /// </summary>
        public static PanelTabPage CreateHorizontal(Component component)
        {
            PanelTabPage page = CreateNew(component);
            PanelElementDescriptor descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewHorizontal, page.Descriptor.ContentParent);
            page.Descriptor.ContentParent = descriptor.ContentParent;
            return page;
        }
        /// <summary>
        /// Create a TabPage with a Grid layout predefined for the content parent.
        /// </summary>
        public static PanelTabPage CreateGrid(Component component)
        {
            PanelTabPage page = CreateNew(component);
            PanelElementDescriptor descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewGrid, page.Descriptor.ContentParent);
            page.Descriptor.ContentParent = descriptor.ContentParent;
            return page;
        }


        public void ShowPage(bool value)
        {
            gameObject.SetActive(value);
        }
    }
}
