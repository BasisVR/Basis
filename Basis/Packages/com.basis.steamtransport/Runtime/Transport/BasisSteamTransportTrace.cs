using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Basis.Scripts.Networking.Steam
{
    public static class BasisSteamTransportTrace
    {
        private const int MaxQueuedLines = 4096;
        private const int FlushBatchSize = 512;
        private const int FlushThreshold = 128;
        private static readonly object sync = new object();
        private static readonly ConcurrentQueue<string> pendingLines = new ConcurrentQueue<string>();
        private static readonly StringBuilder flushBuilder = new StringBuilder(16 * 1024);
        private static string logPath;
        private static DateTime nextFlushUtc = DateTime.MinValue;
        private static int queuedLineCount;
        private static int droppedLineCount;
        public static bool Enabled { get; private set; }

        public static string LogPath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(logPath))
                {
                    logPath = Path.Combine(Application.persistentDataPath, "BasisSteamTransport.log");
                }

                return logPath;
            }
        }

        public static void Clear()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                ResetQueueState();
                lock (sync)
                {
                    File.WriteAllText(LogPath, $"[{DateTime.UtcNow:O}] BasisSteamTransport log start{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BasisSteamTransportTrace] Failed to clear log: {ex.Message}");
            }
        }

        public static void Configure(bool enabled)
        {
            if (!enabled && Enabled)
            {
                FlushPending(force: true);
                ResetQueueState();
            }

            Enabled = enabled;
            nextFlushUtc = DateTime.UtcNow;
        }

        public static void Log(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void FlushPending(bool force = false)
        {
            if (!Enabled && !force)
            {
                return;
            }

            if (Volatile.Read(ref queuedLineCount) == 0 && Volatile.Read(ref droppedLineCount) == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!force && Volatile.Read(ref queuedLineCount) < FlushThreshold && now < nextFlushUtc)
            {
                return;
            }

            try
            {
                lock (sync)
                {
                    if (!force && Volatile.Read(ref queuedLineCount) < FlushThreshold && DateTime.UtcNow < nextFlushUtc)
                    {
                        return;
                    }

                    int linesToFlush = force ? int.MaxValue : FlushBatchSize;
                    StringBuilder builder = flushBuilder;
                    builder.Clear();
                    int flushedCount = 0;

                    while (flushedCount < linesToFlush && pendingLines.TryDequeue(out string line))
                    {
                        builder.Append(line);
                        flushedCount++;
                    }

                    if (flushedCount > 0)
                    {
                        Interlocked.Add(ref queuedLineCount, -flushedCount);
                    }

                    int droppedCount = Interlocked.Exchange(ref droppedLineCount, 0);
                    if (droppedCount > 0)
                    {
                        builder.Append('[')
                            .Append(DateTime.UtcNow.ToString("O"))
                            .Append("] [WARN] Dropped ")
                            .Append(droppedCount)
                            .Append(" trace lines because the queue was full.")
                            .Append(Environment.NewLine);
                    }

                    if (builder.Length == 0)
                    {
                        nextFlushUtc = DateTime.UtcNow.AddMilliseconds(250);
                        return;
                    }

                    File.AppendAllText(LogPath, builder.ToString());
                    nextFlushUtc = DateTime.UtcNow.AddMilliseconds(250);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BasisSteamTransportTrace] Failed to flush log: {ex.Message}");
            }
        }

        private static void Write(string level, string message)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                string line = $"[{DateTime.UtcNow:O}] [{level}] {message}{Environment.NewLine}";
                int nextCount = Interlocked.Increment(ref queuedLineCount);
                if (nextCount > MaxQueuedLines)
                {
                    Interlocked.Decrement(ref queuedLineCount);
                    Interlocked.Increment(ref droppedLineCount);
                    return;
                }

                pendingLines.Enqueue(line);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BasisSteamTransportTrace] Failed to write log: {ex.Message}");
            }
        }

        private static void ResetQueueState()
        {
            while (pendingLines.TryDequeue(out _))
            {
            }

            Interlocked.Exchange(ref queuedLineCount, 0);
            Interlocked.Exchange(ref droppedLineCount, 0);
            nextFlushUtc = DateTime.UtcNow;
        }
    }
}
