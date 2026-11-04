using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    public struct BasisSrvRecord
    {
        public ushort Priority, Weight, Port;
        public uint Ttl;
        public string Target;
    }

    public static class BasisSrvResolver
    {
        public const string Service = "_basis", UdpProtocol = "_udp", TcpProtocol = "_tcp";
        public const ushort TypeSrv = 33, ClassIn = 1;
        public const int DnsPort = 53, MaxNameServers = 3, MaxCacheSeconds = 300, NegativeCacheSeconds = 60, MaxNameLength = 255;
        public static bool Enabled = true;
        public static IPAddress[] FallbackNameServers = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") };
        private static readonly ConcurrentDictionary<string, string> protocols = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, CacheEntry> cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        [ThreadStatic] private static Random random;
        private sealed class CacheEntry
        {
            public BasisSrvRecord[] Records;
            public DateTime Expires;
        }

        static BasisSrvResolver()
        {
            protocols[BasisNetworkStackRegistry.LiteNetLibId] = UdpProtocol;
        }

        public static void RegisterProtocol(string stackId, string protocol)
        {
            if (string.IsNullOrEmpty(stackId)) return;
            if (string.IsNullOrEmpty(protocol)) protocols.TryRemove(stackId, out _);
            else protocols[stackId] = protocol;
        }

        public static void ClearCache() => cache.Clear();

        private static Random Rng => random ?? (random = new Random(Guid.NewGuid().GetHashCode()));

        public static bool TryGetServiceName(ConnectionTarget target, out string serviceName)
        {
            serviceName = null;
            if (!Enabled || target == null) return false;
            string stackId = string.IsNullOrEmpty(target.StackId) ? BasisNetworkStackRegistry.DefaultId : target.StackId;
            if (!protocols.TryGetValue(stackId, out string protocol)) return false;
            string port = target.Get(ConnectionTarget.Keys.Port, string.Empty);
            if (port.Length > 0 && port != HostPortConnectionTargetParser.DefaultPort.ToString(CultureInfo.InvariantCulture)) return false;
            return TryBuildServiceName(target.Get(ConnectionTarget.Keys.Address, string.Empty), protocol, out serviceName);
        }

        public static bool TryBuildServiceName(string host, string protocol, out string serviceName)
        {
            serviceName = null;
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(protocol) || host.Contains("://")) return false;
            host = host.Trim().TrimEnd('.');
            if (host.IndexOf('.') < 0 || IPAddress.TryParse(host, out _)) return false;
            try
            {
                host = new IdnMapping().GetAscii(host);
            }
            catch (ArgumentException)
            {
                return false;
            }
            serviceName = Service + "." + protocol + "." + host;
            return serviceName.Length <= 253;
        }

        public static ConnectionTarget WithEndpoint(ConnectionTarget source, string host, ushort port)
        {
            string portString = port.ToString(CultureInfo.InvariantCulture);
            bool isIPv6 = IPAddress.TryParse(host, out IPAddress ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
            ConnectionTarget target = new ConnectionTarget(source?.StackId, isIPv6 ? $"[{host}]:{portString}" : $"{host}:{portString}");
            if (source != null)
                foreach (KeyValuePair<string, string> pair in source.Properties)
                    target.Set(pair.Key, pair.Value);
            target.Set(ConnectionTarget.Keys.Address, host);
            target.Set(ConnectionTarget.Keys.Port, portString);
            return target;
        }

        public static async Task<BasisSrvRecord[]> ResolveAsync(string serviceName, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(serviceName)) return Array.Empty<BasisSrvRecord>();
            if (cache.TryGetValue(serviceName, out CacheEntry hit) && hit.Expires > DateTime.UtcNow) return Order(hit.Records, Rng);
            byte[] query;
            ushort id = (ushort)Rng.Next(0, 0x10000);
            try
            {
                query = BuildQuery(serviceName, id);
            }
            catch (ArgumentException)
            {
                return Array.Empty<BasisSrvRecord>();
            }
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(Math.Max(1, timeoutMs));
                List<IPAddress> servers = NameServers();
                int perServerMs = Math.Max(250, timeoutMs / Math.Max(1, Math.Min(servers.Count, 2)));
                for (int i = 0; i < servers.Count && !cts.IsCancellationRequested; i++)
                {
                    try
                    {
                        byte[] response = await QueryUdpAsync(servers[i], query, perServerMs, cts.Token).ConfigureAwait(false);
                        if (response == null) continue;
                        if ((response[2] & 0x02) != 0) response = await QueryTcpAsync(servers[i], query, perServerMs, cts.Token).ConfigureAwait(false) ?? response;
                        if (!TryParseResponse(response, id, out BasisSrvRecord[] records, out uint ttl)) continue;
                        cache[serviceName] = new CacheEntry { Records = records, Expires = DateTime.UtcNow.AddSeconds(Math.Min(ttl, MaxCacheSeconds)) };
                        return Order(records, Rng);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        BNL.LogWarning($"SRV lookup for {serviceName} via {servers[i]} failed: {ex.Message}");
                    }
                }
            }
            if (!ct.IsCancellationRequested) cache[serviceName] = new CacheEntry { Records = Array.Empty<BasisSrvRecord>(), Expires = DateTime.UtcNow.AddSeconds(NegativeCacheSeconds) };
            return Array.Empty<BasisSrvRecord>();
        }

        public static List<IPAddress> NameServers()
        {
            List<IPAddress> servers = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (IPAddress address in network.GetIPProperties().DnsAddresses)
                    {
                        if (address.IsIPv6SiteLocal || address.IsIPv6Multicast || servers.Contains(address)) continue;
                        servers.Add(address);
                        if (servers.Count >= MaxNameServers) break;
                    }
                    if (servers.Count >= MaxNameServers) break;
                }
            }
            catch (Exception) { }
            if (FallbackNameServers != null)
                foreach (IPAddress address in FallbackNameServers)
                    if (address != null && !servers.Contains(address)) servers.Add(address);
            return servers;
        }

        public static byte[] BuildQuery(string name, ushort id)
        {
            List<byte> bytes = new List<byte>(name.Length + 18) { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            foreach (string label in name.TrimEnd('.').Split('.'))
            {
                if (label.Length == 0 || label.Length > 63) throw new ArgumentException("Invalid DNS label", nameof(name));
                bytes.Add((byte)label.Length);
                foreach (char c in label)
                {
                    if (c > 0x7F) throw new ArgumentException("DNS names must be ASCII", nameof(name));
                    bytes.Add((byte)c);
                }
            }
            bytes.Add(0);
            if (bytes.Count - 12 > MaxNameLength) throw new ArgumentException("DNS name too long", nameof(name));
            bytes.Add(TypeSrv >> 8); bytes.Add(TypeSrv & 0xFF); bytes.Add(ClassIn >> 8); bytes.Add(ClassIn & 0xFF);
            return bytes.ToArray();
        }

        public static bool TryParseResponse(byte[] data, ushort id, out BasisSrvRecord[] records, out uint ttl)
        {
            records = Array.Empty<BasisSrvRecord>();
            ttl = NegativeCacheSeconds;
            if (data == null || data.Length < 12 || ReadU16(data, 0) != id) return false;
            ushort flags = ReadU16(data, 2);
            if ((flags & 0x8000) == 0) return false;
            int rcode = flags & 0x0F;
            if (rcode == 3) return true;
            if (rcode != 0) return false;
            int questions = ReadU16(data, 4), answers = ReadU16(data, 6), offset = 12;
            for (int i = 0; i < questions; i++)
            {
                if (!SkipName(data, ref offset) || offset + 4 > data.Length) return false;
                offset += 4;
            }
            List<BasisSrvRecord> found = new List<BasisSrvRecord>();
            uint minTtl = uint.MaxValue;
            for (int i = 0; i < answers; i++)
            {
                if (!SkipName(data, ref offset) || offset + 10 > data.Length) return false;
                ushort type = ReadU16(data, offset), cls = ReadU16(data, offset + 2);
                uint recordTtl = (uint)(ReadU16(data, offset + 4) << 16 | ReadU16(data, offset + 6));
                int length = ReadU16(data, offset + 8), start = offset + 10, end = start + length;
                if (end > data.Length) return false;
                if (type == TypeSrv && cls == ClassIn && length >= 7)
                {
                    if (!TryReadName(data, start + 6, out string target)) return false;
                    found.Add(new BasisSrvRecord { Priority = ReadU16(data, start), Weight = ReadU16(data, start + 2), Port = ReadU16(data, start + 4), Ttl = recordTtl, Target = target });
                    minTtl = Math.Min(minTtl, recordTtl);
                }
                offset = end;
            }
            records = found.ToArray();
            if (found.Count > 0) ttl = minTtl;
            return true;
        }

        public static BasisSrvRecord[] Order(BasisSrvRecord[] records, Random rng)
        {
            if (records == null || records.Length == 0) return Array.Empty<BasisSrvRecord>();
            List<BasisSrvRecord> usable = new List<BasisSrvRecord>(records.Length);
            foreach (BasisSrvRecord record in records)
                if (!string.IsNullOrEmpty(record.Target) && record.Port != 0) usable.Add(record);
            List<BasisSrvRecord> ordered = new List<BasisSrvRecord>(usable.Count);
            List<BasisSrvRecord> group = new List<BasisSrvRecord>();
            usable.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            for (int i = 0; i < usable.Count;)
            {
                group.Clear();
                ushort priority = usable[i].Priority;
                for (; i < usable.Count && usable[i].Priority == priority; i++)
                {
                    if (usable[i].Weight == 0) group.Insert(0, usable[i]);
                    else group.Add(usable[i]);
                }
                while (group.Count > 0)
                {
                    int sum = 0;
                    foreach (BasisSrvRecord record in group) sum += record.Weight;
                    int pick = rng.Next(0, sum + 1), running = 0, chosen = group.Count - 1;
                    for (int g = 0; g < group.Count; g++)
                    {
                        running += group[g].Weight;
                        if (running >= pick) { chosen = g; break; }
                    }
                    ordered.Add(group[chosen]);
                    group.RemoveAt(chosen);
                }
            }
            return ordered.ToArray();
        }

        private static async Task<byte[]> QueryUdpAsync(IPAddress server, byte[] query, int timeoutMs, CancellationToken ct)
        {
            using (UdpClient client = new UdpClient(server.AddressFamily))
            {
                await client.SendAsync(query, query.Length, new IPEndPoint(server, DnsPort)).ConfigureAwait(false);
                Task delay = Task.Delay(timeoutMs, ct);
                while (true)
                {
                    Task<UdpReceiveResult> receive = client.ReceiveAsync();
                    if (await Task.WhenAny(receive, delay).ConfigureAwait(false) != receive)
                    {
                        ct.ThrowIfCancellationRequested();
                        return null;
                    }
                    UdpReceiveResult result = await receive.ConfigureAwait(false);
                    if (result.RemoteEndPoint.Port == DnsPort && result.RemoteEndPoint.Address.Equals(server) && result.Buffer.Length >= 12 && result.Buffer[0] == query[0] && result.Buffer[1] == query[1]) return result.Buffer;
                }
            }
        }

        private static async Task<byte[]> QueryTcpAsync(IPAddress server, byte[] query, int timeoutMs, CancellationToken ct)
        {
            using (TcpClient client = new TcpClient(server.AddressFamily))
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (cts.Token.Register(() => client.Dispose()))
            {
                cts.CancelAfter(timeoutMs);
                try
                {
                    await client.ConnectAsync(server, DnsPort).ConfigureAwait(false);
                    NetworkStream stream = client.GetStream();
                    byte[] framed = new byte[query.Length + 2];
                    framed[0] = (byte)(query.Length >> 8);
                    framed[1] = (byte)query.Length;
                    Buffer.BlockCopy(query, 0, framed, 2, query.Length);
                    await stream.WriteAsync(framed, 0, framed.Length, cts.Token).ConfigureAwait(false);
                    byte[] lengthBytes = await ReadExactlyAsync(stream, 2, cts.Token).ConfigureAwait(false);
                    if (lengthBytes == null) return null;
                    return await ReadExactlyAsync(stream, ReadU16(lengthBytes, 0), cts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    return null;
                }
            }
        }

        private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
        {
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, read, count - read, ct).ConfigureAwait(false);
                if (n <= 0) return null;
                read += n;
            }
            return buffer;
        }

        private static ushort ReadU16(byte[] data, int offset) => (ushort)(data[offset] << 8 | data[offset + 1]);

        private static bool SkipName(byte[] data, ref int offset)
        {
            while (offset < data.Length)
            {
                byte length = data[offset];
                if (length == 0) { offset++; return true; }
                if ((length & 0xC0) == 0xC0) { offset += 2; return offset <= data.Length; }
                if ((length & 0xC0) != 0) return false;
                offset += length + 1;
            }
            return false;
        }

        public static bool TryReadName(byte[] data, int offset, out string name)
        {
            name = null;
            StringBuilder builder = new StringBuilder();
            int jumps = 0;
            while (true)
            {
                if (offset >= data.Length) return false;
                byte length = data[offset];
                if (length == 0) break;
                if ((length & 0xC0) == 0xC0)
                {
                    if (offset + 1 >= data.Length || ++jumps > 16) return false;
                    offset = (length & 0x3F) << 8 | data[offset + 1];
                    continue;
                }
                if ((length & 0xC0) != 0 || offset + 1 + length > data.Length) return false;
                if (builder.Length > 0) builder.Append('.');
                for (int i = 0; i < length; i++)
                {
                    byte c = data[offset + 1 + i];
                    if (c <= 0x20 || c >= 0x7F) return false;
                    builder.Append((char)c);
                }
                if (builder.Length > MaxNameLength) return false;
                offset += length + 1;
            }
            name = builder.ToString();
            return true;
        }
    }
}
