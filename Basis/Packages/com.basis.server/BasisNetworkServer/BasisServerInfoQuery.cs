using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace BasisServerHandle
{
    /// <summary>
    /// Handles unconnected "server info" probes — the LiteNetLib UDP equivalent of a
    /// Minecraft server-list-ping. A client sends a tiny query packet to the server's
    /// listening port and gets back the public server name, current/max player count,
    /// MOTD, and the original nonce so the client can measure RTT.
    ///
    /// Wire format (little-endian):
    ///   Query:    [u32 ServerInfoQueryMagic][u16 protoVersion][u16 nonce][padding ≥ ServerInfoMinRequestBytes total]
    ///   Response: [u32 ServerInfoResponseMagic][u16 protoVersion][u16 nonce]
    ///             [u16 online][u16 max][string name][string motd]
    ///
    /// DDoS protections:
    ///   1. <b>Minimum request size</b> — undersized packets (which is what a reflection
    ///      attacker would spoof to maximize amplification) are dropped before any work.
    ///      Clients pad their query so the response is never larger than the request,
    ///      so the amplification factor is &lt; 1.
    ///   2. <b>Global token bucket</b> — caps the total responses-per-second the server
    ///      will emit, regardless of source. Bounds the worst-case outbound bandwidth
    ///      even if every protection above is bypassed.
    ///   3. <b>Bounded per-IP throttle</b> — one response per IP per <see cref="MinIntervalMs"/>;
    ///      the tracking map is capped so spoofed-IP floods can't exhaust memory.
    /// </summary>
    public static class BasisServerInfoQuery
    {
        // --- Per-IP throttle ---
        // One response per IP per window. Doesn't help against spoofed source IPs
        // (the global bucket below handles those), but it stops a single client from
        // monopolizing the response budget.
        private const int MinIntervalMs = 500;
        // Cap memory under spoofed-IP floods. When the dict crosses this size we wipe
        // it — a one-shot reset is rough but bounded and lock-free.
        private const int MaxTrackedIps = 4096;
        private static readonly ConcurrentDictionary<IPAddress, long> _lastSeen = new();

        // --- Global token bucket ---
        // Caps responses/sec across every source. With 384-byte responses at 100 rps
        // the worst-case outbound is ~38 KB/s — small enough to ignore.
        private const double GlobalRefillTokensPerSecond = 100.0;
        private const double GlobalBucketCapacity = 200.0; // burst budget
        private static readonly object _bucketLock = new();
        private static double _tokens = GlobalBucketCapacity;
        private static long _lastRefillTicks;

        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        public static void Subscribe()
        {
            NetworkServer.Listener.NetworkReceiveUnconnectedEvent += HandleQuery;
        }

        public static void Unsubscribe()
        {
            NetworkServer.Listener.NetworkReceiveUnconnectedEvent -= HandleQuery;
        }

        private static void HandleQuery(IPEndPoint remoteEndPoint, NetPacketReader reader)
        {
            try
            {
                // Layer 1 — minimum request size. Drop tiny packets before anything else;
                // they're the cheapest amplification ammo and there's no legitimate reason
                // to send one smaller than ServerInfoMinRequestBytes.
                int totalBytes = reader.AvailableBytes;
                if (totalBytes < BasisNetworkCommons.ServerInfoMinRequestBytes)
                {
                    reader.Recycle(true);
                    return;
                }

                if (totalBytes < 8)
                {
                    reader.Recycle(true);
                    return;
                }

                uint magic = reader.GetUInt();
                if (magic != BasisNetworkCommons.ServerInfoQueryMagic)
                {
                    reader.Recycle(true);
                    return;
                }

                ushort _protoVersion = reader.GetUShort();
                ushort nonce = reader.GetUShort();
                reader.Recycle(true);

                // Layer 2 — per-IP cooldown.
                if (!ShouldRespondPerIp(remoteEndPoint.Address))
                    return;

                // Layer 3 — global response-rate cap.
                if (!TryConsumeGlobalToken())
                    return;

                Configuration cfg = NetworkServer.Configuration;
                int online = NetworkServer.AuthenticatedPeers.Count;
                int max = cfg != null ? cfg.PeerLimit : 0;
                string serverName = cfg?.ServerName ?? string.Empty;
                string motd = cfg?.ServerMotd ?? string.Empty;

                NetDataWriter writer = NetworkServer.RentWriter();
                try
                {
                    writer.Put(BasisNetworkCommons.ServerInfoResponseMagic);
                    writer.Put(BasisNetworkCommons.ServerInfoProtocolVersion);
                    writer.Put(nonce);
                    writer.Put((ushort)Math.Min(online, ushort.MaxValue));
                    writer.Put((ushort)Math.Min(Math.Max(max, 0), ushort.MaxValue));
                    writer.Put(serverName, BasisNetworkCommons.ServerInfoNameMaxLength);
                    writer.Put(motd, BasisNetworkCommons.ServerInfoMotdMaxLength);
                    NetworkServer.Server.SendUnconnectedMessage(writer, remoteEndPoint);
                }
                finally
                {
                    NetworkServer.ReturnWriter(writer);
                }
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"ServerInfoQuery failed: {ex.Message}");
            }
        }

        private static bool ShouldRespondPerIp(IPAddress address)
        {
            // If a flood of unique source IPs is filling the dict, wipe it. Crude but
            // bounded and avoids the per-eviction allocation cost of an LRU. Worst case
            // a flushed entry forfeits its cooldown — acceptable since the global bucket
            // is still in place.
            if (_lastSeen.Count > MaxTrackedIps)
            {
                _lastSeen.Clear();
            }

            long nowMs = _clock.ElapsedMilliseconds;
            long previous = _lastSeen.GetOrAdd(address, 0L);
            if (previous != 0 && nowMs - previous < MinIntervalMs) return false;
            _lastSeen[address] = nowMs;
            return true;
        }

        private static bool TryConsumeGlobalToken()
        {
            lock (_bucketLock)
            {
                long nowTicks = _clock.ElapsedTicks;
                if (_lastRefillTicks == 0) _lastRefillTicks = nowTicks;

                double secondsElapsed = (nowTicks - _lastRefillTicks) / (double)Stopwatch.Frequency;
                if (secondsElapsed > 0)
                {
                    _tokens = Math.Min(GlobalBucketCapacity, _tokens + secondsElapsed * GlobalRefillTokensPerSecond);
                    _lastRefillTicks = nowTicks;
                }

                if (_tokens < 1.0) return false;
                _tokens -= 1.0;
                return true;
            }
        }
    }
}
