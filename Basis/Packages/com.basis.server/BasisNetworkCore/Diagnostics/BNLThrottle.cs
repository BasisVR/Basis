using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

public static class BNLThrottle
{
    public const int MaxKeys = 1024;
    public static long IntervalMs = 5000;
    private static readonly ConcurrentDictionary<string, Entry> entries = new ConcurrentDictionary<string, Entry>();
    public static void Log(string key, string message)
    {
        if (Allow(key, out long skipped)) BNL.Log(With(message, skipped));
    }
    public static void LogWarning(string key, string message)
    {
        if (Allow(key, out long skipped)) BNL.LogWarning(With(message, skipped));
    }
    public static void LogError(string key, string message)
    {
        if (Allow(key, out long skipped)) BNL.LogError(With(message, skipped));
    }
    public static bool Allow(string key, out long skipped)
    {
        skipped = 0;
        long now = (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        if (!entries.TryGetValue(key, out Entry entry))
        {
            if (entries.Count >= MaxKeys) entries.Clear();
            entry = entries.GetOrAdd(key, _ => new Entry { NextMs = long.MinValue });
        }
        lock (entry)
        {
            if (now < entry.NextMs)
            {
                entry.Skipped++;
                return false;
            }
            entry.NextMs = now + Interlocked.Read(ref IntervalMs);
            skipped = entry.Skipped;
            entry.Skipped = 0;
            return true;
        }
    }
    public static void Reset() => entries.Clear();
    private static string With(string message, long skipped) => skipped > 0 ? $"{message} ({skipped} similar messages suppressed)" : message;
    private sealed class Entry
    {
        public long NextMs, Skipped;
    }
}
