using System;
using System.Diagnostics;
using System.Threading;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    /// <summary>
    /// One closed profiling window. Raw window totals only; derived figures are left to the caller
    /// so every consumer computes them the same way.
    /// </summary>
    public sealed class BSRProfilerSnapshot
    {
        public DateTimeOffset CapturedUtc;
        public long Ticks;
        public long Messages;
        public long Sends;
        public long PreSerializations;
        public long PreSerializationsSkipped;
        public double DrainMs;
        public double ProcessMs;
        public double DistanceMs;
        public double UpdateMs;
        public double TriggerMs;
        public double TotalMs;
        public long BundlesEmitted;
        public long BundleMessages;
        public long BundleRawBytes;
        public long BundleCompressedBytes;
        public double BundleDeflateMs;
        public long BundleRetries;
        public long BundleFallbacks;
        public long BundleTailUncompressed;
    }

    /// <summary>
    /// Lock-free, low-overhead profiler for the BSR tick loop.
    /// Disabled by default — enable via EnableBSRProfiling in config.xml or env var.
    /// When disabled, all methods are no-ops (volatile bool check, no branches taken).
    /// Collects a window every 5 seconds, publishes it to <see cref="Latest"/>, and resets counters.
    /// </summary>
    public static class BSRProfiler
    {
        public static volatile bool Enabled;

        /// <summary>Whether a closed window is written to the log. Collection is driven by <see cref="Enabled"/> alone.</summary>
        public static volatile bool WriteToLog;

        private static volatile BSRProfilerSnapshot _latest;

        /// <summary>Most recently closed window, or null until the first one completes.</summary>
        public static BSRProfilerSnapshot Latest => _latest;

        private static readonly double MsToTick = Stopwatch.Frequency / 1000.0;
        private static readonly long PrintIntervalTicks = (long)(5000 * MsToTick);
        private static long _lastPrintTick = Stopwatch.GetTimestamp();

        // Phase timings (accumulated ticks, reset each print interval)
        public static long drainTicks;
        public static long processTicks;
        public static long distanceTicks;
        public static long updateTicks;
        public static long triggerTicks;

        // Counters (reset each print interval)
        public static long tickCount;
        public static long messagesProcessed;
        // Public so thread-local counters can aggregate via Interlocked.Add after Parallel.For
        public static long SendCount;
        private static long _preSerializations;
        private static long _preSerializationsSkipped;

        // Compressed-avatar-bundle metrics. Public so the reduction system can Interlocked.Add
        // from the parallel send loop. All only touched when Enabled is true.
        public static long bundlesEmitted;
        public static long bundleMessages;
        public static long bundleRawBytes;
        public static long bundleCompressedBytes;
        public static long bundleDeflateTicks;
        public static long bundleRetries;
        public static long bundleFallbacks;
        public static long bundleTailUncompressed;

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

        /// <summary>Test seam (InternalsVisibleTo): closes the current window immediately instead of waiting out the interval.</summary>
        internal static void FlushWindowForTests()
        {
            Volatile.Write(ref _lastPrintTick, Stopwatch.GetTimestamp() - PrintIntervalTicks - 1);
            TryPrint();
        }

        /// <summary>Test seam (InternalsVisibleTo): clears every counter, the published window, and both flags.</summary>
        internal static void ResetForTests()
        {
            Enabled = false;
            WriteToLog = false;
            _latest = null;
            tickCount = 0;
            messagesProcessed = 0;
            SendCount = 0;
            _preSerializations = 0;
            _preSerializationsSkipped = 0;
            drainTicks = 0;
            processTicks = 0;
            distanceTicks = 0;
            updateTicks = 0;
            triggerTicks = 0;
            bundlesEmitted = 0;
            bundleMessages = 0;
            bundleRawBytes = 0;
            bundleCompressedBytes = 0;
            bundleDeflateTicks = 0;
            bundleRetries = 0;
            bundleFallbacks = 0;
            bundleTailUncompressed = 0;
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
            double distance = Interlocked.Exchange(ref distanceTicks, 0) / MsToTick;
            double update = Interlocked.Exchange(ref updateTicks, 0) / MsToTick;
            double trigger = Interlocked.Exchange(ref triggerTicks, 0) / MsToTick;

            double total = drain + process + distance + update + trigger;

            // Bundle metrics — exchange even if zero so a flag flip is reflected immediately
            long bEmit = Interlocked.Exchange(ref bundlesEmitted, 0);
            long bMsg = Interlocked.Exchange(ref bundleMessages, 0);
            long bRaw = Interlocked.Exchange(ref bundleRawBytes, 0);
            long bComp = Interlocked.Exchange(ref bundleCompressedBytes, 0);
            long bDeflate = Interlocked.Exchange(ref bundleDeflateTicks, 0);
            long bRetry = Interlocked.Exchange(ref bundleRetries, 0);
            long bFallback = Interlocked.Exchange(ref bundleFallbacks, 0);
            long bTail = Interlocked.Exchange(ref bundleTailUncompressed, 0);

            _latest = new BSRProfilerSnapshot
            {
                CapturedUtc = DateTimeOffset.UtcNow,
                Ticks = ticks,
                Messages = msgs,
                Sends = sends,
                PreSerializations = preSer,
                PreSerializationsSkipped = preSkip,
                DrainMs = drain,
                ProcessMs = process,
                DistanceMs = distance,
                UpdateMs = update,
                TriggerMs = trigger,
                TotalMs = total,
                BundlesEmitted = bEmit,
                BundleMessages = bMsg,
                BundleRawBytes = bRaw,
                BundleCompressedBytes = bComp,
                BundleDeflateMs = bDeflate / MsToTick,
                BundleRetries = bRetry,
                BundleFallbacks = bFallback,
                BundleTailUncompressed = bTail,
            };

            if (!WriteToLog) return;

            BNL.Log($"\n[BSR Profile] {ticks} ticks, {msgs} msgs, {sends} sends, preSer {preSer}/{preSer + preSkip}");
            BNL.Log($"  drain:    {drain / ticks:F3} ms/tick ({drain / total * 100:F1}%)");
            BNL.Log($"  process:  {process / ticks:F3} ms/tick ({process / total * 100:F1}%)");
            BNL.Log($"  distance: {distance / ticks:F3} ms/tick ({distance / total * 100:F1}%)");
            BNL.Log($"  update:   {update / ticks:F3} ms/tick ({update / total * 100:F1}%)");
            BNL.Log($"  trigger:  {trigger / ticks:F3} ms/tick ({trigger / total * 100:F1}%)");
            BNL.Log($"  total:    {total / ticks:F3} ms/tick");

            if (bEmit > 0 || bTail > 0 || bFallback > 0)
            {
                double ratio = bRaw > 0 ? (double)bComp / bRaw : 0;
                double avgMsgsPerBundle = bEmit > 0 ? (double)bMsg / bEmit : 0;
                double avgRawPerBundle = bEmit > 0 ? (double)bRaw / bEmit : 0;
                double avgCompPerBundle = bEmit > 0 ? (double)bComp / bEmit : 0;
                double deflateMs = bDeflate / MsToTick;
                double avgDeflateUs = bEmit > 0 ? (deflateMs * 1000.0) / bEmit : 0;
                double bundlesPerTick = (double)bEmit / ticks;
                double retryRate = bEmit > 0 ? (double)bRetry / bEmit * 100.0 : 0;
                long savedBytes = bRaw - bComp; // raw input vs compressed output
                BNL.Log($"  bundles:  {bEmit} emitted ({bundlesPerTick:F2}/tick), {bMsg} msgs in bundles, {bTail} msgs tail-uncompressed, {bFallback} fallbacks");
                BNL.Log($"            ratio {ratio:F3} ({(1 - ratio) * 100:F1}% saved on bundled bytes), avg {avgMsgsPerBundle:F1} msgs/bundle ({avgRawPerBundle:F0} B raw → {avgCompPerBundle:F0} B compressed)");
                BNL.Log($"            deflate {deflateMs / ticks:F3} ms/tick ({deflateMs / total * 100:F1}% of tick), {avgDeflateUs:F1} µs/bundle, retries {bRetry} ({retryRate:F1}%)");
                BNL.Log($"            saved ~{savedBytes / 1024.0:F1} KB this window before per-message wire overhead");
            }
        }
    }
}
