using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
public static class BasisLogManager
{
    private static readonly ConcurrentQueue<(string logString, string stackTrace, LogType type)> logQueue = new ConcurrentQueue<(string, string, LogType)>();
    private static readonly List<string> logEntries = new List<string>();
    private static readonly List<string> errorEntries = new List<string>();
    private static readonly List<string> warningEntries = new List<string>();
    private static readonly List<string> normalEntries = new List<string>();
    private static readonly object logLock = new object();
    public static int PollingInMs = 40;
    public static bool LogChanged { get; set; }

    static BasisLogManager()
    {
        Thread logProcessingThread = new Thread(LogProcessingLoop);
        logProcessingThread.IsBackground = true;
        logProcessingThread.Start();
    }

    public static List<string> GetCollapsedLogs(LogType type)
    {
        lock (logLock)
        {
            List<string> logs = type switch
            {
                LogType.Error or LogType.Exception => new List<string>(errorEntries),
                LogType.Warning => new List<string>(warningEntries),
                _ => new List<string>(normalEntries)
            };

            // Extract and preserve the color codes
            var colorMap = new Dictionary<string, string>
            {
                { "#FF0000", "<color=#FF0000>" },
                { "#FFA500", "<color=#FFA500>" },
                { "#FFFFFF", "<color=#FFFFFF>" }
            };

            // Helper to extract color from a log entry
            string ExtractColor(string log)
            {
                foreach (var entry in colorMap)
                {
                    if (log.Contains(entry.Value))
                        return entry.Key;
                }
                return "#FFFFFF"; // Default color
            }

            var groupedLogs = logs
                .GroupBy(log => log)
                .Select(group =>
                {
                    string color = ExtractColor(group.Key);
                    return $"{group.Count()}x {colorMap[color]}{group.Key}</color>";
                })
                .ToList();

            return groupedLogs;
        }
    }
    public static List<string> GetCombinedCollapsedLogs()
    {
        lock (logLock)
        {
            // Combine all log entries
            List<string> allLogs = new List<string>(logEntries);

            // Extract and preserve the color codes
            var colorMap = new Dictionary<string, string>
        {
            { "#FF0000", "<color=#FF0000>" },
            { "#FFA500", "<color=#FFA500>" },
            { "#FFFFFF", "<color=#FFFFFF>" }
        };

            // Helper to extract color from a log entry
            string ExtractColor(string log)
            {
                foreach (var entry in colorMap)
                {
                    if (log.Contains(entry.Value))
                        return entry.Key;
                }
                return "#FFFFFF"; // Default color
            }

            var groupedLogs = allLogs
                .GroupBy(log => log)
                .Select(group =>
                {
                    string color = ExtractColor(group.Key);
                    return $"{group.Count()}x {colorMap[color]}{group.Key}</color>";
                })
                .ToList();

            return groupedLogs;
        }
    }

    public static void HandleLog(string logString, string stackTrace, LogType type)
    {
        logQueue.Enqueue((logString, stackTrace, type));
    }

    private static void LogProcessingLoop()
    {
        while (true)
        {
            if (logQueue.TryDequeue(out var logEntry))
            {
               // logEntry.stackTrace
                AddLog(logEntry.logString,logEntry.stackTrace, logEntry.type);
                LogChanged = true;
            }
            Thread.Sleep(PollingInMs); // Prevents CPU overutilization
        }
    }

    private static void AddLog(string logString, string stackTrace, LogType type)
    {
        string coloredLog = ColorizeLog(logString, type);

        lock (logLock)
        {
            AddLogEntry(logEntries, coloredLog);

            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                    AddLogEntry(errorEntries, coloredLog);
                    string stackTraceLog = ColorizeLog(stackTrace, type);
                    AddLogEntry(errorEntries, stackTraceLog);
                    break;
                case LogType.Warning:
                    AddLogEntry(warningEntries, coloredLog);
                    break;
                case LogType.Log:
                    AddLogEntry(normalEntries, coloredLog);
                    break;
            }
        }
    }

    private static string ColorizeLog(string log, LogType type)
    {
        string color = type switch
        {
            LogType.Error or LogType.Exception => "#FF0000",
            LogType.Warning => "#FFA500",
            _ => "#FFFFFF"
        };

        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        return $"<color={color}>[{timestamp}] {log}</color>";
    }

    private static void AddLogEntry(List<string> logList, string log)
    {
        logList.Add(log);
        if (logList.Count > MaximumLogs) // Hardcoded max log entries
            logList.RemoveAt(0);
    }
    public const int MaximumLogs = 500;
    public static List<string> GetLogs(LogType type)
    {
        lock (logLock)
        {
            return type switch
            {
                LogType.Error or LogType.Exception => new List<string>(errorEntries),
                LogType.Warning => new List<string>(warningEntries),
                _ => new List<string>(normalEntries)
            };
        }
    }

    public static List<string> GetAllLogs()
    {
        lock (logLock)
        {
            return new List<string>(logEntries);
        }
    }

    public static void ClearLogs()
    {
        lock (logLock)
        {
            logEntries.Clear();
            errorEntries.Clear();
            warningEntries.Clear();
            normalEntries.Clear();
        }
        LogChanged = true;
    }

    public static void LoadLogsFromDisk()
    {
        string logFilePath = Path.Combine(Application.persistentDataPath, "log.txt");

        if (File.Exists(logFilePath))
        {
            string[] lines = File.ReadAllLines(logFilePath);
            foreach (string line in lines)
            {
                AddLog(line,string.Empty, LogType.Log);
            }
        }
    }
}
