/*
#if NET9_0_OR_GREATER
using Prometheus;
#endif
using System;
using System.Diagnostics;
using System.Threading;

namespace Basis.Network.Server.Prometheus
{
    public static class BasisPrometheus
    {
#if NET9_0_OR_GREATER
        private static KestrelMetricServer _server;

        // Prometheus Metrics
        private static readonly Counter MessagesSentCounter = Metrics.CreateCounter(
            "basis_messages_sent_total",
            "Total number of messages sent to players.",
            new CounterConfiguration { LabelNames = new[] { "message_type" } });

        private static readonly Gauge ConnectedPlayersGauge = Metrics.CreateGauge(
            "basis_connected_players",
            "Number of currently connected players.");

        private static readonly Histogram MessageSizeHistogram = Metrics.CreateHistogram(
            "basis_message_size_bytes",
            "Distribution of message sizes sent to players.",
            new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(100, 2, 10) });

        private static readonly Gauge ServerCpuUsageGauge = Metrics.CreateGauge(
            "basis_server_cpu_usage_percentage",
            "CPU usage percentage of the server.");

        private static readonly Gauge ServerMemoryUsageGauge = Metrics.CreateGauge(
            "basis_server_memory_usage_bytes",
            "Memory usage of the server in bytes.");

        private static Timer _serverMetricsTimer;

        // Variables for calculating CPU usage
        private static TimeSpan _lastCpuTime = TimeSpan.Zero;
        private static DateTime _lastCpuCheck = DateTime.MinValue;
        private static readonly object _cpuUsageLock = new();
#endif

        public static void StartPrometheus(int port = 1234, string url = "/metrics")
        {
#if NET9_0_OR_GREATER
            if (_server == null)
            {
                _server = new KestrelMetricServer(port, url);
                _server.Start();
                Console.WriteLine($"Prometheus metrics server started on port {port}");

                // Start server load monitoring
                StartServerMetricsCollection();
            }
            else
            {
                Console.WriteLine("Prometheus metrics server is already running.");
            }
#endif
        }

        public static void StopPrometheus()
        {
#if NET9_0_OR_GREATER
            if (_server != null)
            {
                _server.Stop();
                Console.WriteLine("Prometheus metrics server stopped.");
                _server = null;

                // Stop server load monitoring
                StopServerMetricsCollection();
            }
#endif
        }

#if NET9_0_OR_GREATER
        public static void RecordMessageSent(string messageType, int messageSize)
        {
            MessagesSentCounter.WithLabels(messageType).Inc();
            MessageSizeHistogram.Observe(messageSize);
            Console.WriteLine($"Recorded message sent: Type={messageType}, Size={messageSize} bytes");
        }

        public static void SetConnectedPlayers(int count)
        {
            ConnectedPlayersGauge.Set(count);
            Console.WriteLine($"Set connected players: {count}");
        }

        private static void StartServerMetricsCollection()
        {
            _serverMetricsTimer = new Timer(_ =>
            {
                // Update CPU and memory usage metrics
                var cpuUsage = GetCpuUsagePercentage();
                var memoryUsage = GetMemoryUsageBytes();

                ServerCpuUsageGauge.Set(cpuUsage);
                ServerMemoryUsageGauge.Set(memoryUsage);

                Console.WriteLine($"Updated server metrics: CPU={cpuUsage}%, Memory={memoryUsage} bytes");
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        private static void StopServerMetricsCollection()
        {
            _serverMetricsTimer?.Dispose();
            _serverMetricsTimer = null;
        }

        private static double GetCpuUsagePercentage()
        {
            lock (_cpuUsageLock)
            {
                var process = Process.GetCurrentProcess();
                var currentCpuTime = process.TotalProcessorTime;
                var now = DateTime.UtcNow;

                if (_lastCpuCheck == DateTime.MinValue)
                {
                    // First measurement, no data to calculate CPU usage yet
                    _lastCpuTime = currentCpuTime;
                    _lastCpuCheck = now;
                    return 0;
                }

                // Calculate CPU usage since the last check
                var cpuUsed = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
                var elapsedMs = (now - _lastCpuCheck).TotalMilliseconds;

                _lastCpuTime = currentCpuTime;
                _lastCpuCheck = now;

                // CPU usage as a percentage (across all cores)
                var cpuUsagePercentage = cpuUsed / (Environment.ProcessorCount * elapsedMs) * 100.0;
                return Math.Max(0, Math.Min(cpuUsagePercentage, 100)); // Clamp to [0, 100]
            }
        }

        private static double GetMemoryUsageBytes()
        {
            // Use Process or GC to get memory usage
            return Process.GetCurrentProcess().WorkingSet64;
        }
#endif
    }
}
*/