using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelTabGroup : PanelSelectionGroup
    {

        public List<PanelTabPage> Pages = new();


        public static class TabGroupStyles
        {
            public static string Vertical =>
                "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/Tab Group Vertical.prefab";
            public static string Horizontal =>
                "Packages/com.basis.framework/BasisUI/Prefabs/Panel Elements/Tab Group Horizontal.prefab";
        }


        public static PanelTabGroup CreateNew(Component parent, LayoutDirection direction)
        {
            switch (direction)
            {
                default:
                case LayoutDirection.Vertical:
                    return CreateNew<PanelTabGroup>(TabGroupStyles.Vertical, parent);
                case LayoutDirection.Horizontal:
                    return CreateNew<PanelTabGroup>(TabGroupStyles.Horizontal, parent);
            }
        }

        public static PanelTabGroup CreateNew(string style, Component parent)
            => CreateNew<PanelTabGroup>(style, parent);


        protected override void ApplyValue()
        {
            base.ApplyValue();

            for (int i = 0; i < Pages.Count; i++)
            {
                if (Pages[i]) Pages[i].ShowPage(i == Value);
            }
        }

        public void AddTab(string tabName, Action onSelected, PanelTabPage page)
        {
            PanelButton tabButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Tab, TabButtonParent);
            SelectionButtons.Add(tabButton);

            tabButton.Descriptor.SetTitle(tabName);
            tabButton.OnClicked += onSelected;
            tabButton.OnClicked += () => OnTabSelected(tabButton);

            Pages.Add(page);
            ApplyValue();
        }
    }
}
