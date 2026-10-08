using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Basis.BasisUI.Styling;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class SettingsProviderConsoleTab
    {
        private static bool _showCollapsedLogs = true;
        private static bool _showAllLogsInOrder = true;

        public static bool RequestUpdate = false;
        private static LogType _currentLogTypeFilter = LogType.Log;
        private static bool _isUpdating = true;

        // Output UI
        private static TMP_Text _outputText;
        private static readonly StringBuilder _sb = new StringBuilder(32_768); // reused buffer

        private class ConsoleTabUpdater : MonoBehaviour
        {
            private const float UpdateInterval = 0.4f;
            private float _timer;

            private void Update()
            {
                if (!_isUpdating)
                {
                    return;
                }

                _timer += Time.unscaledDeltaTime;

                if (_timer < UpdateInterval)
                {
                    return;
                }

                _timer -= UpdateInterval;

                if (BasisLogManager.LogChanged || RequestUpdate)
                {
                    RebuildOutput();
                    RequestUpdate = false;
                }
            }

            private void OnEnable()
            {
                _timer = 0f;
            }
        }

        public static void BuildConsoleUI(RectTransform container)
        {
            _outputText = null;

            // -----------------------
            // Controls group
            // -----------------------
            PanelElementDescriptor controlsGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            controlsGroup.SetTitle(BasisLocalization.Get("settings.developer.console"));

            PanelToggle collapseToggle = PanelToggle.CreateNewEntry(controlsGroup.ContentParent);
            collapseToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.console.collapse"));
            collapseToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.console.collapse.tooltip"));
            collapseToggle.SetValueWithoutNotify(_showCollapsedLogs);
            collapseToggle.OnValueChanged += v =>
            {
                _showCollapsedLogs = v;
                RequestUpdate = true;
            };

            PanelToggle updatingToggle = PanelToggle.CreateNewEntry(controlsGroup.ContentParent);
            updatingToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.console.liveUpdates"));
            updatingToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.console.liveUpdates.tooltip"));
            updatingToggle.SetValueWithoutNotify(_isUpdating);
            updatingToggle.OnValueChanged += v =>
            {
                _isUpdating = v;
                RequestUpdate = true;
            };

            PanelDropdown filterDropdown = PanelDropdown.CreateNewEntry(controlsGroup.ContentParent);
            filterDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.console.filter"));
            filterDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.console.filter.tooltip"));
            filterDropdown.AssignLocalizedEntries(
                new List<string> { "All", "Errors", "Warnings", "Logs" },
                new List<string> { "settings.console.filter.all", "settings.console.filter.errors", "settings.console.filter.warnings", "settings.console.filter.logs" });
            filterDropdown.DropdownComponent.SetValueWithoutNotify(GetFilterIndex());
            filterDropdown.DropdownComponent.onValueChanged.AddListener(OnFilterChanged);

            PanelButton clearBtn = PanelButton.CreateNew(controlsGroup.ContentParent);
            clearBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.console.clearLogs"));
            clearBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.console.clearLogs.tooltip"));
            clearBtn.OnClicked += () =>
            {
                BasisLogManager.ClearLogs();
                RebuildOutput();
                RequestUpdate = true;
            };

            PanelButton copyBtn = PanelButton.CreateNew(controlsGroup.ContentParent);
            copyBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.console.copyLogs"));
            copyBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.console.copyLogs.tooltip"));
            copyBtn.OnClicked += () => BasisClipboard.Copy(BasisLogManager.GetAllLogsPlainText(), copyBtn);

            PanelButton crashBtn = PanelButton.CreateNew(controlsGroup.ContentParent);
            crashBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.console.openCrashReport"));
            crashBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.console.openCrashReport.tooltip"));
            crashBtn.OnClicked += OpenLatestCrashReportFolder;

            PanelButton logsFolderBtn = PanelButton.CreateNew(controlsGroup.ContentParent);
            logsFolderBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.console.openLogsFolder"));
            logsFolderBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.console.openLogsFolder.tooltip"));
            logsFolderBtn.OnClicked += OpenLogsFolder;

            PanelButton crashFolderBtn = PanelButton.CreateNew(controlsGroup.ContentParent);
            crashFolderBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.console.openCrashFolder"));
            crashFolderBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.console.openCrashFolder.tooltip"));
            crashFolderBtn.OnClicked += OpenCrashReportsFolder;

            // -----------------------
            // Output group
            // -----------------------
            PanelElementDescriptor outputGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            outputGroup.SetTitle(BasisLocalization.Get("settings.console.output"));

            EnsureSingleText(outputGroup.ContentParent);

            outputGroup.IsolateAsCanvas();

            // Attach updater for live log refresh
            if (controlsGroup.GetComponent<ConsoleTabUpdater>() == null)
                controlsGroup.gameObject.AddComponent<ConsoleTabUpdater>();

            // Initial build
            RebuildOutput();
        }
        private static void EnsureSingleText(RectTransform parent)
        {
            if (_outputText != null) return;

            var go = new GameObject("ConsoleOutputText", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = Vector2.zero;

            var text = go.AddComponent<TextMeshProUGUI>();
            text.text = string.Empty;
            text.fontSize = 18f;
            text.raycastTarget = true;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.richText = true;
            text.rectTransform.sizeDelta = new Vector2(960, 6500);
            _outputText = text;

            // ensure this object is using the ui style standard
            var label = go.AddComponent<UiStyleLabel>();
            label.SetStyle("Element Subtitle");
        }

        private static void RebuildOutput()
        {
            _sb.Clear();
            List<string> lines = GetCurrentLogLines();
            int count = lines.Count;
            if (count == 0)
            {
                _outputText.SetText(string.Empty);
                BasisLogManager.LogChanged = false;
                return;
            }

            _sb.AppendLine(lines[0]);
            for (int Index = 1; Index < count; Index++)
            {
                _sb.AppendLine(lines[Index]);
            }

            _outputText.SetText(_sb);
            BasisLogManager.LogChanged = false;
        }

        private static int GetFilterIndex()
        {
            if (_showAllLogsInOrder) return 0;

            return _currentLogTypeFilter switch
            {
                LogType.Error => 1,
                LogType.Warning => 2,
                _ => 3,
            };
        }

        private static void OnFilterChanged(int value)
        {
            _showAllLogsInOrder = (value == 0);

            if (!_showAllLogsInOrder)
            {
                _currentLogTypeFilter = value switch
                {
                    1 => LogType.Error,
                    2 => LogType.Warning,
                    _ => LogType.Log
                };
            }
            RequestUpdate = true;
        }

        private static List<string> GetCurrentLogLines()
        {
            if (_showCollapsedLogs)
            {
                if (_showAllLogsInOrder)
                {
                    return BasisLogManager.GetCombinedCollapsedLogs();
                }

                return BasisLogManager.GetCollapsedLogs(_currentLogTypeFilter);
            }
            else
            {
                if (_showAllLogsInOrder)
                {
                    return BasisLogManager.GetAllLogs();
                }

                return BasisLogManager.GetLogs(_currentLogTypeFilter);
            }
        }

        private static string GetCrashDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", Application.companyName, Application.productName, "Crashes");
        }

        private static void OpenLogsFolder()
        {
            try
            {
                OpenInFileBrowser(Application.persistentDataPath, false);
            }
            catch (Exception ex)
            {
                BasisLogManager.HandleLog($"Failed to open logs folder: {ex.Message}", ex.StackTrace, LogType.Error);
            }
        }

        private static void OpenCrashReportsFolder()
        {
            try
            {
                var crashDir = GetCrashDir();
                Directory.CreateDirectory(crashDir);
                OpenInFileBrowser(crashDir, false);
            }
            catch (Exception ex)
            {
                BasisLogManager.HandleLog($"Failed to open crash folder: {ex.Message}", ex.StackTrace, LogType.Error);
            }
        }

        private static void OpenLatestCrashReportFolder()
        {
            try
            {
                var crashDir = GetCrashDir();

                if (!Directory.Exists(crashDir))
                {
                    BasisLogManager.HandleLog("Crash directory does not exist.", "", LogType.Error);
                    return;
                }

                var dirs = new DirectoryInfo(crashDir).GetDirectories();
                var latest = dirs
                    .Where(d => d.EnumerateFileSystemInfos().Any())
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .FirstOrDefault()
                    ?? dirs.OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault();

                if (latest == null)
                {
                    BasisLogManager.HandleLog("No crash folders found.", "", LogType.Error);
                    return;
                }

                var target = Path.Combine(latest.FullName, "error.log");
                bool select = true;
                if (!File.Exists(target))
                {
                    var dump = latest.GetFiles("*.dmp").FirstOrDefault();
                    if (dump != null)
                    {
                        target = dump.FullName;
                    }
                    else
                    {
                        target = latest.FullName;
                        select = false;
                    }
                }

                OpenInFileBrowser(target, select);
            }
            catch (Exception ex)
            {
                BasisLogManager.HandleLog($"Failed to open crash folder: {ex.Message}", ex.StackTrace, LogType.Error);
            }
        }

        private static void OpenInFileBrowser(string path, bool selectFile)
            => BasisFileBrowserUtility.Reveal(path, selectFile);
    }
}
