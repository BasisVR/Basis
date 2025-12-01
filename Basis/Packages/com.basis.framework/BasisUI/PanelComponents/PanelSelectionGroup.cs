using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelSelectionGroup : PanelDataComponent<int>
    {

        public RectTransform TabButtonParent;
        public List<PanelButton> SelectionButtons = new();

        protected override void ApplyValue()
        {
            base.ApplyValue();
            for (int i = 0; i < SelectionButtons.Count; i++)
            {
                if (SelectionButtons[i]) SelectionButtons[i].ButtonStyling.ShowIndicator(i == Value);
            }
        }

        public void AddTab(string tabName, Action onSelected)
        {
            PanelButton tabButton = PanelButton.CreateNew(TabButtonParent);
            SelectionButtons.Add(tabButton);

            tabButton.Descriptor.SetTitle(tabName);
            tabButton.OnClicked += onSelected;
            tabButton.OnClicked += () => OnTabSelected(tabButton);

            ApplyValue();
        }

        protected virtual void OnTabSelected(PanelButton tab)
        {
            SetValue(SelectionButtons.IndexOf(tab));
        }
    }
}
