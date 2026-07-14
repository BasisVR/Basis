using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Basis.Scripts.Networking
{
    /// <summary>Minimal DNS wire support used only by Basis LAN mDNS.</summary>
    internal static class BasisLanMdnsWire
    {
        private const string ServiceType = "_basisvr._udp.local";
        public const int QueryIntervalMs = 2000;

        private const string ProtocolVersion = "1";
        private const int MaxPacketBytes = 9000;
        private const int MaxQuestions = 64;
        private const int MaxRecords = 256;

        internal const ushort A = 1;
        internal const ushort Ptr = 12;
        internal const ushort Txt = 16;
        internal const ushort Aaaa = 28;
        internal const ushort Srv = 33;
        internal const ushort In = 1;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal sealed class Record
        {
            public string Name;
            public ushort Type;
            public ushort Class;
            public uint Ttl;
            public string DomainName;
            public ushort Port;
            public string Target;
            public List<string> TxtValues;
            public IPAddress Address;
        }

        internal sealed class Message
        {
            public bool IsResponse;
            public readonly List<Record> Records = new List<Record>();
        }

        public static byte[] BuildQuery()
        {
            using MemoryStream stream = new MemoryStream(64);
            W16(stream, 0);
            W16(stream, 0);
            W16(stream, 1);
            W16(stream, 0);
            W16(stream, 0);
            W16(stream, 0);
            Bytes(stream, EncodeName(ServiceType));
            W16(stream, Ptr);
            W16(stream, In);
            return stream.ToArray();
        }

        public static bool TryParse(byte[] packet, out Message message)
        {
            message = null;
            if (packet == null || packet.Length < 12 || packet.Length > MaxPacketBytes)
            {
                return false;
            }

            try
            {
                int offset = 0;
                U16(packet, ref offset);
                ushort flags = U16(packet, ref offset);
                ushort questionCount = U16(packet, ref offset);
                ushort answerCount = U16(packet, ref offset);
                ushort authorityCount = U16(packet, ref offset);
                ushort additionalCount = U16(packet, ref offset);
                int recordCount = answerCount + authorityCount + additionalCount;
                if (questionCount > MaxQuestions || recordCount > MaxRecords)
                {
                    return false;
                }

                if ((flags & 0x780F) != 0)
                {
                    return false;
                }

                Message parsed = new Message { IsResponse = (flags & 0x8000) != 0 };
                for (int i = 0; i < questionCount; i++)
                {
                    if (!ReadName(packet, ref offset, out string name) || packet.Length - offset < 4)
                    {
                        return false;
                    }
                    U16(packet, ref offset);
                    U16(packet, ref offset);
                }
                for (int i = 0; i < recordCount; i++)
                {
                    if (!ReadRecord(packet, ref offset, out Record record))
                    {
                        return false;
                    }
                    parsed.Records.Add(record);
                }
                message = parsed;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is DecoderFallbackException || ex is OverflowException)
            {
                return false;
            }
        }

        public static void Extract(
            Message message,
            IPEndPoint remote,
            Action<BasisLanAdvertisement, IPAddress> found,
            Action<Guid> removed)
        {
            if (message == null || !message.IsResponse)
            {
                return;
            }

            foreach (Record pointer in message.Records)
            {
                if (pointer.Type != Ptr || !EqualName(pointer.Name, ServiceType) || string.IsNullOrWhiteSpace(pointer.DomainName))
                {
                    continue;
                }

                if (pointer.Ttl == 0)
                {
                    if (TryInstanceId(message, pointer.DomainName, out Guid id))
                    {
                        removed?.Invoke(id);
                    }
                    continue;
                }

                Record srv = null;
                Record txt = null;
                foreach (Record record in message.Records)
                {
                    if (!EqualName(record.Name, pointer.DomainName))
                    {
                        continue;
                    }
                    if (record.Type == Srv && srv == null) srv = record;
                    else if (record.Type == Txt && txt == null) txt = record;
                }
                if (srv == null || srv.Port == 0 || string.IsNullOrWhiteSpace(srv.Target) || txt?.TxtValues == null)
                {
                    continue;
                }

                Dictionary<string, string> values = Properties(txt.TxtValues);
                if (!values.TryGetValue("protocol", out string protocol)
                    || protocol != ProtocolVersion
                    || !values.TryGetValue("id", out string idText)
                    || !Guid.TryParseExact(idText, "N", out Guid instanceId)
                    || instanceId == Guid.Empty
                    || !values.TryGetValue("pwd", out string password)
                    || (password != "0" && password != "1"))
                {
                    continue;
                }

                IPAddress address = SelectAddress(message, srv.Target, remote?.Address);
                if (address == null)
                {
                    continue;
                }

                values.TryGetValue("stack", out string stack);
                values.TryGetValue("name", out string name);
                values.TryGetValue("motd", out string motd);
                found?.Invoke(new BasisLanAdvertisement(
                    instanceId,
                    srv.Port,
                    password == "1",
                    LimitUtf8(stack, BasisLanDiscoveryProtocol.MaxStackIdBytes),
                    LimitUtf8(name, BasisLanDiscoveryProtocol.MaxServerNameBytes),
                    LimitUtf8(motd, BasisLanDiscoveryProtocol.MaxMotdBytes)), address);
            }
        }

        public static string LimitUtf8(string value, int maxBytes)
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

        private static byte[] EncodeName(string value)
        {
            string normalized = Normalize(value);
            using MemoryStream stream = new MemoryStream(128);
            if (normalized.Length != 0)
            {
                foreach (string label in normalized.Split('.'))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(label);
                    if (bytes.Length == 0 || bytes.Length > 63) throw new InvalidOperationException("Invalid mDNS label.");
                    stream.WriteByte((byte)bytes.Length); Bytes(stream, bytes);
                }
            }
            stream.WriteByte(0);
            if (stream.Length > 255) throw new InvalidOperationException("Invalid mDNS name.");
            return stream.ToArray();
        }

        private static bool ReadRecord(byte[] packet, ref int offset, out Record record)
        {
            record = null;
            if (!ReadName(packet, ref offset, out string name) || packet.Length - offset < 10) return false;
            ushort type = U16(packet, ref offset);
            ushort recordClass = U16(packet, ref offset);
            uint ttl = U32(packet, ref offset);
            ushort length = U16(packet, ref offset);
            int start = offset;
            int end = start + length;
            if (end < start || end > packet.Length) return false;

            Record parsed = new Record { Name = name, Type = type, Class = recordClass, Ttl = ttl };
            if (type == Ptr)
            {
                int pos = start;
                if (!ReadName(packet, ref pos, out parsed.DomainName) || pos > end) return false;
            }
            else if (type == Srv)
            {
                if (length < 7) return false;
                int pos = start + 4;
                parsed.Port = U16(packet, ref pos);
                if (!ReadName(packet, ref pos, out parsed.Target) || pos > end) return false;
            }
            else if (type == Txt)
            {
                parsed.TxtValues = new List<string>();
                int pos = start;
                while (pos < end)
                {
                    int size = packet[pos++];
                    if (pos + size > end) return false;
                    parsed.TxtValues.Add(StrictUtf8.GetString(packet, pos, size));
                    pos += size;
                }
            }
            else if ((type == A && length == 4) || (type == Aaaa && length == 16))
            {
                byte[] bytes = new byte[length];
                Buffer.BlockCopy(packet, start, bytes, 0, length);
                parsed.Address = new IPAddress(bytes);
            }
            offset = end;
            record = parsed;
            return true;
        }

        private static bool ReadName(byte[] packet, ref int offset, out string value)
        {
            value = string.Empty;
            if (offset < 0 || offset >= packet.Length) return false;
            StringBuilder result = new StringBuilder(64);
            HashSet<int> pointers = null;
            int pos = offset;
            int resume = -1;
            int encoded = 1;
            while (true)
            {
                if (pos >= packet.Length) return false;
                byte length = packet[pos++];
                if (length == 0)
                {
                    offset = resume >= 0 ? resume : pos;
                    value = result.ToString();
                    return true;
                }
                if ((length & 0xC0) == 0xC0)
                {
                    if (pos >= packet.Length) return false;
                    int pointer = ((length & 0x3F) << 8) | packet[pos++];
                    if (pointer >= packet.Length) return false;
                    resume = resume >= 0 ? resume : pos;
                    pointers ??= new HashSet<int>();
                    if (!pointers.Add(pointer) || pointers.Count > 32) return false;
                    pos = pointer;
                    continue;
                }
                if ((length & 0xC0) != 0 || length > 63 || pos + length > packet.Length) return false;
                encoded += length + 1;
                if (encoded > 255) return false;
                if (result.Length != 0) result.Append('.');
                result.Append(StrictUtf8.GetString(packet, pos, length));
                pos += length;
            }
        }

        private static bool TryInstanceId(Message message, string instanceName, out Guid id)
        {
            id = Guid.Empty;
            foreach (Record record in message.Records)
            {
                if (record.Type == Txt && EqualName(record.Name, instanceName) && record.TxtValues != null)
                {
                    Dictionary<string, string> values = Properties(record.TxtValues);
                    if (values.TryGetValue("id", out string text) && Guid.TryParseExact(text, "N", out id) && id != Guid.Empty)
                        return true;
                }
            }
            string label = Normalize(instanceName);
            int dot = label.IndexOf('.');
            if (dot >= 0) label = label.Substring(0, dot);
            return label.StartsWith("Basis-", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(label.Substring(6), "N", out id)
                && id != Guid.Empty;
        }

        private static Dictionary<string, string> Properties(List<string> values)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                int separator = value?.IndexOf('=') ?? -1;
                if (separator > 0 && !result.ContainsKey(value.Substring(0, separator)))
                    result.Add(value.Substring(0, separator), value.Substring(separator + 1));
            }
            return result;
        }

        private static IPAddress SelectAddress(Message message, string target, IPAddress remote)
        {
            IPAddress ipv6 = null;
            foreach (Record record in message.Records)
            {
                if (!EqualName(record.Name, target) || !Usable(record.Address)) continue;
                if (record.Address.AddressFamily == AddressFamily.InterNetwork) return record.Address;
                if (record.Address.AddressFamily == AddressFamily.InterNetworkV6 && ipv6 == null) ipv6 = record.Address;
            }
            if (Usable(remote) && remote.AddressFamily == AddressFamily.InterNetwork) return remote;
            if (ipv6 != null)
            {
                if (ipv6.IsIPv6LinkLocal
                    && ipv6.ScopeId == 0
                    && remote != null
                    && remote.AddressFamily == AddressFamily.InterNetworkV6
                    && remote.ScopeId != 0)
                {
                    return new IPAddress(ipv6.GetAddressBytes(), remote.ScopeId);
                }
                return ipv6;
            }
            return Usable(remote) ? remote : null;
        }

        private static bool EqualName(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        private static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('.');
        private static bool Usable(IPAddress address) => address != null && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.Broadcast) && !address.IsIPv6Multicast;

        private static ushort U16(byte[] packet, ref int offset)
        {
            if (packet.Length - offset < 2) throw new IOException();
            ushort value = (ushort)((packet[offset] << 8) | packet[offset + 1]);
            offset += 2;
            return value;
        }

        private static uint U32(byte[] packet, ref int offset)
        {
            if (packet.Length - offset < 4) throw new IOException();
            uint value = ((uint)packet[offset] << 24) | ((uint)packet[offset + 1] << 16) | ((uint)packet[offset + 2] << 8) | packet[offset + 3];
            offset += 4;
            return value;
        }

        private static void W16(Stream stream, ushort value) { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static void Bytes(Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
    }
}
