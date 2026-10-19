using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace Basis.Network.Core
{
    public sealed class BasisMultiTransportNetManager : NetManager
    {
        private const int MaxUnconnectedRoutes = 4096;
        private static readonly long UnconnectedRouteLifetimeTicks = 30L * Stopwatch.Frequency;
        private readonly NetManager[] _transports;
        private readonly string[] _stackIds;
        private readonly BasisPeerIdAllocator _peerIds;
        private readonly ConcurrentDictionary<IPEndPoint, Route> _unconnectedRoutes = new ConcurrentDictionary<IPEndPoint, Route>();

        private readonly struct Route
        {
            public readonly NetManager Transport;
            public readonly long Stamp;
            public Route(NetManager transport, long stamp)
            {
                Transport = transport;
                Stamp = stamp;
            }
        }

        public BasisMultiTransportNetManager(IReadOnlyList<string> stackIds, EventBasedNetListener listener, Configuration configuration, Func<string, EventBasedNetListener, Configuration, NetManager> create, BasisPeerIdAllocator peerIds = null)
        {
            if (stackIds == null || stackIds.Count == 0) throw new ArgumentException("At least one transport is required", nameof(stackIds));
            if (listener == null) throw new ArgumentNullException(nameof(listener));
            if (create == null) throw new ArgumentNullException(nameof(create));
            _peerIds = peerIds ?? new BasisPeerIdAllocator();
            _transports = new NetManager[stackIds.Count];
            _stackIds = new string[stackIds.Count];
            for (int index = 0; index < stackIds.Count; index++)
            {
                string stackId = stackIds[index];
                EventBasedNetListener inner = new EventBasedNetListener();
                NetManager transport = create(stackId, inner, configuration);
                if (transport == null) throw new InvalidOperationException($"Transport '{stackId}' could not be created");
                if (transport is IBasisSharedPeerIds shared)
                {
                    shared.UsePeerIdAllocator(_peerIds);
                }
                else
                {
                    BNL.LogWarning($"Transport '{stackId}' does not take shared player ids; its ids may collide with another transport's.");
                }
                Forward(inner, listener, transport);
                _transports[index] = transport;
                _stackIds[index] = stackId;
            }
        }

        public IReadOnlyList<NetManager> Transports => _transports;

        public IReadOnlyList<string> TransportStackIds => _stackIds;

        public BasisPeerIdAllocator PeerIds => _peerIds;

        public string StackId => string.Join(",", _stackIds);

        private void Forward(EventBasedNetListener inner, EventBasedNetListener outer, NetManager transport)
        {
            inner.ConnectionRequestEvent += outer.RaiseConnectionRequest;
            inner.PeerConnectedEvent += outer.RaisePeerConnected;
            inner.PeerDisconnectedEvent += outer.RaisePeerDisconnected;
            inner.NetworkReceiveEvent += outer.RaiseNetworkReceive;
            inner.NetworkErrorEvent += outer.RaiseNetworkError;
            inner.NetworkReceiveUnconnectedEvent += (endPoint, reader) =>
            {
                RememberRoute(endPoint, transport);
                outer.RaiseNetworkReceiveUnconnected(endPoint, reader);
            };
        }

        private void RememberRoute(IPEndPoint endPoint, NetManager transport)
        {
            if (endPoint == null) return;
            long now = Stopwatch.GetTimestamp();
            if (_unconnectedRoutes.Count >= MaxUnconnectedRoutes)
            {
                foreach (KeyValuePair<IPEndPoint, Route> entry in _unconnectedRoutes)
                {
                    if (now - entry.Value.Stamp > UnconnectedRouteLifetimeTicks)
                    {
                        _unconnectedRoutes.TryRemove(entry.Key, out _);
                    }
                }
                if (_unconnectedRoutes.Count >= MaxUnconnectedRoutes)
                {
                    _unconnectedRoutes.Clear();
                }
            }
            _unconnectedRoutes[endPoint] = new Route(transport, now);
        }

        public bool TryGetTransport<T>(out T transport) where T : class, NetManager
        {
            foreach (NetManager candidate in _transports)
            {
                if (candidate is T typed)
                {
                    transport = typed;
                    return true;
                }
            }
            transport = null;
            return false;
        }

        public NetManager GetTransport(string stackId)
        {
            for (int index = 0; index < _stackIds.Length; index++)
            {
                if (string.Equals(_stackIds[index], stackId, StringComparison.OrdinalIgnoreCase)) return _transports[index];
            }
            return null;
        }

        public void Start(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            foreach (NetManager transport in _transports)
            {
                transport.Start(IPv4Address, IPv6Address, SetPort);
            }
        }

        public void StartManual(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            foreach (NetManager transport in _transports)
            {
                transport.StartManual(IPv4Address, IPv6Address, SetPort);
            }
        }

        public void PollEvents()
        {
            foreach (NetManager transport in _transports)
            {
                transport.PollEvents();
            }
        }

        public void ManualUpdate(float elapsedMilliseconds)
        {
            foreach (NetManager transport in _transports)
            {
                transport.ManualUpdate(elapsedMilliseconds);
            }
        }

        public void Flush()
        {
            foreach (NetManager transport in _transports)
            {
                transport.Flush();
            }
        }

        public void Stop()
        {
            foreach (NetManager transport in _transports)
            {
                try
                {
                    transport.Stop();
                }
                catch (Exception ex)
                {
                    BNL.LogWarning($"Stopping transport '{transport.StackId}' failed: {ex.Message}");
                }
            }
            _unconnectedRoutes.Clear();
            _peerIds.Reset();
        }

        public NetPeer Connect(string sIP, int port, NetDataWriter Writer)
        {
            return _transports[0].Connect(sIP, port, Writer);
        }

        public bool SendUnconnectedMessage(NetDataWriter writer, IPEndPoint remoteEndPoint)
        {
            if (remoteEndPoint != null && _unconnectedRoutes.TryGetValue(remoteEndPoint, out Route route))
            {
                return route.Transport.SendUnconnectedMessage(writer, remoteEndPoint);
            }
            return _transports[0].SendUnconnectedMessage(writer, remoteEndPoint);
        }

        public NetStatistics Statistics
        {
            get
            {
                NetStatistics total = new NetStatistics();
                foreach (NetManager transport in _transports)
                {
                    NetStatistics stats = transport.Statistics;
                    if (stats == null) continue;
                    total.PacketsSent += stats.PacketsSent;
                    total.PacketsReceived += stats.PacketsReceived;
                    total.BytesSent += stats.BytesSent;
                    total.BytesReceived += stats.BytesReceived;
                    total.PacketLoss += stats.PacketLoss;
                }
                return total;
            }
        }

        public int ConnectedPeersCount
        {
            get
            {
                int total = 0;
                foreach (NetManager transport in _transports)
                {
                    total += transport.ConnectedPeersCount;
                }
                return total;
            }
        }

        public long UnreliableDropped
        {
            get
            {
                long total = 0;
                foreach (NetManager transport in _transports)
                {
                    total += transport.UnreliableDropped;
                }
                return total;
            }
        }

        public long PriorityUnreliableDropped
        {
            get
            {
                long total = 0;
                foreach (NetManager transport in _transports)
                {
                    total += transport.PriorityUnreliableDropped;
                }
                return total;
            }
        }

        public bool IsRunning
        {
            get
            {
                foreach (NetManager transport in _transports)
                {
                    if (!transport.IsRunning) return false;
                }
                return true;
            }
        }

        public string ListenDescription
        {
            get
            {
                StringBuilder builder = new StringBuilder();
                foreach (NetManager transport in _transports)
                {
                    string description = transport.ListenDescription;
                    if (string.IsNullOrEmpty(description)) continue;
                    if (builder.Length > 0) builder.Append(", ");
                    builder.Append(description);
                }
                return builder.Length == 0 ? null : builder.ToString();
            }
        }
    }

    public static class BasisNetManagerTransports
    {
        public static T FindTransport<T>(this NetManager manager) where T : class, NetManager
        {
            if (manager is T direct) return direct;
            if (manager is BasisMultiTransportNetManager multi && multi.TryGetTransport(out T inner)) return inner;
            return null;
        }

        public static IReadOnlyList<NetManager> Transports(this NetManager manager)
        {
            if (manager == null) return Array.Empty<NetManager>();
            if (manager is BasisMultiTransportNetManager multi) return multi.Transports;
            return new[] { manager };
        }

        public static T FindCapability<T>(this NetManager manager) where T : class
        {
            foreach (NetManager transport in manager.Transports())
            {
                if (transport is T capability) return capability;
            }
            return null;
        }
    }
}
