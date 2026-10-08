using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
[AutoStaticsCleanup]
public static partial class BasisLogManager
{
    [NoAutoStaticsCleanup] private static BlockingCollection<(string logString, string stackTrace, LogType type)> logQueue;
    private static Thread logProcessingThread;
    private static readonly Queue<string> logEntries = new Queue<string>();
    private static readonly Queue<string> errorEntries = new Queue<string>();
    private static readonly Queue<string> warningEntries = new Queue<string>();
    private static readonly Queue<string> normalEntries = new Queue<string>();
    [NoAutoStaticsCleanup] private static readonly object logLock = new object();
    public static bool LogChanged { get; set; }

    public static void Start()
    {
        lock (logLock)
        {
            if (logProcessingThread != null) return;
            logQueue = new BlockingCollection<(string, string, LogType)>();
            logProcessingThread = new Thread(LogProcessingLoop) { IsBackground = true };
            logProcessingThread.Start(logQueue);
        }
    }

    public static void Stop()
    {
        lock (logLock)
        {
            logQueue?.CompleteAdding();
            logProcessingThread = null;
        }
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

            var grouped = logs
                .GroupBy(CollapseKey)
                .Select(g =>
                {
                    // pick one original colored line to preserve the original color + formatting
                    // but DO NOT wrap it in another color tag
                    string sampleColored = logs.First(l => CollapseKey(l) == g.Key);

                    // optional: show the clean text instead of the timestamped sample
                    // string display = $"{g.Count()}x {g.Key}";
                    // return display;

                    return $"{g.Count()}x {sampleColored}";
                })
                .ToList();

            return grouped;
        }
    }
    public static List<string> GetCombinedCollapsedLogs()
    {
        lock (logLock)
        {
            return logEntries
                .GroupBy(CollapseKey)
                .Select(g => $"{g.Count()}x {g.First()}")
                .ToList();
        }
    }
    private static string StripColorTags(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // remove <color=...> and </color>
        s = System.Text.RegularExpressions.Regex.Replace(s, @"</?color.*?>", "");
        return s;
    }

    private static string StripTimestampPrefix(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // removes "[12:34:56] " at start
        return System.Text.RegularExpressions.Regex.Replace(s, @"^\[\d{2}:\d{2}:\d{2}\]\s*", "");
    }

    private static string CollapseKey(string coloredLog)
    {
        // coloredLog is currently "<color=...>[time] message</color>"
        var plain = StripColorTags(coloredLog);
        plain = StripTimestampPrefix(plain);

        // Optional: normalize whitespace so tiny differences don't break collapsing
        plain = plain.Replace("\r\n", "\n").Trim();
        return plain;
    }
    public static void HandleLog(string logString, string stackTrace, LogType type)
    {
        if (logQueue == null) Start();
        try
        {
            logQueue.Add((logString, stackTrace, type));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void LogProcessingLoop(object queue)
    {
        foreach (var logEntry in ((BlockingCollection<(string logString, string stackTrace, LogType type)>)queue).GetConsumingEnumerable())
        {
            AddLog(logEntry.logString, logEntry.stackTrace, logEntry.type);
            LogChanged = true;
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

    private static void AddLogEntry(Queue<string> logList, string log)
    {
        logList.Enqueue(log);
        if (logList.Count > MaximumLogs)
            logList.Dequeue();
    }
    public const int MaximumLogs = 300;
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

    public static string GetAllLogsPlainText()
    {
        lock (logLock)
        {
            return string.Join("\n", logEntries.Select(StripColorTags));
        }
    }
}
