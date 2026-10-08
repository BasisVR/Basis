#if !UNITY_2017_1_OR_NEWER
using Basis.Network.Core;
using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Server
{
    public sealed class BasisRestApiHandler : IDisposable
    {
        private const int MaxConcurrentRequests = 32;

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _semaphore = new(MaxConcurrentRequests, MaxConcurrentRequests);
        private readonly BasisRestApiRoutes _routes;
        private readonly string _apiKey;
        private readonly byte[] _keyHash;
        private int _disposed;
        private static readonly byte[] Empty = Array.Empty<byte>();

        public BasisRestApiHandler(Configuration config, IServerControl control = null)
        {
            _apiKey  = config.ApiKey;
            _keyHash = string.IsNullOrEmpty(_apiKey)
                ? Array.Empty<byte>()
                : HashBytes(Encoding.UTF8.GetBytes(_apiKey));

            if (string.IsNullOrEmpty(_apiKey))
                BNL.LogWarning("[REST API] No ApiKey configured — all requests will be rejected. Set ApiKey in config to enable the REST API.");

            _routes = new BasisRestApiRoutes(control ?? new BasisServerControl());
            _listener.Prefixes.Add($"http://{FormatHost(config.ApiHost)}:{config.ApiPort}/");
            _listener.Start();
            _ = ListenLoopAsync(_cts.Token);
            BNL.Log($"REST API started at http://{FormatHost(config.ApiHost)}:{config.ApiPort}/api/");
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (HttpListenerException e)
                {
                    if (!token.IsCancellationRequested) BNL.LogWarning("REST API listener stopped unexpectedly: " + e.Message);
                    return;
                }
                catch (Exception e) { BNL.LogWarning("REST API loop error: " + e); continue; }

                if (!_semaphore.Wait(0))
                {
                    try { ctx.Response.StatusCode = 503; ctx.Response.Close(Empty, false); } catch { }
                    continue;
                }

                var captured = ctx;
                var capturedToken = token;
                _ = Task.Run(() =>
                {
                    try { HandleRequest(captured, capturedToken); }
                    finally { _semaphore.Release(); }
                });
            }
        }

        private void HandleRequest(HttpListenerContext ctx, CancellationToken token)
        {
            try
            {
                var req = ctx.Request;
                var res = ctx.Response;
                res.Headers["Cache-Control"] = "no-store, max-age=0";
                res.Headers["X-Content-Type-Options"] = "nosniff";

                if (!Authenticate(req))
                {
                    res.StatusCode = 401;
                    res.Headers["WWW-Authenticate"] = "Bearer realm=\"basis-server\"";
                    res.Close(Empty, false);
                    return;
                }

                var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length < 2 || segments[0] != "api")
                {
                    res.StatusCode = 404;
                    res.Close(Empty, false);
                    return;
                }

                _routes.Dispatch(req, res, segments, token);
            }
            catch (Exception e)
            {
                BNL.LogError("REST API unhandled: " + e);
                try { ctx.Response?.Abort(); } catch { }
            }
        }

        private bool Authenticate(HttpListenerRequest req)
        {
            if (string.IsNullOrEmpty(_apiKey)) return false;
            var auth = req.Headers["Authorization"];
            if (auth == null || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
            var tokenHash = HashBytes(Encoding.UTF8.GetBytes(auth.Substring("Bearer ".Length)));
            return CryptographicOperations.FixedTimeEquals(_keyHash, tokenHash);
        }

        private static byte[] HashBytes(byte[] data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(data);
        }

        // HttpListener URL prefixes require bracket notation for IPv6 address literals.
        private static string FormatHost(string host) =>
            !IPAddress.TryParse(host, out IPAddress addr) ? host
                : addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any) ? "+"
                : addr.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _cts.Dispose();
            _semaphore.Dispose();
        }
    }
}
#endif
