using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    /// <summary>Shared dual-stack UDP/5353 transport for Basis mDNS browsing and advertising.</summary>
    public sealed class BasisLanMdnsTransport : IDisposable
    {
        public const int Port = 5353;
        public static readonly IPAddress Ipv4MulticastAddress = IPAddress.Parse("224.0.0.251");
        public static readonly IPAddress Ipv6MulticastAddress = IPAddress.Parse("ff02::fb");

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
                catch (Exception ex) { BNL.LogWarning($"IPv4 mDNS unavailable: {ex.Message}"); }
            }
            if (Socket.OSSupportsIPv6)
            {
                try { _ipv6 = CreateIpv6(); }
                catch (Exception ex) { BNL.LogWarning($"IPv6 mDNS unavailable: {ex.Message}"); }
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
            if (packet == null || packet.Length == 0 || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            lock (_sendGate)
            {
                SendIpv4(packet);
                SendIpv6(packet);
            }
        }

        public void SendUnicast(byte[] packet, IPEndPoint endpoint)
        {
            if (packet == null || packet.Length == 0 || endpoint == null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

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
                ConfigureShared(client);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

                bool joined = false;
                foreach (IPAddress address in _ipv4Interfaces)
                {
                    try
                    {
                        client.Client.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.AddMembership,
                            new MulticastOption(Ipv4MulticastAddress, address));
                        joined = true;
                    }
                    catch (SocketException) { }
                }
                if (!joined)
                {
                    client.JoinMulticastGroup(Ipv4MulticastAddress);
                }
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
                ConfigureShared(client);
                client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, Port));
                client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
                client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);

                bool joined = false;
                foreach (int index in _ipv6Interfaces)
                {
                    try
                    {
                        client.Client.SetSocketOption(
                            SocketOptionLevel.IPv6,
                            SocketOptionName.AddMembership,
                            new IPv6MulticastOption(Ipv6MulticastAddress, index));
                        joined = true;
                    }
                    catch (SocketException) { }
                }
                if (!joined)
                {
                    client.JoinMulticastGroup(Ipv6MulticastAddress);
                }
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private static void ConfigureShared(UdpClient client)
        {
            try { client.Client.ExclusiveAddressUse = false; }
            catch (PlatformNotSupportedException) { }
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        private void SendIpv4(byte[] packet)
        {
            if (_ipv4 == null)
            {
                return;
            }

            IPEndPoint endpoint = new IPEndPoint(Ipv4MulticastAddress, Port);
            bool sent = false;
            foreach (IPAddress address in _ipv4Interfaces)
            {
                try
                {
                    _ipv4.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface,
                        address.GetAddressBytes());
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
            if (_ipv6 == null)
            {
                return;
            }

            bool sent = false;
            foreach (int index in _ipv6Interfaces)
            {
                try
                {
                    _ipv6.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
                    IPAddress scoped = new IPAddress(Ipv6MulticastAddress.GetAddressBytes(), index);
                    _ipv6.Send(packet, packet.Length, new IPEndPoint(scoped, Port));
                    sent = true;
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
            if (!sent)
            {
                try { _ipv6.Send(packet, packet.Length, new IPEndPoint(Ipv6MulticastAddress, Port)); }
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
                    {
                        _received(result.Buffer, result.RemoteEndPoint);
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        BNL.LogWarning($"LAN mDNS receive failed: {ex.Message}");
                    }
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
                    if (nic.OperationalStatus != OperationalStatus.Up
                        || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    IPInterfaceProperties properties = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation info in properties.UnicastAddresses)
                    {
                        if (info.Address.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(info.Address))
                        {
                            ipv4.Add(info.Address);
                        }
                    }
                    try
                    {
                        IPv6InterfaceProperties v6 = properties.GetIPv6Properties();
                        if (v6 != null && v6.Index > 0)
                        {
                            ipv6.Add(v6.Index);
                        }
                    }
                    catch (Exception ex) when (ex is NetworkInformationException
                                               || ex is PlatformNotSupportedException
                                               || ex is NotImplementedException)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }

            _ipv4Interfaces.AddRange(ipv4);
            _ipv6Interfaces.AddRange(ipv6);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try { _ipv4?.Close(); }
            catch (ObjectDisposedException) { }
            try { _ipv6?.Close(); }
            catch (ObjectDisposedException) { }
            _ipv4 = null;
            _ipv6 = null;
        }
    }
}
