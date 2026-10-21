using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using static SerializableBasis;

namespace Basis.BasisUI
{
    public interface IBasisMenuSharePlace
    {
        string DescribeSharePlace(PanelComponent control, out string label);
        bool OpenSharePlace(string place);
        RectTransform SharePlaceRoot(string place);
    }

    [AutoStaticsCleanup]
    public static partial class BasisMenuLocation
    {
        public const string NoTab = "-";
        public const string PathSeparator = " › ";
        private const string Step = ">";
        private const char PlaceMark = '@';
        private const char TitleKeyMark = '#';
        private const char TitleTextMark = '=';
        private const float RevealViewFraction = 0.25f;
        private const float BuildPatienceSeconds = 6f;
        private const float RetrySeconds = 0.2f;

        private static readonly List<PanelSectionToggle> _sectionChain = new();

        public static string MenuKey(Type providerType)
        {
            if (providerType == null) return null;
            string name = providerType.Name;
            if (name.StartsWith("Basis", StringComparison.Ordinal)) name = name.Substring(5);
            if (name.EndsWith("PanelProvider", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 13);
            else if (name.EndsWith("Provider", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 8);
            char[] key = new char[Math.Min(name.Length, ContentShareSetting.MaxMenuLength)];
            int length = 0;
            for (int i = 0; i < name.Length && length < key.Length; i++)
            {
                char c = char.ToLowerInvariant(name[i]);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) key[length++] = c;
            }
            return length > 0 ? new string(key, 0, length) : null;
        }

        public static BasisMenuActionProvider<BasisMainMenu> FindProvider(string menuKey)
        {
            if (string.IsNullOrEmpty(menuKey)) return null;
            List<BasisMenuActionProvider<BasisMainMenu>> providers = BasisMenuBase<BasisMainMenu>.Providers;
            for (int i = 0; i < providers.Count; i++)
            {
                if (providers[i] != null && MenuKey(providers[i].GetType()) == menuKey) return providers[i];
            }
            return null;
        }

        public static bool IsOnOpenPanel(PanelComponent control) => TryGetSite(control, out _, out _, out _);

        public static bool TryLocate(PanelComponent control, out string menu, out string tab, out string path)
        {
            menu = null;
            tab = null;
            path = null;
            if (!TryGetSite(control, out BasisMenuActionProvider<BasisMainMenu> provider, out string place, out string label)) return false;
            List<string> titles = new List<string>();
            if (!string.IsNullOrEmpty(provider.Title)) titles.Add(provider.Title);
            if (!string.IsNullOrEmpty(label)) titles.Add(label);
            menu = MenuKey(provider.GetType());
            tab = JoinPlace(place, DescribeTabs(control.transform, OpenPanel().transform, titles));
            AppendSectionTitles(control.transform, titles);
            path = string.Join(PathSeparator, titles);
            return menu != null;
        }

        private static bool TryGetSite(PanelComponent control, out BasisMenuActionProvider<BasisMainMenu> provider, out string place, out string label)
        {
            provider = null;
            place = null;
            label = null;
            BasisMenuPanel panel = OpenPanel();
            if (control == null || panel == null || !control.transform.IsChildOf(panel.transform)) return false;
            provider = BasisMainMenu.ActivePanelProvider;
            if (provider == null) return false;
            if (provider is IBasisMenuSharePlace places)
            {
                place = places.DescribeSharePlace(control, out label);
                if (!string.IsNullOrEmpty(place)) return place.IndexOf(Step[0]) < 0;
            }
            place = null;
            label = null;
            RectTransform content = OpenContent();
            return !provider.Hidden && content != null && control.transform.IsChildOf(content);
        }

        public static string ControlKey(PanelComponent control)
        {
            if (control == null) return null;
            string bound = control.BoundSettingKey;
            if (!string.IsNullOrEmpty(bound)) return bound;
            string title = TitleOf(control);
            if (string.IsNullOrEmpty(title)) return null;
            if (!BasisLocalization.TryFindKey(title, out string titleKey)) return TitleTextMark + title;
            List<string> keys = new List<string> { titleKey };
            PanelSectionToggle.GetSectionChain(PanelSectionToggle.GetOwningSection(control.transform), _sectionChain);
            for (int i = _sectionChain.Count - 1; i >= 0; i--)
            {
                if (!BasisLocalization.TryFindKey(TitleOf(_sectionChain[i]), out string sectionKey)) break;
                keys.Insert(0, sectionKey);
            }
            _sectionChain.Clear();
            string key = TitleKeyMark + string.Join(Step, keys);
            while (key.Length > ContentShareSetting.MaxKeyLength && keys.Count > 1)
            {
                keys.RemoveAt(0);
                key = TitleKeyMark + string.Join(Step, keys);
            }
            return key;
        }

        public static Predicate<PanelComponent> Matcher(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key[0] == TitleTextMark)
            {
                string text = key.Substring(1);
                return control => TitleOf(control) == text;
            }
            if (key[0] != TitleKeyMark) return control => control != null && control.BoundSettingKey == key;
            string[] keys = key.Substring(1).Split(Step[0]);
            string[] texts = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                if (!BasisLocalization.TryGet(keys[i], out texts[i])) return null;
            }
            return control => IsTitled(control, texts);
        }

        public static void Reveal(string menu, string tab, Predicate<PanelComponent> match, Action<PanelComponent, string> onShown, Action onMissing)
        {
            BasisMenuActionProvider<BasisMainMenu> provider = match != null ? FindProvider(menu) : null;
            string place = TakePlace(tab, out tab);
            IBasisMenuSharePlace places = place != null ? provider as IBasisMenuSharePlace : null;
            bool opened = provider != null && (place != null ? places != null : !(provider is IBasisMenuSharePlace && provider.Hidden));
            if (places != null)
            {
                opened = places.OpenSharePlace(place);
            }
            else if (opened)
            {
                BasisMainMenu.OpenWithProvider(provider);
            }
            BasisMenuPanel panel = opened && BasisMainMenu.ActivePanelProvider == provider ? OpenPanel() : null;
            if (panel == null)
            {
                onMissing?.Invoke();
                return;
            }
            panel.StartCoroutine(RevealWhenBuilt(provider, places, place, tab, match, onShown, onMissing));
        }

        private static IEnumerator RevealWhenBuilt(BasisMenuActionProvider<BasisMainMenu> provider, IBasisMenuSharePlace places, string place, string tab, Predicate<PanelComponent> match, Action<PanelComponent, string> onShown, Action onMissing)
        {
            float giveUp = Time.unscaledTime + BuildPatienceSeconds;
            PanelComponent control = null;
            while (control == null)
            {
                if (BasisMainMenu.ActivePanelProvider != provider) yield break;
                if (places == null && provider.Hidden)
                {
                    yield return new WaitForSecondsRealtime(RetrySeconds);
                    continue;
                }
                RectTransform page = SelectTabs(places != null ? places.SharePlaceRoot(place) : OpenContent(), tab);
                control = page != null ? BasisPanelSearch.RevealControl(page, match) : null;
                if (control != null) break;
                if (places == null || Time.unscaledTime >= giveUp)
                {
                    onMissing?.Invoke();
                    yield break;
                }
                yield return new WaitForSecondsRealtime(RetrySeconds);
            }
            yield return ShowRevealed(control, onShown, onMissing);
        }

        internal static string JoinPlace(string place, string tab)
        {
            if (string.IsNullOrEmpty(place)) return tab;
            return string.IsNullOrEmpty(tab) || tab == NoTab ? PlaceMark + place : PlaceMark + place + Step + tab;
        }

        internal static string TakePlace(string route, out string tab)
        {
            tab = route;
            if (string.IsNullOrEmpty(route) || route[0] != PlaceMark) return null;
            int end = route.IndexOf(Step[0]);
            tab = end < 0 ? NoTab : route.Substring(end + 1);
            return end < 0 ? route.Substring(1) : route.Substring(1, end - 1);
        }

        internal static string DescribeTabs(Transform node, Transform root, List<string> titles)
        {
            List<string> keys = new List<string>();
            int insertAt = titles.Count;
            for (Transform current = node; current != null && current != root; current = current.parent)
            {
                if (!current.TryGetComponent(out PanelTabPage page) || !TryGetTab(page, out PanelTabGroup group, out int index)) continue;
                string title = TitleOf(group.SelectionButtons[index]);
                keys.Insert(0, BasisLocalization.TryFindKey(title, out string key) ? key : index.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(title)) titles.Insert(insertAt, title);
            }
            return keys.Count > 0 ? string.Join(Step, keys) : NoTab;
        }

        internal static RectTransform SelectTabs(RectTransform root, string tab)
        {
            if (root == null || string.IsNullOrEmpty(tab) || tab == NoTab) return root;
            string[] steps = tab.Split(Step[0]);
            for (int i = 0; i < steps.Length && root != null; i++)
            {
                root = SelectTab(root, steps[i]);
            }
            return root;
        }

        private static RectTransform SelectTab(RectTransform root, string tab)
        {
            PanelTabGroup[] groups = root.GetComponentsInChildren<PanelTabGroup>(true);
            for (int i = 0; i < groups.Length; i++)
            {
                int index = FindTab(groups[i], tab);
                if (index < 0) continue;
                PanelButton button = groups[i].SelectionButtons[index];
                if (!button.gameObject.activeSelf) return null;
                if (groups[i].Value != index) button.OnClicked?.Invoke();
                PanelTabPage page = index < groups[i].Pages.Count ? groups[i].Pages[index] : null;
                return page != null ? page.Descriptor.ContentParent : null;
            }
            return null;
        }

        private static int FindTab(PanelTabGroup group, string tab)
        {
            List<PanelButton> buttons = group.SelectionButtons;
            if (int.TryParse(tab, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return index < buttons.Count && buttons[index] != null ? index : -1;
            if (!BasisLocalization.TryGet(tab, out string title)) return -1;
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i] != null && TitleOf(buttons[i]) == title) return i;
            }
            return -1;
        }

        private static bool TryGetTab(PanelTabPage page, out PanelTabGroup group, out int index)
        {
            Transform parent = page.transform.parent;
            group = parent != null ? parent.GetComponentInParent<PanelTabGroup>(true) : null;
            index = group != null ? group.Pages.IndexOf(page) : -1;
            return index >= 0 && index < group.SelectionButtons.Count && group.SelectionButtons[index] != null;
        }

        private static void AppendSectionTitles(Transform node, List<string> titles)
        {
            PanelSectionToggle.GetSectionChain(PanelSectionToggle.GetOwningSection(node), _sectionChain);
            for (int i = 0; i < _sectionChain.Count; i++)
            {
                string title = TitleOf(_sectionChain[i]);
                if (!string.IsNullOrEmpty(title)) titles.Add(title);
            }
            _sectionChain.Clear();
        }

        private static bool IsTitled(PanelComponent control, string[] texts)
        {
            int last = texts.Length - 1;
            if (TitleOf(control) != texts[last]) return false;
            Transform node = control.transform;
            for (int i = last - 1; i >= 0; i--)
            {
                PanelSectionToggle section = PanelSectionToggle.GetOwningSection(node);
                if (TitleOf(section) != texts[i]) return false;
                node = section.transform;
            }
            return true;
        }

        internal static string TitleOf(PanelComponent component)
        {
            if (component == null) return null;
            string title = component.Descriptor != null ? component.Descriptor.Title : null;
            return string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(component.ShareTitleKey) ? BasisLocalization.Get(component.ShareTitleKey) : title;
        }

        private static BasisMenuPanel OpenPanel()
        {
            BasisMenuBase<BasisMainMenu> menu = BasisMenuBase<BasisMainMenu>.Instance;
            return menu ? menu.ActiveMenu : null;
        }

        private static RectTransform OpenContent()
        {
            BasisMenuPanel panel = OpenPanel();
            return panel != null && panel.Descriptor != null ? panel.Descriptor.ContentParent : null;
        }

        private static IEnumerator ShowRevealed(PanelComponent control, Action<PanelComponent, string> onShown, Action onMissing)
        {
            yield return null;
            yield return null;
            if (control == null)
            {
                onMissing?.Invoke();
                yield break;
            }
            ScrollRect scroll = control.GetComponentInParent<ScrollRect>(true);
            BasisPanelSearch.ScrollToRow(scroll != null && scroll.content != null ? scroll.content : control.rectTransform, control.rectTransform, RevealViewFraction);
            onShown?.Invoke(control, TryLocate(control, out _, out _, out string path) ? path : null);
        }
    }
}
