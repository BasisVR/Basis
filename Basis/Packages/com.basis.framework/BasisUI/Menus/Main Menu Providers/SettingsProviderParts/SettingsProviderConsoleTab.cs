using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderConsoleTab
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
                LastSize = -1;
            }
        }

        public static PanelTabPage ConsoleTab(PanelTabGroup tabGroup)
        {
            BasisLogManager.LoadLogsFromDisk();

            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetTitle("Console");
            descriptor.SetDescription("Runtime log viewer (filters, collapse, crash reports).");

            RectTransform container = descriptor.ContentParent;

            // -----------------------
            // Controls group
            // -----------------------
            PanelElementDescriptor controlsGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            controlsGroup.SetTitle("Log Settings");

            PanelToggle collapseToggle = PanelToggle.CreateNewEntry(controlsGroup.ContentParent);
            collapseToggle.Descriptor.SetTitle("Collapse");
            collapseToggle.SetValueWithoutNotify(_showCollapsedLogs);
            collapseToggle.OnValueChanged += v =>
            {
                _showCollapsedLogs = v;
                RequestUpdate = true;
            };

            PanelToggle updatingToggle = PanelToggle.CreateNewEntry(controlsGroup.ContentParent);
            updatingToggle.Descriptor.SetTitle("Live Updates");
            updatingToggle.SetValueWithoutNotify(_isUpdating);
            updatingToggle.OnValueChanged += v =>
            {
                _isUpdating = v;
                RequestUpdate = true;
            };

            PanelDropdown filterDropdown = PanelDropdown.CreateNewEntry(controlsGroup.ContentParent);
            filterDropdown.Descriptor.SetTitle("Filter");
            filterDropdown.AssignEntries(new List<string> { "All", "Errors", "Warnings", "Logs" });
            filterDropdown.DropdownComponent.SetValueWithoutNotify(GetFilterIndex());
            filterDropdown.DropdownComponent.onValueChanged.AddListener(OnFilterChanged);

            PanelButton clearBtn = PanelButton.CreateNew(controlsGroup.ContentParent);
            clearBtn.Descriptor.SetTitle("Clear Logs");
            clearBtn.OnClicked += () =>
            {
                BasisLogManager.ClearLogs();
                RebuildOutput();
                RequestUpdate = true;
            };

            PanelButton crashBtn = PanelButton.CreateNew(controlsGroup.ContentParent);
            crashBtn.Descriptor.SetTitle("Open Latest Crash Report");
            crashBtn.OnClicked += OpenLatestCrashReportFolder;

            // -----------------------
            // Output group + ScrollViewVertical
            // -----------------------
            PanelElementDescriptor outputGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            outputGroup.SetTitle("Output");

            EnsureSingleText(outputGroup.ContentParent);

            // Attach updater
            tab.gameObject.AddComponent<ConsoleTabUpdater>();

            // Initial build
            RebuildOutput();

            descriptor.ForceRebuild();
            return tab;
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

        public static float LastSize = 0;
        private static void OpenLatestCrashReportFolder()
        {
            try
            {
                var crashDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Temp", "Unity", "Crashes");

                if (!Directory.Exists(crashDir))
                {
                    BasisLogManager.HandleLog("Crash directory does not exist.", "", LogType.Error);
                    return;
                }

                var latest = new DirectoryInfo(crashDir).GetDirectories()
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest == null)
                {
                    BasisLogManager.HandleLog("No crash folders found.", "", LogType.Error);
                    return;
                }

                var target = Path.Combine(latest.FullName, "error.log");
                if (!File.Exists(target)) target = latest.FullName;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{target}\"",
                    UseShellExecute = true
                });
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
                Process.Start("open", $"-R \"{target}\"");
#else
                Application.OpenURL(latest.FullName);
#endif
            }
            catch (Exception ex)
            {
                BasisLogManager.HandleLog($"Failed to open crash folder: {ex.Message}", ex.StackTrace, LogType.Error);
            }
        }
    }
}
