using Basis.Network.Core;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Fires a single unconnected "server info" probe at a server's listening port and
    /// awaits the response — the LiteNetLib equivalent of a Minecraft Server List Ping.
    /// Each query spins up its own ephemeral LiteNetLib NetManager so the active
    /// connection (if any) is left alone, then tears it down on completion or timeout.
    /// </summary>
    public static class BasisServerInfoClient
    {
        public class ServerInfo
        {
            public ushort Online;
            public ushort Max;
            public ushort ProtocolVersion;
            public string Name;
            public string Motd;
            public int RoundTripMs;
            public IPEndPoint Endpoint;
        }

        public class ServerInfoResult
        {
            public ServerInfo Info;
            public string Error;
            public bool TimedOut;
            public bool Reachable => Info != null;
        }

        public static async Task<ServerInfoResult> QueryAsync(string host, ushort port, int timeoutMs = 3000, CancellationToken ct = default)
        {
            var result = new ServerInfoResult();
            if (string.IsNullOrWhiteSpace(host))
            {
                result.Error = "Host is empty";
                return result;
            }

            EventBasedNetListener listener = new EventBasedNetListener();
            LNLNetManager manager = new LNLNetManager(listener, new Configuration());

            ushort nonce;
            unchecked { nonce = (ushort)Guid.NewGuid().GetHashCode(); }

            TaskCompletionSource<ServerInfo> tcs = new TaskCompletionSource<ServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            Stopwatch rtt = new Stopwatch();

            void OnReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader)
            {
                try
                {
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

                    tcs.TrySetResult(new ServerInfo
                    {
                        Online = online,
                        Max = max,
                        ProtocolVersion = proto,
                        Name = name,
                        Motd = motd,
                        RoundTripMs = (int)rtt.ElapsedMilliseconds,
                        Endpoint = remoteEndPoint,
                    });
                }
                catch
                {
                    // Malformed response — let the timeout path handle it.
                }
                finally
                {
                    reader.Recycle(true);
                }
            }

            listener.NetworkReceiveUnconnectedEvent += OnReceiveUnconnected;

            try
            {
                IPAddress address = await ResolveHostAsync(host, ct).ConfigureAwait(false);
                if (address == null)
                {
                    result.Error = "DNS resolution failed";
                    return result;
                }

                IPEndPoint endpoint = new IPEndPoint(address, port);

                // Bind to an OS-picked ephemeral port — the response endpoint is the
                // server's listening port, but our outbound port doesn't matter.
                manager.Start(IPAddress.Any, IPAddress.IPv6Any, 0);

                NetDataWriter writer = new NetDataWriter(true, BasisNetworkCommons.ServerInfoMinRequestBytes);
                writer.Put(BasisNetworkCommons.ServerInfoQueryMagic);
                writer.Put(BasisNetworkCommons.ServerInfoProtocolVersion);
                writer.Put(nonce);
                // Pad up to the server's minimum-request threshold. The server drops
                // anything smaller — the padding is what makes this query unattractive
                // as a UDP reflection/amplification vector (response size <= request size).
                int padBytes = BasisNetworkCommons.ServerInfoMinRequestBytes - writer.Length;
                if (padBytes > 0) writer.Put(new byte[padBytes]);

                rtt.Start();
                if (!manager.SendUnconnectedMessage(writer, endpoint))
                {
                    result.Error = "Send failed";
                    return result;
                }

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                using CancellationTokenRegistration reg = cts.Token.Register(() => tcs.TrySetCanceled());

                try
                {
                    result.Info = await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    result.TimedOut = true;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                listener.NetworkReceiveUnconnectedEvent -= OnReceiveUnconnected;
                try { manager.Stop(); } catch { /* swallow shutdown noise */ }
            }

            return result;
        }

        private static async Task<IPAddress> ResolveHostAsync(string host, CancellationToken ct)
        {
            if (IPAddress.TryParse(host, out IPAddress parsed))
                return parsed;

            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            if (addresses == null || addresses.Length == 0)
                return null;

            foreach (IPAddress a in addresses)
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;

            foreach (IPAddress a in addresses)
                if (a.AddressFamily == AddressFamily.InterNetworkV6) return a;

            return addresses[0];
        }
    }
}
