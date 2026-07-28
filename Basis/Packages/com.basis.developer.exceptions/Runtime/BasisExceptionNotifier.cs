using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Basis.Scripts.Device_Management;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class BasisExceptionNotifier
    {
        private static readonly HashSet<string> Seen = new();
        private static readonly Regex ColorTags = new("</?color.*?>");
        private static readonly Regex TagPrefix = new(@"^\s*\[(\w+)\]");
        private static readonly Regex StackFrameType = new(@"([A-Za-z_][\w.]*)[.:][A-Za-z_]\w*\s*\(");

        private static bool _hooked;
        private static bool _presenting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (_hooked) return;
            _hooked = true;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            try
            {
                if (BasisDebug.ReportSuppressed) return;

                bool enabled = type switch
                {
                    LogType.Exception => BasisSettingsDefaults.ExceptionNotifications.RawValue,
                    LogType.Error => BasisSettingsDefaults.ErrorNotifications.RawValue,
                    _ => false,
                };
                if (!enabled) return;

                string message = StripColorTags(condition);
                string key = message + "\n" + FirstLine(stackTrace);
                lock (Seen)
                {
                    if (!Seen.Add(key)) return;
                }

                string system = ResolveSystem(message, stackTrace);
                byte severity = type == LogType.Exception ? (byte)1 : (byte)0;
                // Persist locally first (always, even when disconnected or reporting is off) so a
                // hard crash that kills the app before the live send is still re-sent next launch.
                BasisCrashReportStore.Persist(severity, system, message, stackTrace);
                BasisErrorReportSender.Report(severity, system, message, stackTrace);
                BasisDeviceManagement.EnqueueOnMainThread(() => Present(type, system, message, stackTrace));
            }
            catch
            {
            }
        }

        private static void Present(LogType type, string system, string message, string stackTrace)
        {
            if (_presenting) return;
            _presenting = true;
            try
            {
                string label = string.IsNullOrEmpty(system)
                    ? BasisLocalization.Get("notification.exception.unknownSystem")
                    : system;
                string title = string.Format(BasisLocalization.Get("notification.exception.title"), label);
                string plain = Truncate(message, 400);
                string details = $"[{label}]\n{message}\n\n{stackTrace}";

                if (BasisMainMenu.Instance)
                {
                    BasisMenuDialoguePanel before = BasisMainMenu.Instance.Dialogue;

                    BasisMainMenu.Instance.OpenDialogue(
                        title,
                        plain,
                        BasisLocalization.Get("notification.exception.copy"),
                        BasisLocalization.Get("notification.exception.dismiss"),
                        accepted =>
                        {
                            if (accepted) GUIUtility.systemCopyBuffer = details;
                        },
                        divertible: true);

                    // Only enrich a dialogue this call actually created. OpenDialogue
                    // drops the request (keeping the existing panel) when one is already
                    // open, and routes to the notification list under do-not-disturb.
                    BasisMenuDialoguePanel after = BasisMainMenu.Instance.Dialogue;
                    if (after && after != before && after.Descriptor)
                    {
                        after.Descriptor.SetRichDescription(BuildRich(type, label, message, stackTrace));
                    }
                }
                else
                {
                    BasisNotificationCenter.AddPending(
                        title,
                        plain,
                        AddressableAssets.Sprites.Information,
                        reopen: () => Present(type, system, message, stackTrace));
                }
            }
            catch
            {
            }
            finally
            {
                _presenting = false;
            }
        }

        // Palette tuned for the dark dialogue background: the name and message stay
        // near-white for legibility, and the severity colour is reserved for the small
        // badge so it reads as an accent rather than fighting the body text.
        private const string ColName = "#F0F0F0";
        private const string ColMessage = "#E2E2E2";
        private const string ColFrame = "#9AA0A6";
        private const string ColFrameLoc = "#6B7075";
        private const string ColHint = "#767B80";
        private const string AccentException = "#FF6B6B";
        private const string AccentError = "#FFB454";

        private static string BuildRich(LogType type, string label, string message, string stackTrace)
        {
            string accent = type == LogType.Exception ? AccentException : AccentError;
            string kind = type == LogType.Exception ? "EXCEPTION" : "ERROR";

            StringBuilder sb = new();

            // Header: system name (legible near-white) + a small severity badge.
            sb.Append("<b><color=").Append(ColName).Append('>')
              .Append(Wrap(label))
              .Append("</color></b>   <size=72%><b><color=").Append(accent).Append('>')
              .Append(kind)
              .Append("</color></b></size>");

            // Message.
            sb.Append("\n\n<color=").Append(ColMessage).Append('>')
              .Append(Wrap(Truncate(message, 300)))
              .Append("</color>");

            // First few stack frames, formatted as "call  file:line" and dimmed.
            string preview = StackPreview(stackTrace, 3);
            if (!string.IsNullOrEmpty(preview))
            {
                sb.Append("\n\n<size=80%>").Append(preview).Append("</size>");
            }

            // Hint that the full trace is on the Copy button.
            sb.Append("\n\n<size=76%><i><color=").Append(ColHint).Append('>')
              .Append(BasisLocalization.Get("notification.exception.copyHint"))
              .Append("</color></i></size>");

            return sb.ToString();
        }

        private static string ResolveSystem(string message, string stackTrace)
        {
            Match tag = TagPrefix.Match(message);
            if (tag.Success && Enum.IsDefined(typeof(BasisDebug.LogTag), tag.Groups[1].Value))
            {
                return tag.Groups[1].Value;
            }

            if (!string.IsNullOrEmpty(stackTrace))
            {
                Match frame = StackFrameType.Match(stackTrace);
                if (frame.Success)
                {
                    string declaringType = frame.Groups[1].Value;
                    int lastDot = declaringType.LastIndexOf('.');
                    string shortName = lastDot >= 0 ? declaringType.Substring(lastDot + 1) : declaringType;
                    if (!string.IsNullOrEmpty(shortName)) return shortName;
                }
            }

            return null;
        }

        private static string StackPreview(string stackTrace, int maxLines)
        {
            if (string.IsNullOrEmpty(stackTrace)) return string.Empty;
            string[] lines = stackTrace.Replace("\r\n", "\n").Split('\n');
            StringBuilder sb = new();
            int taken = 0;
            for (int i = 0; i < lines.Length && taken < maxLines; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (taken > 0) sb.Append('\n');
                sb.Append(FormatFrame(line));
                taken++;
            }
            return sb.ToString();
        }

        // Turns one raw stack line into "call  location" with the call in mid-grey and
        // the file:line dimmer. Handles both Unity frame forms — "Type.Method () (at
        // path:line)" and "at Type.Method () [0x..] in path:line" — and falls back to
        // the whole line when neither matches.
        private static string FormatFrame(string raw)
        {
            string line = raw;
            if (line.StartsWith("at ")) line = line.Substring(3);

            string call = line;
            string loc = null;

            int atIdx = line.IndexOf(" (at ", StringComparison.Ordinal);
            if (atIdx >= 0)
            {
                call = line.Substring(0, atIdx);
                int locStart = atIdx + 5;
                int close = line.LastIndexOf(')');
                loc = close > locStart ? line.Substring(locStart, close - locStart) : line.Substring(locStart);
            }
            else
            {
                int inIdx = line.IndexOf("] in ", StringComparison.Ordinal);
                if (inIdx >= 0)
                {
                    int brk = line.IndexOf(" [", StringComparison.Ordinal);
                    call = brk >= 0 ? line.Substring(0, brk) : line.Substring(0, inIdx);
                    loc = line.Substring(inIdx + 5);
                }
            }

            // Reduce a full path to just the file name + line.
            if (!string.IsNullOrEmpty(loc))
            {
                int slash = loc.LastIndexOfAny(PathSeparators);
                if (slash >= 0 && slash < loc.Length - 1) loc = loc.Substring(slash + 1);
            }

            StringBuilder sb = new();
            sb.Append("<color=").Append(ColFrame).Append('>').Append(Wrap(call.Trim())).Append("</color>");
            if (!string.IsNullOrEmpty(loc))
            {
                sb.Append("  <color=").Append(ColFrameLoc).Append('>').Append(Wrap(loc.Trim())).Append("</color>");
            }
            return sb.ToString();
        }

        private static readonly char[] PathSeparators = { '/', '\\' };

        // Wrap arbitrary payload text so TMP doesn't read its angle brackets as tags.
        // The inner replace defuses any literal </noparse> the payload might contain.
        private static string Wrap(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return "<noparse>" + value.Replace("</noparse>", "< /noparse>") + "</noparse>";
        }

        private static string StripColorTags(string value)
            => string.IsNullOrEmpty(value) ? value : ColorTags.Replace(value, string.Empty);

        private static string FirstLine(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            int newline = value.IndexOf('\n');
            return (newline >= 0 ? value.Substring(0, newline) : value).Trim();
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max) + "...";
        }
    }
}
