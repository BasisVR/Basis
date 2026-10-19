using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class BasisHeadlessHealthCheck : IDisposable
{
    private static readonly byte[] Empty = Array.Empty<byte>();

    private readonly HttpListener httpListener = new HttpListener();
    private readonly CancellationTokenSource cts = new CancellationTokenSource();
    private readonly string pathNormalized;
    private Task listenTask;

    public BasisHeadlessHealthCheck(string host, int port, string path)
    {
        pathNormalized = NormalizePath(path);
        httpListener.Prefixes.Add($"http://{FormatHost(host)}:{port}/");
        httpListener.Start();
        BasisHeadlessRuntimeStatus.SetHealthListenerRunning(true);
        listenTask = ListenLoopAsync(cts.Token);
    }

    // HttpListener URL prefixes require bracket notation for IPv6 address literals.
    private static string FormatHost(string host) =>
        !IPAddress.TryParse(host, out IPAddress addr) ? host
            : addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any) ? "+"
            : addr.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        path = path.Trim();
        if (!path.StartsWith("/"))
        {
            path = "/" + path;
        }

        if (path.Length > 1 && path.EndsWith("/"))
        {
            path = path.Substring(0, path.Length - 1);
        }

        return path;
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
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning("Headless health check loop error: " + ex, BasisDebug.LogTag.Networking);
                continue;
            }

            _ = Task.Run(() => HandleRequest(context), token);
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            HttpListenerRequest req = context.Request;
            HttpListenerResponse res = context.Response;

            res.Headers["Cache-Control"] = "no-store, max-age=0";
            res.Headers["X-Content-Type-Options"] = "nosniff";

            if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 405;
                res.Close(Empty, false);
                return;
            }

            string reqPath = NormalizePath(req.Url.AbsolutePath);
            if (!string.Equals(reqPath, pathNormalized, StringComparison.Ordinal))
            {
                res.StatusCode = 404;
                res.Close(Empty, false);
                return;
            }

            BasisHeadlessRuntimeStatus.Snapshot snapshot = BasisHeadlessRuntimeStatus.CreateSnapshot();
            bool healthy = snapshot.IsConnected && snapshot.State == BasisHeadlessConnectionState.Connected;
            string json = BuildJson(snapshot, healthy);
            byte[] payload = Encoding.UTF8.GetBytes(json);

            res.StatusCode = healthy ? 200 : 503;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentEncoding = Encoding.UTF8;
            res.ContentLength64 = payload.Length;
            res.OutputStream.Write(payload, 0, payload.Length);
            res.OutputStream.Close();
        }
        catch
        {
            try
            {
                context?.Response?.Abort();
            }
            catch
            {
            }
        }
    }

    private static string BuildJson(BasisHeadlessRuntimeStatus.Snapshot snapshot, bool healthy)
    {
        StringBuilder builder = new StringBuilder(512);
        builder.Append('{');
        AppendBool(builder, "listening", snapshot.IsHealthListenerRunning);
        AppendBool(builder, "connected", snapshot.IsConnected);
        AppendBool(builder, "healthy", healthy);
        AppendString(builder, "state", snapshot.State.ToString());
        AppendString(builder, "serverIp", snapshot.ConfiguredServerIp);
        AppendInt(builder, "serverPort", snapshot.ConfiguredServerPort);
        AppendBool(builder, "retryEnabled", snapshot.RetryEnabled);
        AppendInt(builder, "currentRetryAttempt", snapshot.CurrentRetryAttempt);
        AppendInt(builder, "maxRetryAttempts", snapshot.MaxRetryAttempts);
        AppendInt(builder, "retryDelaySeconds", snapshot.RetryDelaySeconds);
        AppendInt(builder, "totalDisconnectCount", snapshot.TotalDisconnectCount);
        AppendInt(builder, "totalReconnectSuccessCount", snapshot.TotalReconnectSuccessCount);
        AppendString(builder, "lastDisconnectReason", snapshot.LastDisconnectReason);
        AppendString(builder, "lastDisconnectSocketError", snapshot.LastDisconnectSocketError);
        AppendNullableString(builder, "lastDisconnectMessage", snapshot.LastDisconnectMessage);
        AppendString(builder, "healthPath", snapshot.HealthPath);
        AppendString(builder, "currentTimeUtc", DateTimeOffset.UtcNow.ToString("O"));
        AppendString(builder, "startTimeUtc", snapshot.StartTimeUtc.ToString("O"));
        AppendNullableDate(builder, "lastConnectAttemptUtc", snapshot.LastConnectAttemptUtc);
        AppendNullableDate(builder, "lastConnectedUtc", snapshot.LastConnectedUtc);
        AppendNullableDate(builder, "lastDisconnectedUtc", snapshot.LastDisconnectedUtc);
        AppendString(builder, "lastHealthStateChangeUtc", snapshot.LastHealthStateChangeUtc.ToString("O"));
        AppendInt(builder, "remotePlayerCount", snapshot.RemotePlayerCount);
        AppendMegabytes(builder, "workingSetMb", snapshot.WorkingSetBytes);
        AppendMegabytes(builder, "gcHeapMb", snapshot.GcHeapBytes);
        AppendMegabytes(builder, "monoHeapMb", snapshot.MonoHeapBytes);
        AppendMegabytes(builder, "monoUsedMb", snapshot.MonoUsedBytes);
        AppendMegabytes(builder, "totalAllocatedMb", snapshot.TotalAllocatedBytes);
        AppendMegabytes(builder, "totalReservedMb", snapshot.TotalReservedBytes);
        AppendMegabytes(builder, "totalUnusedReservedMb", snapshot.TotalUnusedReservedBytes);
        AppendNullableDate(builder, "lastMemorySampleUtc", snapshot.LastMemorySampleUtc);
        AppendMegabytes(builder, "textureMb", snapshot.TextureBytes);
        AppendInt(builder, "textureCount", snapshot.TextureCount);
        AppendMegabytes(builder, "meshMb", snapshot.MeshBytes);
        AppendInt(builder, "meshCount", snapshot.MeshCount);
        AppendMegabytes(builder, "audioClipMb", snapshot.AudioClipBytes);
        AppendInt(builder, "audioClipCount", snapshot.AudioClipCount);
        AppendNullableDate(builder, "lastAssetSweepUtc", snapshot.LastAssetSweepUtc);
        if (builder[builder.Length - 1] == ',')
        {
            builder.Length--;
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendBool(StringBuilder builder, string name, bool value)
    {
        builder.Append('"').Append(name).Append("\":").Append(value ? "true" : "false").Append(',');
    }

    private static void AppendInt(StringBuilder builder, string name, int value)
    {
        builder.Append('"').Append(name).Append("\":").Append(value).Append(',');
    }

    /// <summary>
    /// Emits a byte count as megabytes with one decimal. Invariant formatting is required —
    /// a comma decimal separator would produce malformed JSON on a European locale.
    /// </summary>
    private static void AppendMegabytes(StringBuilder builder, string name, long bytes)
    {
        double megabytes = bytes / (1024d * 1024d);
        builder.Append('"').Append(name).Append("\":")
            .Append(megabytes.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
    }

    private static void AppendString(StringBuilder builder, string name, string value)
    {
        builder.Append('"').Append(name).Append("\":\"").Append(Escape(value ?? string.Empty)).Append("\",");
    }

    private static void AppendNullableString(StringBuilder builder, string name, string value)
    {
        builder.Append('"').Append(name).Append("\":");
        if (value == null)
        {
            builder.Append("null,");
            return;
        }

        builder.Append('"').Append(Escape(value)).Append("\",");
    }

    private static void AppendNullableDate(StringBuilder builder, string name, DateTimeOffset? value)
    {
        builder.Append('"').Append(name).Append("\":");
        if (value.HasValue)
        {
            builder.Append('"').Append(value.Value.ToString("O")).Append("\",");
        }
        else
        {
            builder.Append("null,");
        }
    }

    private static string Escape(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    public void Dispose()
    {
        if (cts.IsCancellationRequested)
        {
            return;
        }

        cts.Cancel();
        BasisHeadlessRuntimeStatus.SetHealthListenerRunning(false);
        try
        {
            httpListener.Stop();
        }
        catch
        {
        }

        try
        {
            httpListener.Close();
        }
        catch
        {
        }

        try
        {
            listenTask?.Wait(250);
        }
        catch
        {
        }

        cts.Dispose();
    }
}
