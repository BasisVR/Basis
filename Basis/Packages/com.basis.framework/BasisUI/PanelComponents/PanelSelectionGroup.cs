using System;
using System.Collections.Generic;
using Basis.BTween;
using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelSelectionGroup : PanelDataComponent<int>
    {

        public RectTransform TabButtonParent;
        public List<PanelButton> SelectionButtons = new();

        private int _previousValue = -1;
        private TweenScale _tabPunchTween;

        protected override void ApplyValue()
        {
            base.ApplyValue();
            for (int i = 0; i < SelectionButtons.Count; i++)
            {
                if (SelectionButtons[i]) SelectionButtons[i].ButtonStyling.ShowIndicator(i == Value);
            }

            AnimateTabSelection();
        }

        private void AnimateTabSelection()
        {
            if (!Application.isPlaying) return;
            if (Value == _previousValue) return;
            if (Value < 0 || Value >= SelectionButtons.Count) return;

            // Only animate after initial setup
            bool isFirstSelection = _previousValue == -1;
            _previousValue = Value;
            if (isFirstSelection) return;

            PanelButton selected = SelectionButtons[Value];
            if (selected == null) return;

            // Scale emphasis on the newly selected tab
            if (_tabPunchTween != null && _tabPunchTween.Active) _tabPunchTween.Reset();
            _tabPunchTween = selected.transform.TweenScale(0.1f, selected.transform.localScale, Vector3.one * 1.08f)
                .SetEase(Easing.OutCubic)
                .AddCallback(() =>
                {
                    if (selected != null)
                    {
                        selected.transform.TweenScale(0.15f, Vector3.one * 1.08f, Vector3.one)
                            .SetEase(Easing.OutBack);
                    }
                });
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
