using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    /// <summary>
    /// The LiteNetLib server-info probe, registered by <see cref="BasisLiteNetLibTransport"/>.
    /// Fires a single unconnected "server info" packet at the listening port (the LiteNetLib
    /// equivalent of a Minecraft Server List Ping) and awaits the response.
    ///
    /// When the hostname resolves to both AAAA and A records, IPv6 is attempted first with
    /// half the total timeout budget. If IPv6 times out or fails the probe falls through to
    /// IPv4 with the remaining budget. The winning <see cref="IPAddress"/> is stored in
    /// <see cref="ServerProbeResult.ResolvedAddress"/> so callers can connect directly to the
    /// confirmed-reachable address family without re-resolving DNS.
    /// </summary>
    public static class BasisLiteNetLibProbe
    {
        public static async Task<ServerProbeResult> ProbeAsync(ConnectionTarget target, int timeoutMs, CancellationToken ct)
        {
            ServerProbeResult fail = new ServerProbeResult();
            if (target == null) { fail.Error = "Target is null"; return fail; }

            string host = target.Get(ConnectionTarget.Keys.Address, string.Empty);
            if (string.IsNullOrWhiteSpace(host)) { fail.Error = "Host is empty"; return fail; }

            string portString = target.Get(ConnectionTarget.Keys.Port,
                HostPortConnectionTargetParser.DefaultPort.ToString(CultureInfo.InvariantCulture));
            if (!ushort.TryParse(portString, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort port) || port == 0)
            {
                fail.Error = "Port is invalid";
                return fail;
            }

            // Resolve all addresses upfront so we can pick the right family.
            IPAddress[] ipv6, ipv4;
            try
            {
                ResolvedAddresses resolved = await ResolveAllAsync(host, ct).ConfigureAwait(false);
                ipv6 = resolved.IPv6;
                ipv4 = resolved.IPv4;
            }
            catch (Exception ex)
            {
                fail.Error = "DNS resolution failed: " + ex.Message;
                return fail;
            }

            if (ipv6.Length == 0 && ipv4.Length == 0)
            {
                fail.Error = "DNS resolution returned no addresses";
                return fail;
            }

            // Split budget: half for the IPv6 attempt when both families exist.
            // If only one family is present it gets the full budget.
            bool bothFamilies = ipv6.Length > 0 && ipv4.Length > 0;
            int v6Budget = bothFamilies ? timeoutMs / 2 : timeoutMs;
            int v4Budget = bothFamilies ? Math.Max(timeoutMs - v6Budget, 1000) : timeoutMs;

            EventBasedNetListener listener = new EventBasedNetListener();
            Configuration probeConfig = new Configuration { NetworkStackId = BasisNetworkStackRegistry.LiteNetLibId };
            NetManager manager = BasisNetworkStackRegistry.Create(probeConfig.NetworkStackId, listener, probeConfig);
            // Start with dual-stack so we can send to either address family.
            manager.Start(IPAddress.Any, IPAddress.IPv6Any, 0);

            ushort nonce;
            unchecked { nonce = (ushort)Guid.NewGuid().GetHashCode(); }

            try
            {
                // ── IPv6 first ────────────────────────────────────────────────────────
                if (ipv6.Length > 0)
                {
                    ServerProbeResult r = await SendProbeAsync(
                        listener, manager, nonce, port, ipv6[0], v6Budget, ct).ConfigureAwait(false);
                    if (r.Reachable)
                    {
                        r.ResolvedAddress = ipv6[0];
                        return r;
                    }
                    if (ct.IsCancellationRequested) return r;
                }

                // ── IPv4 fallback ─────────────────────────────────────────────────────
                if (ipv4.Length > 0)
                {
                    ServerProbeResult r = await SendProbeAsync(
                        listener, manager, nonce, port, ipv4[0], v4Budget, ct).ConfigureAwait(false);
                    if (r.Reachable) r.ResolvedAddress = ipv4[0];
                    return r;
                }

                fail.Error = "No reachable address found";
                fail.TimedOut = true;
                return fail;
            }
            finally
            {
                try { manager.Stop(); } catch { }
            }
        }

        /// <summary>
        /// Sends a single probe packet to <paramref name="address"/>:<paramref name="port"/>
        /// and waits up to <paramref name="timeoutMs"/> for a matching response.
        /// The handler also checks the source endpoint to reject stale packets from a
        /// previous attempt that arrive late.
        /// </summary>
        private static async Task<ServerProbeResult> SendProbeAsync(
            EventBasedNetListener listener,
            NetManager manager,
            ushort nonce,
            ushort port,
            IPAddress address,
            int timeoutMs,
            CancellationToken ct)
        {
            IPEndPoint endpoint = new IPEndPoint(address, port);
            TaskCompletionSource<ServerProbeResult> tcs =
                new TaskCompletionSource<ServerProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Stopwatch rtt = new Stopwatch();

            void OnReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader)
            {
                try
                {
                    // Reject packets from the wrong endpoint (stale reply from a prior attempt).
                    if (!remoteEndPoint.Equals(endpoint)) return;
                    if (reader.AvailableBytes < 8) return;

                    uint magic = reader.GetUInt();
                    if (magic != BasisNetworkCommons.ServerInfoResponseMagic) return;

                    ushort proto = reader.GetUShort();
                    ushort returnedNonce = reader.GetUShort();
                    if (returnedNonce != nonce) return;

                    ushort online = reader.GetUShort();
                    ushort max = reader.GetUShort();
                    string name = reader.GetString();
                    string motd = reader.GetString();

                    tcs.TrySetResult(new ServerProbeResult
                    {
                        Reachable = true,
                        Online = online,
                        Max = max,
                        ProtocolVersion = proto,
                        Name = name,
                        Motd = motd,
                        RoundTripMs = (int)rtt.ElapsedMilliseconds,
                    });
                }
                catch { }
                finally { reader.Recycle(true); }
            }

            listener.NetworkReceiveUnconnectedEvent += OnReceiveUnconnected;
            try
            {
                NetDataWriter writer = new NetDataWriter(true, BasisNetworkCommons.ServerInfoMinRequestBytes);
                writer.Put(BasisNetworkCommons.ServerInfoQueryMagic);
                writer.Put(BasisNetworkCommons.ServerInfoProtocolVersion);
                writer.Put(nonce);
                int padBytes = BasisNetworkCommons.ServerInfoMinRequestBytes - writer.Length;
                if (padBytes > 0) writer.Put(new byte[padBytes]);

                rtt.Start();
                if (!manager.SendUnconnectedMessage(writer, endpoint))
                    return new ServerProbeResult { Error = "Send failed" };

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                using CancellationTokenRegistration reg = cts.Token.Register(
                    () => tcs.TrySetCanceled());

                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new ServerProbeResult { TimedOut = true };
                }
            }
            finally
            {
                listener.NetworkReceiveUnconnectedEvent -= OnReceiveUnconnected;
            }
        }

        private readonly struct ResolvedAddresses
        {
            public readonly IPAddress[] IPv6;
            public readonly IPAddress[] IPv4;
            public ResolvedAddresses(IPAddress[] ipv6, IPAddress[] ipv4)
            { IPv6 = ipv6; IPv4 = ipv4; }
        }

        /// <summary>
        /// Resolves <paramref name="host"/> and splits the results into IPv6 and IPv4 buckets.
        /// Literal IP addresses are placed directly into the matching bucket without DNS.
        /// </summary>
        private static async Task<ResolvedAddresses> ResolveAllAsync(string host, CancellationToken ct)
        {
            if (IPAddress.TryParse(host, out IPAddress parsed))
            {
                if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
                    return new ResolvedAddresses(new[] { parsed }, Array.Empty<IPAddress>());
                return new ResolvedAddresses(Array.Empty<IPAddress>(), new[] { parsed });
            }

            IPAddress[] all = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);

            List<IPAddress> v6 = new List<IPAddress>();
            List<IPAddress> v4 = new List<IPAddress>();
            foreach (IPAddress a in all)
            {
                if (a.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(a);
                else if (a.AddressFamily == AddressFamily.InterNetwork) v4.Add(a);
            }
            return new ResolvedAddresses(v6.ToArray(), v4.ToArray());
        }
    }
}
