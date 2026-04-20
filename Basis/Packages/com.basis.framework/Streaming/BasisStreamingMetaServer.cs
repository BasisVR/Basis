using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Streaming
{
    /// <summary>
    /// Loopback-only HTTP listener that exposes a small JSON snapshot of client
    /// metrics (FPS, CCU, ping) for consumption by OBS Browser Source or similar.
    /// Bound to 127.0.0.1 so nothing leaves the machine.
    /// </summary>
    public sealed class BasisStreamingMetaServer : IDisposable
    {
        public sealed class Snapshot
        {
            public float Fps;
            public int Ccu;
            public int PeerLimit;
            public int RoundTripMs;
            public int PingMs;
            public bool Connected;
            public DateTimeOffset TimeUtc;
        }

        private static readonly byte[] Empty = Array.Empty<byte>();
        private static readonly byte[] OverlayHtmlBytes = Encoding.UTF8.GetBytes(OverlayHtml);

        private readonly HttpListener listener = new HttpListener();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly Task listenTask;

        private Snapshot currentSnapshot = new Snapshot { TimeUtc = DateTimeOffset.UtcNow };

        public string Prefix { get; }

        public BasisStreamingMetaServer(string host, int port)
        {
            Prefix = $"http://{host}:{port}/";
            listener.Prefixes.Add(Prefix);
            listener.Start();
            listenTask = ListenLoopAsync(cts.Token);
        }

        /// <summary>
        /// Replace the published snapshot. Called from Unity's main thread; HTTP
        /// handlers read it on thread-pool threads via <see cref="Volatile.Read"/>.
        /// </summary>
        public void PublishSnapshot(Snapshot snapshot)
        {
            Volatile.Write(ref currentSnapshot, snapshot);
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
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
                    BasisDebug.LogError("[BasisStreamingMeta] listen loop error: " + ex);
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

                res.Headers["Access-Control-Allow-Origin"] = "*";
                res.Headers["Cache-Control"] = "no-store, max-age=0";
                res.Headers["X-Content-Type-Options"] = "nosniff";

                if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    res.StatusCode = 405;
                    res.Close(Empty, false);
                    return;
                }

                string path = req.Url.AbsolutePath;

                if (string.Equals(path, "/stats.json", StringComparison.Ordinal))
                {
                    ServeJson(res);
                    return;
                }

                if (string.Equals(path, "/overlay.html", StringComparison.Ordinal) ||
                    string.Equals(path, "/", StringComparison.Ordinal))
                {
                    ServeBytes(res, OverlayHtmlBytes, "text/html; charset=utf-8");
                    return;
                }

                res.StatusCode = 404;
                res.Close(Empty, false);
            }
            catch
            {
                try { context?.Response?.Abort(); } catch { }
            }
        }

        private void ServeJson(HttpListenerResponse res)
        {
            Snapshot s = Volatile.Read(ref currentSnapshot);
            byte[] payload = Encoding.UTF8.GetBytes(BuildJson(s));
            ServeBytes(res, payload, "application/json; charset=utf-8");
        }

        private static void ServeBytes(HttpListenerResponse res, byte[] payload, string contentType)
        {
            res.StatusCode = 200;
            res.ContentType = contentType;
            res.ContentLength64 = payload.Length;
            res.OutputStream.Write(payload, 0, payload.Length);
            res.OutputStream.Close();
        }

        private static string BuildJson(Snapshot s)
        {
            StringBuilder b = new StringBuilder(192);
            CultureInfo inv = CultureInfo.InvariantCulture;
            b.Append('{');
            b.Append("\"fps\":").Append(s.Fps.ToString("F2", inv));
            b.Append(",\"ccu\":").Append(s.Ccu.ToString(inv));
            b.Append(",\"peerLimit\":").Append(s.PeerLimit.ToString(inv));
            b.Append(",\"rtt\":").Append(s.RoundTripMs.ToString(inv));
            b.Append(",\"ping\":").Append(s.PingMs.ToString(inv));
            b.Append(",\"connected\":").Append(s.Connected ? "true" : "false");
            b.Append(",\"timeUtc\":\"").Append(s.TimeUtc.ToString("O", inv)).Append('"');
            b.Append('}');
            return b.ToString();
        }

        public void Dispose()
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            cts.Cancel();
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            try { listenTask?.Wait(250); } catch { }
            cts.Dispose();
        }

        private const string OverlayHtml =
@"<!doctype html>
<html><head><meta charset=""utf-8""><title>Basis Streaming Meta</title>
<style>
  html,body{margin:0;padding:0;background:transparent;font-family:Inter,""Segoe UI"",sans-serif;color:#fff}
  .panel{display:inline-flex;gap:16px;padding:10px 14px;background:rgba(0,0,0,.55);border-radius:10px;font-size:20px;line-height:1}
  .cell{display:flex;flex-direction:column;gap:2px}
  .k{opacity:.65;font-size:11px;text-transform:uppercase;letter-spacing:.08em}
  .v{font-weight:600;font-variant-numeric:tabular-nums}
  .off .v{opacity:.4}
</style></head>
<body><div id=""panel"" class=""panel off"">
  <div class=""cell""><span class=""k"">FPS</span><span class=""v"" id=""fps"">--</span></div>
  <div class=""cell""><span class=""k"">CCU</span><span class=""v"" id=""ccu"">--</span></div>
</div>
<script>
  const $p=document.getElementById('panel'),$f=document.getElementById('fps'),$c=document.getElementById('ccu');
  async function tick(){
    try{
      const r=await fetch('/stats.json',{cache:'no-store'});
      if(!r.ok)throw 0;
      const s=await r.json();
      $f.textContent=Math.round(s.fps);
      $c.textContent=s.connected?(s.peerLimit>0?(s.ccu+'/'+s.peerLimit):s.ccu):'—';
      $p.classList.remove('off');
    }catch(e){$p.classList.add('off');}
  }
  tick();setInterval(tick,500);
</script></body></html>";
    }
}
