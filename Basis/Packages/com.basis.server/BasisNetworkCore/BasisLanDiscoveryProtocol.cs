using MeaMod.DNS.Multicast;
using System;
using System.Collections.Generic;
using System.Net;
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
            string legacyKey,
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
                    return ReadLegacy(properties, legacyKey, maxBytes);
                }
                encoded.Append(chunk);
            }

            if (encoded.Length != 0)
            {
                try
                {
                    byte[] bytes = Convert.FromBase64String(encoded.ToString());
                    if (bytes.Length <= maxBytes)
                    {
                        return StrictUtf8.GetString(bytes);
                    }
                }
                catch (Exception ex) when (ex is FormatException || ex is DecoderFallbackException)
                {
                }
            }

            return ReadLegacy(properties, legacyKey, maxBytes);
        }

        private static string ReadLegacy(
            Dictionary<string, string> properties,
            string key,
            int maxBytes)
        {
            properties.TryGetValue(key, out string value);
            return LimitUtf8(value, maxBytes);
        }

        private static int TxtValueBudget(string key)
        {
            return 254 - Encoding.ASCII.GetByteCount(key);
        }
    }
}
