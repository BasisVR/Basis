using System;
using System.Collections.Generic;
using System.Text;
using Unity.Scripting.LifecycleManagement;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisSettingFavourites
    {
        public const int MaxFavourites = 32, MaxTitleLength = 64;
        public readonly struct Entry
        {
            public readonly string Key, TitleKey, Title;
            public Entry(string key, string titleKey, string title)
            {
                Key = key;
                TitleKey = titleKey;
                Title = title;
            }
        }
        private static readonly List<Entry> entries = new List<Entry>();
        private static string parsedFrom;
        private static int version;
        [NoAutoStaticsCleanup] private static readonly Action<bool> valueChanged = ValueChanged;
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
            entry = index >= 0 && index < entries.Count ? entries[index] : default;
            return index >= 0 && index < entries.Count;
        }
        public static string DisplayTitle(Entry entry)
        {
            if (!string.IsNullOrEmpty(entry.TitleKey) && BasisLocalization.TryGet(entry.TitleKey, out string text) && !string.IsNullOrEmpty(text)) return text;
            return string.IsNullOrEmpty(entry.Title) ? entry.Key : entry.Title;
        }
        public static bool Contains(string key)
        {
            Sync();
            return IndexOf(key) >= 0;
        }
        public static bool CanFavourite(PanelComponent control) => control is PanelDataComponent<bool> toggle && toggle.SettingsBinding != null && BasisSettingsBindings.TryGet(toggle.SettingsBinding.BindingKey, out BasisSettingsBinding<bool> _);
        public static bool Add(PanelComponent control)
        {
            if (!CanFavourite(control)) return false;
            Sync();
            string key = control.BoundSettingKey;
            if (IndexOf(key) >= 0) return true;
            if (entries.Count >= MaxFavourites) return false;
            string title = Clean(BasisMenuLocation.TitleOf(control));
            string titleKey = !string.IsNullOrEmpty(title) && BasisLocalization.TryFindKey(title, out string found) ? found : string.Empty;
            entries.Add(new Entry(key, titleKey, title));
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
            if (!Contains(key) || !BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<bool> binding)) return false;
            on = binding.RawValue;
            return true;
        }
        public static bool SetValue(string key, bool on)
        {
            if (!Contains(key) || !BasisSettingsBindings.TryGet(key, out BasisSettingsBinding<bool> binding)) return false;
            if (binding.RawValue != on)
            {
                binding.SetValue(on);
                RefreshOpenControls(key, on);
            }
            return true;
        }
        public static void AddOption(PanelComponent control, BasisMenuDialoguePanel dialogue)
        {
            if (dialogue == null || !CanFavourite(control)) return;
            string key = control.BoundSettingKey;
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
        private static int IndexOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            for (int i = 0; i < entries.Count; i++) if (entries[i].Key == key) return i;
            return -1;
        }
        private static void RefreshOpenControls(string key, bool on)
        {
            BasisMenuBase<BasisMainMenu> menu = BasisMenuBase<BasisMainMenu>.Instance;
            BasisMenuPanel panel = menu ? menu.ActiveMenu : null;
            if (panel == null) return;
            foreach (PanelDataComponent<bool> toggle in panel.GetComponentsInChildren<PanelDataComponent<bool>>(true))
            {
                if (toggle != null && toggle.BoundSettingKey == key) toggle.SetValueWithoutNotify(on);
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
                string[] fields = lines[i].Split('\t');
                string key = fields[0].Trim();
                if (key.Length == 0 || !BasisSettingRequests.ValidKey(key) || IndexOf(key) >= 0) continue;
                entries.Add(new Entry(key, fields.Length > 1 ? fields[1].Trim() : string.Empty, fields.Length > 2 ? Clean(fields[2]) : string.Empty));
            }
            Watch();
            version++;
            Notify();
        }
        private static void Save()
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) builder.Append('\n');
                builder.Append(entries[i].Key).Append('\t').Append(entries[i].TitleKey).Append('\t').Append(entries[i].Title);
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
                if (!BasisSettingsBindings.TryGet(entries[i].Key, out BasisSettingsBinding<bool> binding)) continue;
                binding.OnChanged -= valueChanged;
                binding.OnChanged += valueChanged;
            }
        }
        private static void ValueChanged(bool _)
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
