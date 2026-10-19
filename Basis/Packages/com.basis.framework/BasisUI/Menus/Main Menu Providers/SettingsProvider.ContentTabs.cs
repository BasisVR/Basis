using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    public partial class SettingsProvider
    {
        private sealed class ContentTab
        {
            public string Key;
            public string Label;
            public bool Visible = true;
            public Func<PanelTabGroup, PanelTabPage> Builder;
        }

        private static readonly List<ContentTab> _contentTabs = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearContentTabs() => _contentTabs.Clear();

        public static bool HasContentTab(string key) => FindContentTab(key) != null;

        public static bool AddContentTab(string key, string label, Func<PanelTabGroup, PanelTabPage> builder)
        {
            if (string.IsNullOrEmpty(key) || builder == null || FindContentTab(key) != null)
            {
                return false;
            }

            ContentTab tab = new ContentTab { Key = key, Label = label ?? string.Empty, Builder = builder };
            _contentTabs.Add(tab);
            if (_searchTabGroup != null)
            {
                AddContentLazyTab(_searchTabGroup, tab);
            }
            return true;
        }

        public static void RemoveContentTab(string key)
        {
            ContentTab tab = FindContentTab(key);
            if (tab == null)
            {
                return;
            }

            _contentTabs.Remove(tab);
            if (_searchTabGroup != null)
            {
                ReleaseContentTab(_searchTabGroup, key);
            }
            _tabKeyToIndex.Remove(key);
            _lazyTabs.RemoveAll(lazy => lazy.Key == key);
            _tabSearches.RemoveAll(search => search.Key == key);
        }

        public static void RenameContentTab(string key, string label)
        {
            ContentTab tab = FindContentTab(key);
            if (tab == null)
            {
                return;
            }

            tab.Label = label ?? string.Empty;
            PanelButton button = ContentTabButton(key);
            if (button != null)
            {
                button.Descriptor.SetTitle(tab.Label);
            }
        }

        public static void SetContentTabVisible(string key, bool visible)
        {
            ContentTab tab = FindContentTab(key);
            if (tab == null || tab.Visible == visible)
            {
                return;
            }

            tab.Visible = visible;
            if (_searchTabGroup != null)
            {
                SetTabVisible(_searchTabGroup, key, visible);
            }
        }

        public static bool IsContentTabShown(string key)
        {
            return FindContentTab(key) != null &&
                   _searchTabGroup != null &&
                   BasisMainMenu.ActiveMenuTitle == StaticTitle &&
                   _tabKeyToIndex.TryGetValue(key, out int index) &&
                   _searchTabGroup.Value == index;
        }

        private static void AddContentTabs(PanelTabGroup tabGroup)
        {
            for (int i = 0; i < _contentTabs.Count; i++)
            {
                AddContentLazyTab(tabGroup, _contentTabs[i]);
            }
        }

        private static void AddContentLazyTab(PanelTabGroup tabGroup, ContentTab tab)
        {
            PanelTabPage placeholder = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            int index = FreeContentSlot(tabGroup);
            PanelButton button = index >= 0 ? tabGroup.SelectionButtons[index] : null;
            if (button == null)
            {
                button = PanelButton.CreateNew(PanelButton.ButtonStyles.Tab, tabGroup.TabButtonParent);
            }
            if (placeholder == null || button == null)
            {
                if (placeholder != null)
                {
                    placeholder.ReleaseInstance();
                }
                return;
            }

            if (index >= 0)
            {
                tabGroup.Pages[index] = placeholder;
                tabGroup.SelectionButtons[index] = button;
            }
            else
            {
                index = tabGroup.Pages.Count;
                tabGroup.Pages.Add(placeholder);
                tabGroup.SelectionButtons.Add(button);
            }

            LazyTab lazy = new LazyTab
            {
                Key = tab.Key,
                Index = index,
                Builder = () => BuildContentTab(tabGroup, tab),
                Placeholder = placeholder,
            };
            _lazyTabs.Add(lazy);
            _tabKeyToIndex[tab.Key] = index;

            button.OnClicked = null;
            button.OnClicked += () =>
            {
                _lastSelectedTabKey = tab.Key;
                BindPageReset(tab.Key, RealizeTab(tabGroup, lazy, forSearch: false));
            };
            button.OnClicked += () => tabGroup.SetValue(tabGroup.SelectionButtons.IndexOf(button));
            button.Descriptor.SetTitle(tab.Label);
            button.Descriptor.DisableRichText();
            button.ButtonStyling.ShowIndicator(false);
            button.transform.SetAsLastSibling();
            button.gameObject.SetActive(tab.Visible);
            placeholder.HideImmediate();
        }

        private static PanelTabPage BuildContentTab(PanelTabGroup tabGroup, ContentTab tab)
        {
            try
            {
                return tab.Builder(tabGroup);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            }
        }

        private static int FreeContentSlot(PanelTabGroup tabGroup)
        {
            int count = Math.Min(tabGroup.Pages.Count, tabGroup.SelectionButtons.Count);
            for (int i = 0; i < count; i++)
            {
                if (i != tabGroup.Value && ReferenceEquals(tabGroup.Pages[i], null) && !_tabKeyToIndex.ContainsValue(i))
                {
                    return i;
                }
            }
            return -1;
        }

        private static void ReleaseContentTab(PanelTabGroup tabGroup, string key)
        {
            if (!_tabKeyToIndex.TryGetValue(key, out int index) || index < 0 ||
                index >= tabGroup.Pages.Count || index >= tabGroup.SelectionButtons.Count)
            {
                return;
            }

            if (tabGroup.Value == index)
            {
                NavigateToTab(tabGroup, "settings.tab.general");
            }

            PanelButton button = tabGroup.SelectionButtons[index];
            if (button != null)
            {
                button.OnClicked = null;
                button.gameObject.SetActive(false);
            }

            PanelTabPage page = tabGroup.Pages[index];
            tabGroup.Pages[index] = null;
            if (page != null)
            {
                page.ReleaseInstance();
            }
        }

        private static PanelButton ContentTabButton(string key)
        {
            if (_searchTabGroup == null || !_tabKeyToIndex.TryGetValue(key, out int index) ||
                index < 0 || index >= _searchTabGroup.SelectionButtons.Count)
            {
                return null;
            }

            PanelButton button = _searchTabGroup.SelectionButtons[index];
            return button != null ? button : null;
        }

        private static string TabLabel(string key)
        {
            ContentTab tab = FindContentTab(key);
            return tab != null ? tab.Label : BasisLocalization.Get(key);
        }

        private static ContentTab FindContentTab(string key)
        {
            for (int i = 0; i < _contentTabs.Count; i++)
            {
                if (_contentTabs[i].Key == key)
                {
                    return _contentTabs[i];
                }
            }
            return null;
        }
    }
}
