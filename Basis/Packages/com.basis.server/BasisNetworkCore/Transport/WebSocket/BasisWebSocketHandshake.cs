using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    public static class BasisWebSocketHandshake
    {
        public const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        public const int MaxRequestBytes = 16 * 1024;

        public sealed class Request
        {
            public string Method = string.Empty;
            public string Path = string.Empty;
            public string Version = string.Empty;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string Header(string name)
            {
                return Headers.TryGetValue(name, out string value) ? value : null;
            }

            public bool IsWebSocketUpgrade
            {
                get
                {
                    string upgrade = Header("Upgrade");
                    string connection = Header("Connection");
                    return string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase)
                        && upgrade != null && upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0
                        && connection != null && connection.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                        && !string.IsNullOrEmpty(Header("Sec-WebSocket-Key"));
                }
            }

            public bool OffersProtocol(string protocol)
            {
                string offered = Header("Sec-WebSocket-Protocol");
                if (string.IsNullOrEmpty(offered)) return false;
                foreach (string part in offered.Split(','))
                {
                    if (string.Equals(part.Trim(), protocol, StringComparison.Ordinal)) return true;
                }
                return false;
            }
        }

        public static async Task<Request> ReadRequestAsync(Stream stream, CancellationToken token)
        {
            byte[] buffer = new byte[2048];
            int count = 0;
            while (true)
            {
                if (count == buffer.Length)
                {
                    if (buffer.Length >= MaxRequestBytes) return null;
                    Array.Resize(ref buffer, Math.Min(MaxRequestBytes, buffer.Length * 2));
                }
                int read = await stream.ReadAsync(buffer, count, buffer.Length - count, token).ConfigureAwait(false);
                if (read <= 0) return null;
                int searchFrom = Math.Max(0, count - 3);
                count += read;
                int end = FindHeaderEnd(buffer, searchFrom, count);
                if (end < 0) continue;
                if (end != count) return null;
                return Parse(Encoding.ASCII.GetString(buffer, 0, end));
            }
        }

        private static int FindHeaderEnd(byte[] buffer, int from, int count)
        {
            for (int index = from; index + 3 < count; index++)
            {
                if (buffer[index] == '\r' && buffer[index + 1] == '\n' && buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
                {
                    return index + 4;
                }
            }
            return -1;
        }

        public static Request Parse(string text)
        {
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 3) return null;
            Request request = new Request
            {
                Method = requestLine[0],
                Path = requestLine[1],
                Version = requestLine[2],
            };
            for (int index = 1; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (request.Headers.TryGetValue(name, out string existing))
                {
                    request.Headers[name] = existing + ", " + value;
                }
                else
                {
                    request.Headers[name] = value;
                }
            }
            return request;
        }

        public static string ComputeAccept(string key)
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + AcceptGuid));
                return Convert.ToBase64String(hash);
            }
        }

        public static byte[] BuildSwitchingProtocols(string accept, string protocol)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("HTTP/1.1 101 Switching Protocols\r\n");
            builder.Append("Upgrade: websocket\r\n");
            builder.Append("Connection: Upgrade\r\n");
            builder.Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n");
            if (!string.IsNullOrEmpty(protocol))
            {
                builder.Append("Sec-WebSocket-Protocol: ").Append(protocol).Append("\r\n");
            }
            builder.Append("\r\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        public static byte[] BuildPlainResponse(int status, string reason, string body)
        {
            byte[] content = Encoding.UTF8.GetBytes(body ?? string.Empty);
            StringBuilder builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            builder.Append("Content-Type: text/plain; charset=utf-8\r\n");
            builder.Append("Content-Length: ").Append(content.Length).Append("\r\n");
            builder.Append("Cache-Control: no-store\r\n");
            if (status == 426)
            {
                builder.Append("Upgrade: websocket\r\n");
                builder.Append("Sec-WebSocket-Version: 13\r\n");
            }
            builder.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(builder.ToString());
            byte[] response = new byte[head.Length + content.Length];
            Buffer.BlockCopy(head, 0, response, 0, head.Length);
            Buffer.BlockCopy(content, 0, response, head.Length, content.Length);
            return response;
        }

        public static bool PathMatches(string configuredPath, string requestPath)
        {
            if (string.IsNullOrEmpty(configuredPath) || configuredPath == "*") return true;
            string path = requestPath ?? string.Empty;
            int query = path.IndexOf('?');
            if (query >= 0) path = path.Substring(0, query);
            string expected = configuredPath.StartsWith("/", StringComparison.Ordinal) ? configuredPath : "/" + configuredPath;
            return string.Equals(path.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        public static bool OriginAllowed(string allowedOrigins, string origin)
        {
            if (string.IsNullOrWhiteSpace(allowedOrigins)) return true;
            if (string.IsNullOrEmpty(origin)) return true;
            foreach (string part in allowedOrigins.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string allowed = part.Trim().TrimEnd('/');
                if (allowed == "*") return true;
                if (string.Equals(allowed, origin.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static IPAddress ResolveClientAddress(IPAddress immediate, Request request, BasisTrustedProxyList proxies)
        {
            IPAddress client = Normalize(immediate);
            if (request == null || proxies == null || !proxies.Contains(client)) return client;
            string forwarded = request.Header("X-Forwarded-For");
            if (!string.IsNullOrEmpty(forwarded))
            {
                string[] hops = forwarded.Split(',');
                for (int index = hops.Length - 1; index >= 0; index--)
                {
                    if (!TryParseHop(hops[index], out IPAddress hop)) break;
                    client = hop;
                    if (!proxies.Contains(hop)) return hop;
                }
                return client;
            }
            string realIp = request.Header("X-Real-IP");
            if (!string.IsNullOrEmpty(realIp) && TryParseHop(realIp, out IPAddress real)) return real;
            return client;
        }

        private static bool TryParseHop(string value, out IPAddress address)
        {
            address = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string text = value.Trim();
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                int close = text.IndexOf(']');
                if (close > 0) text = text.Substring(1, close - 1);
            }
            else if (text.IndexOf(':') > 0 && text.IndexOf(':') == text.LastIndexOf(':'))
            {
                text = text.Substring(0, text.IndexOf(':'));
            }
            if (!IPAddress.TryParse(text, out IPAddress parsed)) return false;
            address = Normalize(parsed);
            return true;
        }

        public static IPAddress Normalize(IPAddress address)
        {
            if (address != null && address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            {
                return address.MapToIPv4();
            }
            return address;
        }
    }

    public sealed class BasisTrustedProxyList
    {
        private readonly List<KeyValuePair<byte[], int>> _networks = new List<KeyValuePair<byte[], int>>();

        public BasisTrustedProxyList(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return;
            foreach (string part in spec.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string text = part.Trim();
                int prefix = -1;
                int slash = text.IndexOf('/');
                if (slash > 0)
                {
                    if (!int.TryParse(text.Substring(slash + 1), out prefix)) continue;
                    text = text.Substring(0, slash);
                }
                if (!IPAddress.TryParse(text, out IPAddress address)) continue;
                address = BasisWebSocketHandshake.Normalize(address);
                byte[] bytes = address.GetAddressBytes();
                int bits = bytes.Length * 8;
                if (prefix < 0 || prefix > bits) prefix = bits;
                _networks.Add(new KeyValuePair<byte[], int>(bytes, prefix));
            }
        }

        public bool IsEmpty => _networks.Count == 0;

        public bool Contains(IPAddress address)
        {
            if (address == null || _networks.Count == 0) return false;
            byte[] bytes = BasisWebSocketHandshake.Normalize(address).GetAddressBytes();
            foreach (KeyValuePair<byte[], int> network in _networks)
            {
                if (network.Key.Length != bytes.Length) continue;
                if (PrefixMatches(network.Key, bytes, network.Value)) return true;
            }
            return false;
        }

        private static bool PrefixMatches(byte[] network, byte[] candidate, int prefix)
        {
            int fullBytes = prefix / 8;
            for (int index = 0; index < fullBytes; index++)
            {
                if (network[index] != candidate[index]) return false;
            }
            int remainder = prefix % 8;
            if (remainder == 0) return true;
            int mask = 0xFF << (8 - remainder) & 0xFF;
            return (network[fullBytes] & mask) == (candidate[fullBytes] & mask);
        }
    }
}
