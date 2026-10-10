using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisCilboxPermissionText
    {
        public const string TypePrefix = "Type:";
        public const string MethodPrefix = "Method:";
        public const string FieldPrefix = "Field:";
        private const int DetailNames = 4;

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
            new Rule("BasisMediaPlayer", "LoadLocalPath", "localMedia", true),
            new Rule("BasisMediaPlayer", "LoadSource", "localMedia", true),
            new Rule("BasisMediaPlayer", "CaptureScreenshot", "screenshots", false),
            new Rule("BasisMediaPlayer", null, "mediaUrls", true),
            new Rule("System.Environment", null, "deviceInfo", false),
            new Rule("UnityEngine.SystemInfo", null, "deviceInfo", false),
            new Rule("UnityEngine.Device.", null, "deviceInfo", false),
            new Rule("System.Threading.", null, "background", false),
            new Rule("UnityEngine.PlayerPrefs", null, "storage", false),
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
            new Rule("Basis.", null, "basisInternals", false),
        };

        public static List<Line> Describe(IEnumerable<string> items)
        {
            List<string> keys = new List<string>();
            Dictionary<string, List<string>> names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            HashSet<string> severe = new HashSet<string>(StringComparer.Ordinal);
            foreach (string item in items)
            {
                if (string.IsNullOrEmpty(item)) continue;
                Split(item, out string type, out string member);
                string key = Classify(type, member, out bool isSevere);
                if (!names.TryGetValue(key, out List<string> list))
                {
                    list = new List<string>();
                    names[key] = list;
                    keys.Add(key);
                }
                string shortName = ShortName(type, member);
                if (!list.Contains(shortName)) list.Add(shortName);
                if (isSevere) severe.Add(key);
            }

            List<Line> lines = new List<Line>(keys.Count);
            foreach (string key in keys)
            {
                if (severe.Contains(key)) lines.Add(MakeLine(key, names[key], true));
            }
            foreach (string key in keys)
            {
                if (!severe.Contains(key)) lines.Add(MakeLine(key, names[key], false));
            }
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

        private static Line MakeLine(string key, List<string> names, bool severe)
        {
            string detail = names.Count <= DetailNames
                ? string.Join(", ", names)
                : string.Join(", ", names.GetRange(0, DetailNames)) + " " + BasisLocalization.Get("settings.cilboxPermissions.more", names.Count - DetailNames);
            return new Line(BasisLocalization.Get("settings.cilboxPermissions.cap." + key), detail, severe);
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

        private static string ShortName(string type, string member)
        {
            int cut = Math.Max(type.LastIndexOf('.'), type.LastIndexOf('+'));
            string shortType = cut >= 0 ? type.Substring(cut + 1) : type;
            return member == null ? shortType : shortType + "." + member;
        }
    }
}
