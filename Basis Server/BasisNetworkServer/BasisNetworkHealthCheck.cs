using Basis.Network.Core;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Server
{
    public sealed class BasisNetworkHealthCheck : IDisposable
    {
        private static readonly byte[] Empty = Array.Empty<byte>();

        private readonly HttpListener httpListener = new HttpListener();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        private readonly string host;
        private readonly ushort port;
        private readonly string pathNormalized;

        private readonly DateTimeOffset startTimeUtc;

        private Task listenTask;

        public BasisNetworkHealthCheck(Configuration config)
        {
            host = config.HealthCheckHost;
            port = config.HealthCheckPort;

            // Normalize path: ensure leading slash, remove trailing slash (except root)
            pathNormalized = NormalizePath(config.HealthPath);

            // Prefix must end with slash. IPv6 address literals need bracket notation.
            httpListener.Prefixes.Add($"http://{FormatHost(host)}:{port}/");
            httpListener.Start();

            startTimeUtc = DateTimeOffset.UtcNow;

            listenTask = ListenLoopAsync(cts.Token);

            BNL.Log($"HTTP health check started at 'http://{FormatHost(host)}:{port}{pathNormalized}'");
        }

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "/";

            p = p.Trim();
            if (!p.StartsWith("/")) p = "/" + p;

            // Remove trailing slash unless it's "/"
            if (p.Length > 1 && p.EndsWith("/")) p = p.Substring(0, p.Length - 1);

            return p;
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context = null;

                try
                {
                    context = await httpListener.GetContextAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return; // listener closed
                }
                catch (HttpListenerException)
                {
                    return; // listener stopped or error
                }
                catch (Exception e)
                {
                    BNL.LogWarning("HTTP health check loop error: " + e);
                    continue;
                }

                _ = Task.Run(() => HandleRequest(context), token);
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var req = context.Request;
                var res = context.Response;

                // Basic hardening / semantics
                res.Headers["Cache-Control"] = "no-store, max-age=0";
                res.Headers["X-Content-Type-Options"] = "nosniff";

                if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    res.StatusCode = 405;
                    res.Close(Empty, false);
                    return;
                }

                var reqPath = NormalizePath(req.Url.AbsolutePath);
                if (!string.Equals(reqPath, pathNormalized, StringComparison.Ordinal))
                {
                    res.StatusCode = 404;
                    res.Close(Empty, false);
                    return;
                }

                // Decide readiness (example: "listening" means process alive; "ready" means server exists)
                bool ready = NetworkServer.Server != null; // replace with your real readiness check
                res.StatusCode = ready ? 200 : 503;

                var nowUtc = DateTimeOffset.UtcNow;

                // Build JSON with numeric fields as numbers (no quotes)
                // If you want *zero* JSON escaping worries, keep version as a simple value you control.
                string json;

                string bsr = NetworkServer.Configuration.HealthIncludeBSRProfiling
                    ? ",\"bsr\":" + BuildBsrJson()
                    : string.Empty;

                if (NetworkServer.Configuration.EnableStatistics && NetworkServer.Server != null)
                {
                    int visitors = NetworkServer.Server.ConnectedPeersCount;
                    long sent = NetworkServer.Server.Statistics.BytesSent;
                    long recv = NetworkServer.Server.Statistics.BytesReceived;
                    int capacity = NetworkServer.Configuration.PeerLimit;

                    json =
                        "{" +
                        "\"listening\":true," +
                        $"\"ready\":{(ready ? "true" : "false")}," +
                        $"\"visitors\":{visitors}," +
                        $"\"capacity\":{capacity}," +
                        $"\"sent\":{sent}," +
                        $"\"recv\":{recv}," +
                        $"\"currentTime\":\"{nowUtc:O}\"," +
                        $"\"startTime\":\"{startTimeUtc:O}\"," +
                        $"\"version\":\"{BasisNetworkVersion.ServerVersion}\"" +
                        bsr +
                        "}";
                }
                else
                {
                    json =
                        "{" +
                        "\"listening\":true," +
                        $"\"ready\":{(ready ? "true" : "false")}," +
                        $"\"currentTime\":\"{nowUtc:O}\"," +
                        $"\"startTime\":\"{startTimeUtc:O}\"," +
                        $"\"version\":\"{BasisNetworkVersion.ServerVersion}\"" +
                        bsr +
                        "}";
                }

                byte[] payload = Encoding.UTF8.GetBytes(json);

                res.ContentType = "application/json; charset=utf-8";
                res.ContentEncoding = Encoding.UTF8;
                res.ContentLength64 = payload.Length;

                res.OutputStream.Write(payload, 0, payload.Length);
                res.OutputStream.Close();
            }
            catch
            {
                try { context?.Response?.Abort(); } catch { /* ignore */ }
            }
        }

        private static string Num(double value, string format) =>
            double.IsNaN(value) || double.IsInfinity(value)
                ? "0"
                : value.ToString(format, CultureInfo.InvariantCulture);

        private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string BuildBsrJson()
        {
            long interval = BasisServerReductionSystemEvents.intervalMs;

            StringBuilder sb = new StringBuilder(768);
            sb.Append("{\"load\":{")
              .Append("\"tickMs\":").Append(Num(BasisServerReductionSystemEvents.TickMsEma, "F3"))
              .Append(",\"overrunRatio\":").Append(Num(BasisServerReductionSystemEvents.TickOverrunRatio, "F4"))
              .Append(",\"intervalMs\":").Append(Int(interval))
              .Append(",\"hz\":").Append(Int(1000 / Math.Max(1, interval)))
              .Append(",\"shedTier\":").Append(Int(BasisServerReductionSystemEvents.LoadShedTier))
              .Append(",\"shedTierName\":\"").Append(BasisServerReductionSystemEvents.LoadShedTierLabel).Append('"')
              .Append(",\"sliceCount\":").Append(Int(BasisServerReductionSystemEvents.SliceCount))
              .Append('}');

            BSRProfilerSnapshot s = BSRProfiler.Latest;
            if (s == null)
            {
                return sb.Append(",\"window\":null}").ToString();
            }

            double ticks = s.Ticks;

            sb.Append(",\"window\":{")
              .Append("\"capturedTime\":\"").Append(s.CapturedUtc.ToString("O", CultureInfo.InvariantCulture)).Append('"')
              .Append(",\"ticks\":").Append(Int(s.Ticks))
              .Append(",\"messages\":").Append(Int(s.Messages))
              .Append(",\"sends\":").Append(Int(s.Sends))
              .Append(",\"preSerialized\":").Append(Int(s.PreSerializations))
              .Append(",\"preSerializedSkipped\":").Append(Int(s.PreSerializationsSkipped))
              .Append(",\"msPerTick\":{")
              .Append("\"drain\":").Append(Num(s.DrainMs / ticks, "F4"))
              .Append(",\"process\":").Append(Num(s.ProcessMs / ticks, "F4"))
              .Append(",\"distance\":").Append(Num(s.DistanceMs / ticks, "F4"))
              .Append(",\"update\":").Append(Num(s.UpdateMs / ticks, "F4"))
              .Append(",\"trigger\":").Append(Num(s.TriggerMs / ticks, "F4"))
              .Append(",\"total\":").Append(Num(s.TotalMs / ticks, "F4"))
              .Append('}');

            sb.Append(",\"bundles\":{")
              .Append("\"emitted\":").Append(Int(s.BundlesEmitted))
              .Append(",\"messages\":").Append(Int(s.BundleMessages))
              .Append(",\"tailUncompressed\":").Append(Int(s.BundleTailUncompressed))
              .Append(",\"fallbacks\":").Append(Int(s.BundleFallbacks))
              .Append(",\"retries\":").Append(Int(s.BundleRetries))
              .Append(",\"rawBytes\":").Append(Int(s.BundleRawBytes))
              .Append(",\"compressedBytes\":").Append(Int(s.BundleCompressedBytes))
              .Append(",\"savedBytes\":").Append(Int(s.BundleRawBytes - s.BundleCompressedBytes))
              .Append(",\"ratio\":").Append(Num(s.BundleRawBytes > 0 ? (double)s.BundleCompressedBytes / s.BundleRawBytes : 0, "F4"))
              .Append(",\"perTick\":").Append(Num(s.BundlesEmitted / ticks, "F4"))
              .Append(",\"avgMessages\":").Append(Num(s.BundlesEmitted > 0 ? (double)s.BundleMessages / s.BundlesEmitted : 0, "F2"))
              .Append(",\"deflateMsPerTick\":").Append(Num(s.BundleDeflateMs / ticks, "F4"))
              .Append(",\"avgDeflateUs\":").Append(Num(s.BundlesEmitted > 0 ? (s.BundleDeflateMs * 1000.0) / s.BundlesEmitted : 0, "F2"))
              .Append("}}}");

            return sb.ToString();
        }

        // HttpListener URL prefixes require bracket notation for IPv6 address literals.
        private static string FormatHost(string host) =>
            IPAddress.TryParse(host, out IPAddress addr) && addr.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{host}]"
                : host;

        public void Stop() => Dispose();

        public void Dispose()
        {
            if (cts.IsCancellationRequested) return;

            cts.Cancel();

            try { httpListener.Stop(); } catch { }
            try { httpListener.Close(); } catch { }

            try { listenTask?.Wait(250); } catch { }

            cts.Dispose();
        }
    }
}
