using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    public class PanelTabGroup : PanelBindableElement<int>
    {

        public enum TabType
        {
            Callback,
            Page,
        }

        public struct TabData
        {
            public TabType Type;
            public PanelButton Button;
            public Action<bool> Callback;
            public PanelLayoutContainer Page;
            public bool? Value;

            public TabData(PanelButton button, Action<bool> callback)
            {
                Type = TabType.Callback;
                Button = button;
                Callback = callback;
                Page = null;
                Value = null;
            }

            public TabData(PanelButton button, PanelLayoutContainer page)
            {
                Type = TabType.Page;
                Button = button;
                Callback = null;
                Page = page;
                Value = null;
            }

            public void Set(bool value, bool ignoreMatchingStates = false)
            {
                if (Value == value && !ignoreMatchingStates)
                {
                    // Debug.Log("Value already matched.");
                    return;
                }

                Value = value;
                Button.ButtonStyling.ShowIndicator(value);

                switch (Type)
                {
                    case TabType.Callback:
                        Callback?.Invoke(value);
                        break;
                    case TabType.Page:
                        Page.SetActive(value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        public static class TabGroupStyles
        {
            public static string Default => "Packages/com.basis.framework/BasisUI/Prefabs/Elements/Panel Tab Group.prefab";
        }

        public List<PanelButton> Buttons = new();
        public List<TabData> Tabs = new();

        public virtual string ButtonStyle => PanelButton.ButtonStyles.Tab;

        /// <summary>
        /// Direction is handled via Horizontal/Vertical Layout Groups and should not be changed.
        /// </summary>
        public LayoutDirection Direction => _direction;
        protected LayoutDirection _direction;

        [SerializeField] protected PanelLayoutContainer _layoutContainer;



        public static PanelTabGroup CreateNew(Component parent)
            => CreateNew<PanelTabGroup>(TabGroupStyles.Default, parent);

        public static PanelTabGroup CreateNew(string style, Component parent)
            => CreateNew<PanelTabGroup>(style, parent);


        public void Init(LayoutDirection direction)
        {
            _direction = direction;
            _layoutContainer = PanelLayoutContainer.CreateNew(ContentParent, direction);
            ContentParent = _layoutContainer.rectTransform;
        }

        public void AddTab(string title, Sprite icon, bool iconIsAddressable, Action<bool> action)
        {
            PanelButton button = PanelButton.CreateNew<PanelButton>(ButtonStyle, ContentParent);
            button.PanelElement.SetTitle(title);
            button.SetIcon(icon, iconIsAddressable);
            button.OnClicked.AddListener(() => OnTabPressed(button));

            Buttons.Add(button);
            Tabs.Add(new TabData(button, action));
        }

        public void AddTab(string title, Sprite icon, bool iconIsAddressable, PanelLayoutContainer page)
        {
            PanelButton button = PanelButton.CreateNew(ButtonStyle, ContentParent);
            button.PanelElement.SetTitle(title);
            button.SetIcon(icon, iconIsAddressable);
            button.OnClicked.AddListener(() => OnTabPressed(button));

            Buttons.Add(button);
            Tabs.Add(new TabData(button, page));
        }

        private void OnTabPressed(PanelButton button)
        {
            int index = Buttons.IndexOf(button);
            SetValue(index);
        }

        public override void OnValueChanged()
        {
            base.OnValueChanged();
            for (int i = 0; i < Tabs.Count; i++)
            {
                Tabs[i].Set(RawValue == i);
            }
        }

        protected override void OnBoundValueLoaded()
        {
            base.OnBoundValueLoaded();
            for (int i = 0; i < Tabs.Count; i++)
            {
                Tabs[i].Set(RawValue == i, true);
            }
        }
    }
}
