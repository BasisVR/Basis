using Basis.Network.Server;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace BasisRestApi.Tests
{
    [Collection("RestApi")]
    public class HealthCheckTests
    {
        private static ushort FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            ushort port = (ushort)((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static async Task<JsonElement> GetHealthAsync(Configuration config)
        {
            ushort port = FreePort();
            config.HealthCheckHost = "localhost";
            config.HealthCheckPort = port;
            config.HealthPath = "/health";

            Configuration previous = NetworkServer.Configuration;
            NetworkServer.Configuration = config;

            using var check = new BasisNetworkHealthCheck(config);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
                using var response = await client.GetAsync("/health");
                string body = await response.Content.ReadAsStringAsync();
                return JsonDocument.Parse(body).RootElement.Clone();
            }
            finally
            {
                NetworkServer.Configuration = previous;
            }
        }

        [Fact]
        public async Task Health_OmitsBsr_WhenDisabled()
        {
            JsonElement root = await GetHealthAsync(new Configuration { HealthIncludeBSRProfiling = false });

            Assert.False(root.TryGetProperty("bsr", out _));
            Assert.True(root.GetProperty("listening").GetBoolean());
            Assert.True(root.TryGetProperty("version", out _));
        }

        [Fact]
        public async Task Health_IncludesLiveLoad_WhenEnabled()
        {
            JsonElement root = await GetHealthAsync(new Configuration { HealthIncludeBSRProfiling = true });

            JsonElement load = root.GetProperty("bsr").GetProperty("load");

            Assert.Equal(JsonValueKind.Number, load.GetProperty("tickMs").ValueKind);
            Assert.Equal(JsonValueKind.Number, load.GetProperty("overrunRatio").ValueKind);
            Assert.Equal(JsonValueKind.Number, load.GetProperty("intervalMs").ValueKind);
            Assert.Equal(JsonValueKind.Number, load.GetProperty("hz").ValueKind);
            Assert.Equal(JsonValueKind.Number, load.GetProperty("shedTier").ValueKind);
            Assert.Equal(JsonValueKind.Number, load.GetProperty("sliceCount").ValueKind);
            Assert.Equal(JsonValueKind.String, load.GetProperty("shedTierName").ValueKind);
        }

        // BSRProfiler is process-global mutable state, so a single test owns its whole lifecycle
        // rather than splitting it across siblings that can interleave.
        [Fact]
        public async Task Health_SerializesProfilingWindow_UnderCommaDecimalCulture()
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            try
            {
                ResetProfiler();

                JsonElement before = await GetHealthAsync(new Configuration { HealthIncludeBSRProfiling = true });
                Assert.Equal(JsonValueKind.Null, before.GetProperty("bsr").GetProperty("window").ValueKind);

                BSRProfiler.Enabled = true;
                BSRProfiler.tickCount = 40;
                BSRProfiler.messagesProcessed = 900;
                BSRProfiler.SendCount = 120;
                BSRProfiler.drainTicks = Stopwatch.Frequency / 1000;
                BSRProfiler.processTicks = Stopwatch.Frequency / 500;
                BSRProfiler.bundlesEmitted = 8;
                BSRProfiler.bundleMessages = 64;
                BSRProfiler.bundleRawBytes = 4096;
                BSRProfiler.bundleCompressedBytes = 1024;
                BSRProfiler.FlushWindowForTests();

                Assert.NotNull(BSRProfiler.Latest);

                JsonElement root = await GetHealthAsync(new Configuration { HealthIncludeBSRProfiling = true });
                JsonElement window = root.GetProperty("bsr").GetProperty("window");

                Assert.Equal(40, window.GetProperty("ticks").GetInt64());
                Assert.Equal(900, window.GetProperty("messages").GetInt64());
                Assert.Equal(120, window.GetProperty("sends").GetInt64());

                JsonElement perTick = window.GetProperty("msPerTick");
                Assert.Equal(JsonValueKind.Number, perTick.GetProperty("drain").ValueKind);
                Assert.True(perTick.GetProperty("total").GetDouble() > 0);

                JsonElement bundles = window.GetProperty("bundles");
                Assert.Equal(8, bundles.GetProperty("emitted").GetInt64());
                Assert.Equal(3072, bundles.GetProperty("savedBytes").GetInt64());
                Assert.Equal(0.25, bundles.GetProperty("ratio").GetDouble(), 3);
                Assert.Equal(8, bundles.GetProperty("avgMessages").GetDouble(), 3);

                ResetProfiler();
                BSRProfiler.Enabled = true;
                BSRProfiler.tickCount = 5;
                BSRProfiler.FlushWindowForTests();

                JsonElement sparse = await GetHealthAsync(new Configuration { HealthIncludeBSRProfiling = true });
                JsonElement sparseWindow = sparse.GetProperty("bsr").GetProperty("window");

                Assert.Equal(0, sparseWindow.GetProperty("msPerTick").GetProperty("total").GetDouble());
                Assert.Equal(0, sparseWindow.GetProperty("bundles").GetProperty("ratio").GetDouble());
                Assert.Equal(0, sparseWindow.GetProperty("bundles").GetProperty("avgDeflateUs").GetDouble());
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                ResetProfiler();
            }
        }

        private static void ResetProfiler() => BSRProfiler.ResetForTests();
    }
}
