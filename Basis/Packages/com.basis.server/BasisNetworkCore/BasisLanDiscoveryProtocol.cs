using System;
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

    /// <summary>Shared constants and text limits for Basis LAN DNS-SD.</summary>
    internal static class BasisLanDiscoveryProtocol
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public const string ServiceName = "_basisvr._udp";
        public const string ProtocolVersion = "1";
        public const int MaxStackIdBytes = 64;
        public const int MaxServerNameBytes = 128;
        public const int MaxMotdBytes = 384;

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

        internal static string LimitTxtProperty(string key, string value, int maxValueBytes)
        {
            int txtBudget = Math.Max(0, 254 - Encoding.UTF8.GetByteCount(key ?? string.Empty));
            return LimitUtf8(value, Math.Min(maxValueBytes, txtBudget));
        }

        internal static bool IsAscii(string value)
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

        internal static string EncodeTxtUtf8(string value, int maxBytes)
        {
            string limited = LimitUtf8(value, maxBytes);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(limited));
        }

        internal static bool TryDecodeTxtUtf8(string encoded, int maxBytes, out string value)
        {
            value = string.Empty;
            if (encoded == null || maxBytes < 0)
            {
                return false;
            }

            int maxEncodedLength = ((maxBytes + 2) / 3) * 4;
            if (encoded.Length > maxEncodedLength)
            {
                return false;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
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
    }
}
