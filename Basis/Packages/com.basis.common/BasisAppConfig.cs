using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.Common
{
    [NoAutoStaticsCleanup]
    public static class BasisAppConfig
    {
        public const string FileName = "basis.config.json";
        public const string ResourceName = "BasisAppConfig";
        public const string MainMenu = "mainMenu", Nameplates = "nameplates", LoadingBar = "loadingBar", JoinLeaveMessages = "joinLeaveMessages", OnScreenControls = "onScreenControls";
        public const string Microphone = "microphone";
        public static string ProductName, CompanyName, Identifier, Version;
        private static Dictionary<string, bool> features = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static Dictionary<string, string> settings = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool loaded;
        private static readonly object gate = new object();
        public static string EditorPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));
        public static bool Feature(string name, bool fallback = true)
        {
            EnsureLoaded();
            lock (gate)
            {
                return features.TryGetValue(name, out bool on) ? on : fallback;
            }
        }
        public static bool TryGetSetting(string key, out string value)
        {
            EnsureLoaded();
            lock (gate)
            {
                return settings.TryGetValue(key, out value);
            }
        }
        public static bool TryGetSetting<T>(string key, out T value)
        {
            value = default;
            if (!TryGetSetting(key, out string raw))
            {
                return false;
            }
            object parsed = null;
            if (typeof(T) == typeof(string))
            {
                parsed = raw;
            }
            else if (typeof(T) == typeof(bool) && bool.TryParse(raw, out bool b))
            {
                parsed = b;
            }
            else if (typeof(T) == typeof(float) && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                parsed = f;
            }
            else if (typeof(T) == typeof(int) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= int.MinValue && d <= int.MaxValue)
            {
                parsed = (int)Math.Round(d);
            }
            if (parsed == null)
            {
                BasisDebug.LogWarning($"{FileName}: settings.{key} = {raw} isn't a {typeof(T).Name}; the built-in default is used.");
                return false;
            }
            value = (T)parsed;
            return true;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void LoadAtStartup()
        {
            loaded = false;
            EnsureLoaded();
        }
        public static void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }
            string text = null;
#if UNITY_EDITOR
            try
            {
                if (File.Exists(EditorPath))
                {
                    text = File.ReadAllText(EditorPath);
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"{FileName}: couldn't read {EditorPath}: {e.Message}");
            }
#else
            TextAsset asset = Resources.Load<TextAsset>(ResourceName);
            if (asset != null)
            {
                text = asset.text;
                Resources.UnloadAsset(asset);
            }
#endif
            Load(text);
        }
        public static void Load(string json)
        {
            Dictionary<string, bool> newFeatures = new Dictionary<string, bool>(StringComparer.Ordinal);
            Dictionary<string, string> newSettings = new Dictionary<string, string>(StringComparer.Ordinal);
            string product = null, company = null, identifier = null, version = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    if (new Reader(json).Value() is Dictionary<string, object> root)
                    {
                        product = root.TryGetValue("productName", out object p) ? p as string : null;
                        company = root.TryGetValue("companyName", out object c) ? c as string : null;
                        identifier = root.TryGetValue("identifier", out object i) ? i as string : null;
                        version = root.TryGetValue("version", out object v) ? v as string : null;
                        if (root.TryGetValue("features", out object f) && f is Dictionary<string, object> featureMap)
                        {
                            foreach (KeyValuePair<string, object> pair in featureMap)
                            {
                                if (pair.Value is bool on)
                                {
                                    newFeatures[pair.Key] = on;
                                }
                                else
                                {
                                    BasisDebug.LogWarning($"{FileName}: features.{pair.Key} must be true or false.");
                                }
                            }
                        }
                        if (root.TryGetValue("settings", out object s) && s is Dictionary<string, object> settingMap)
                        {
                            foreach (KeyValuePair<string, object> pair in settingMap)
                            {
                                string raw = pair.Value switch
                                {
                                    string text => text,
                                    bool on => on ? "true" : "false",
                                    double number => number.ToString("R", CultureInfo.InvariantCulture),
                                    _ => null,
                                };
                                if (raw != null)
                                {
                                    newSettings[pair.Key] = raw;
                                }
                            }
                        }
                    }
                }
                catch (FormatException e)
                {
                    BasisDebug.LogError($"{FileName} isn't valid JSON ({e.Message}); Basis runs with every feature on.");
                    newFeatures.Clear();
                    newSettings.Clear();
                }
            }
            lock (gate)
            {
                features = newFeatures;
                settings = newSettings;
                ProductName = product;
                CompanyName = company;
                Identifier = identifier;
                Version = version;
                loaded = true;
            }
        }
        private sealed class Reader
        {
            private readonly string text;
            private int index;
            public Reader(string text)
            {
                this.text = text;
            }
            public object Value()
            {
                Skip();
                if (index >= text.Length)
                {
                    throw new FormatException("unexpected end");
                }
                char c = text[index];
                if (c == '{')
                {
                    return Object();
                }
                if (c == '[')
                {
                    return Array();
                }
                if (c == '"')
                {
                    return String();
                }
                if (Match("true"))
                {
                    return true;
                }
                if (Match("false"))
                {
                    return false;
                }
                if (Match("null"))
                {
                    return null;
                }
                return Number();
            }
            private Dictionary<string, object> Object()
            {
                Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
                index++;
                Skip();
                if (Peek() == '}')
                {
                    index++;
                    return map;
                }
                while (true)
                {
                    Skip();
                    if (Peek() == '}')
                    {
                        index++;
                        return map;
                    }
                    if (Peek() != '"')
                    {
                        throw new FormatException($"expected a name at {index}");
                    }
                    string key = String();
                    Skip();
                    Expect(':');
                    map[key] = Value();
                    Skip();
                    if (Peek() == ',')
                    {
                        index++;
                        continue;
                    }
                    Expect('}');
                    return map;
                }
            }
            private List<object> Array()
            {
                List<object> list = new List<object>();
                index++;
                while (true)
                {
                    Skip();
                    if (Peek() == ']')
                    {
                        index++;
                        return list;
                    }
                    list.Add(Value());
                    Skip();
                    if (Peek() == ',')
                    {
                        index++;
                        continue;
                    }
                    Expect(']');
                    return list;
                }
            }
            private string String()
            {
                index++;
                StringBuilder builder = new StringBuilder();
                while (index < text.Length)
                {
                    char c = text[index++];
                    if (c == '"')
                    {
                        return builder.ToString();
                    }
                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }
                    if (index >= text.Length)
                    {
                        break;
                    }
                    char e = text[index++];
                    switch (e)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 't': builder.Append('\t'); break;
                        case 'r': builder.Append('\r'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'u':
                            if (index + 4 > text.Length)
                            {
                                throw new FormatException("bad escape");
                            }
                            builder.Append((char)int.Parse(text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            index += 4;
                            break;
                        default: builder.Append(e); break;
                    }
                }
                throw new FormatException("unterminated string");
            }
            private double Number()
            {
                int start = index;
                while (index < text.Length && "+-0123456789.eE".IndexOf(text[index]) >= 0)
                {
                    index++;
                }
                if (index == start || !double.TryParse(text.Substring(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    throw new FormatException($"unexpected '{text[start]}' at {start}");
                }
                return value;
            }
            private bool Match(string word)
            {
                if (string.CompareOrdinal(text, index, word, 0, word.Length) != 0)
                {
                    return false;
                }
                index += word.Length;
                return true;
            }
            private char Peek() => index < text.Length ? text[index] : '\0';
            private void Expect(char c)
            {
                if (Peek() != c)
                {
                    throw new FormatException($"expected '{c}' at {index}");
                }
                index++;
            }
            private void Skip()
            {
                while (index < text.Length)
                {
                    char c = text[index];
                    if (char.IsWhiteSpace(c))
                    {
                        index++;
                    }
                    else if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
                    {
                        while (index < text.Length && text[index] != '\n')
                        {
                            index++;
                        }
                    }
                    else if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
                    {
                        int end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                        index = end < 0 ? text.Length : end + 2;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
    }
}
