using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;
using System;
using System.Linq;
using System.Net;

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
            Guid instanceId,
            ushort serverPort,
            string networkStackId,
            string serverName,
            string motd,
            bool requiresPassword)
        {
            if (instanceId == Guid.Empty)
            {
                throw new ArgumentException("LAN server instance ID cannot be empty.", nameof(instanceId));
            }
            if (serverPort == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(serverPort));
            }

            ServiceDiscovery discovery = null;
            try
            {
                discovery = new ServiceDiscovery();
                discovery.Mdns.IgnoreDuplicateMessages = true;

                ServiceProfile profile = CreateProfile(
                    instanceId,
                    serverPort,
                    networkStackId,
                    serverName,
                    motd,
                    requiresPassword,
                    GetAdvertisedAddresses());

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

        internal static ServiceProfile CreateProfile(
            Guid instanceId,
            ushort serverPort,
            string networkStackId,
            string serverName,
            string motd,
            bool requiresPassword,
            IPAddress[] addresses)
        {
            string id = instanceId.ToString("N");
            string effectiveStackId = string.IsNullOrWhiteSpace(networkStackId)
                ? BasisNetworkStackRegistry.DefaultId
                : networkStackId;
            string effectiveServerName = string.IsNullOrWhiteSpace(serverName)
                ? "Basis Server"
                : serverName;

            ServiceProfile profile = new ServiceProfile(
                new DomainName($"Basis-{id}"),
                new DomainName(BasisLanDiscoveryProtocol.ServiceName),
                serverPort,
                addresses ?? Array.Empty<IPAddress>());
            profile.AddProperty("protocol", BasisLanDiscoveryProtocol.ProtocolVersion);
            profile.AddProperty("id", id);
            BasisLanDiscoveryProtocol.AddMetadata(
                profile,
                "stack64",
                effectiveStackId,
                BasisLanDiscoveryProtocol.MaxStackIdBytes);
            BasisLanDiscoveryProtocol.AddMetadata(
                profile,
                "name64",
                effectiveServerName,
                BasisLanDiscoveryProtocol.MaxServerNameBytes);
            BasisLanDiscoveryProtocol.AddMetadata(
                profile,
                "motd64",
                motd ?? string.Empty,
                BasisLanDiscoveryProtocol.MaxMotdBytes);
            profile.AddProperty("pwd", requiresPassword ? "1" : "0");
            return profile;
        }

        private static IPAddress[] GetAdvertisedAddresses()
        {
            try
            {
                return MulticastService.GetIPAddresses()
                    .Where(address => BasisLanAddressUtility.IsUsable(address))
                    .Distinct()
                    .ToArray();
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"Basis LAN address discovery failed: {ex.Message}");
                return Array.Empty<IPAddress>();
            }
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
