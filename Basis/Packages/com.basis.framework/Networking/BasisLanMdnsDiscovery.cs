using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Scripts.Networking
{
    /// <summary>Small dual-stack UDP/5353 transport used only by Basis LAN discovery.</summary>
    internal sealed class BasisLanMdnsTransport : IDisposable
    {
        private readonly object _sendGate = new object();
        private readonly Action<byte[], IPEndPoint> _received;
        private readonly List<IPAddress> _ipv4Interfaces = new List<IPAddress>();
        private readonly List<int> _ipv6Interfaces = new List<int>();
        private UdpClient _ipv4;
        private UdpClient _ipv6;
        private int _disposed;

        public BasisLanMdnsTransport(Action<byte[], IPEndPoint> received)
        {
            _received = received ?? throw new ArgumentNullException(nameof(received));
            DiscoverInterfaces();

            if (Socket.OSSupportsIPv4)
            {
                try { _ipv4 = CreateIpv4(); }
                catch (Exception ex) { BasisDebug.LogWarning($"IPv4 mDNS unavailable: {ex.Message}"); }
            }
            if (Socket.OSSupportsIPv6)
            {
                try { _ipv6 = CreateIpv6(); }
                catch (Exception ex) { BasisDebug.LogWarning($"IPv6 mDNS unavailable: {ex.Message}"); }
            }
            if (_ipv4 == null && _ipv6 == null)
            {
                throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            }

            if (_ipv4 != null) _ = Task.Run(() => ReceiveLoopAsync(_ipv4));
            if (_ipv6 != null) _ = Task.Run(() => ReceiveLoopAsync(_ipv6));
        }

        public void SendMulticast(byte[] packet)
        {
            if (packet == null || packet.Length == 0 || Volatile.Read(ref _disposed) != 0) return;
            lock (_sendGate)
            {
                SendIpv4(packet);
                SendIpv6(packet);
            }
        }

        public void SendUnicast(byte[] packet, IPEndPoint endpoint)
        {
            if (packet == null || endpoint == null || Volatile.Read(ref _disposed) != 0) return;
            lock (_sendGate)
            {
                try
                {
                    UdpClient client = endpoint.AddressFamily == AddressFamily.InterNetwork ? _ipv4 : _ipv6;
                    client?.Send(packet, packet.Length, endpoint);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private UdpClient CreateIpv4()
        {
            UdpClient client = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                Shared(client);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, BasisLanMdnsWire.Port));
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

                bool joined = false;
                foreach (IPAddress address in _ipv4Interfaces)
                {
                    try
                    {
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                            new MulticastOption(BasisLanMdnsWire.MulticastAddress, address));
                        joined = true;
                    }
                    catch (SocketException) { }
                }
                if (!joined) client.JoinMulticastGroup(BasisLanMdnsWire.MulticastAddress);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private UdpClient CreateIpv6()
        {
            UdpClient client = new UdpClient(AddressFamily.InterNetworkV6);
            try
            {
                Shared(client);
                client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, BasisLanMdnsWire.Port));
                client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
                client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);
                IPAddress group = IPAddress.Parse("ff02::fb");

                bool joined = false;
                foreach (int index in _ipv6Interfaces)
                {
                    try
                    {
                        client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                            new IPv6MulticastOption(group, index));
                        joined = true;
                    }
                    catch (SocketException) { }
                }
                if (!joined) client.JoinMulticastGroup(group);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private static void Shared(UdpClient client)
        {
            try { client.Client.ExclusiveAddressUse = false; }
            catch (PlatformNotSupportedException) { }
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        private void SendIpv4(byte[] packet)
        {
            if (_ipv4 == null) return;
            IPEndPoint endpoint = new IPEndPoint(BasisLanMdnsWire.MulticastAddress, BasisLanMdnsWire.Port);
            bool sent = false;
            foreach (IPAddress address in _ipv4Interfaces)
            {
                try
                {
                    _ipv4.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    _ipv4.Send(packet, packet.Length, endpoint);
                    sent = true;
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
            if (!sent)
            {
                try { _ipv4.Send(packet, packet.Length, endpoint); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private void SendIpv6(byte[] packet)
        {
            if (_ipv6 == null) return;
            IPAddress group = IPAddress.Parse("ff02::fb");
            bool sent = false;
            foreach (int index in _ipv6Interfaces)
            {
                try
                {
                    _ipv6.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
                    IPAddress scoped = new IPAddress(group.GetAddressBytes(), index);
                    _ipv6.Send(packet, packet.Length, new IPEndPoint(scoped, BasisLanMdnsWire.Port));
                    sent = true;
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
            if (!sent)
            {
                try { _ipv6.Send(packet, packet.Length, new IPEndPoint(group, BasisLanMdnsWire.Port)); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private async Task ReceiveLoopAsync(UdpClient client)
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    UdpReceiveResult result = await client.ReceiveAsync().ConfigureAwait(false);
                    if (result.RemoteEndPoint != null && result.Buffer != null)
                        _received(result.Buffer, result.RemoteEndPoint);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException)
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _disposed) == 0)
                        BasisDebug.LogWarning($"LAN mDNS receive failed: {ex.Message}");
                }
            }
        }

        private void DiscoverInterfaces()
        {
            HashSet<IPAddress> ipv4 = new HashSet<IPAddress>();
            HashSet<int> ipv6 = new HashSet<int>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties properties = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation info in properties.UnicastAddresses)
                    {
                        if (info.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(info.Address))
                            ipv4.Add(info.Address);
                    }
                    try
                    {
                        IPv6InterfaceProperties v6 = properties.GetIPv6Properties();
                        if (v6 != null && v6.Index > 0) ipv6.Add(v6.Index);
                    }
                    catch (Exception ex) when (ex is NetworkInformationException || ex is PlatformNotSupportedException || ex is NotImplementedException) { }
                }
            }
            catch (Exception) { }
            _ipv4Interfaces.AddRange(ipv4);
            _ipv6Interfaces.AddRange(ipv6);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _ipv4?.Close(); } catch (ObjectDisposedException) { }
            try { _ipv6?.Close(); } catch (ObjectDisposedException) { }
            _ipv4 = null;
            _ipv6 = null;
        }
    }

    internal sealed class BasisLanMdnsBrowser : IDisposable
    {
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly Action<BasisLanDiscoveryProtocol.Advertisement, IPAddress> _found;
        private readonly Action<Guid> _removed;
        private readonly BasisLanMdnsTransport _transport;
        private bool _disposed;

        public BasisLanMdnsBrowser(Action<BasisLanDiscoveryProtocol.Advertisement, IPAddress> found, Action<Guid> removed)
        {
            _found = found ?? throw new ArgumentNullException(nameof(found));
            _removed = removed ?? throw new ArgumentNullException(nameof(removed));
            try
            {
                _transport = new BasisLanMdnsTransport(OnPacket);
            }
            catch
            {
                _cancellation.Dispose();
                throw;
            }
            _ = Task.Run(() => QueryLoopAsync(_cancellation.Token));
        }

        private async Task QueryLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                byte[] query = BasisLanMdnsWire.BuildQuery();
                while (!cancellationToken.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        if (_disposed) return;
                        _transport.SendMulticast(query);
                    }
                    await Task.Delay(BasisLanMdnsWire.QueryIntervalMs, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    BasisDebug.LogWarning($"LAN mDNS discovery stopped: {ex.Message}");
            }
        }

        private void OnPacket(byte[] packet, IPEndPoint remote)
        {
            if (!BasisLanMdnsWire.TryParse(packet, out BasisLanMdnsWire.Message message) || !message.IsResponse) return;
            lock (_gate)
            {
                if (_disposed) return;
                BasisLanMdnsWire.Extract(message, remote, _found, _removed);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
                _transport.Dispose();
                _cancellation.Dispose();
            }
        }
    }
}
