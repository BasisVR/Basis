using Basis.Network.Core;
using System;
using System.Net;
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

            // Prefix must end with slash.
            httpListener.Prefixes.Add($"http://{host}:{port}/");
            httpListener.Start();

            startTimeUtc = DateTimeOffset.UtcNow;

            listenTask = ListenLoopAsync(cts.Token);

            BNL.Log($"HTTP health check started at 'http://{host}:{port}{pathNormalized}'");
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
