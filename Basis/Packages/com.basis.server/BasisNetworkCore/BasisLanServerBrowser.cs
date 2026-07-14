using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    /// <summary>
    /// Browses Basis DNS-SD services through MeaMod.DNS and translates them into the
    /// framework-independent advertisement model used by the server directory.
    /// </summary>
    public sealed class BasisLanServerBrowser : IDisposable
    {
        private const int QueryIntervalMs = 2000;
        private const int MaxRecords = 256;
        private const int MaxTxtProperties = 32;
        private static readonly DomainName ServiceName = new DomainName(BasisLanDiscoveryProtocol.ServiceName);

        private readonly object _gate = new object();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly Action<BasisLanAdvertisement, IPAddress> _found;
        private readonly Action<Guid> _removed;
        private ServiceDiscovery _discovery;
        private volatile bool _disposed;

        public BasisLanServerBrowser(
            Action<BasisLanAdvertisement, IPAddress> found,
            Action<Guid> removed)
        {
            _found = found ?? throw new ArgumentNullException(nameof(found));
            _removed = removed ?? throw new ArgumentNullException(nameof(removed));

            try
            {
                _discovery = new ServiceDiscovery();
                _discovery.Mdns.IgnoreDuplicateMessages = true;
                _discovery.ServiceInstanceDiscovered += OnServiceDiscovered;
                _discovery.ServiceInstanceShutdown += OnServiceShutdown;
                Query();
                _ = Task.Run(() => QueryLoopAsync(_cancellation.Token));
            }
            catch
            {
                _discovery?.Dispose();
                _discovery = null;
                _cancellation.Dispose();
                throw;
            }
        }

        public void Query()
        {
            ServiceDiscovery discovery;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                discovery = _discovery;
            }

            try
            {
                discovery?.QueryServiceInstances(ServiceName);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    BNL.LogWarning($"Basis LAN DNS-SD query failed: {ex.Message}");
                }
            }
        }

        private async Task QueryLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(QueryIntervalMs, cancellationToken).ConfigureAwait(false);
                    Query();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    BNL.LogWarning($"Basis LAN DNS-SD browsing stopped: {ex.Message}");
                }
            }
        }

        private void OnServiceDiscovered(object sender, ServiceInstanceDiscoveryEventArgs args)
        {
            if (_disposed || args == null)
            {
                return;
            }

            if (TryExtractAdvertisement(
                    args.Message,
                    args.ServiceInstanceName,
                    args.RemoteEndPoint?.Address,
                    out BasisLanAdvertisement advertisement,
                    out IPAddress address))
            {
                _found(advertisement, address);
            }
        }

        private void OnServiceShutdown(object sender, ServiceInstanceShutdownEventArgs args)
        {
            if (_disposed || args == null)
            {
                return;
            }

            if (TryReadInstanceId(args.Message, args.ServiceInstanceName, out Guid instanceId))
            {
                _removed(instanceId);
            }
        }

        internal static bool TryExtractAdvertisement(
            Message message,
            DomainName serviceInstanceName,
            IPAddress remoteAddress,
            out BasisLanAdvertisement advertisement,
            out IPAddress address)
        {
            advertisement = default;
            address = null;
            if (message == null || serviceInstanceName == null)
            {
                return false;
            }

            List<ResourceRecord> records = CollectRecords(message);
            SRVRecord service = null;
            TXTRecord text = null;
            foreach (ResourceRecord record in records)
            {
                if (!NamesEqual(record?.Name, serviceInstanceName))
                {
                    continue;
                }

                if (service == null && record is SRVRecord srv)
                {
                    service = srv;
                }
                else if (text == null && record is TXTRecord txt)
                {
                    text = txt;
                }
            }

            if (service == null || service.Port == 0 || service.Target == null || text?.Strings == null)
            {
                return false;
            }

            Dictionary<string, string> properties = ReadProperties(text.Strings);
            if (!properties.TryGetValue("protocol", out string protocol)
                || !string.Equals(protocol, BasisLanDiscoveryProtocol.ProtocolVersion, StringComparison.Ordinal)
                || !properties.TryGetValue("id", out string idText)
                || !Guid.TryParseExact(idText, "N", out Guid instanceId)
                || instanceId == Guid.Empty
                || !properties.TryGetValue("pwd", out string password)
                || (password != "0" && password != "1"))
            {
                return false;
            }

            address = SelectAddress(records, service.Target, remoteAddress);
            if (address == null)
            {
                return false;
            }

            string stackId = ReadMetadata(
                properties,
                "stack",
                "stack64",
                BasisLanDiscoveryProtocol.MaxStackIdBytes);
            string serverName = ReadMetadata(
                properties,
                "name",
                "name64",
                BasisLanDiscoveryProtocol.MaxServerNameBytes);
            string motd = ReadMetadata(
                properties,
                "motd",
                "motd64",
                BasisLanDiscoveryProtocol.MaxMotdBytes);
            advertisement = new BasisLanAdvertisement(
                instanceId,
                service.Port,
                password == "1",
                stackId,
                serverName,
                motd);
            return true;
        }

        private static bool TryReadInstanceId(
            Message message,
            DomainName serviceInstanceName,
            out Guid instanceId)
        {
            instanceId = Guid.Empty;
            if (message != null)
            {
                foreach (ResourceRecord record in CollectRecords(message))
                {
                    if (!(record is TXTRecord text)
                        || !NamesEqual(record.Name, serviceInstanceName)
                        || text.Strings == null)
                    {
                        continue;
                    }

                    Dictionary<string, string> properties = ReadProperties(text.Strings);
                    if (properties.TryGetValue("id", out string idText)
                        && Guid.TryParseExact(idText, "N", out instanceId)
                        && instanceId != Guid.Empty)
                    {
                        return true;
                    }
                }
            }

            string instance = serviceInstanceName?.ToString() ?? string.Empty;
            int separator = instance.IndexOf('.');
            if (separator >= 0)
            {
                instance = instance.Substring(0, separator);
            }
            return instance.StartsWith("Basis-", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(instance.Substring(6), "N", out instanceId)
                && instanceId != Guid.Empty;
        }

        private static List<ResourceRecord> CollectRecords(Message message)
        {
            List<ResourceRecord> records = new List<ResourceRecord>();
            AddRecords(records, message.Answers);
            AddRecords(records, message.AuthorityRecords);
            AddRecords(records, message.AdditionalRecords);
            return records;
        }

        private static void AddRecords(List<ResourceRecord> target, IList<ResourceRecord> source)
        {
            if (source == null)
            {
                return;
            }

            for (int i = 0; i < source.Count && target.Count < MaxRecords; i++)
            {
                if (source[i] != null)
                {
                    target.Add(source[i]);
                }
            }
        }

        private static Dictionary<string, string> ReadProperties(IList<string> values)
        {
            Dictionary<string, string> properties =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (values == null)
            {
                return properties;
            }

            for (int i = 0; i < values.Count && properties.Count < MaxTxtProperties; i++)
            {
                string value = values[i];
                int separator = value?.IndexOf('=') ?? -1;
                if (separator <= 0)
                {
                    continue;
                }

                string key = value.Substring(0, separator);
                if (!properties.ContainsKey(key))
                {
                    properties.Add(key, value.Substring(separator + 1));
                }
            }
            return properties;
        }

        private static string ReadMetadata(
            Dictionary<string, string> properties,
            string legacyKey,
            string encodedKey,
            int maxBytes)
        {
            if (TryReadEncodedMetadata(properties, encodedKey, maxBytes, out string decoded))
            {
                return decoded;
            }

            properties.TryGetValue(legacyKey, out string legacy);
            return BasisLanDiscoveryProtocol.LimitUtf8(legacy, maxBytes);
        }

        private static bool TryReadEncodedMetadata(
            Dictionary<string, string> properties,
            string encodedKey,
            int maxBytes,
            out string value)
        {
            value = string.Empty;
            if (!properties.TryGetValue(encodedKey + "-0", out string firstChunk))
            {
                return false;
            }

            StringBuilder encoded = new StringBuilder(firstChunk);
            for (int index = 1; index <= 9; index++)
            {
                if (!properties.TryGetValue($"{encodedKey}-{index}", out string chunk))
                {
                    break;
                }
                encoded.Append(chunk);
            }

            return BasisLanDiscoveryProtocol.TryDecodeTxtUtf8(
                encoded.ToString(),
                maxBytes,
                out value);
        }

        private static IPAddress SelectAddress(
            List<ResourceRecord> records,
            DomainName hostName,
            IPAddress remoteAddress)
        {
            if (IsUsableAddress(remoteAddress))
            {
                return remoteAddress;
            }

            IPAddress selected = null;
            foreach (ResourceRecord record in records)
            {
                if (!(record is AddressRecord addressRecord)
                    || !NamesEqual(record.Name, hostName)
                    || !IsUsableAddress(addressRecord.Address))
                {
                    continue;
                }

                IPAddress candidate = RestoreScope(addressRecord.Address, remoteAddress);
                if (selected == null || AddressPreferenceRank(candidate) < AddressPreferenceRank(selected))
                {
                    selected = candidate;
                }
            }
            return selected;
        }

        private static IPAddress RestoreScope(IPAddress address, IPAddress remoteAddress)
        {
            if (address != null
                && address.AddressFamily == AddressFamily.InterNetworkV6
                && address.IsIPv6LinkLocal
                && address.ScopeId == 0
                && remoteAddress != null
                && remoteAddress.AddressFamily == AddressFamily.InterNetworkV6
                && remoteAddress.ScopeId != 0)
            {
                return new IPAddress(address.GetAddressBytes(), remoteAddress.ScopeId);
            }
            return address;
        }

        private static int AddressPreferenceRank(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();
                return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254 ? 1 : 0;
            }
            return address.IsIPv6LinkLocal ? 3 : 2;
        }

        private static bool NamesEqual(DomainName left, DomainName right)
        {
            return left != null && right != null && left.Equals(right);
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
            ServiceDiscovery discovery;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try { _cancellation.Cancel(); }
                catch (ObjectDisposedException) { }

                discovery = _discovery;
                _discovery = null;
                if (discovery != null)
                {
                    discovery.ServiceInstanceDiscovered -= OnServiceDiscovered;
                    discovery.ServiceInstanceShutdown -= OnServiceShutdown;
                }
            }

            discovery?.Dispose();
            _cancellation.Dispose();
        }
    }
}
