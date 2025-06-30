using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
public class BasisConsoleLogger : BasisUIBase
{
    public TextMeshProUGUI logText;
    public static bool showAllLogsInOrder = true;
    public static bool showCollapsedLogs = false;
    public static LogType currentLogTypeFilter = LogType.Log;
    public TMP_Dropdown Dropdown;
    public Button ClearButton;
    public Button CollapseButton;
    public Button StopButton;
    public Button FindCrashButton;
    public RectTransform Transform;
    public static bool IsUpdating = true;
    public TextMeshProUGUI CollapseButtonText;
    public TextMeshProUGUI StopButtonText;
    public BasisButtonHeldCallBack BasisButtonHeldCallBack;
    public Button MouseLock;
    public static BasisConsoleLogger Instance;
    public static bool IsActive = false;

    public TextMeshProUGUI VersionAndInfo;
    private void Awake()
    {
        if (BasisHelpers.CheckInstance(Instance))
        {
            Instance = this;
        }
        BasisLogManager.LoadLogsFromDisk();
        Dropdown.onValueChanged.AddListener(HandlePressed);
        ClearButton.onClick.AddListener(ClearLogs);
        CollapseButton.onClick.AddListener(Collapse);
        StopButton.onClick.AddListener(StopStartLoggingToUI);
        BasisButtonHeldCallBack.OnButtonReleased += OnButtonReleased;
        BasisButtonHeldCallBack.OnButtonPressed += OnButtonPressed;
        MouseLock.onClick.AddListener(ToggleMouse);
        FindCrashButton.onClick.AddListener(OpenLatestCrashReportFolder);
        IsActive = true;

        VersionAndInfo.text =
            $"Version: {Application.version} | " +
            $"Unity Version: {Application.unityVersion} | " +
            $"Platform: {Application.platform} | " +
            $"Mode: {BasisDeviceManagement.CurrentMode} | " +
            $"Build GUID: {Application.buildGUID} | " +
            $"Log Path: {Application.consoleLogPath} | " +
            $"Data Path: {Application.dataPath}";
    }
    public Canvas Canvas;
    private void OpenLatestCrashReportFolder()
    {
        string crashDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "Unity", "Crashes"
        );

        if (!Directory.Exists(crashDirectory))
        {
            BasisLogManager.HandleLog("Crash directory does not exist.","", LogType.Error);
            return;
        }

        var latestFolder = new DirectoryInfo(crashDirectory).GetDirectories()
            .OrderByDescending(d => d.CreationTime)
            .FirstOrDefault();

        if (latestFolder != null)
        {
            try
            {
                // This opens File Explorer with the folder selected
                Process.Start(new ProcessStartInfo()
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{Path.Combine(latestFolder.FullName, "error.log")}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                BasisLogManager.HandleLog($"Failed to open crash folder: {ex.Message}", ex.StackTrace, LogType.Error);
            }
        }
        else
        {
            BasisLogManager.HandleLog("No crash folders found.", "", LogType.Error);
        }
    }
    public void ToggleMouse()
    {
        BasisCursorManagement.LockCursor(nameof(BasisConsoleLogger));
    }
    public void OnButtonReleased()
    {

    }
    public void OnButtonPressed()
    {

    }
    public void StopStartLoggingToUI()
    {
        IsUpdating = !IsUpdating;
        if (IsUpdating)
        {
            StopButtonText.text = "Stop";
        }
        else
        {
            StopButtonText.text = "Start";
        }
    }
    public void Collapse()
    {
        showCollapsedLogs = !showCollapsedLogs;
        if (showCollapsedLogs)
        {
            CollapseButtonText.text = "Uncollapse";
        }
        else
        {
            CollapseButtonText.text = "Collapse";
        }
        UpdateLogDisplay();
    }

    public void HandlePressed(int Value)
    {
        showAllLogsInOrder = false;
        switch (Value)
        {
            case 0:
                showAllLogsInOrder = true;
                break;
            case 1:
                currentLogTypeFilter = LogType.Error;
                break;
            case 2:
                currentLogTypeFilter = LogType.Warning;
                break;
            case 3:
                currentLogTypeFilter = LogType.Log;
                break;
        }
        UpdateLogDisplay();
    }
    public static void QueryLogDisplay()
    {
        if (IsActive && IsUpdating && BasisLogManager.LogChanged)
        {
            Instance.UpdateLogDisplay();
        }
    }

    private void UpdateLogDisplay()
    {
        StringBuilder currentLogDisplay = new StringBuilder();

        if (showCollapsedLogs)
        {
            if (showAllLogsInOrder)
            {
                currentLogDisplay.Append(string.Join("\n", BasisLogManager.GetCombinedCollapsedLogs()));
            }
            else
            {
                currentLogDisplay.Append(string.Join("\n", BasisLogManager.GetCollapsedLogs(currentLogTypeFilter)));
            }
        }
        else if (showAllLogsInOrder)
        {
            currentLogDisplay.Append(string.Join("\n", BasisLogManager.GetAllLogs()));
        }
        else
        {
            currentLogDisplay.Append(string.Join("\n", BasisLogManager.GetLogs(currentLogTypeFilter)));
        }

        logText.text = currentLogDisplay.ToString();
        BasisLogManager.LogChanged = false;
    }

    public void ClearLogs()
    {
        BasisLogManager.ClearLogs();
        logText.text = string.Empty;
    }

    public override void DestroyEvent()
    {
        BasisCursorManagement.LockCursor(nameof(BasisConsoleLogger));
    }

    public override void InitalizeEvent()
    {
        BasisCursorManagement.UnlockCursor(nameof(BasisConsoleLogger));
    }
    public void OnDestroy()
    {
        IsActive = false;
    }
}
