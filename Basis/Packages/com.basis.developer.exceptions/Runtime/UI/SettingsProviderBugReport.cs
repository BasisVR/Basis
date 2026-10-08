using System;
using System.Text;
using Basis.Scripts.Networking;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Developer-tab "Report a Bug" section. Mirrors the fields of the project's
    /// GitHub bug form (.github/ISSUE_TEMPLATE/1-bug.yml) and, on submit, opens
    /// GitHub's new-issue page for that form with every field pre-filled through
    /// query parameters. The user reviews and submits on GitHub, so no API token
    /// or auth has to live in the client.
    /// </summary>
    public static class SettingsProviderBugReport
    {
        private const string NewIssueUrl = "https://github.com/BasisVR/Basis/issues/new";
        private const string IssueTemplate = "1-bug.yml";

        // GitHub rejects very long prefill URLs with a 414, so keep the assembled
        // URL under this and shed the log field (the usual offender) when over.
        private const int MaxUrlLength = 7000;
        private const int MaxAttachedLogChars = 2500;
        private const int FieldCharLimit = 4000;

        // The server records severity 1 as "exception"; routing a user report through the
        // same path files it under that label, tagged with this system name.
        private const byte ServerReportSeverity = 1;
        private const string ServerReportSystem = "BugReport";

        [RuntimeInitializeOnLoadMethod]
        private static void Register()
        {
            SettingsProvider.DeveloperSectionBuilders.Remove(BuildBugReportGroup);
            SettingsProvider.DeveloperSectionBuilders.Add(BuildBugReportGroup);
        }

        public static void BuildBugReportGroup(RectTransform container)
        {
            PanelElementDescriptor group =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            group.SetTitle(BasisLocalization.Get("settings.developer.bugReport.title"));
            group.SetDescription(BasisLocalization.Get("settings.developer.bugReport.description"));

            RectTransform parent = group.ContentParent;

            PanelToggle enableToggle = PanelToggle.CreateNewEntry(parent);
            enableToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.bugReport.enable"));
            enableToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.bugReport.enable.tooltip"));
            enableToggle.SetValueWithoutNotify(false);

            PanelTextField summaryField = CreateField(parent, "settings.developer.bugReport.summary", "settings.developer.bugReport.summary.tooltip", false, 120);
            PanelTextField describeField = CreateRequiredField(parent, "settings.developer.bugReport.describe", "settings.developer.bugReport.describe.tooltip");
            PanelTextField reproField = CreateRequiredField(parent, "settings.developer.bugReport.repro", "settings.developer.bugReport.repro.tooltip");
            PanelTextField expectedField = CreateRequiredField(parent, "settings.developer.bugReport.expected", "settings.developer.bugReport.expected.tooltip");
            PanelTextField contextField = CreateField(parent, "settings.developer.bugReport.context", "settings.developer.bugReport.context.tooltip");

            PanelButton submitButton = PanelButton.CreateNew(parent);
            submitButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.bugReport.submit"));
            submitButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.bugReport.submit.tooltip"));
            submitButton.OnClicked += () => Submit(summaryField, describeField, reproField, expectedField, contextField);

            PanelButton serverButton = PanelButton.CreateNew(parent);
            serverButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.bugReport.sendServer"));
            serverButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.bugReport.sendServer.tooltip"));
            serverButton.OnClicked += () => SendToServer(summaryField, describeField, reproField, expectedField, contextField);

            PanelButton copyButton = PanelButton.CreateNew(parent);
            copyButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.bugReport.copy"));
            copyButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.bugReport.copy.tooltip"));
            copyButton.OnClicked += () =>
            {
                GUIUtility.systemCopyBuffer = BuildMarkdown(summaryField, describeField, reproField, expectedField, contextField);
                Notify("settings.developer.bugReport.copied");
            };

            PanelElementDescriptor[] gated =
            {
                summaryField.Descriptor, describeField.Descriptor, reproField.Descriptor,
                expectedField.Descriptor, contextField.Descriptor,
                submitButton.Descriptor, serverButton.Descriptor, copyButton.Descriptor,
            };

            void RefreshVisibility(bool on)
            {
                foreach (PanelElementDescriptor d in gated) d.SetActive(on);
                group.ForceRebuild();
            }
            RefreshVisibility(false);
            enableToggle.OnValueChanged += RefreshVisibility;
        }

        private static PanelTextField CreateField(RectTransform parent, string titleKey, string tooltipKey, bool multiline = true, int charLimit = FieldCharLimit)
        {
            PanelTextField field = PanelTextField.CreateNewEntry(parent);
            field.Descriptor.SetTitle(BasisLocalization.Get(titleKey));
            field.Descriptor.SetTooltip(BasisLocalization.Get(tooltipKey));
            field.SetValueWithoutNotify(string.Empty);
            if (field._inputField != null)
            {
                field._inputField.contentType = TMP_InputField.ContentType.Standard;
                field._inputField.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
                field._inputField.characterLimit = charLimit;
            }
            return field;
        }

        /// <summary>
        /// A field the report cannot be sent without. It stays neutral until the reporter types in it
        /// or presses a send button, at which point the ones actually missing light up — the generic
        /// "fill in the required fields" notice never said which.
        /// </summary>
        private static PanelTextField CreateRequiredField(RectTransform parent, string titleKey, string tooltipKey, bool multiline = true, int charLimit = FieldCharLimit)
        {
            PanelTextField field = CreateField(parent, titleKey, tooltipKey, multiline, charLimit);
            field.SetRequired(BasisLocalization.Get("ui.validation.requiredNamed", BasisLocalization.Get(titleKey)),
                gradeImmediately: false);
            return field;
        }

        private static bool ValidateRequired(PanelTextField describe, PanelTextField repro, PanelTextField expected)
        {
            bool describeOk = describe.Validate();
            bool reproOk = repro.Validate();
            bool expectedOk = expected.Validate();
            return describeOk && reproOk && expectedOk;
        }

        private static void Submit(
            PanelTextField summary, PanelTextField describe, PanelTextField repro, PanelTextField expected,
            PanelTextField context)
        {
            if (!ValidateRequired(describe, repro, expected))
            {
                Notify("settings.developer.bugReport.missingFields");
                return;
            }

            string describeText = Read(describe);
            string reproText = Read(repro);
            string expectedText = Read(expected);

            string url = BuildPrefillUrl(Read(summary), describeText, reproText, expectedText,
                GatherBuildInfo(), GatherRecentLogs(), Read(context), out bool trimmed);

            if (trimmed)
            {
                GUIUtility.systemCopyBuffer = BuildMarkdown(summary, describe, repro, expected, context);
            }

            Application.OpenURL(url);
            Notify(trimmed ? "settings.developer.bugReport.openedTrimmed" : "settings.developer.bugReport.opened");
        }

        private static void SendToServer(
            PanelTextField summary, PanelTextField describe, PanelTextField repro, PanelTextField expected,
            PanelTextField context)
        {
            if (!ValidateRequired(describe, repro, expected))
            {
                Notify("settings.developer.bugReport.missingFields");
                return;
            }

            if (BasisNetworkConnection.LocalPlayerPeer == null)
            {
                Notify("settings.developer.bugReport.notConnected");
                return;
            }

            if (!BasisNetworkModeration.CrashReportingEnabled)
            {
                Notify("settings.developer.bugReport.serverDisabled");
                return;
            }

            string summaryText = Read(summary);
            string headline = string.IsNullOrWhiteSpace(summaryText) ? Read(describe) : summaryText;
            string report = BuildMarkdown(summary, describe, repro, expected, context);

            BasisErrorReportSender.Report(ServerReportSeverity, ServerReportSystem, headline, report);
            Notify("settings.developer.bugReport.sentServer");
        }

        private static string BuildPrefillUrl(
            string summary, string describe, string repro, string expected,
            string build, string logs, string context, out bool trimmed)
        {
            trimmed = false;
            string url = Assemble(summary, describe, repro, expected, build, logs, context);
            if (url.Length <= MaxUrlLength) return url;

            trimmed = true;
            string notice = BasisLocalization.Get("settings.developer.bugReport.trimmedNotice");
            url = Assemble(summary, describe, repro, expected, build, notice, context);
            if (url.Length <= MaxUrlLength) return url;

            describe = Clamp(describe, 1200, notice);
            repro = Clamp(repro, 800, notice);
            expected = Clamp(expected, 600, notice);
            return Assemble(summary, describe, repro, expected, build, notice, string.Empty);
        }

        // Query keys (Describe, reproduction, ...) are the field ids declared in
        // 1-bug.yml; GitHub maps each parameter onto the form field of that id.
        private static string Assemble(
            string summary, string describe, string repro, string expected,
            string build, string logs, string context)
        {
            StringBuilder sb = new StringBuilder(NewIssueUrl.Length + 1024);
            sb.Append(NewIssueUrl).Append("?template=").Append(IssueTemplate);
            AppendField(sb, "title", summary);
            AppendField(sb, "Describe", describe);
            AppendField(sb, "reproduction", repro);
            AppendField(sb, "expected-behavior", expected);
            AppendField(sb, "buildinfo", build);
            AppendField(sb, "log-files", logs);
            AppendField(sb, "additional-context", context);
            return sb.ToString();
        }

        private static void AppendField(StringBuilder sb, string id, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            sb.Append('&').Append(id).Append('=').Append(Uri.EscapeDataString(value));
        }

        private static string Clamp(string value, int max, string notice)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max) + "\n" + notice;
        }

        private static string Read(PanelTextField field)
        {
            if (field == null || field._inputField == null) return string.Empty;
            return field._inputField.text ?? string.Empty;
        }

        private static string GatherBuildInfo()
        {
            return SettingsProvider.BuildInfoString()
                + $"OS: {SystemInfo.operatingSystem}\n"
                + $"CPU: {SystemInfo.processorType} ({SystemInfo.processorCount} threads)\n"
                + $"GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})\n"
                + $"RAM: {SystemInfo.systemMemorySize} MB\n";
        }

        private static string GatherRecentLogs()
        {
            string all = BasisLogManager.GetAllLogsPlainText();
            if (string.IsNullOrEmpty(all)) return BasisLocalization.Get("settings.developer.bugReport.noLogs");
            if (all.Length <= MaxAttachedLogChars) return all;

            string tail = all.Substring(all.Length - MaxAttachedLogChars);
            int firstBreak = tail.IndexOf('\n');
            if (firstBreak >= 0 && firstBreak < tail.Length - 1) tail = tail.Substring(firstBreak + 1);
            return "…(truncated)\n" + tail;
        }

        private static string BuildMarkdown(
            PanelTextField summary, PanelTextField describe, PanelTextField repro, PanelTextField expected,
            PanelTextField context)
        {
            StringBuilder sb = new StringBuilder(2048);
            string summaryText = Read(summary);
            if (!string.IsNullOrWhiteSpace(summaryText)) sb.Append("# ").Append(summaryText).Append("\n\n");
            AppendSection(sb, "Describe the bug?", Read(describe));
            AppendSection(sb, "Steps to Reproduce", Read(repro));
            AppendSection(sb, "The expected behavior", Read(expected));
            AppendSection(sb, "Build Info", GatherBuildInfo());
            AppendSection(sb, "Log Files", GatherRecentLogs());
            AppendSection(sb, "Additional Context", Read(context));
            return sb.ToString();
        }

        private static void AppendSection(StringBuilder sb, string header, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            sb.Append("### ").Append(header).Append('\n').Append(body).Append("\n\n");
        }

        private static void Notify(string messageKey)
        {
            string message = BasisLocalization.Get(messageKey);
            if (BasisMainMenu.Instance != null)
            {
                BasisMainMenu.Instance.OpenDialogue(
                    BasisLocalization.Get("settings.developer.bugReport.title"),
                    message,
                    BasisLocalization.Get("ui.ok"),
                    _ => { },
                    true);
            }
            else
            {
                BasisDebug.Log(message);
            }
        }
    }
}
