using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    /// <summary>
    /// Announces a running Basis server on the local network through the Basis LAN
    /// datagram protocol and standard mDNS/DNS-SD. This class has no Unity dependency,
    /// so both dedicated servers and in-client hosts use the same implementation.
    /// </summary>
    public sealed class BasisLanServerAnnouncer : IDisposable
    {
        public const int DiscoveryPort = 42960;
        public const int MdnsPort = 5353;

        private const uint PacketMagic = 0xBA515201u;
        private const ushort PacketVersion = 1;
        private const int InitialAdvertisementDelayMs = 750;
        private const int AdvertisementIntervalMs = 1500;
        private const int MaxStackIdBytes = 64;
        private const int MaxServerNameBytes = 128;
        private const int MaxMotdBytes = 384;
        private const string MdnsServiceType = "_basisvr._udp.local";
        private const string MdnsMetaServiceType = "_services._dns-sd._udp.local";
        private const uint MdnsTtlSeconds = 120;
        private const ushort DnsA = 1;
        private const ushort DnsPtr = 12;
        private const ushort DnsTxt = 16;
        private const ushort DnsAaaa = 28;
        private const ushort DnsSrv = 33;
        private const ushort DnsAny = 255;
        private const ushort DnsIn = 1;
        private const ushort DnsFlushIn = 0x8001;

        private static readonly IPAddress DiscoveryMulticastAddress = IPAddress.Parse("239.255.42.99");
        private static readonly IPAddress MdnsIpv4Address = IPAddress.Parse("224.0.0.251");
        private static readonly IPAddress MdnsIpv6Address = IPAddress.Parse("ff02::fb");

        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly List<IPAddress> _ipv4Interfaces = new List<IPAddress>();
        private readonly List<int> _ipv6Interfaces = new List<int>();
        private readonly List<IPAddress> _advertisedAddresses = new List<IPAddress>();
        private readonly byte[] _customPayload;
        private readonly byte[] _mdnsAnnouncement;
        private readonly byte[] _mdnsMetaResponse;
        private readonly byte[] _mdnsCombinedResponse;
        private readonly byte[] _mdnsGoodbye;
        private readonly string _instanceName;
        private readonly string _hostName;
        private readonly ushort _serverPort;
        private readonly List<string> _txtValues;

        private UdpClient _customSender;
        private UdpClient _mdnsIpv4;
        private UdpClient _mdnsIpv6;
        private long _lastMdnsResponseTicks;
        private int _lastMdnsResponseKey;
        private string _lastMdnsResponseRemote;
        private bool _disposed;

        public Guid InstanceId { get; }
        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return !_disposed;
                }
            }
        }

        public BasisLanServerAnnouncer(
            ushort serverPort,
            string networkStackId,
            string serverName,
            string motd,
            bool requiresPassword)
        {
            if (serverPort == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(serverPort));
            }

            InstanceId = Guid.NewGuid();
            _serverPort = serverPort;
            string id = InstanceId.ToString("N");
            string effectiveStackId = string.IsNullOrWhiteSpace(networkStackId)
                ? BasisNetworkStackRegistry.DefaultId
                : networkStackId;
            string effectiveServerName = string.IsNullOrWhiteSpace(serverName) ? "Basis Server" : serverName;
            string effectiveMotd = motd ?? string.Empty;

            _instanceName = $"Basis-{id}.{MdnsServiceType}";
            _hostName = $"basis-{id}.local";
            _txtValues = new List<string>
            {
                TxtValue("protocol", "1"),
                TxtValue("id", id),
                TxtValue("stack", effectiveStackId),
                TxtValue("name", effectiveServerName),
                TxtValue("motd", effectiveMotd),
                TxtValue("pwd", requiresPassword ? "1" : "0"),
            };

            DiscoverInterfaces();
            _customPayload = BuildCustomPayload(
                InstanceId,
                serverPort,
                requiresPassword,
                effectiveStackId,
                effectiveServerName,
                effectiveMotd);
            _mdnsAnnouncement = BuildMdnsPacket(MdnsTtlSeconds, includeMeta: false, includeService: true);
            _mdnsMetaResponse = BuildMdnsPacket(MdnsTtlSeconds, includeMeta: true, includeService: false);
            _mdnsCombinedResponse = BuildMdnsPacket(MdnsTtlSeconds, includeMeta: true, includeService: true);
            _mdnsGoodbye = BuildMdnsPacket(0, includeMeta: false, includeService: true);

            TryCreateSockets();
            if (_customSender == null && _mdnsIpv4 == null && _mdnsIpv6 == null)
            {
                _cancellation.Dispose();
                throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            }

            if (_mdnsIpv4 != null)
            {
                _ = Task.Run(() => ReceiveMdnsLoopAsync(_mdnsIpv4, _cancellation.Token));
            }
            if (_mdnsIpv6 != null)
            {
                _ = Task.Run(() => ReceiveMdnsLoopAsync(_mdnsIpv6, _cancellation.Token));
            }
            if (_customSender != null)
            {
                _ = Task.Run(() => CustomAdvertisementLoopAsync(_cancellation.Token));
            }
            if (_mdnsIpv4 != null || _mdnsIpv6 != null)
            {
                _ = Task.Run(() => InitialMdnsAnnouncementsAsync(_cancellation.Token));
            }
        }

        private void TryCreateSockets()
        {
            try
            {
                _customSender = new UdpClient(AddressFamily.InterNetwork)
                {
                    EnableBroadcast = true,
                    MulticastLoopback = true,
                };
                _customSender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            }
            catch (Exception ex)
            {
                _customSender?.Dispose();
                _customSender = null;
                BNL.LogWarning($"Basis LAN datagram announcements are unavailable: {ex.Message}");
            }

            if (Socket.OSSupportsIPv4)
            {
                try { _mdnsIpv4 = CreateMdnsIpv4Socket(); }
                catch (Exception ex) { BNL.LogWarning($"IPv4 mDNS announcements are unavailable: {ex.Message}"); }
            }
            if (Socket.OSSupportsIPv6)
            {
                try { _mdnsIpv6 = CreateMdnsIpv6Socket(); }
                catch (Exception ex) { BNL.LogWarning($"IPv6 mDNS announcements are unavailable: {ex.Message}"); }
            }
        }

        private UdpClient CreateMdnsIpv4Socket()
        {
            UdpClient client = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                ConfigureSharedSocket(client);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
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
                            new MulticastOption(MdnsIpv4Address, address));
                        joined = true;
                    }
                    catch (SocketException) { }
                }
                if (!joined)
                {
                    client.JoinMulticastGroup(MdnsIpv4Address);
                }
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private UdpClient CreateMdnsIpv6Socket()
        {
            UdpClient client = new UdpClient(AddressFamily.InterNetworkV6);
            try
            {
                ConfigureSharedSocket(client);
                client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, MdnsPort));
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
                            new IPv6MulticastOption(MdnsIpv6Address, index));
                        joined = true;
                    }
                    catch (SocketException) { }
                }
                if (!joined)
                {
                    client.JoinMulticastGroup(MdnsIpv6Address);
                }
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private static void ConfigureSharedSocket(UdpClient client)
        {
            try { client.Client.ExclusiveAddressUse = false; }
            catch (PlatformNotSupportedException) { }
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        private async Task CustomAdvertisementLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(InitialAdvertisementDelayMs, cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    SendCustomAdvertisement();
                    await Task.Delay(AdvertisementIntervalMs, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    BNL.LogWarning($"Basis LAN announcements stopped: {ex.Message}");
                }
            }
        }

        private async Task InitialMdnsAnnouncementsAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                SendMdnsMulticast(_mdnsAnnouncement);
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                SendMdnsMulticast(_mdnsAnnouncement);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    BNL.LogWarning($"Basis mDNS startup announcement failed: {ex.Message}");
                }
            }
        }

        private async Task ReceiveMdnsLoopAsync(UdpClient client, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult result = await client.ReceiveAsync().ConfigureAwait(false);
                    ProcessMdnsQuery(result.Buffer, result.RemoteEndPoint);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        BNL.LogWarning($"Basis mDNS receive failed: {ex.Message}");
                    }
                }
            }
        }

        private void ProcessMdnsQuery(byte[] packet, IPEndPoint remoteEndPoint)
        {
            if (!TryMatchMdnsQuery(
                    packet,
                    out bool wantsUnicast,
                    out bool includeMeta,
                    out bool includeService,
                    out ushort queryId))
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                int responseKey = (includeMeta ? 1 : 0) | (includeService ? 2 : 0);
                string responseRemote = remoteEndPoint?.ToString() ?? string.Empty;
                long now = Stopwatch.GetTimestamp();
                if (_lastMdnsResponseTicks != 0
                    && _lastMdnsResponseKey == responseKey
                    && string.Equals(_lastMdnsResponseRemote, responseRemote, StringComparison.Ordinal)
                    && now - _lastMdnsResponseTicks < Stopwatch.Frequency / 50)
                {
                    return;
                }
                _lastMdnsResponseTicks = now;
                _lastMdnsResponseKey = responseKey;
                _lastMdnsResponseRemote = responseRemote;

                byte[] response = includeMeta
                    ? (includeService ? _mdnsCombinedResponse : _mdnsMetaResponse)
                    : _mdnsAnnouncement;
                bool legacyUnicast = remoteEndPoint != null && remoteEndPoint.Port != MdnsPort;
                if (legacyUnicast)
                {
                    response = WithDnsId(response, queryId);
                }

                if (wantsUnicast || legacyUnicast)
                {
                    SendMdnsUnicast(response, remoteEndPoint);
                }
                else
                {
                    SendMdnsMulticast(response);
                }
            }
        }

        private void SendCustomAdvertisement()
        {
            UdpClient sender;
            lock (_gate)
            {
                if (_disposed) return;
                sender = _customSender;
            }
            if (sender == null) return;

            HashSet<string> sent = new HashSet<string>(StringComparer.Ordinal);
            SendCustomTo(sender, new IPEndPoint(DiscoveryMulticastAddress, DiscoveryPort), sent);
            SendCustomTo(sender, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), sent);

            try
            {
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation addressInformation in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (addressInformation.Address.AddressFamily != AddressFamily.InterNetwork
                            || addressInformation.IPv4Mask == null)
                        {
                            continue;
                        }

                        byte[] addressBytes = addressInformation.Address.GetAddressBytes();
                        byte[] maskBytes = addressInformation.IPv4Mask.GetAddressBytes();
                        if (addressBytes.Length != 4 || maskBytes.Length != 4) continue;

                        byte[] broadcastBytes = new byte[4];
                        for (int i = 0; i < broadcastBytes.Length; i++)
                        {
                            broadcastBytes[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
                        }
                        SendCustomTo(sender, new IPEndPoint(new IPAddress(broadcastBytes), DiscoveryPort), sent);
                    }
                }
            }
            catch (Exception)
            {
                // Multicast and limited broadcast were already attempted.
            }
        }

        private void SendCustomTo(UdpClient sender, IPEndPoint endpoint, HashSet<string> sent)
        {
            if (!sent.Add(endpoint.ToString())) return;
            try { sender.Send(_customPayload, _customPayload.Length, endpoint); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private void SendMdnsMulticast(byte[] packet)
        {
            if (packet == null || packet.Length == 0) return;

            UdpClient ipv4 = _mdnsIpv4;
            if (ipv4 != null)
            {
                IPEndPoint endpoint = new IPEndPoint(MdnsIpv4Address, MdnsPort);
                bool sent = false;
                foreach (IPAddress address in _ipv4Interfaces)
                {
                    try
                    {
                        ipv4.Client.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.MulticastInterface,
                            address.GetAddressBytes());
                        ipv4.Send(packet, packet.Length, endpoint);
                        sent = true;
                    }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { break; }
                }
                if (!sent)
                {
                    try { ipv4.Send(packet, packet.Length, endpoint); }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                }
            }

            UdpClient ipv6 = _mdnsIpv6;
            if (ipv6 != null)
            {
                bool sent = false;
                foreach (int index in _ipv6Interfaces)
                {
                    try
                    {
                        ipv6.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
                        IPAddress scoped = new IPAddress(MdnsIpv6Address.GetAddressBytes(), index);
                        ipv6.Send(packet, packet.Length, new IPEndPoint(scoped, MdnsPort));
                        sent = true;
                    }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { break; }
                }
                if (!sent)
                {
                    try { ipv6.Send(packet, packet.Length, new IPEndPoint(MdnsIpv6Address, MdnsPort)); }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        private void SendMdnsUnicast(byte[] packet, IPEndPoint endpoint)
        {
            if (packet == null || endpoint == null) return;
            try
            {
                UdpClient client = endpoint.AddressFamily == AddressFamily.InterNetwork
                    ? _mdnsIpv4
                    : _mdnsIpv6;
                client?.Send(packet, packet.Length, endpoint);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private void DiscoverInterfaces()
        {
            HashSet<IPAddress> ipv4 = new HashSet<IPAddress>();
            HashSet<int> ipv6 = new HashSet<int>();
            HashSet<IPAddress> advertised = new HashSet<IPAddress>();
            try
            {
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    IPInterfaceProperties properties = networkInterface.GetIPProperties();
                    foreach (UnicastIPAddressInformation information in properties.UnicastAddresses)
                    {
                        IPAddress address = information.Address;
                        if (!IsUsableAddress(address) || IPAddress.IsLoopback(address)) continue;

                        if (address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            ipv4.Add(address);
                            advertised.Add(address);
                        }
                        else if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal)
                        {
                            advertised.Add(address);
                        }
                    }

                    try
                    {
                        IPv6InterfaceProperties propertiesV6 = properties.GetIPv6Properties();
                        if (propertiesV6 != null && propertiesV6.Index > 0)
                        {
                            ipv6.Add(propertiesV6.Index);
                        }
                    }
                    catch (Exception ex) when (ex is NetworkInformationException
                                               || ex is PlatformNotSupportedException
                                               || ex is NotImplementedException) { }
                }
            }
            catch (Exception) { }

            _ipv4Interfaces.AddRange(ipv4);
            _ipv6Interfaces.AddRange(ipv6);
            _advertisedAddresses.AddRange(advertised);
        }

        private bool TryMatchMdnsQuery(
            byte[] packet,
            out bool wantsUnicast,
            out bool includeMeta,
            out bool includeService,
            out ushort queryId)
        {
            wantsUnicast = false;
            includeMeta = false;
            includeService = false;
            queryId = 0;
            if (packet == null || packet.Length < 12 || packet.Length > 9000)
            {
                return false;
            }

            try
            {
                int offset = 0;
                queryId = ReadUInt16(packet, ref offset);
                ushort flags = ReadUInt16(packet, ref offset);
                ushort questionCount = ReadUInt16(packet, ref offset);
                ReadUInt16(packet, ref offset);
                ReadUInt16(packet, ref offset);
                ReadUInt16(packet, ref offset);
                if ((flags & 0x8000) != 0 || (flags & 0x780F) != 0 || questionCount > 64)
                {
                    return false;
                }

                bool matched = false;
                for (int i = 0; i < questionCount; i++)
                {
                    if (!ReadName(packet, ref offset, out string name) || packet.Length - offset < 4)
                    {
                        return false;
                    }
                    ushort type = ReadUInt16(packet, ref offset);
                    ushort recordClass = ReadUInt16(packet, ref offset);
                    if ((recordClass & 0x7FFF) != DnsIn) continue;

                    bool metaRequested = EqualName(name, MdnsMetaServiceType)
                        && (type == DnsPtr || type == DnsAny);
                    bool serviceRequested = (EqualName(name, MdnsServiceType) && (type == DnsPtr || type == DnsAny))
                        || (EqualName(name, _instanceName) && (type == DnsSrv || type == DnsTxt || type == DnsAny))
                        || (EqualName(name, _hostName) && (type == DnsA || type == DnsAaaa || type == DnsAny));
                    if (!metaRequested && !serviceRequested) continue;

                    matched = true;
                    includeMeta |= metaRequested;
                    includeService |= serviceRequested;
                    wantsUnicast |= (recordClass & 0x8000) != 0;
                }
                return matched;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is DecoderFallbackException)
            {
                return false;
            }
        }

        private byte[] BuildMdnsPacket(uint ttl, bool includeMeta, bool includeService)
        {
            List<DnsRecord> answers = new List<DnsRecord>();
            List<DnsRecord> additional = new List<DnsRecord>();
            if (includeMeta)
            {
                answers.Add(new DnsRecord(MdnsMetaServiceType, DnsPtr, DnsIn, ttl, EncodeName(MdnsServiceType)));
            }
            if (includeService)
            {
                answers.Add(new DnsRecord(MdnsServiceType, DnsPtr, DnsIn, ttl, EncodeName(_instanceName)));
                additional.Add(new DnsRecord(_instanceName, DnsSrv, DnsFlushIn, ttl, EncodeSrv(_serverPort, _hostName)));
                additional.Add(new DnsRecord(_instanceName, DnsTxt, DnsFlushIn, ttl, EncodeTxt(_txtValues)));
                foreach (IPAddress address in _advertisedAddresses)
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        additional.Add(new DnsRecord(_hostName, DnsA, DnsFlushIn, ttl, address.GetAddressBytes()));
                    }
                    else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        additional.Add(new DnsRecord(_hostName, DnsAaaa, DnsFlushIn, ttl, address.GetAddressBytes()));
                    }
                }
            }

            using MemoryStream stream = new MemoryStream(512);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0x8400);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, checked((ushort)answers.Count));
            WriteUInt16(stream, 0);
            WriteUInt16(stream, checked((ushort)additional.Count));
            foreach (DnsRecord record in answers) WriteDnsRecord(stream, record);
            foreach (DnsRecord record in additional) WriteDnsRecord(stream, record);
            if (stream.Length > 9000) throw new InvalidOperationException("Basis mDNS announcement is too large.");
            return stream.ToArray();
        }

        private static byte[] WithDnsId(byte[] packet, ushort id)
        {
            byte[] response = (byte[])packet.Clone();
            response[0] = (byte)(id >> 8);
            response[1] = (byte)id;
            return response;
        }

        private static byte[] BuildCustomPayload(
            Guid instanceId,
            ushort serverPort,
            bool requiresPassword,
            string networkStackId,
            string serverName,
            string motd)
        {
            using MemoryStream stream = new MemoryStream(256);
            using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(PacketMagic);
            writer.Write(PacketVersion);
            writer.Write(instanceId.ToByteArray());
            writer.Write(serverPort);
            writer.Write(requiresPassword ? (byte)1 : (byte)0);
            WritePacketString(writer, networkStackId, MaxStackIdBytes);
            WritePacketString(writer, serverName, MaxServerNameBytes);
            WritePacketString(writer, motd, MaxMotdBytes);
            writer.Flush();
            return stream.ToArray();
        }

        private static void WritePacketString(BinaryWriter writer, string value, int maxBytes)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(LimitUtf8(value, maxBytes));
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string TxtValue(string key, string value)
        {
            int budget = 254 - Encoding.UTF8.GetByteCount(key);
            return key + "=" + LimitUtf8(value, Math.Max(0, budget));
        }

        private static string LimitUtf8(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value) || maxBytes <= 0) return string.Empty;
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;

            int length = value.Length;
            while (length > 0 && Encoding.UTF8.GetByteCount(value, 0, length) > maxBytes)
            {
                length--;
                if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            }
            return length == 0 ? string.Empty : value.Substring(0, length);
        }

        private readonly struct DnsRecord
        {
            public readonly string Name;
            public readonly ushort Type;
            public readonly ushort Class;
            public readonly uint Ttl;
            public readonly byte[] Data;

            public DnsRecord(string name, ushort type, ushort recordClass, uint ttl, byte[] data)
            {
                Name = name;
                Type = type;
                Class = recordClass;
                Ttl = ttl;
                Data = data;
            }
        }

        private static void WriteDnsRecord(Stream stream, DnsRecord record)
        {
            WriteBytes(stream, EncodeName(record.Name));
            WriteUInt16(stream, record.Type);
            WriteUInt16(stream, record.Class);
            WriteUInt32(stream, record.Ttl);
            WriteUInt16(stream, checked((ushort)record.Data.Length));
            WriteBytes(stream, record.Data);
        }

        private static byte[] EncodeSrv(ushort port, string target)
        {
            using MemoryStream stream = new MemoryStream(64);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, port);
            WriteBytes(stream, EncodeName(target));
            return stream.ToArray();
        }

        private static byte[] EncodeTxt(List<string> values)
        {
            using MemoryStream stream = new MemoryStream(256);
            foreach (string value in values)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                if (bytes.Length > 255) throw new InvalidOperationException("Basis mDNS TXT value is too large.");
                stream.WriteByte((byte)bytes.Length);
                WriteBytes(stream, bytes);
            }
            return stream.ToArray();
        }

        private static byte[] EncodeName(string value)
        {
            string normalized = NormalizeName(value);
            using MemoryStream stream = new MemoryStream(128);
            if (normalized.Length != 0)
            {
                foreach (string label in normalized.Split('.'))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(label);
                    if (bytes.Length == 0 || bytes.Length > 63)
                    {
                        throw new InvalidOperationException("Invalid mDNS label.");
                    }
                    stream.WriteByte((byte)bytes.Length);
                    WriteBytes(stream, bytes);
                }
            }
            stream.WriteByte(0);
            if (stream.Length > 255) throw new InvalidOperationException("Invalid mDNS name.");
            return stream.ToArray();
        }

        private static bool ReadName(byte[] packet, ref int offset, out string value)
        {
            value = string.Empty;
            if (offset < 0 || offset >= packet.Length) return false;

            UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);
            StringBuilder result = new StringBuilder(64);
            HashSet<int> pointers = null;
            int position = offset;
            int resume = -1;
            int encodedLength = 1;
            while (true)
            {
                if (position >= packet.Length) return false;
                byte length = packet[position++];
                if (length == 0)
                {
                    offset = resume >= 0 ? resume : position;
                    value = result.ToString();
                    return true;
                }
                if ((length & 0xC0) == 0xC0)
                {
                    if (position >= packet.Length) return false;
                    int pointer = ((length & 0x3F) << 8) | packet[position++];
                    if (pointer >= packet.Length) return false;
                    resume = resume >= 0 ? resume : position;
                    pointers ??= new HashSet<int>();
                    if (!pointers.Add(pointer) || pointers.Count > 32) return false;
                    position = pointer;
                    continue;
                }
                if ((length & 0xC0) != 0 || length > 63 || position + length > packet.Length)
                {
                    return false;
                }
                encodedLength += length + 1;
                if (encodedLength > 255) return false;
                if (result.Length != 0) result.Append('.');
                result.Append(strictUtf8.GetString(packet, position, length));
                position += length;
            }
        }

        private static ushort ReadUInt16(byte[] packet, ref int offset)
        {
            if (packet.Length - offset < 2) throw new IOException();
            ushort value = (ushort)((packet[offset] << 8) | packet[offset + 1]);
            offset += 2;
            return value;
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteBytes(Stream stream, byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        private static bool EqualName(string left, string right)
        {
            return string.Equals(NormalizeName(left), NormalizeName(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('.');
        }

        private static bool IsUsableAddress(IPAddress address)
        {
            return address != null
                && !address.Equals(IPAddress.Any)
                && !address.Equals(IPAddress.IPv6Any)
                && !address.Equals(IPAddress.Broadcast)
                && !address.IsIPv6Multicast;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { _cancellation.Cancel(); }
                catch (ObjectDisposedException) { }

                try
                {
                    SendMdnsMulticast(_mdnsGoodbye);
                    SendMdnsMulticast(_mdnsGoodbye);
                }
                catch (Exception ex)
                {
                    BNL.LogWarning($"Basis mDNS goodbye failed: {ex.Message}");
                }

                try { _customSender?.Close(); }
                catch (ObjectDisposedException) { }
                try { _mdnsIpv4?.Close(); }
                catch (ObjectDisposedException) { }
                try { _mdnsIpv6?.Close(); }
                catch (ObjectDisposedException) { }

                _customSender = null;
                _mdnsIpv4 = null;
                _mdnsIpv6 = null;
                _cancellation.Dispose();
            }
        }
    }
}
