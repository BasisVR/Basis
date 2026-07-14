using System;
using System.IO;
using System.Net;
using System.Text;

namespace Basis.Network.Core
{
    /// <summary>Metadata carried by Basis LAN discovery packets and DNS-SD records.</summary>
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

    /// <summary>Shared Basis LAN datagram format used by servers and clients.</summary>
    public static class BasisLanDiscoveryProtocol
    {
        public const int DiscoveryPort = 42960;
        public const uint Magic = 0xBA515201u;
        public const ushort Version = 1;
        public const int MaxPacketBytes = 1024;
        public const int MaxStackIdBytes = 64;
        public const int MaxServerNameBytes = 128;
        public const int MaxMotdBytes = 384;

        public static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.42.99");

        public static byte[] Serialize(BasisLanAdvertisement advertisement)
        {
            using (MemoryStream stream = new MemoryStream(256))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(advertisement.InstanceId.ToByteArray());
                writer.Write(advertisement.ServerPort);
                writer.Write(advertisement.RequiresPassword ? (byte)1 : (byte)0);
                WriteString(writer, advertisement.NetworkStackId, MaxStackIdBytes);
                WriteString(writer, advertisement.ServerName, MaxServerNameBytes);
                WriteString(writer, advertisement.Motd, MaxMotdBytes);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryDeserialize(byte[] data, out BasisLanAdvertisement advertisement)
        {
            advertisement = default;
            if (data == null || data.Length < 31 || data.Length > MaxPacketBytes)
            {
                return false;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(data, false))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version)
                    {
                        return false;
                    }

                    byte[] guidBytes = reader.ReadBytes(16);
                    if (guidBytes.Length != 16)
                    {
                        return false;
                    }

                    ushort serverPort = reader.ReadUInt16();
                    byte flags = reader.ReadByte();
                    if (serverPort == 0
                        || (flags & ~1) != 0
                        || !TryReadString(reader, stream, MaxStackIdBytes, out string stackId)
                        || !TryReadString(reader, stream, MaxServerNameBytes, out string serverName)
                        || !TryReadString(reader, stream, MaxMotdBytes, out string motd))
                    {
                        return false;
                    }

                    advertisement = new BasisLanAdvertisement(
                        new Guid(guidBytes),
                        serverPort,
                        (flags & 1) != 0,
                        stackId,
                        serverName,
                        motd);
                    return advertisement.InstanceId != Guid.Empty;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string LimitUtf8(string value, int maxBytes)
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

        private static void WriteString(BinaryWriter writer, string value, int maxBytes)
        {
            string limited = LimitUtf8(value, maxBytes);
            byte[] bytes = Encoding.UTF8.GetBytes(limited);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static bool TryReadString(BinaryReader reader, MemoryStream stream, int maxBytes, out string value)
        {
            value = string.Empty;
            if (stream.Length - stream.Position < sizeof(ushort))
            {
                return false;
            }

            ushort byteCount = reader.ReadUInt16();
            if (byteCount > maxBytes || stream.Length - stream.Position < byteCount)
            {
                return false;
            }

            byte[] bytes = reader.ReadBytes(byteCount);
            if (bytes.Length != byteCount)
            {
                return false;
            }

            value = Encoding.UTF8.GetString(bytes);
            return true;
        }
    }
}
