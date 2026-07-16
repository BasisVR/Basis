using MeaMod.DNS.Multicast;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Basis.Network.Core
{
    /// <summary>Metadata published through the Basis DNS-SD service.</summary>
    public readonly struct BasisLanAdvertisement
    {
        public readonly Guid InstanceId;
        public readonly ushort ServerPort;
        public readonly bool RequiresPassword;
        public readonly string NetworkStackId;
        public readonly string ServerName;
        public readonly string Motd;

        public BasisLanAdvertisement(
            Guid instanceId,
            ushort serverPort,
            bool requiresPassword,
            string networkStackId,
            string serverName,
            string motd)
        {
            InstanceId = instanceId;
            ServerPort = serverPort;
            RequiresPassword = requiresPassword;
            NetworkStackId = networkStackId ?? string.Empty;
            ServerName = serverName ?? string.Empty;
            Motd = motd ?? string.Empty;
        }
    }

    internal readonly struct BasisLanIpv4Subnet
    {
        public readonly IPAddress Address;
        public readonly IPAddress Mask;

        public BasisLanIpv4Subnet(IPAddress address, IPAddress mask)
        {
            Address = address;
            Mask = mask;
        }
    }

    /// <summary>Shared address filtering and preference rules for Basis LAN discovery.</summary>
    public static class BasisLanAddressUtility
    {
        public static bool IsUsable(IPAddress address, bool allowLoopback = false)
        {
            if (address == null || (!allowLoopback && IPAddress.IsLoopback(address)))
            {
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte firstOctet = address.GetAddressBytes()[0];
                return !address.Equals(IPAddress.Any)
                    && !address.Equals(IPAddress.Broadcast)
                    && (firstOctet < 224 || firstOctet > 239);
            }

            return address.AddressFamily == AddressFamily.InterNetworkV6
                && !address.Equals(IPAddress.IPv6Any)
                && !address.IsIPv6Multicast;
        }

        public static int PreferenceRank(IPAddress address)
        {
            if (address?.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();
                return bytes[0] == 169 && bytes[1] == 254 ? 2 : 0;
            }

            return address?.AddressFamily == AddressFamily.InterNetworkV6
                ? address.IsIPv6LinkLocal ? 3 : 1
                : int.MaxValue;
        }

        internal static IPAddress[] GetPreferredAdvertisedAddresses()
        {
            List<IPAddress> gatewayAddresses = new List<IPAddress>();
            List<IPAddress> fallbackAddresses = new List<IPAddress>();

            try
            {
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    try
                    {
                        IPInterfaceProperties properties = networkInterface.GetIPProperties();
                        bool hasUsableGateway = properties.GatewayAddresses
                            .Any(gateway => IsUsable(gateway?.Address));

                        foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                        {
                            if (!IsUsable(unicast?.Address))
                            {
                                continue;
                            }

                            fallbackAddresses.Add(unicast.Address);
                            if (hasUsableGateway)
                            {
                                gatewayAddresses.Add(unicast.Address);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore interfaces that disappear or reject property queries mid-enumeration.
                    }
                }
            }
            catch
            {
                // Fall through to an empty set; the DNS-SD response source remains usable as a fallback.
            }

            List<IPAddress> selected = gatewayAddresses.Count > 0
                ? gatewayAddresses
                : fallbackAddresses;
            return selected
                .Distinct()
                .OrderBy(PreferenceRank)
                .ToArray();
        }

        internal static BasisLanIpv4Subnet[] GetLocalIpv4Subnets()
        {
            List<BasisLanIpv4Subnet> gatewaySubnets = new List<BasisLanIpv4Subnet>();
            List<BasisLanIpv4Subnet> fallbackSubnets = new List<BasisLanIpv4Subnet>();
            try
            {
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    try
                    {
                        IPInterfaceProperties properties = networkInterface.GetIPProperties();
                        bool hasUsableGateway = properties.GatewayAddresses
                            .Any(gateway => IsUsable(gateway?.Address));
                        foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                        {
                            if (unicast?.Address?.AddressFamily != AddressFamily.InterNetwork
                                || unicast.IPv4Mask?.AddressFamily != AddressFamily.InterNetwork
                                || !IsUsable(unicast.Address))
                            {
                                continue;
                            }

                            BasisLanIpv4Subnet subnet =
                                new BasisLanIpv4Subnet(unicast.Address, unicast.IPv4Mask);
                            fallbackSubnets.Add(subnet);
                            if (hasUsableGateway)
                            {
                                gatewaySubnets.Add(subnet);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore interfaces that disappear or reject property queries mid-enumeration.
                    }
                }
            }
            catch
            {
            }
            return (gatewaySubnets.Count > 0 ? gatewaySubnets : fallbackSubnets).ToArray();
        }

        internal static bool IsOnSameIpv4Subnet(
            IPAddress candidate,
            BasisLanIpv4Subnet subnet)
        {
            if (candidate?.AddressFamily != AddressFamily.InterNetwork
                || subnet.Address?.AddressFamily != AddressFamily.InterNetwork
                || subnet.Mask?.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            byte[] candidateBytes = candidate.GetAddressBytes();
            byte[] localBytes = subnet.Address.GetAddressBytes();
            byte[] maskBytes = subnet.Mask.GetAddressBytes();
            for (int index = 0; index < candidateBytes.Length; index++)
            {
                if ((candidateBytes[index] & maskBytes[index])
                    != (localBytes[index] & maskBytes[index]))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>Shared constants and bounded TXT metadata encoding for Basis LAN DNS-SD.</summary>
    internal static class BasisLanDiscoveryProtocol
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal const string ServiceName = "_basisvr._udp";
        internal const string ProtocolVersion = "1";
        internal const int MaxStackIdBytes = 64;
        internal const int MaxServerNameBytes = 128;
        internal const int MaxMotdBytes = 384;

        internal static string LimitUtf8(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value) || maxBytes <= 0)
            {
                return string.Empty;
            }
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            {
                return value;
            }

            int length = value.Length;
            while (length > 0 && Encoding.UTF8.GetByteCount(value, 0, length) > maxBytes)
            {
                length--;
                if (length > 0 && char.IsHighSurrogate(value[length - 1]))
                {
                    length--;
                }
            }
            return length == 0 ? string.Empty : value.Substring(0, length);
        }

        internal static void AddMetadata(
            ServiceProfile profile,
            string encodedKey,
            string value,
            int maxBytes)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrEmpty(encodedKey)) throw new ArgumentException("TXT key is required.", nameof(encodedKey));

            string encoded = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(LimitUtf8(value, maxBytes)));
            for (int index = 0, offset = 0; offset < encoded.Length; index++)
            {
                string chunkKey = $"{encodedKey}-{index}";
                int chunkLength = Math.Min(TxtValueBudget(chunkKey), encoded.Length - offset);
                profile.AddProperty(chunkKey, encoded.Substring(offset, chunkLength));
                offset += chunkLength;
            }
        }

        internal static string ReadMetadata(
            Dictionary<string, string> properties,
            string encodedKey,
            int maxBytes)
        {
            int maxEncodedLength = ((maxBytes + 2) / 3) * 4;
            StringBuilder encoded = new StringBuilder(maxEncodedLength);
            for (int index = 0;
                 properties.TryGetValue($"{encodedKey}-{index}", out string chunk);
                 index++)
            {
                if (chunk.Length > maxEncodedLength - encoded.Length)
                {
                    return string.Empty;
                }
                encoded.Append(chunk);
            }

            if (encoded.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(encoded.ToString());
                return bytes.Length <= maxBytes
                    ? StrictUtf8.GetString(bytes)
                    : string.Empty;
            }
            catch (Exception ex) when (ex is FormatException || ex is DecoderFallbackException)
            {
                return string.Empty;
            }
        }

        private static int TxtValueBudget(string key)
        {
            return 254 - Encoding.ASCII.GetByteCount(key);
        }
    }
}
