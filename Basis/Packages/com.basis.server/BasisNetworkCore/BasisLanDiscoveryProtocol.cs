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
            if (address == null
                || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.Broadcast)
                || address.IsIPv6Multicast
                || (!allowLoopback && IPAddress.IsLoopback(address)))
            {
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();
                return bytes.Length == 4 && (bytes[0] < 224 || bytes[0] > 239);
            }

            return address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        public static int PreferenceRank(IPAddress address)
        {
            if (address == null)
            {
                return int.MaxValue;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();
                bool linkLocal = bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
                return linkLocal ? 2 : 0;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6LinkLocal ? 3 : 1;
            }

            return int.MaxValue;
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
        internal const int MaxEncodedChunks = 10;

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
            string legacyKey,
            string encodedKey,
            string value,
            int maxBytes)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrEmpty(legacyKey)) throw new ArgumentException("TXT key is required.", nameof(legacyKey));
            if (string.IsNullOrEmpty(encodedKey)) throw new ArgumentException("Encoded TXT key is required.", nameof(encodedKey));

            string limited = LimitUtf8(value, maxBytes);
            int legacyBudget = TxtValueBudget(legacyKey);
            bool ascii = IsAscii(limited);
            if (ascii)
            {
                string legacy = LimitUtf8(limited, Math.Min(maxBytes, legacyBudget));
                profile.AddProperty(legacyKey, legacy);
                if (Encoding.ASCII.GetByteCount(limited) <= legacyBudget)
                {
                    return;
                }
            }

            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(limited));
            int offset = 0;
            for (int index = 0; offset < encoded.Length; index++)
            {
                if (index >= MaxEncodedChunks)
                {
                    throw new InvalidOperationException("Basis LAN metadata requires too many TXT chunks.");
                }

                string chunkKey = $"{encodedKey}-{index}";
                int chunkBudget = TxtValueBudget(chunkKey);
                if (chunkBudget <= 0)
                {
                    throw new InvalidOperationException("Basis LAN TXT key leaves no room for a value.");
                }

                int chunkLength = Math.Min(chunkBudget, encoded.Length - offset);
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
            if (TryReadEncodedMetadata(properties, encodedKey, maxBytes, out string decoded))
            {
                return decoded;
            }

            properties.TryGetValue(legacyKey, out string legacy);
            return LimitUtf8(legacy, maxBytes);
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
            for (int index = 1; index < MaxEncodedChunks; index++)
            {
                if (!properties.TryGetValue($"{encodedKey}-{index}", out string chunk))
                {
                    break;
                }
                encoded.Append(chunk);
            }

            int maxEncodedLength = ((maxBytes + 2) / 3) * 4;
            if (encoded.Length > maxEncodedLength)
            {
                return false;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(encoded.ToString());
                if (bytes.Length > maxBytes)
                {
                    return false;
                }
                value = StrictUtf8.GetString(bytes);
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is DecoderFallbackException)
            {
                return false;
            }
        }

        private static int TxtValueBudget(string key)
        {
            return Math.Max(0, 254 - Encoding.ASCII.GetByteCount(key ?? string.Empty));
        }

        private static bool IsAscii(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 0x7F)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
