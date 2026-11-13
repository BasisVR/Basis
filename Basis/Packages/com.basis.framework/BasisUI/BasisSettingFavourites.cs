using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisSettingFavourites
    {
        public const int MaxFavourites = 32, MaxTitleLength = 64, MaxOptions = 32;
        public enum Kind
        {
            Toggle = 0,
            Slider = 1,
            Choice = 2,
            Button = 3,
        }
        public readonly struct Entry
        {
            public readonly string Key, TitleKey, Title;
            public readonly Kind Kind;
            public readonly PanelSlider.SliderSettings Slider;
            public readonly string[] OptionValues, OptionKeys, OptionLabels;
            public readonly bool Menu;
            public Entry(string key, string titleKey, string title, Kind kind, PanelSlider.SliderSettings slider, string[] optionValues, string[] optionKeys, string[] optionLabels, bool menu = false)
            {
                Menu = menu;
                Key = key;
                TitleKey = titleKey;
                Title = title;
                Kind = kind;
                Slider = slider;
                OptionValues = optionValues;
                OptionKeys = optionKeys;
                OptionLabels = optionLabels;
            }
            public int OptionCount => OptionValues != null ? OptionValues.Length : 0;
        }
        private static readonly List<Entry> entries = new List<Entry>();
        private readonly struct LiveToggle
        {
            public readonly Func<bool?> Get;
            public readonly Action<bool> Set;
            public LiveToggle(Func<bool?> get, Action<bool> set)
            {
                Get = get;
                Set = set;
            }
        }
        private static readonly Dictionary<string, LiveToggle> liveToggles = new Dictionary<string, LiveToggle>();
        private static string parsedFrom;
        private static int version;
        [NoAutoStaticsCleanup] private static readonly Action<bool> boolChanged = _ => ValueChanged();
        [NoAutoStaticsCleanup] private static readonly Action<float> floatChanged = _ => ValueChanged();
        [NoAutoStaticsCleanup] private static readonly Action<string> stringChanged = _ => ValueChanged();
        public static event Action Changed;
        public static int Version
        {
            get
            {
                Sync();
                return version;
            }
        }
        public static int Count
        {
            get
            {
                Sync();
                return entries.Count;
            }
        }
        public static bool TryGet(int index, out Entry entry)
        {
            Sync();
            entry = index >= 0 && index < entries.Count ? Resolve(entries[index]) : default;
            return index >= 0 && index < entries.Count;
        }
        public static void RegisterLive(string key, Func<bool?> get, Action<bool> set)
        {
            if (!BasisSettingRequests.ValidKey(key) || get == null || set == null) return;
            liveToggles[key] = new LiveToggle(get, set);
            LiveChanged(key);
        }
        public static bool IsLive(string key) => !string.IsNullOrEmpty(key) && liveToggles.ContainsKey(key);
        public static void LiveChanged(string key)
        {
            if (Contains(key)) ValueChanged();
        }
        public static string KeyOf(PanelComponent control) => control == null ? null : control.BoundSettingKey ?? control.LiveFavouriteKey;
        public static string DisplayTitle(Entry entry) => Localized(entry.TitleKey, entry.Title, entry.Key);
        public static string OptionLabel(Entry entry, int option)
        {
            if (option < 0 || option >= entry.OptionCount) return string.Empty;
            return Localized(entry.OptionKeys[option], entry.OptionLabels[option], entry.OptionValues[option]);
        }
        public static bool Contains(string key)
        {
            Sync();
            return IndexOf(key) >= 0;
        }
        public static bool CanFavourite(PanelComponent control)
        {
            switch (control)
            {
                case PanelDataComponent<bool> toggle:
                    if (toggle.SettingsBinding == null) return IsLive(toggle.LiveFavouriteKey);
                    return BasisSettingsBindings.TryGet(toggle.SettingsBinding.BindingKey, out BasisSettingsBinding<bool> _);
                case PanelSlider slider:
                    return slider.SettingsBinding != null && slider.Settings.SliderMax > slider.Settings.SliderMin && BasisSettingsBindings.TryGet(slider.SettingsBinding.BindingKey, out BasisSettingsBinding<float> _);
                case PanelDropdown dropdown:
                    if (dropdown.SettingsBinding == null || !BasisSettingsBindings.TryGet(dropdown.SettingsBinding.BindingKey, out BasisSettingsBinding<string> _)) return false;
                    if (dropdown.Entries == null || dropdown.Entries.Count < 2 || dropdown.Entries.Count > MaxOptions) return false;
                    for (int i = 0; i < dropdown.Entries.Count; i++) if (!Storable(dropdown.Entries[i])) return false;
                    return true;
                default:
                    return false;
            }
        }
        public static bool Add(PanelComponent control)
        {
            if (!CanFavourite(control)) return false;
            Sync();
            string key = KeyOf(control);
            if (IndexOf(key) >= 0) return true;
            if (entries.Count >= MaxFavourites) return false;
            string title = Clean(BasisMenuLocation.TitleOf(control));
            string titleKey = FindKey(title);
            switch (control)
            {
                case PanelSlider slider:
                    entries.Add(new Entry(key, titleKey, title, Kind.Slider, slider.Settings, null, null, null));
                    break;
                case PanelDropdown dropdown:
                    int count = dropdown.Entries.Count;
                    string[] values = new string[count], keys = new string[count], labels = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = dropdown.Entries[i];
                        labels[i] = Clean(dropdown.DropdownComponent != null && dropdown.DropdownComponent.options.Count == count ? dropdown.DropdownComponent.options[i].text : values[i]);
                        keys[i] = FindKey(labels[i]);
                    }
                    entries.Add(new Entry(key, titleKey, title, Kind.Choice, default, values, keys, labels));
                    break;
                default:
                    entries.Add(new Entry(key, titleKey, title, Kind.Toggle, default, null, null, null));
                    break;
            }
            Save();
            return true;
        }
        public static bool Remove(string key)
        {
            Sync();
            int index = IndexOf(key);
            if (index < 0) return false;
            entries.RemoveAt(index);
            Save();
            return true;
        }
        public static bool TryGetValue(string key, out bool on)
        {
            on = false;
            if (!TryFind(key, Kind.Toggle, out Entry entry)) return false;
            if (entry.Menu)
            {
                BasisMenuActionProvider<BasisMainMenu> provider = FindProvider(key);
                if (provider == null || !provider.FavouriteIsToggle) return false;
                on = provider.FavouriteOn;
                return true;
            }
            if (liveToggles.TryGetValue(key, out LiveToggle live))
            {
                bool? current = live.Get();
                on = current == true;
                return current.HasValue;
            }
            if (!BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<bool> binding)) return false;
            on = binding.RawValue;
            return true;
        }
        public static bool SetValue(string key, bool on)
        {
            if (!TryFind(key, Kind.Toggle, out Entry entry)) return false;
            if (entry.Menu)
            {
                BasisMenuActionProvider<BasisMainMenu> provider = FindProvider(key);
                if (provider == null || !provider.FavouriteIsToggle) return false;
                if (provider.FavouriteOn != on) provider.SetFavouriteOn(on);
                return true;
            }
            if (liveToggles.TryGetValue(key, out LiveToggle live))
            {
                bool? current = live.Get();
                if (!current.HasValue) return false;
                if (current.Value != on)
                {
                    live.Set(on);
                    RefreshOpenControls(key, on);
                    ValueChanged();
                }
                return true;
            }
            if (!BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<bool> binding)) return false;
            if (binding.RawValue != on)
            {
                binding.SetValue(on);
                RefreshOpenControls(key, on);
            }
            return true;
        }
        public static bool TryGetSlider(string key, out float value)
        {
            value = 0f;
            if (!TryFind(key, Kind.Slider, out _) || !BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<float> binding)) return false;
            value = binding.RawValue;
            return true;
        }
        public static bool SetSlider(string key, float value)
        {
            if (!TryFind(key, Kind.Slider, out Entry entry) || !BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<float> binding)) return false;
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            value = Conform(entry, value);
            if (binding.RawValue != value)
            {
                binding.SetValue(value);
                RefreshOpenControls(key, value);
            }
            return true;
        }
        public static float Conform(Entry entry, float value)
        {
            value = Mathf.Clamp(value, entry.Slider.SliderMin, entry.Slider.SliderMax);
            return entry.Slider.UseWholeNumbers ? Mathf.Round(value) : value;
        }
        public static string FormatSlider(Entry entry, float value)
        {
            if (entry.Kind != Kind.Slider) return string.Empty;
            return PanelSlider.FormatValue(value, entry.Slider) ?? value.ToString(CultureInfo.InvariantCulture);
        }
        public static bool TryGetChoice(string key, out int option)
        {
            option = -1;
            if (!TryFind(key, Kind.Choice, out Entry entry) || !BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<string> binding)) return false;
            for (int i = 0; i < entry.OptionCount; i++)
            {
                if (string.Equals(entry.OptionValues[i], binding.RawValue, StringComparison.OrdinalIgnoreCase))
                {
                    option = i;
                    break;
                }
            }
            return true;
        }
        public static bool SetChoice(string key, int option)
        {
            if (!TryFind(key, Kind.Choice, out Entry entry) || option < 0 || option >= entry.OptionCount || !BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<string> binding)) return false;
            string value = entry.OptionValues[option];
            if (!string.Equals(binding.RawValue, value, StringComparison.Ordinal))
            {
                binding.SetValue(value);
                RefreshOpenControls(key, value);
            }
            return true;
        }
        public static bool Press(string key)
        {
            if (!TryFind(key, Kind.Button, out _)) return false;
            BasisMenuActionProvider<BasisMainMenu> provider = FindProvider(key);
            if (provider == null) return false;
            if (provider.FavouriteIsToggle) provider.SetFavouriteOn(!provider.FavouriteOn);
            else BasisMainMenu.OpenWithProvider(provider);
            return true;
        }
        public static bool CanFavourite(BasisMenuActionProvider<BasisMainMenu> provider) => provider != null && BasisSettingRequests.ValidKey(provider.FavouriteKey) && !string.IsNullOrEmpty(Clean(provider.FavouriteTitle));
        public static bool Add(BasisMenuActionProvider<BasisMainMenu> provider)
        {
            if (!CanFavourite(provider)) return false;
            Sync();
            string key = provider.FavouriteKey;
            if (IndexOf(key) >= 0) return true;
            if (entries.Count >= MaxFavourites) return false;
            string title = Clean(provider.FavouriteTitle);
            entries.Add(new Entry(key, FindKey(title), title, provider.FavouriteIsToggle ? Kind.Toggle : Kind.Button, default, null, null, null, true));
            Save();
            return true;
        }
        public static void MenuChanged() => ValueChanged();
        public static void BindMenuOptions(BasisMenuActionProvider<BasisMainMenu> provider, PanelButton button)
        {
            if (button == null || provider == null || (!provider.CanRemoveFromMenu && !CanFavourite(provider))) return;
            button.OnResetRequested = () => OfferMenuOptions(provider);
        }
        public static void OfferMenuOptions(BasisMenuActionProvider<BasisMainMenu> provider)
        {
            BasisMenuBase<BasisMainMenu> menu = BasisMenuBase<BasisMainMenu>.Instance;
            if (provider == null || menu == null || (menu.Dialogue != null && !menu.Dialogue.IsReleased)) return;
            List<(string Label, Action Run)> options = new List<(string Label, Action Run)>(2);
            if (CanFavourite(provider))
            {
                string key = provider.FavouriteKey;
                bool favourite = Contains(key);
                if (favourite || Count < MaxFavourites) options.Add((BasisLocalization.Get(favourite ? "settings.favourites.remove" : "settings.favourites.add"), () =>
                {
                    if (favourite) Remove(key);
                    else Add(provider);
                }));
            }
            if (provider.CanRemoveFromMenu) options.Add((BasisLocalization.Get("menu.removeFromMenu"), provider.RemoveFromMenu));
            if (options.Count == 0) return;
            string label = Clean(provider.FavouriteTitle);
            menu.OpenDialogue(BasisLocalization.Get("ui.panelOptions.title"), string.Format(BasisLocalization.Get("ui.panelOptions.body"), string.IsNullOrEmpty(label) ? provider.Title : label), options[0].Label, BasisLocalization.Get("ui.cancel"), confirmed =>
            {
                if (confirmed) options[0].Run();
            });
            BasisMenuDialoguePanel dialogue = menu.Dialogue;
            if (dialogue == null) return;
            dialogue.CaptureOnClose = false;
            for (int i = 1; i < options.Count; i++)
            {
                Action run = options[i].Run;
                dialogue.AddOption(options[i].Label, () =>
                {
                    run();
                    if (dialogue != null && !dialogue.IsReleased) dialogue.ReleaseInstance();
                }, false);
            }
        }
        private static BasisMenuActionProvider<BasisMainMenu> FindProvider(string key)
        {
            List<BasisMenuActionProvider<BasisMainMenu>> providers = BasisMenuBase<BasisMainMenu>.Providers;
            for (int i = 0; i < providers.Count; i++)
            {
                BasisMenuActionProvider<BasisMainMenu> provider = providers[i];
                if (provider != null && !provider.Hidden && provider.FavouriteKey == key) return provider;
            }
            return null;
        }
        public static void AddOption(PanelComponent control, BasisMenuDialoguePanel dialogue)
        {
            if (dialogue == null || !CanFavourite(control)) return;
            string key = KeyOf(control);
            bool favourite = Contains(key);
            if (!favourite && Count >= MaxFavourites) return;
            dialogue.AddOption(BasisLocalization.Get(favourite ? "settings.favourites.remove" : "settings.favourites.add"), () =>
            {
                if (favourite) Remove(key);
                else Add(control);
                if (dialogue != null && !dialogue.IsReleased)
                {
                    dialogue.CaptureOnClose = false;
                    dialogue.ReleaseInstance();
                }
            }, false);
        }
        public static bool OfferOptions(PanelComponent control)
        {
            BasisMenuBase<BasisMainMenu> menu = BasisMenuBase<BasisMainMenu>.Instance;
            if (!CanFavourite(control) || menu == null || menu.Dialogue != null) return false;
            string key = KeyOf(control);
            bool favourite = Contains(key);
            if (!favourite && Count >= MaxFavourites) return false;
            string label = Clean(BasisMenuLocation.TitleOf(control));
            menu.OpenDialogue(BasisLocalization.Get("ui.panelOptions.title"), string.Format(BasisLocalization.Get("ui.panelOptions.body"), string.IsNullOrEmpty(label) ? key : label), BasisLocalization.Get(favourite ? "settings.favourites.remove" : "settings.favourites.add"), BasisLocalization.Get("ui.cancel"), confirmed =>
            {
                if (!confirmed) return;
                if (favourite) Remove(key);
                else Add(control);
            });
            if (menu.Dialogue == null) return true;
            BasisContentShareSetting.AddShareOption(control, menu.Dialogue);
            return true;
        }
        private static Entry Resolve(Entry entry)
        {
            if (!entry.Menu || entry.Kind != Kind.Button) return entry;
            BasisMenuActionProvider<BasisMainMenu> provider = FindProvider(entry.Key);
            return provider != null && provider.FavouriteIsToggle ? new Entry(entry.Key, entry.TitleKey, entry.Title, Kind.Toggle, default, null, null, null, true) : entry;
        }
        private static bool TryFind(string key, Kind kind, out Entry entry)
        {
            Sync();
            int index = IndexOf(key);
            entry = index >= 0 ? Resolve(entries[index]) : default;
            return index >= 0 && entry.Kind == kind;
        }
        private static int IndexOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            for (int i = 0; i < entries.Count; i++) if (entries[i].Key == key) return i;
            return -1;
        }
        private static void RefreshOpenControls<T>(string key, T value)
        {
            BasisMenuBase<BasisMainMenu> menu = BasisMenuBase<BasisMainMenu>.Instance;
            BasisMenuPanel panel = menu ? menu.ActiveMenu : null;
            if (panel == null) return;
            foreach (PanelDataComponent<T> control in panel.GetComponentsInChildren<PanelDataComponent<T>>(true))
            {
                if (control != null && KeyOf(control) == key) control.SetValueWithoutNotify(value);
            }
        }
        private static void Sync()
        {
            string raw = BasisSettingsDefaults.FavouriteSettings.RawValue ?? string.Empty;
            if (raw == parsedFrom) return;
            parsedFrom = raw;
            entries.Clear();
            string[] lines = raw.Split('\n');
            for (int i = 0; i < lines.Length && entries.Count < MaxFavourites; i++)
            {
                if (TryParse(lines[i], out Entry entry) && IndexOf(entry.Key) < 0) entries.Add(entry);
            }
            Watch();
            version++;
            Notify();
        }
        private static bool TryParse(string line, out Entry entry)
        {
            entry = default;
            string[] fields = line.Split('\t');
            string key = fields[0].Trim();
            if (key.Length == 0 || !BasisSettingRequests.ValidKey(key)) return false;
            string titleKey = fields.Length > 1 ? fields[1].Trim() : string.Empty;
            string title = fields.Length > 2 ? Clean(fields[2]) : string.Empty;
            string kind = fields.Length > 3 ? fields[3].Trim() : "t";
            if (kind == "m" || kind == "b")
            {
                entry = new Entry(key, titleKey, title, kind == "m" ? Kind.Toggle : Kind.Button, default, null, null, null, true);
                return true;
            }
            if (kind == "s")
            {
                if (fields.Length < 9 || !Number(fields[4], out float min) || !Number(fields[5], out float max) || !(max > min)) return false;
                if (!int.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int decimals) || !int.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode)) return false;
                if (!Enum.IsDefined(typeof(ValueDisplayMode), mode)) return false;
                PanelSlider.SliderSettings slider = PanelSlider.SliderSettings.Advanced(title, min, max, fields[6].Trim() == "1", Mathf.Clamp(decimals, 0, 6), (ValueDisplayMode)mode);
                entry = new Entry(key, titleKey, title, Kind.Slider, slider, null, null, null);
                return true;
            }
            if (kind == "c")
            {
                if (fields.Length < 5 || !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 2 || count > MaxOptions || fields.Length < 5 + count * 3) return false;
                string[] values = new string[count], keys = new string[count], labels = new string[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = fields[5 + i * 3];
                    keys[i] = fields[6 + i * 3].Trim();
                    labels[i] = Clean(fields[7 + i * 3]);
                }
                entry = new Entry(key, titleKey, title, Kind.Choice, default, values, keys, labels);
                return true;
            }
            entry = new Entry(key, titleKey, title, Kind.Toggle, default, null, null, null);
            return true;
        }
        private static void Save()
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (i > 0) builder.Append('\n');
                builder.Append(entry.Key).Append('\t').Append(entry.TitleKey).Append('\t').Append(entry.Title);
                if (entry.Menu)
                {
                    builder.Append(entry.Kind == Kind.Toggle ? "\tm" : "\tb");
                    continue;
                }
                switch (entry.Kind)
                {
                    case Kind.Slider:
                        builder.Append("\ts\t").Append(entry.Slider.SliderMin.ToString("R", CultureInfo.InvariantCulture))
                            .Append('\t').Append(entry.Slider.SliderMax.ToString("R", CultureInfo.InvariantCulture))
                            .Append('\t').Append(entry.Slider.UseWholeNumbers ? '1' : '0')
                            .Append('\t').Append(entry.Slider.DecimalPlaces.ToString(CultureInfo.InvariantCulture))
                            .Append('\t').Append(((int)entry.Slider.DisplayMode).ToString(CultureInfo.InvariantCulture));
                        break;
                    case Kind.Choice:
                        builder.Append("\tc\t").Append(entry.OptionCount.ToString(CultureInfo.InvariantCulture));
                        for (int o = 0; o < entry.OptionCount; o++) builder.Append('\t').Append(entry.OptionValues[o]).Append('\t').Append(entry.OptionKeys[o]).Append('\t').Append(entry.OptionLabels[o]);
                        break;
                }
            }
            string raw = builder.ToString();
            parsedFrom = raw;
            BasisSettingsDefaults.FavouriteSettings.SetValue(raw);
            Watch();
            version++;
            Notify();
        }
        private static void Watch()
        {
            for (int i = 0; i < entries.Count; i++)
            {
                string key = entries[i].Key;
                if (entries[i].Menu) continue;
                switch (entries[i].Kind)
                {
                    case Kind.Slider:
                        if (!BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<float> slider)) break;
                        slider.OnChanged -= floatChanged;
                        slider.OnChanged += floatChanged;
                        break;
                    case Kind.Choice:
                        if (!BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<string> choice)) break;
                        choice.OnChanged -= stringChanged;
                        choice.OnChanged += stringChanged;
                        break;
                    default:
                        if (!BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<bool> toggle)) break;
                        toggle.OnChanged -= boolChanged;
                        toggle.OnChanged += boolChanged;
                        break;
                }
            }
        }
        private static void ValueChanged()
        {
            version++;
            Notify();
        }
        private static void Notify()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                BasisDebug.LogErrorUnreported($"[SettingFavourites] A change listener threw: {e}");
            }
        }
        private static string Localized(string key, string text, string fallback)
        {
            if (!string.IsNullOrEmpty(key) && BasisLocalization.TryGet(key, out string found) && !string.IsNullOrEmpty(found)) return found;
            return string.IsNullOrEmpty(text) ? fallback : text;
        }
        private static string FindKey(string text) => !string.IsNullOrEmpty(text) && BasisLocalization.TryFindKey(text, out string found) ? found : string.Empty;
        private static bool Number(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Storable(string value)
        {
            if (value == null) return false;
            for (int i = 0; i < value.Length; i++) if (char.IsControl(value[i])) return false;
            return true;
        }
        private static string Clean(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;
            StringBuilder builder = new StringBuilder(Math.Min(title.Length, MaxTitleLength));
            for (int i = 0; i < title.Length && builder.Length < MaxTitleLength; i++)
            {
                char c = title[i];
                builder.Append(c == '\t' || c == '\n' || c == '\r' || char.IsControl(c) ? ' ' : c);
            }
            return builder.ToString().Trim();
        }
    }
}
