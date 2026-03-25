using System.Diagnostics;
using System.Threading;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    /// <summary>
    /// Lock-free, low-overhead profiler for the BSR tick loop.
    /// Disabled by default — enable via EnableBSRProfiling in config.xml or env var.
    /// When disabled, all methods are no-ops (volatile bool check, no branches taken).
    /// Prints a summary every 5 seconds and resets counters.
    /// </summary>
    public static class BSRProfiler
    {
        public static volatile bool Enabled;

        private static readonly double MsToTick = Stopwatch.Frequency / 1000.0;
        private static readonly long PrintIntervalTicks = (long)(5000 * MsToTick);
        private static long _lastPrintTick = Stopwatch.GetTimestamp();

        // Phase timings (accumulated ticks, reset each print interval)
        public static long drainTicks;
        public static long processTicks;
        public static long updateTicks;
        public static long triggerTicks;

        // Counters (reset each print interval)
        public static long tickCount;
        public static long messagesProcessed;
        // Public so thread-local counters can aggregate via Interlocked.Add after Parallel.For
        public static long SendCount;
        private static long _preSerializations;
        private static long _preSerializationsSkipped;

        public static void IncrementPreSerializations()
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _preSerializations);
        }

        public static void IncrementPreSerializationsSkipped()
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _preSerializationsSkipped);
        }

        public static void TryPrint()
        {
            if (!Enabled) return;

            long now = Stopwatch.GetTimestamp();
            if (now - Volatile.Read(ref _lastPrintTick) < PrintIntervalTicks) return;
            Volatile.Write(ref _lastPrintTick, now);

            long ticks = Interlocked.Exchange(ref tickCount, 0);
            if (ticks == 0) return;

            long msgs = Interlocked.Exchange(ref messagesProcessed, 0);
            long sends = Interlocked.Exchange(ref SendCount, 0);
            long preSer = Interlocked.Exchange(ref _preSerializations, 0);
            long preSkip = Interlocked.Exchange(ref _preSerializationsSkipped, 0);

            double drain = Interlocked.Exchange(ref drainTicks, 0) / MsToTick;
            double process = Interlocked.Exchange(ref processTicks, 0) / MsToTick;
            double update = Interlocked.Exchange(ref updateTicks, 0) / MsToTick;
            double trigger = Interlocked.Exchange(ref triggerTicks, 0) / MsToTick;

            double total = drain + process + update + trigger;

            BNL.Log($"\n[BSR Profile] {ticks} ticks, {msgs} msgs, {sends} sends, preSer {preSer}/{preSer + preSkip}");
            BNL.Log($"  drain:   {drain / ticks:F3} ms/tick ({drain / total * 100:F1}%)");
            BNL.Log($"  process: {process / ticks:F3} ms/tick ({process / total * 100:F1}%)");
            BNL.Log($"  update:  {update / ticks:F3} ms/tick ({update / total * 100:F1}%)");
            BNL.Log($"  trigger: {trigger / ticks:F3} ms/tick ({trigger / total * 100:F1}%)");
            BNL.Log($"  total:   {total / ticks:F3} ms/tick");
        }
    }
}
