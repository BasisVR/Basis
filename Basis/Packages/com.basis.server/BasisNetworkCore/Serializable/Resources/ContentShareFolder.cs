using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public static partial class SerializableBasis
{
    public static class ContentShareFolder
    {
        public const string Header = "basis-folder:1";
        public const int MaxItems = 128;
        public const int MaxIdLength = 64;
        public const int MaxNameLength = 64;
        public const int MaxItemNameLength = 96;

        public struct Item
        {
            public ContentShareType Type;
            public string Url;
            public string Password;
            public string Name;
        }

        public sealed class Contents
        {
            public string Id;
            public string Name;
            public List<Item> Items = new List<Item>();
        }

        public static bool IsItemType(ContentShareType type) => type == ContentShareType.Avatar || type == ContentShareType.Prop || type == ContentShareType.World;

        public static bool IsFolderText(string text) => text != null && text.StartsWith(Header, StringComparison.Ordinal) && (text.Length == Header.Length || text[Header.Length] == '\n' || text[Header.Length] == '\r');

        public static bool IsShareableUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.Length > ContentSharePayload.MaxLength) return false;
            for (int i = 0; i < url.Length; i++)
            {
                if (char.IsControl(url[i])) return false;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed)) return false;
            return (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(parsed.Host);
        }

        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength) return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || c == '_')) return false;
            }
            return true;
        }

        public static string CleanName(string name, int maxLength)
        {
            if (string.IsNullOrEmpty(name) || maxLength <= 0) return string.Empty;
            StringBuilder builder = new StringBuilder(Math.Min(name.Length, maxLength));
            bool pendingSpace = false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }
                if (char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
                bool pair = char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]);
                if (char.IsSurrogate(c) && !pair) continue;
                if (builder.Length + (pendingSpace ? 1 : 0) + (pair ? 2 : 1) > maxLength) break;
                if (pendingSpace) builder.Append(' ');
                pendingSpace = false;
                builder.Append(c);
                if (pair) builder.Append(name[++i]);
            }
            return builder.ToString();
        }

        public static string Write(string id, string name, IReadOnlyList<Item> items)
        {
            StringBuilder builder = new StringBuilder(Header.Length + 96 + (items?.Count ?? 0) * 192);
            builder.Append(Header).Append('\n');
            AppendField(builder, id);
            builder.Append('\t');
            AppendField(builder, CleanName(name, MaxNameLength));
            if (items == null) return builder.ToString();
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                builder.Append('\n').Append(((byte)item.Type).ToString(CultureInfo.InvariantCulture)).Append('\t');
                AppendField(builder, item.Url);
                builder.Append('\t');
                AppendField(builder, item.Password);
                builder.Append('\t');
                AppendField(builder, CleanName(item.Name, MaxItemNameLength));
            }
            return builder.ToString();
        }

        public static bool TryRead(string payload, out Contents contents)
        {
            contents = null;
            if (!IsFolderText(payload) || payload.Length > ContentSharePayload.MaxLength) return false;
            string[] lines = payload.Split('\n');
            if (lines.Length < 3 || lines[0].TrimEnd('\r') != Header) return false;
            string[] folder = lines[1].TrimEnd('\r').Split('\t');
            string id = ReadField(folder[0]);
            if (!IsValidId(id)) return false;
            Contents read = new Contents { Id = id, Name = folder.Length > 1 ? CleanName(ReadField(folder[1]), MaxNameLength) : string.Empty };
            for (int i = 2; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                if (read.Items.Count == MaxItems) return false;
                string[] fields = line.Split('\t');
                if (fields.Length < 3 || !byte.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte type) || !IsItemType((ContentShareType)type)) return false;
                string url = ReadField(fields[1]);
                string password = ReadField(fields[2]);
                if (!IsShareableUrl(url) || password.Length == 0) return false;
                read.Items.Add(new Item
                {
                    Type = (ContentShareType)type,
                    Url = url,
                    Password = password,
                    Name = fields.Length > 3 ? CleanName(ReadField(fields[3]), MaxItemNameLength) : string.Empty,
                });
            }
            if (read.Items.Count == 0) return false;
            contents = read;
            return true;
        }

        internal static void AppendField(StringBuilder builder, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '%': builder.Append("%25"); break;
                    case '\t': builder.Append("%09"); break;
                    case '\n': builder.Append("%0A"); break;
                    case '\r': builder.Append("%0D"); break;
                    default: builder.Append(c); break;
                }
            }
        }

        internal static string ReadField(string field)
        {
            if (field.IndexOf('%') < 0) return field;
            StringBuilder builder = new StringBuilder(field.Length);
            for (int i = 0; i < field.Length; i++)
            {
                char c = field[i];
                if (c == '%' && i + 2 < field.Length)
                {
                    char decoded = Decode(field[i + 1], field[i + 2]);
                    if (decoded != '\0')
                    {
                        builder.Append(decoded);
                        i += 2;
                        continue;
                    }
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static char Decode(char high, char low)
        {
            if (high == '2' && low == '5') return '%';
            if (high != '0') return '\0';
            switch (low)
            {
                case '9': return '\t';
                case 'A': case 'a': return '\n';
                case 'D': case 'd': return '\r';
                default: return '\0';
            }
        }
    }
}
