using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;

/// <summary>
/// Captures local error/exception reports to disk during a session and, if the previous
/// session ended without a clean shutdown (i.e. it crashed), re-sends what it had captured
/// once the client reconnects and the server has reporting enabled. This is what lets a
/// hard crash — which can never report live, because the app is gone — still reach the
/// server's per-player crash store on the next launch.
///
/// Layout under <c>Application.persistentDataPath/CrashReports</c>:
///   session.active : present while a session is running; deleted on a clean quit.
///   pending.jsonl  : reports captured during the CURRENT session.
///   replay.jsonl   : an outbox of reports carried over from a crashed session, awaiting
///                    send. It is deleted only once the reports have actually been handed off
///                    for sending, so a crash before that point simply retries next launch,
///                    while a successful send removes the outbox so the same reports are
///                    never sent again on a later reboot.
///
/// Records are stored one per line as <c>severity \t base64(system) \t base64(message) \t
/// base64(stack)</c> — base64 keeps newlines/arbitrary characters from breaking the line
/// format without pulling in a JSON dependency.
/// </summary>
public static class BasisCrashReportStore
{
    private const int MaxReplayEntries = 50;
    private const long MaxFileBytes = 2L * 1024 * 1024;
    private const int MaxMessageChars = 2000;
    private const int MaxStackChars = 12000;

    // Brief acknowledgement that the carried-over crash reports are being flushed on reconnect.
    // These are fire-and-forget sends queued in a tight loop, so the indicator clears itself after
    // ReplayIndicatorSeconds rather than tracking a per-packet curve.
    private const string ReplayIndicatorKey = "CrashReportReplay";
    private const string ReplayIndicatorLabel = "Uploading crash reports";
    private const float ReplayIndicatorPercent = 80f;
    private const float ReplayIndicatorSeconds = 1.5f;

    private static readonly object FileLock = new object();
    private static readonly List<Entry> _previous = new List<Entry>();

    private static string _markerPath;
    private static string _pendingPath;
    private static string _replayPath;
    private static bool _initialized;
    private static bool _replayed;

    private struct Entry
    {
        public string System;
        public string Message;
        public string Stack;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "CrashReports");
            _markerPath = Path.Combine(dir, "session.active");
            _pendingPath = Path.Combine(dir, "pending.jsonl");
            _replayPath = Path.Combine(dir, "replay.jsonl");
            Directory.CreateDirectory(dir);

            lock (FileLock)
            {
                bool previousCrashed = File.Exists(_markerPath);

                // A crashed session's freshly-captured reports move into the outbox so they
                // survive even if THIS session also crashes before it can send them.
                if (previousCrashed && File.Exists(_pendingPath))
                {
                    FoldPendingIntoReplay();
                }

                // The current session always starts with an empty pending log.
                TryDelete(_pendingPath);

                // Load whatever is still waiting to be sent — this boot's fold, plus anything a
                // previous boot loaded but never managed to send. Independent of the marker, so
                // an outbox that survived a clean (but offline) shutdown still gets retried.
                if (File.Exists(_replayPath))
                {
                    LoadReplay();
                }

                // (Re)arm the marker for this session.
                try { File.WriteAllText(_markerPath, DateTime.UtcNow.ToString("o")); } catch { }
            }

            Application.quitting += OnQuit;
            BasisNetworkModeration.OnCrashReportingStateChanged += OnCrashReportingStateChanged;
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning($"BasisCrashReportStore init failed: {e.Message}");
        }
    }

    private static void OnQuit()
    {
        // Clean shutdown → drop this session's pending captures (they were sent live, or
        // intentionally not sent). The outbox (replay.jsonl) is deliberately left alone: if it
        // still holds unsent reports they should go out on the next launch.
        lock (FileLock)
        {
            TryDelete(_pendingPath);
            TryDelete(_markerPath);
        }
    }

    private static void OnCrashReportingStateChanged(bool enabled)
    {
        if (enabled) TryReplay();
    }

    /// <summary>Append one captured report to the current session's pending log. Thread-safe.</summary>
    public static void Persist(byte severity, string system, string message, string stackTrace)
    {
        if (!_initialized || string.IsNullOrEmpty(_pendingPath)) return;
        try
        {
            string line = severity
                + "\t" + Encode(system)
                + "\t" + Encode(Truncate(message, MaxMessageChars))
                + "\t" + Encode(Truncate(stackTrace, MaxStackChars));
            lock (FileLock)
            {
                if (OverSizeLimit(_pendingPath)) return; // don't let an exception storm fill the disk
                File.AppendAllText(_pendingPath, line + "\n");
            }
        }
        catch { }
    }

    /// <summary>
    /// Send any reports carried over from a previous crashed session, once connected and the
    /// server allows reporting. Runs at most once per launch; the on-disk outbox is deleted as
    /// soon as the reports are taken for sending, so the same reports are never re-sent on a
    /// later reboot (and a crash before this point simply retries the unchanged outbox).
    /// </summary>
    public static void TryReplay()
    {
        if (_replayed) return;
        if (!BasisNetworkModeration.CrashReportingEnabled) return;
        if (BasisNetworkConnection.LocalPlayerPeer == null) return;

        List<Entry> toSend;
        lock (FileLock)
        {
            if (_previous.Count == 0)
            {
                // Nothing to replay — clear any stale outbox and stop checking.
                TryDelete(_replayPath);
                _replayed = true;
                return;
            }
            toSend = new List<Entry>(_previous);
            _previous.Clear();
            // Mark as handled: deleting the outbox here is what guarantees these reports are
            // not picked up and sent again after the next reboot.
            TryDelete(_replayPath);
        }
        _replayed = true;

        BasisUILoadingBar.ProgressReportTransient(ReplayIndicatorKey, ReplayIndicatorPercent, ReplayIndicatorLabel, ReplayIndicatorSeconds);
        foreach (Entry e in toSend)
        {
            BasisErrorReportSender.SendPrevious(e.System, e.Message, e.Stack);
        }
        BasisDebug.Log($"Replayed {toSend.Count} crash report(s) from the previous session.", BasisDebug.LogTag.Networking);
    }

    // Append pending.jsonl onto replay.jsonl (capped to the most recent entries), so
    // carried-over reports accumulate in the outbox. Caller holds FileLock.
    private static void FoldPendingIntoReplay()
    {
        try
        {
            List<string> lines = new List<string>();
            if (File.Exists(_replayPath)) lines.AddRange(File.ReadAllLines(_replayPath));
            if (File.Exists(_pendingPath)) lines.AddRange(File.ReadAllLines(_pendingPath));

            if (lines.Count > MaxReplayEntries)
                lines.RemoveRange(0, lines.Count - MaxReplayEntries);

            StringBuilder sb = new StringBuilder();
            foreach (string l in lines)
            {
                if (!string.IsNullOrEmpty(l)) sb.Append(l).Append('\n');
            }
            File.WriteAllText(_replayPath, sb.ToString());
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning($"Failed to fold pending crash reports into the outbox: {e.Message}");
        }
    }

    // Caller holds FileLock.
    private static void LoadReplay()
    {
        try
        {
            string[] lines = File.ReadAllLines(_replayPath);
            int start = Math.Max(0, lines.Length - MaxReplayEntries);
            for (int i = start; i < lines.Length; i++)
            {
                if (TryParse(lines[i], out Entry entry)) _previous.Add(entry);
            }
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning($"Failed to load carried-over crash reports: {e.Message}");
        }
    }

    private static bool OverSizeLimit(string path)
    {
        try
        {
            FileInfo info = new FileInfo(path);
            return info.Exists && info.Length > MaxFileBytes;
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
    }

    private static bool TryParse(string line, out Entry entry)
    {
        entry = default;
        if (string.IsNullOrEmpty(line)) return false;
        string[] parts = line.Split('\t');
        if (parts.Length < 4) return false;
        try
        {
            entry.System = Decode(parts[1]);
            entry.Message = Decode(parts[2]);
            entry.Stack = Decode(parts[3]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Encode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string Decode(string value)
        => string.IsNullOrEmpty(value) ? string.Empty : Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value.Substring(0, max);
    }
}
