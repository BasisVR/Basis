using System;
using System.Globalization;
using System.Text;

public static partial class SerializableBasis
{
    public static class ContentShareSetting
    {
        public const string Header = "basis-setting:1";
        public const string SettingsMenu = "settings";
        public const string BoolType = "bool";
        public const string NumberType = "number";
        public const string TextType = "text";
        public const string TrueText = "true";
        public const string FalseText = "false";
        public const int MaxMenuLength = 32;
        public const int MaxKeyLength = 128;
        public const int MaxTitleLength = 96;
        public const int MaxPathLength = 256;
        public const int MaxValueLength = 256;
        public const int MaxNumberLength = 64;
        public const int MaxValueTextLength = 96;

        public sealed class Contents
        {
            public string Menu = SettingsMenu;
            public string Tab;
            public string Key;
            public string Title;
            public string Path;
            public string ValueType;
            public string Value;
            public string ValueText;
            public bool HasValue => !string.IsNullOrEmpty(ValueType);
        }

        public static bool IsSettingText(string text) => text != null && text.StartsWith(Header, StringComparison.Ordinal) && (text.Length == Header.Length || text[Header.Length] == '\n' || text[Header.Length] == '\r');

        public static bool IsValidMenu(string menu)
        {
            if (string.IsNullOrEmpty(menu) || menu.Length > MaxMenuLength) return false;
            for (int i = 0; i < menu.Length; i++)
            {
                char c = menu[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || c == '-' || c == '_' || c == '.')) return false;
            }
            return true;
        }

        public static bool IsValidKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength) return false;
            return IsPlainText(key);
        }

        public static bool IsValidValue(string type, string value)
        {
            if (value == null || value.Length > MaxValueLength) return false;
            switch (type)
            {
                case BoolType:
                    return value == TrueText || value == FalseText;
                case NumberType:
                    return value.Length <= MaxNumberLength && TryReadNumber(value, out _);
                case TextType:
                    return IsPlainText(value);
                default:
                    return false;
            }
        }

        public static string WriteBool(bool value) => value ? TrueText : FalseText;

        public static string WriteNumber(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        public static string WriteNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

        public static bool TryReadNumber(string value, out double number)
        {
            number = 0;
            if (string.IsNullOrEmpty(value)) return false;
            if (!double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out number)) return false;
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }

        public static string Write(Contents contents)
        {
            StringBuilder builder = new StringBuilder(Header.Length + 256);
            builder.Append(Header).Append('\n');
            ContentShareFolder.AppendField(builder, contents.Menu);
            builder.Append('\t');
            ContentShareFolder.AppendField(builder, contents.Tab);
            builder.Append('\t');
            ContentShareFolder.AppendField(builder, contents.Key);
            builder.Append('\n');
            ContentShareFolder.AppendField(builder, ContentShareFolder.CleanName(contents.Title, MaxTitleLength));
            builder.Append('\t');
            ContentShareFolder.AppendField(builder, ContentShareFolder.CleanName(contents.Path, MaxPathLength));
            if (!contents.HasValue) return builder.ToString();
            builder.Append('\n');
            ContentShareFolder.AppendField(builder, contents.ValueType);
            builder.Append('\t');
            ContentShareFolder.AppendField(builder, contents.Value);
            builder.Append('\t');
            ContentShareFolder.AppendField(builder, ContentShareFolder.CleanName(contents.ValueText, MaxValueTextLength));
            return builder.ToString();
        }

        public static bool TryRead(string payload, out Contents contents)
        {
            contents = null;
            if (!IsSettingText(payload) || payload.Length > ContentSharePayload.MaxTextLength) return false;
            string[] lines = payload.Split('\n');
            if (lines.Length < 3 || lines[0].TrimEnd('\r') != Header) return false;
            string[] where = lines[1].TrimEnd('\r').Split('\t');
            if (where.Length < 3) return false;
            string menu = ContentShareFolder.ReadField(where[0]);
            string tab = ContentShareFolder.ReadField(where[1]);
            string key = ContentShareFolder.ReadField(where[2]);
            if (!IsValidMenu(menu) || !IsValidKey(tab) || !IsValidKey(key)) return false;
            string[] label = lines[2].TrimEnd('\r').Split('\t');
            Contents read = new Contents
            {
                Menu = menu,
                Tab = tab,
                Key = key,
                Title = ContentShareFolder.CleanName(ContentShareFolder.ReadField(label[0]), MaxTitleLength),
                Path = label.Length > 1 ? ContentShareFolder.CleanName(ContentShareFolder.ReadField(label[1]), MaxPathLength) : string.Empty,
            };
            string valueLine = lines.Length > 3 ? lines[3].TrimEnd('\r') : string.Empty;
            if (valueLine.Length > 0)
            {
                string[] value = valueLine.Split('\t');
                if (value.Length < 2) return false;
                string type = ContentShareFolder.ReadField(value[0]);
                string text = ContentShareFolder.ReadField(value[1]);
                if (!IsValidValue(type, text)) return false;
                read.ValueType = type;
                read.Value = text;
                read.ValueText = value.Length > 2 ? ContentShareFolder.CleanName(ContentShareFolder.ReadField(value[2]), MaxValueTextLength) : string.Empty;
            }
            contents = read;
            return true;
        }

        private static bool IsPlainText(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsControl(c)) return false;
                if (char.IsSurrogate(c))
                {
                    if (!char.IsSurrogatePair(value, i) || CharUnicodeInfo.GetUnicodeCategory(value, i) == UnicodeCategory.Format) return false;
                    i++;
                    continue;
                }
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) return false;
            }
            return true;
        }
    }
}
