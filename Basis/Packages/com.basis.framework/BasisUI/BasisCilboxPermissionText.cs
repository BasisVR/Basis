using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisCilboxPermissionText
    {
        public const string TypePrefix = "Type:";
        public const string MethodPrefix = "Method:";
        public const string FieldPrefix = "Field:";
        private const int DetailNames = 4;
        private const int ReviewPlaces = 12;
        private const int ReviewCodes = 6;
        private const int ReviewNameLength = 64;

        public readonly struct Line
        {
            public readonly string Text;
            public readonly string Detail;
            public readonly bool Severe;

            public Line(string text, string detail, bool severe)
            {
                Text = text;
                Detail = detail;
                Severe = severe;
            }
        }

        public readonly struct Use
        {
            public readonly string Item;
            public readonly string Where;
            public readonly string Code;

            public Use(string item, string where, string code)
            {
                Item = item;
                Where = where;
                Code = code;
            }
        }

        private readonly struct Rule
        {
            public readonly string Type;
            public readonly string Member;
            public readonly string Key;
            public readonly bool Severe;

            public Rule(string type, string member, string key, bool severe)
            {
                Type = type;
                Member = member;
                Key = key;
                Severe = severe;
            }
        }

        [NoAutoStaticsCleanup] private static readonly Rule[] Rules =
        {
            new Rule("UnityEngine.Application", "OpenURL", "openWebPages", true),
            new Rule("UnityEngine.Application", "Quit", "quit", true),
            new Rule("UnityEngine.Application", null, "appControl", true),
            new Rule("System.IO.", null, "files", true),
            new Rule("System.Net.", null, "internet", true),
            new Rule("UnityEngine.Networking.", null, "internet", true),
            new Rule("System.Diagnostics.Process", null, "programs", true),
            new Rule("System.Reflection.", null, "sandboxEscape", true),
            new Rule("System.Runtime.", null, "sandboxEscape", true),
            new Rule("System.Type", null, "sandboxEscape", true),
            new Rule("System.Activator", null, "sandboxEscape", true),
            new Rule("System.AppDomain", null, "sandboxEscape", true),
            new Rule("UnityEngine.Microphone", null, "microphone", true),
            new Rule("UnityEngine.WebCamTexture", null, "webcam", true),
            new Rule("UnityEngine.WebCamDevice", null, "webcam", true),
            new Rule("UnityEngine.SceneManagement.", null, "scenes", true),
            new Rule("Basis.Scripts.BasisSdk.Players.BasisLocalPlayer", null, "yourPlayer", true),
            new Rule("Basis.Shims.BasisVelocityShim", null, "yourPlayer", true),
            new Rule("BasisMediaPlayer", "LoadLocalPath", "localMedia", true),
            new Rule("BasisMediaPlayer", "LoadSource", "localMedia", true),
            new Rule("BasisMediaPlayer", "CaptureScreenshot", "screenshots", false),
            new Rule("BasisMediaPlayer", null, "mediaUrls", true),
            new Rule("System.Environment", null, "deviceInfo", false),
            new Rule("UnityEngine.SystemInfo", null, "deviceInfo", false),
            new Rule("UnityEngine.Device.", null, "deviceInfo", false),
            new Rule("System.Threading.", null, "background", false),
            new Rule("UnityEngine.PlayerPrefs", null, "storage", true),
            new Rule("UnityEngine.Input", null, "input", false),
            new Rule("UnityEngine.InputSystem.", null, "input", false),
            new Rule("UnityEngine.GameObject", "AddComponent", "addComponents", false),
            new Rule("UnityEngine.Screen", null, "graphics", false),
            new Rule("UnityEngine.QualitySettings", null, "graphics", false),
            new Rule("UnityEngine.RenderSettings", null, "graphics", false),
            new Rule("UnityEngine.Rendering.", null, "graphics", false),
            new Rule("UnityEngine.Graphics", null, "graphics", false),
            new Rule("UnityEngine.Camera", null, "cameras", false),
            new Rule("UnityEngine.Physics", null, "physics", false),
            new Rule("UnityEngine.Physics2D", null, "physics", false),
            new Rule("UnityEngine.Audio.", null, "audio", false),
            new Rule("UnityEngine.AudioListener", null, "audio", false),
            new Rule("UnityEngine.AudioSettings", null, "audio", false),
            new Rule("Basis.Shims.BasisSaveShim", "LoadReadOnly", "readSaves", false),
            new Rule("Basis.Shims.BasisSaveShim", null, "saves", false),
            new Rule("Basis.Shims.BasisServerStorageShim", null, "serverSaves", false),
            new Rule("Basis.Shims.BasisWebRequestShim", null, "webRequests", true),
            new Rule("Basis.", null, "basisInternals", false),
        };

        public static List<Line> Describe(IEnumerable<string> items)
        {
            List<(string Key, bool Severe, List<string> Items)> groups = Group(items);
            List<Line> lines = new List<Line>(groups.Count);
            foreach ((string key, bool severe, List<string> group) in groups)
            {
                List<string> names = new List<string>(group.Count);
                foreach (string item in group)
                {
                    string name = ShortName(item);
                    if (!names.Contains(name)) names.Add(name);
                }
                lines.Add(new Line(BasisLocalization.Get("settings.cilboxPermissions.cap." + key), Join(names, DetailNames), severe));
            }
            return lines;
        }

        public static List<Line> Review(IEnumerable<string> items, IEnumerable<string> blocked, IReadOnlyList<Use> uses)
        {
            List<(string Key, bool Severe, List<string> Items)> groups = Group(items);
            List<Line> lines = new List<Line>(groups.Count + 1);
            foreach ((string key, bool severe, List<string> group) in groups)
            {
                lines.Add(new Line(BasisLocalization.Get("settings.cilboxPermissions.cap." + key), ReviewBody(BasisLocalization.Get("settings.cilboxPermissions.why." + key), group, uses), severe));
            }
            List<string> never = new List<string>();
            if (blocked != null)
            {
                foreach (string item in blocked)
                {
                    if (!string.IsNullOrEmpty(item)) never.Add(item);
                }
            }
            if (never.Count > 0) lines.Add(new Line(BasisLocalization.Get("settings.cilboxPermissions.review.blocked"), ReviewBody(BasisLocalization.Get("settings.cilboxPermissions.review.blocked.why"), never, uses), true));
            return lines;
        }

        public static string Summary(IEnumerable<string> items)
        {
            List<Line> lines = Describe(items);
            List<string> texts = new List<string>(lines.Count);
            foreach (Line line in lines) texts.Add(line.Text);
            return string.Join(", ", texts);
        }

        public static bool AnySevere(IEnumerable<string> items)
        {
            foreach (Line line in Describe(items))
            {
                if (line.Severe) return true;
            }
            return false;
        }

        private static List<(string Key, bool Severe, List<string> Items)> Group(IEnumerable<string> items)
        {
            List<string> keys = new List<string>();
            Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            HashSet<string> severe = new HashSet<string>(StringComparer.Ordinal);
            foreach (string item in items)
            {
                if (string.IsNullOrEmpty(item)) continue;
                Split(item, out string type, out string member);
                string key = Classify(type, member, out bool isSevere);
                if (!groups.TryGetValue(key, out List<string> list))
                {
                    list = new List<string>();
                    groups[key] = list;
                    keys.Add(key);
                }
                list.Add(item);
                if (isSevere) severe.Add(key);
            }
            List<(string Key, bool Severe, List<string> Items)> ordered = new List<(string Key, bool Severe, List<string> Items)>(keys.Count);
            foreach (string key in keys)
            {
                if (severe.Contains(key)) ordered.Add((key, true, groups[key]));
            }
            foreach (string key in keys)
            {
                if (!severe.Contains(key)) ordered.Add((key, false, groups[key]));
            }
            return ordered;
        }

        private static string ReviewBody(string why, List<string> items, IReadOnlyList<Use> uses)
        {
            HashSet<string> wanted = new HashSet<string>(items, StringComparer.Ordinal);
            HashSet<string> placed = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, List<string>> codes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> places = new List<string>();
            if (uses != null)
            {
                foreach (Use use in uses)
                {
                    if (use.Item == null || !wanted.Contains(use.Item)) continue;
                    placed.Add(use.Item);
                    string where = Clean(use.Where);
                    if (!codes.TryGetValue(where, out List<string> list))
                    {
                        list = new List<string>();
                        codes[where] = list;
                        places.Add(where);
                    }
                    string code = Clean(use.Code);
                    if (!list.Contains(code)) list.Add(code);
                }
            }
            places.Sort(StringComparer.OrdinalIgnoreCase);
            StringBuilder body = new StringBuilder(why);
            for (int i = 0; i < places.Count && i < ReviewPlaces; i++)
            {
                body.Append('\n').Append(BasisLocalization.Get("settings.cilboxPermissions.review.in", places[i], Join(codes[places[i]], ReviewCodes)));
            }
            if (places.Count > ReviewPlaces) body.Append('\n').Append(BasisLocalization.Get("settings.cilboxPermissions.more", places.Count - ReviewPlaces));
            List<string> unplaced = new List<string>();
            foreach (string item in items)
            {
                string name = Clean(ShortName(item));
                if (!placed.Contains(item) && !unplaced.Contains(name)) unplaced.Add(name);
            }
            if (unplaced.Count > 0) body.Append('\n').Append(BasisLocalization.Get("settings.cilboxPermissions.review.unplaced", Join(unplaced, ReviewCodes)));
            return body.ToString();
        }

        private static string Join(List<string> names, int shown)
        {
            return names.Count <= shown
                ? string.Join(", ", names)
                : string.Join(", ", names.GetRange(0, shown)) + " " + BasisLocalization.Get("settings.cilboxPermissions.more", names.Count - shown);
        }

        private static string Clean(string text)
        {
            StringBuilder clean = new StringBuilder(ReviewNameLength);
            if (text != null)
            {
                foreach (char c in text)
                {
                    if (clean.Length >= ReviewNameLength || (char.IsHighSurrogate(c) && clean.Length == ReviewNameLength - 1)) break;
                    if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
                    clean.Append(c);
                }
            }
            return clean.Length > 0 ? clean.ToString() : "?";
        }

        private static string Classify(string type, string member, out bool severe)
        {
            severe = false;
            if (member != null)
            {
                if (member == "SendMessage" || member == "SendMessageUpwards" || member == "BroadcastMessage") return "messages";
                if (member.Contains("Invoke")) return "invoke";
            }
            foreach (Rule rule in Rules)
            {
                bool typeMatch = rule.Type.EndsWith(".", StringComparison.Ordinal)
                    ? type.StartsWith(rule.Type, StringComparison.Ordinal)
                    : string.Equals(type, rule.Type, StringComparison.Ordinal);
                if (!typeMatch) continue;
                if (rule.Member != null && !string.Equals(rule.Member, member, StringComparison.Ordinal)) continue;
                severe = rule.Severe;
                return rule.Key;
            }
            return "other";
        }

        private static void Split(string item, out string type, out string member)
        {
            member = null;
            if (item.StartsWith(TypePrefix, StringComparison.Ordinal))
            {
                type = item.Substring(TypePrefix.Length);
                return;
            }
            string name = item.StartsWith(MethodPrefix, StringComparison.Ordinal) ? item.Substring(MethodPrefix.Length)
                : item.StartsWith(FieldPrefix, StringComparison.Ordinal) ? item.Substring(FieldPrefix.Length)
                : item;
            int split = name.EndsWith("..ctor", StringComparison.Ordinal) ? name.Length - 6 : name.LastIndexOf('.');
            if (split <= 0)
            {
                type = name;
                return;
            }
            type = name.Substring(0, split);
            member = name.Substring(split + 1);
        }

        private static string ShortName(string item)
        {
            Split(item, out string type, out string member);
            return ShortName(type, member);
        }

        private static string ShortName(string type, string member)
        {
            int cut = Math.Max(type.LastIndexOf('.'), type.LastIndexOf('+'));
            string shortType = cut >= 0 ? type.Substring(cut + 1) : type;
            return member == null ? shortType : shortType + "." + member;
        }
    }
}
