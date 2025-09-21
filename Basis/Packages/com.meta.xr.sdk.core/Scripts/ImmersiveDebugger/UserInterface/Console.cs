/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */


using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using Meta.XR.ImmersiveDebugger.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for the console panel of Immersive Debugger.
    /// Act as the container for all the UI elements within the console and consumes logs data from <see cref="ConsoleLogsCache"/>.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    [DefaultExecutionOrder(1)] // After UI Elements
    public class Console : DebugPanel
    {
        private const int NumberOfLines = 14;
        private const int FullLogPanelBottomMargin = 40;
        private const int ContractedLogPanelBottomMargin = 140;

        internal bool Dirty { get; set; }

        private ScrollView _scrollView;
        private ScrollView _scrollViewLogDetails;

        private ProxyFlex<ConsoleLine, ProxyConsoleLine> _proxyFlex;

        private Flex _flex;
        private Flex _buttonsAnchor;

        private List<SeverityEntry> _severities = new();
        private Dictionary<LogType, SeverityEntry> _severitiesPerType = new();

        private SeverityEntry GetSeverity(LogType logType)
        {
            return _severitiesPerType.TryGetValue(logType, out var severity) ? severity : null;
        }

        private readonly List<LogEntry> _entries = new();
        private readonly List<LogEntry> _allEntries = new();
        private readonly Dictionary<int, LogEntry> _entryMap = new();
        private Label _logDetailLabel;
        private Toggle _collapseBtn;
        private Texture2D _collapseActiveIcon;
        private Texture2D _collapseInactiveIcon;
        private ButtonWithIcon _logDetailPaneCloseBtn;

        private Vector3 _currentPosition;
        private Vector3 _targetPosition;
        private readonly float _lerpSpeed = 10f;
        private bool _lerpCompleted = true;
        private Background _logDetailPaneBackground;
        private ImageStyle _logDetailPaneBackgroundImageStyle;

        internal bool LogCollapseMode { get; private set; }
        internal int MaximumNumberOfLogEntries { get; private set; }

        public ImageStyle LogDetailBackgroundStyle
        {
            set
            {
                _logDetailPaneBackground.Sprite = value.sprite;
                _logDetailPaneBackground.Color = value.color;
                _logDetailPaneBackground.PixelDensityMultiplier = value.pixelDensityMultiplier;
            }
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            _flex = Append<Flex>("main");
            _flex.LayoutStyle = Style.Load<LayoutStyle>("ConsoleFlex");

            // List of Panel Buttons
            _buttonsAnchor = _flex.Append<Flex>("buttons");
            _buttonsAnchor.LayoutStyle = Style.Load<LayoutStyle>("ConsoleButtons");

            // Log collapse
            LogCollapseMode = RuntimeSettings.Instance.CollapsedIdenticalLogEntries;

            _collapseActiveIcon = Resources.Load<Texture2D>("Textures/compress_icon");
            _collapseInactiveIcon = Resources.Load<Texture2D>("Textures/expand_icon");

            _collapseBtn = RegisterControl("LogCollapse", LogCollapseMode ? _collapseInactiveIcon : _collapseActiveIcon, Style.Load<ImageStyle>("LogCollapseIcon"), ToggleCollapseMode);
            _collapseBtn.State = LogCollapseMode;

            // Bin button
            RegisterControl("Clear", Resources.Load<Texture2D>("Textures/bin_icon"), Style.Load<ImageStyle>("BinIcon"), Clear);

            // Severity buttons
            var errorSeverity = new SeverityEntry(this, "Error", Resources.Load<Texture2D>("Textures/error_icon"), Style.Load<ImageStyle>("ErrorIcon"), Style.Load<ImageStyle>("PillError"));
            var warningSeverity = new SeverityEntry(this, "Warning", Resources.Load<Texture2D>("Textures/warning_icon"), Style.Load<ImageStyle>("WarningIcon"), Style.Load<ImageStyle>("PillWarning"));
            var infoSeverity = new SeverityEntry(this, "Log", Resources.Load<Texture2D>("Textures/notice_icon"), Style.Load<ImageStyle>("NoticeIcon"), Style.Load<ImageStyle>("PillInfo"));
            _severities.Add(infoSeverity);
            _severities.Add(warningSeverity);
            _severities.Add(errorSeverity);
            _severitiesPerType.Add(LogType.Assert, errorSeverity);
            _severitiesPerType.Add(LogType.Error, errorSeverity);
            _severitiesPerType.Add(LogType.Exception, errorSeverity);
            _severitiesPerType.Add(LogType.Warning, warningSeverity);
            _severitiesPerType.Add(LogType.Log, infoSeverity);

            var runtimeSettings = RuntimeSettings.Instance;
            errorSeverity.ShouldShow = runtimeSettings.ShowErrorLog;
            warningSeverity.ShouldShow = runtimeSettings.ShowWarningLog;
            infoSeverity.ShouldShow = runtimeSettings.ShowInfoLog;

            // List for Log
            _scrollView = Append<ScrollView>("logs");
            _scrollView.LayoutStyle = Style.Instantiate<LayoutStyle>("LogsScrollView");
            _scrollView.Flex.LayoutStyle = Style.Load<LayoutStyle>("ConsoleLogs");

            MaximumNumberOfLogEntries = runtimeSettings.MaximumNumberOfLogEntries;
            _proxyFlex = new ProxyFlex<ConsoleLine, ProxyConsoleLine>(NumberOfLines, MaximumNumberOfLogEntries, Style.Load<LayoutStyle>("ConsoleLine"), _scrollView);

            // Log detail panel
            _logDetailPaneBackground = Append<Background>("background");
            _logDetailPaneBackground.LayoutStyle = Style.Load<LayoutStyle>("LogDetailsPaneBackground");
            _logDetailPaneBackgroundImageStyle = Style.Load<ImageStyle>("LogDetailPaneBackground");
            LogDetailBackgroundStyle = _logDetailPaneBackgroundImageStyle;

            _scrollViewLogDetails = Append<ScrollView>("details");
            _scrollViewLogDetails.LayoutStyle = Style.Load<LayoutStyle>("LogDetailsScrollView");
            _scrollViewLogDetails.Flex.LayoutStyle = Style.Load<LayoutStyle>("ConsoleLogDetails");

            _logDetailLabel = _scrollViewLogDetails.Flex.Append<Label>("entry");
            _logDetailLabel.LayoutStyle = Style.Instantiate<LayoutStyle>("ConsoleLineLogDetailsLabel");
            _logDetailLabel.TextStyle = Style.Load<TextStyle>("ConsoleLogDetailsLabel");
            _logDetailLabel.Text.horizontalOverflow = HorizontalWrapMode.Wrap;

            // Log detail panel close button
            _logDetailPaneCloseBtn = Append<ButtonWithIcon>("close");
            _logDetailPaneCloseBtn.LayoutStyle = Style.Load<LayoutStyle>("LogDetailPaneCloseButton");
            _logDetailPaneCloseBtn.Icon = Resources.Load<Texture2D>("Textures/close_icon");
            _logDetailPaneCloseBtn.IconStyle = Style.Load<ImageStyle>("LogDetailPaneCloseButton");
            _logDetailPaneCloseBtn.Callback = HideLogDetailsPanel;

            HideLogDetailsPanel();

            LogCollapseMode = RuntimeSettings.Instance.CollapsedIdenticalLogEntries;
            LogEntry.OnDisplayDetails = OnConsoleLineClicked;
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            ConsoleLogsCache.OnLogReceived -= EnqueueLogEntry; // avoid duplicated registration if domain reload disabled
            ConsoleLogsCache.OnLogReceived += EnqueueLogEntry;
            ConsoleLogsCache.ConsumeStartupLogs(EnqueueLogEntry);
        }

        protected override void OnDisable()
        {
            ConsoleLogsCache.OnLogReceived -= EnqueueLogEntry;

            base.OnDisable();
        }

        protected override void OnTransparencyChanged()
        {
            base.OnTransparencyChanged();
            _logDetailPaneBackground.Color = Transparent ? _logDetailPaneBackgroundImageStyle.colorOff : _logDetailPaneBackgroundImageStyle.color;
        }

        internal Label RegisterCount()
        {
            var label = _buttonsAnchor.Append<Label>("");
            label.LayoutStyle = Style.Load<LayoutStyle>("ConsoleButtonCount");
            label.TextStyle = Style.Load<TextStyle>("ConsoleButtonCount");
            return label;
        }

        internal Toggle RegisterControl(string buttonName, Texture2D icon, ImageStyle style, Action callback)
        {
            if (buttonName == null) throw new ArgumentNullException(nameof(buttonName));
            if (icon == null) throw new ArgumentNullException(nameof(icon));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var toggle = _buttonsAnchor.Append<Toggle>(buttonName);
            toggle.LayoutStyle = Style.Load<LayoutStyle>("ConsoleButton");
            toggle.Icon = icon;
            toggle.IconStyle = style ? style : Style.Default<ImageStyle>();
            toggle.Callback = callback;
            return toggle;
        }

        private void ToggleCollapseMode()
        {
            LogCollapseMode = !LogCollapseMode;
            _collapseBtn.Icon = LogCollapseMode ? _collapseInactiveIcon : _collapseActiveIcon;

            if (LogCollapseMode)
            {
                MergeEntries();
            }
            else
            {
                FlattenEntries();
            }
        }

        private void EnqueueLogEntry(string logString, string stackTrace, LogType type)
        {
            var severity = GetSeverity(type);
            if (severity == null)
            {
                return;
            }

            var hash = ComputeLogHash(logString, stackTrace);
            if (_entryMap.TryGetValue(hash, out var entry) && LogCollapseMode)
            {
                _entries.Remove(entry);
                _proxyFlex.RemoveProxy(entry.Line);
                entry.Count++;
            }
            else
            {
                if (_entries.Count >= MaximumNumberOfLogEntries)
                {
                    RemoveLogEntry(_entries[0]);
                }

                entry = OVRObjectPool.Get<LogEntry>();
                entry.Setup(logString, stackTrace, severity);
                _entryMap[hash] = entry;
            }

            _entries.Add(entry);

            // Need to duplicate otherwise changing one instance will affect others.
            var clonedEntry = OVRObjectPool.Get<LogEntry>();
            clonedEntry.Setup(logString, stackTrace, severity);
            _allEntries.Add(clonedEntry);

            severity.Count++;

            AppendToProxyFlex(entry);
        }

        private void RemoveLogEntry(LogEntry logEntry)
        {
            logEntry.Severity.Count -= logEntry.Count;

            _entries.Remove(logEntry);
            _allEntries.RemoveAll(entry =>
            {
                var canRemove = entry == logEntry;
                if (canRemove)
                {
                    OVRObjectPool.Return(entry);
                }

                return canRemove;
            });

            OVRObjectPool.Return(logEntry);
        }

        private void Update()
        {
            if (Dirty)
            {
                RefreshAllEntries();
                Dirty = false;
            }

            _proxyFlex.Update();

            // Animation
            if (_lerpCompleted) return;
            _currentPosition = Utils.LerpPosition(_currentPosition, _targetPosition, _lerpSpeed);
            _lerpCompleted = _currentPosition == _targetPosition;
            SphericalCoordinates = _currentPosition;
        }

        private void Clear()
        {
            _entries.Clear();

            foreach (var entry in _allEntries)
            {
                OVRObjectPool.Return(entry);
            }
            _allEntries.Clear();

            _entryMap.Clear();
            _proxyFlex.Clear();
            foreach (var severity in _severities)
            {
                severity.Reset();
            }

            HideLogDetailsPanel();

            Dirty = true;
        }

        private void RefreshAllEntries()
        {
            foreach (var entry in _entries)
            {
                if (!entry.Severity.ShouldShow)
                {
                    if (entry.Shown)
                    {
                        _proxyFlex.RemoveProxy(entry.Line);
                        entry.Line = null;
                    }
                }
                else
                {
                    if (!entry.Shown)
                    {
                        var line = _proxyFlex.AppendProxy();
                        line.Entry = entry;
                        entry.Line = line;
                    }
                }
            }
        }

        private void MergeEntries()
        {
            _entries.Clear();
            _proxyFlex.Clear();
            ResetLogCount();

            foreach (var entry in _allEntries)
            {
                var hash = ComputeLogHash(entry.Label, entry.Callstack);
                if (_entryMap.TryGetValue(hash, out var mappedEntry))
                {
                    _entries.Remove(mappedEntry);
                    _proxyFlex.RemoveProxy(mappedEntry.Line);
                    mappedEntry.Count++;
                }

                _entries.Add(mappedEntry);
                AppendToProxyFlex(mappedEntry);
            }

            Dirty = true;
        }

        private void ResetLogCount()
        {
            foreach (var entry in _allEntries) entry.Count = 0;
        }

        private void FlattenEntries()
        {
            _entries.Clear();
            _proxyFlex.Clear();
            foreach (var entry in _allEntries)
            {
                _entries.Add(entry);
                AppendToProxyFlex(entry);
            }

            Dirty = true;
        }

        private void AppendToProxyFlex(LogEntry entry)
        {
            if (entry.Severity.ShouldShow)
            {
                var line = _proxyFlex.AppendProxy();
                line.Entry = entry;
                entry.Line = line;
            }
        }

        private void OnConsoleLineClicked(LogEntry entry)
        {
            ShowLogDetailsPanel();

            _logDetailLabel.Content = $"{entry.Label}\n{entry.Callstack}";
            _logDetailLabel.SetHeight(_logDetailLabel.Text.preferredHeight + 20);
            _logDetailLabel.RefreshLayout();

            _scrollViewLogDetails.Progress = 1.0f;
        }

        private void ShowLogDetailsPanel()
        {
            if (_scrollViewLogDetails.Visibility) return;

            _scrollViewLogDetails.Show();
            _logDetailPaneCloseBtn.Show();
            _logDetailPaneBackground.Show();
            _scrollView.LayoutStyle.bottomRightMargin.y = ContractedLogPanelBottomMargin;
            _scrollView.RefreshLayout();
        }

        private void HideLogDetailsPanel()
        {
            if (!_scrollViewLogDetails.Visibility) return;

            _scrollViewLogDetails.Hide();
            _logDetailPaneCloseBtn.Hide();
            _logDetailPaneBackground.Hide();
            _scrollView.LayoutStyle.bottomRightMargin.y = FullLogPanelBottomMargin;
            _scrollView.RefreshLayout();
        }

        private static int ComputeLogHash(string content, string stackTrace)
        {
            var hash = new HashCode();
            hash.Add(content.GetHashCode());
            hash.Add(stackTrace.GetHashCode());
            return hash.ToHashCode();
        }

        internal void SetPanelPosition(RuntimeSettings.DistanceOption distanceOption, bool skipAnimation = false)
        {
            var consolePanelPositions = ValueContainer<Vector3>.Load("ConsolePanelPositions");
            _targetPosition = distanceOption switch
            {
                RuntimeSettings.DistanceOption.Close => consolePanelPositions["Close"],
                RuntimeSettings.DistanceOption.Far => consolePanelPositions["Far"],
                _ => consolePanelPositions["Default"]
            };

            if (skipAnimation)
            {
                SphericalCoordinates = _targetPosition;
                _currentPosition = _targetPosition;
                return;
            }

            _lerpCompleted = false;
        }
    }
}
