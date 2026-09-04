using Basis.Network.Core;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Server
{
    public sealed class BasisNetworkHealthCheck : IDisposable
    {
        private static readonly byte[] Empty = Array.Empty<byte>();
        // Same backpressure as the REST handler: without it every probe spawned an uncapped
        // Task.Run, so an aggressive scraper (or a scanner — this port has no auth) could fan out
        // arbitrarily many in-flight contexts.
        private const int MaxConcurrentRequests = 32;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);

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
            try
            {
                httpListener.Start();
            }
            catch (HttpListenerException ex)
            {
                BNL.LogError($"HTTP health check disabled: could not listen on 'http://{FormatHost(host)}:{port}/' ({ex.Message})");
                return;
            }

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

                if (!_semaphore.Wait(0))
                {
                    try { context.Response.StatusCode = 503; context.Response.Close(Empty, false); } catch { }
                    continue;
                }

                var captured = context;
                _ = Task.Run(() =>
                {
                    try { HandleRequest(captured); }
                    finally { _semaphore.Release(); }
                }, token);
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

                // Always on, unlike the BSR block: these are a handful of counter reads, and GC
                // behaviour is the one thing that was completely invisible here. Working set alone
                // cannot distinguish a server holding live state from one drowning in collections,
                // and those want opposite fixes.
                string gc = ",\"gc\":" + BuildGcJson();

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
                        // Datagram counts alongside the byte counts, so egress work can be read as
                        // packet rate and not just volume - the two move independently.
                        $"\"packetsSent\":{NetworkServer.Server.Statistics.PacketsSent}," +
                        $"\"packetsRecv\":{NetworkServer.Server.Statistics.PacketsReceived}," +
                        // Zero on a healthy instance. Rising means the server is shedding position
                        // updates because it cannot drain what it produces — the one number that
                        // distinguishes "busy" from "past capacity", and there was no way to see it.
                        $"\"droppedUnreliable\":{NetworkServer.Server.UnreliableDropped}," +
                        // Voice drops, counted apart from the line above. Bulk shedding is the
                        // designed response to load and a busy instance will show plenty of it;
                        // anything here is audio somebody did not hear, so the two must never be
                        // read as one number. Non-zero means the priority queue overflowed, which
                        // is a much louder signal than the same count of avatar updates.
                        $"\"droppedVoice\":{NetworkServer.Server.PriorityUnreliableDropped}," +
                        // The bound those drops are measured against. Without it the drop count is
                        // unreadable — you cannot tell a server that is genuinely past capacity from
                        // one whose queue is simply sized too small, which is exactly the confusion
                        // that let a fixed 256 shed half of all avatar updates unnoticed.
                        $"\"queuePerPeer\":{(NetworkServer.Server as LNLNetManager)?.manager?.EffectiveUnreliableQueuePerPeer ?? 0}," +
                        // The voice queue's own bound. Reported separately because it is sized on a
                        // different budget and is expected to be the DEEPER of the two — reading a
                        // voice drop against the bulk bound would make a correctly-tuned server look
                        // misconfigured.
                        $"\"voiceQueuePerPeer\":{(NetworkServer.Server as LNLNetManager)?.manager?.EffectivePriorityUnreliableQueuePerPeer ?? 0}," +
                        $"\"currentTime\":\"{nowUtc:O}\"," +
                        $"\"startTime\":\"{startTimeUtc:O}\"," +
                        $"\"version\":\"{BasisNetworkVersion.ServerVersion}\"" +
                        gc +
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
                        gc +
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

        /// <summary>
        /// GC counters, so allocation pressure can be told apart from live state.
        ///
        /// <para><c>allocatedMb</c> is cumulative for the process; the useful reading is its slope
        /// between two samples, which is the allocation RATE. <c>pauseTimePercent</c> is the runtime's
        /// own figure for time spent paused in GC and is the single number that says whether
        /// collections are costing throughput.</para>
        ///
        /// <para>Heap COUNT is deliberately absent: Server GC's heap count is adapted at runtime by
        /// DATAS and the runtime exposes no supported way to read the current value, so any number
        /// here would be inferred rather than measured. <c>committedMb</c> is the honest proxy —
        /// DATAS scaling down shows up there.</para>
        /// </summary>
        private static string BuildGcJson()
        {
            // The richer counters are net5+; this assembly also targets netstandard2.1 for the Unity
            // package, where the health endpoint does not run but still has to compile.
            string extra = string.Empty;
#if NET5_0_OR_GREATER
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            extra = ",\"allocatedMb\":" + Num(GC.GetTotalAllocatedBytes(precise: false) / 1048576.0, "F1") +
                    ",\"committedMb\":" + Num(info.TotalCommittedBytes / 1048576.0, "F1") +
                    ",\"fragmentedMb\":" + Num(info.FragmentedBytes / 1048576.0, "F1") +
                    ",\"pauseTimePercent\":" + Num(info.PauseTimePercentage, "F3");
#endif
            return "{" +
                   "\"gen0\":" + Int(GC.CollectionCount(0)) +
                   ",\"gen1\":" + Int(GC.CollectionCount(1)) +
                   ",\"gen2\":" + Int(GC.CollectionCount(2)) +
                   ",\"heapMb\":" + Num(GC.GetTotalMemory(forceFullCollection: false) / 1048576.0, "F1") +
                   extra +
                   ",\"serverGc\":" + (GCSettings.IsServerGC ? "true" : "false") +
                   ",\"latencyMode\":\"" + GCSettings.LatencyMode + "\"" +
                   "}";
        }

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
              // The send phase, in the terms its worker count is actually sized in. On the always-on
              // half of this payload rather than behind the profiler window, because the width is
              // steered from these on every server and the benchmark fits BSRSendPhaseBudgetPercent
              // from them - a reading that only exists when someone has switched profiling on is one
              // neither of those can rely on. sendDuty x sendBudgetPercent is the send pass's share
              // of the whole period; tickMs/intervalMs minus that is what the rest of the tick costs,
              // which is the quantity the budget share is a complement of.
              .Append(",\"sendWorkers\":").Append(Int(BasisServerReductionSystemEvents.SendWorkers))
              .Append(",\"sendWorkerCap\":").Append(Int(BasisServerReductionSystemEvents.SendWorkerCeiling))
              .Append(",\"sendBudgetPercent\":").Append(Int(BasisServerReductionSystemEvents.SendPhaseBudgetPercent))
              .Append(",\"sendDuty\":").Append(Num(BasisServerReductionSystemEvents.SendBudgetDuty, "F4"))
              .Append(",\"pairsPerWorkerMs\":").Append(Num(BasisServerReductionSystemEvents.PairsPerWorkerMs, "F2"))
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
              // Zstd half of the hybrid codec, broken out so a run can be judged on what the
              // two codecs each cost and returned rather than on the blended average — which
              // moves whenever the keyframe/delta traffic mix does, independently of either
              // codec getting better or worse. "dictGeneration":0 means no dictionary is
              // embedded and the Zstd path is inert.
              .Append(",\"zstd\":{")
              .Append("\"dictGeneration\":").Append(Int(BasisAvatarBundleZstd.DictionaryGeneration))
              .Append(",\"emitted\":").Append(Int(s.BundleZstdEmitted))
              .Append(",\"shareOfBundles\":").Append(Num(s.BundlesEmitted > 0 ? (double)s.BundleZstdEmitted / s.BundlesEmitted : 0, "F4"))
              .Append(",\"rawBytes\":").Append(Int(s.BundleZstdRawBytes))
              .Append(",\"compressedBytes\":").Append(Int(s.BundleZstdCompressedBytes))
              .Append(",\"ratio\":").Append(Num(s.BundleZstdRawBytes > 0 ? (double)s.BundleZstdCompressedBytes / s.BundleZstdRawBytes : 0, "F4"))
              .Append(",\"msPerTick\":").Append(Num(s.BundleZstdMs / ticks, "F4"))
              .Append(",\"avgUs\":").Append(Num(s.BundleZstdEmitted > 0 ? (s.BundleZstdMs * 1000.0) / s.BundleZstdEmitted : 0, "F2"))
              // LZ4's share is the remainder of the totals above, reported explicitly so the
              // two codecs can be compared without the reader having to subtract.
              .Append(",\"lz4Ratio\":").Append(Num(s.BundleRawBytes - s.BundleZstdRawBytes > 0
                  ? (double)(s.BundleCompressedBytes - s.BundleZstdCompressedBytes) / (s.BundleRawBytes - s.BundleZstdRawBytes) : 0, "F4"))
              .Append(",\"lz4AvgUs\":").Append(Num(s.BundlesEmitted - s.BundleZstdEmitted > 0
                  ? ((s.BundleDeflateMs - s.BundleZstdMs) * 1000.0) / (s.BundlesEmitted - s.BundleZstdEmitted) : 0, "F2"))
              .Append('}')
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
            _semaphore.Dispose();
        }
    }
}
