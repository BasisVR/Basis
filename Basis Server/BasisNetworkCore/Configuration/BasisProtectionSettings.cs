using System;
using System.Collections.Generic;

namespace Basis.Network.Core
{
    public static class BasisProtectionSettings
    {
        public const int MaxEntries = 256, MaxTextLength = 512;
        public sealed class Entry
        {
            public string Key, Group, Title, Description;
            public bool WholeNumber;
            public double Min, Max;
            public Func<double> Get;
            public Action<double> Set;
        }
        public struct Info
        {
            public string Key, Group, Title, Description;
            public bool WholeNumber;
            public double Min, Max, Value;
        }
        private static readonly List<Entry> entries = new List<Entry>();
        public static event Action Changed;
        public static void Register(string key, string group, string title, string description, bool wholeNumber, double min, double max, Func<double> get, Action<double> set)
        {
            if (string.IsNullOrEmpty(key) || get == null || set == null) return;
            Entry entry = new Entry { Key = key, Group = group ?? string.Empty, Title = title ?? key, Description = description ?? string.Empty, WholeNumber = wholeNumber, Min = min, Max = max, Get = get, Set = set };
            lock (entries)
            {
                int index = entries.FindIndex(e => e.Key == key);
                if (index >= 0) entries[index] = entry;
                else if (entries.Count < MaxEntries) entries.Add(entry);
            }
        }
        public static void RegisterField(Func<object> holder, Type type, string field, string group, double min, double max, Action applied = null)
        {
            System.Reflection.FieldInfo info = type?.GetField(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (info == null || holder == null) return;
            Type valueType = info.FieldType;
            bool whole = valueType == typeof(int) || valueType == typeof(long) || valueType == typeof(bool) || valueType == typeof(byte) || valueType == typeof(ushort);
            Register(field, group, Humanize(field), BasisConfigXmlDocs.GetComment(type, field), whole, min, max,
                () =>
                {
                    object target = holder();
                    if (target == null) return 0;
                    object value = info.GetValue(target);
                    return value is bool flag ? (flag ? 1 : 0) : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                },
                value =>
                {
                    object target = holder();
                    if (target == null) return;
                    object converted = valueType == typeof(bool) ? (object)(value >= 0.5) : Convert.ChangeType(value, valueType, System.Globalization.CultureInfo.InvariantCulture);
                    info.SetValue(target, converted);
                    applied?.Invoke();
                });
        }
        public static string Humanize(string field)
        {
            if (string.IsNullOrEmpty(field)) return string.Empty;
            System.Text.StringBuilder builder = new System.Text.StringBuilder(field.Length + 8);
            for (int index = 0; index < field.Length; index++)
            {
                char c = field[index];
                bool boundary = index > 0 && char.IsUpper(c) && (char.IsLower(field[index - 1]) || (index + 1 < field.Length && char.IsLower(field[index + 1]) && char.IsUpper(field[index - 1])));
                if (boundary) builder.Append(' ');
                builder.Append(c);
            }
            return builder.ToString();
        }
        public static List<Entry> Snapshot()
        {
            lock (entries) return new List<Entry>(entries);
        }
        public static bool TrySet(string key, double value, out double applied)
        {
            applied = 0;
            Entry entry;
            lock (entries) entry = entries.Find(e => e.Key == key);
            if (entry == null || double.IsNaN(value) || double.IsInfinity(value)) return false;
            if (value < entry.Min) value = entry.Min;
            if (value > entry.Max) value = entry.Max;
            if (entry.WholeNumber) value = Math.Round(value);
            entry.Set(value);
            applied = entry.Get();
            return true;
        }
        public static void RaiseChanged() => Changed?.Invoke();
        public static void Write(NetDataWriter writer)
        {
            List<Entry> snapshot = Snapshot();
            writer.Put((ushort)snapshot.Count);
            foreach (Entry entry in snapshot)
            {
                writer.Put(entry.Key);
                writer.Put(entry.Group);
                writer.Put(entry.Title);
                writer.Put(entry.Description);
                writer.Put(entry.WholeNumber);
                writer.Put(entry.Min);
                writer.Put(entry.Max);
                writer.Put(entry.Get());
            }
        }
        public static bool TryRead(NetDataReader reader, out List<Info> infos)
        {
            infos = new List<Info>();
            if (!reader.TryGetUShort(out ushort count) || count > MaxEntries) return false;
            for (int index = 0; index < count; index++)
            {
                Info info = new Info
                {
                    Key = reader.GetString(MaxTextLength),
                    Group = reader.GetString(MaxTextLength),
                    Title = reader.GetString(MaxTextLength),
                    Description = reader.GetString(MaxTextLength * 4),
                    WholeNumber = reader.GetBool(),
                    Min = reader.GetDouble(),
                    Max = reader.GetDouble(),
                    Value = reader.GetDouble(),
                };
                infos.Add(info);
            }
            return true;
        }
        public static void WriteChanges(NetDataWriter writer, IReadOnlyList<KeyValuePair<string, double>> changes)
        {
            int count = Math.Min(changes.Count, MaxEntries);
            writer.Put((ushort)count);
            for (int index = 0; index < count; index++)
            {
                writer.Put(changes[index].Key);
                writer.Put(changes[index].Value);
            }
        }
        public static bool TryReadChanges(NetDataReader reader, out List<KeyValuePair<string, double>> changes)
        {
            changes = new List<KeyValuePair<string, double>>();
            if (!reader.TryGetUShort(out ushort count) || count > MaxEntries) return false;
            for (int index = 0; index < count; index++)
            {
                if (!reader.TryGetString(out string key) || key.Length > MaxTextLength || !reader.TryGetDouble(out double value)) return false;
                changes.Add(new KeyValuePair<string, double>(key, value));
            }
            return true;
        }
    }
}
