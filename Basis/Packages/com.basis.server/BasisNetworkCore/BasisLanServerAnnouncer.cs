using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;
using System;
using System.Linq;
using System.Net;
using System.Text;

namespace Basis.Network.Core
{
    /// <summary>
    /// Announces a running Basis server as a DNS-SD service through MeaMod.DNS.
    /// This class has no Unity dependency, so dedicated and in-client hosts share it.
    /// </summary>
    public sealed class BasisLanServerAnnouncer : IDisposable
    {
        private readonly object _gate = new object();
        private ServiceDiscovery _discovery;
        private ServiceProfile _profile;
        private bool _disposed;

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

            string id = Guid.NewGuid().ToString("N");
            string effectiveStackId = string.IsNullOrWhiteSpace(networkStackId)
                ? BasisNetworkStackRegistry.DefaultId
                : networkStackId;
            string effectiveServerName = string.IsNullOrWhiteSpace(serverName)
                ? "Basis Server"
                : serverName;

            ServiceDiscovery discovery = null;
            try
            {
                discovery = new ServiceDiscovery();
                discovery.Mdns.IgnoreDuplicateMessages = true;

                IPAddress[] addresses = GetAdvertisedAddresses();
                ServiceProfile profile = new ServiceProfile(
                    new DomainName($"Basis-{id}"),
                    new DomainName(BasisLanDiscoveryProtocol.ServiceName),
                    serverPort,
                    addresses);
                profile.AddProperty("protocol", BasisLanDiscoveryProtocol.ProtocolVersion);
                profile.AddProperty("id", id);
                AddMetadata(
                    profile,
                    "stack",
                    "stack64",
                    effectiveStackId,
                    BasisLanDiscoveryProtocol.MaxStackIdBytes);
                AddMetadata(
                    profile,
                    "name",
                    "name64",
                    effectiveServerName,
                    BasisLanDiscoveryProtocol.MaxServerNameBytes);
                AddMetadata(
                    profile,
                    "motd",
                    "motd64",
                    motd ?? string.Empty,
                    BasisLanDiscoveryProtocol.MaxMotdBytes);
                profile.AddProperty("pwd", requiresPassword ? "1" : "0");

                discovery.Advertise(profile);
                _profile = profile;
                _discovery = discovery;
            }
            catch
            {
                discovery?.Dispose();
                throw;
            }
        }

        private static void AddMetadata(
            ServiceProfile profile,
            string legacyKey,
            string encodedKey,
            string value,
            int maxBytes)
        {
            string limited = BasisLanDiscoveryProtocol.LimitUtf8(value, maxBytes);
            if (BasisLanDiscoveryProtocol.IsAscii(limited))
            {
                profile.AddProperty(
                    legacyKey,
                    BasisLanDiscoveryProtocol.LimitTxtProperty(legacyKey, limited, maxBytes));
                return;
            }

            string encoded = BasisLanDiscoveryProtocol.EncodeTxtUtf8(limited, maxBytes);
            int offset = 0;
            for (int index = 0; offset < encoded.Length; index++)
            {
                if (index > 9)
                {
                    throw new InvalidOperationException("Basis LAN metadata requires too many TXT chunks.");
                }

                string chunkKey = $"{encodedKey}-{index}";
                int chunkBudget = Math.Max(1, 254 - Encoding.ASCII.GetByteCount(chunkKey));
                int chunkLength = Math.Min(chunkBudget, encoded.Length - offset);
                profile.AddProperty(chunkKey, encoded.Substring(offset, chunkLength));
                offset += chunkLength;
            }
        }

        private static IPAddress[] GetAdvertisedAddresses()
        {
            try
            {
                return MulticastService.GetLinkLocalAddresses()
                    .Where(IsUsableAddress)
                    .Distinct()
                    .ToArray();
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"Basis LAN address discovery failed: {ex.Message}");
                return Array.Empty<IPAddress>();
            }
        }

        private static bool IsUsableAddress(IPAddress address)
        {
            return address != null
                && !IPAddress.IsLoopback(address)
                && !address.Equals(IPAddress.Any)
                && !address.Equals(IPAddress.IPv6Any)
                && !address.Equals(IPAddress.Broadcast)
                && !address.IsIPv6Multicast;
        }

        public void Dispose()
        {
            ServiceDiscovery discovery;
            ServiceProfile profile;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                discovery = _discovery;
                profile = _profile;
                _discovery = null;
                _profile = null;
            }

            if (discovery == null)
            {
                return;
            }

            try
            {
                if (profile != null)
                {
                    discovery.Unadvertise(profile);
                }
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"Basis LAN DNS-SD goodbye failed: {ex.Message}");
            }
            finally
            {
                discovery.Dispose();
            }
        }
    }
}
