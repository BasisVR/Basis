using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace Basis.Network.Server.Generic
{
    public static class BasisServerBeeKeys
    {
        public const int HeaderBytes = 8, IterationCount = 10000, IvBytes = 16;
        public const long MaxConnectorBytes = 64L * 1024 * 1024, MaxSkipBytes = 512L * 1024 * 1024;
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        public static async Task<(byte[] Grant, string Error)> DeriveAsync(string url, string password, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrEmpty(password)) return (null, "The content has no password.");
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return (null, "The server can only read content keys from http or https addresses.");
                byte[] header = await ReadAsync(uri, 0, HeaderBytes, cancellationToken).ConfigureAwait(false);
                long connectorLength = BitConverter.ToInt64(header, 0);
                if (connectorLength <= BasisContentKeyProtocol.SaltBytes + IvBytes || connectorLength > MaxConnectorBytes) return (null, "The content header is invalid.");
                byte[] connector = await ReadAsync(uri, HeaderBytes, (int)connectorLength, cancellationToken).ConfigureAwait(false);
                byte[] json = Decrypt(password, connector);
                if (json == null) return (null, "The password does not unlock this content.");
                List<(long EndByte, List<long> Payloads)> sections = ParseSections(json);
                if (sections == null || sections.Count == 0) return (null, "The content description is damaged.");
                List<byte[]> salts = new List<byte[]> { Slice(connector, 0, BasisContentKeyProtocol.SaltBytes) };
                long start = HeaderBytes + connectorLength;
                foreach ((long endByte, List<long> payloads) in sections)
                {
                    salts.Add(await ReadAsync(uri, start, BasisContentKeyProtocol.SaltBytes, cancellationToken).ConfigureAwait(false));
                    foreach (long offset in payloads) salts.Add(await ReadAsync(uri, start + offset, BasisContentKeyProtocol.SaltBytes, cancellationToken).ConfigureAwait(false));
                    start += endByte;
                }
                if (salts.Count > BasisContentKeyProtocol.MaxGrantEntries) salts.RemoveRange(BasisContentKeyProtocol.MaxGrantEntries, salts.Count - BasisContentKeyProtocol.MaxGrantEntries);
                return (await Task.Run(() => Derive(password, salts), cancellationToken).ConfigureAwait(false), null);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }
        public static byte[] Derive(string password, IReadOnlyList<byte[]> salts)
        {
            byte[] grant = new byte[2 + salts.Count * BasisContentKeyProtocol.GrantEntryBytes];
            grant[0] = (byte)salts.Count;
            grant[1] = (byte)(salts.Count >> 8);
            Parallel.For(0, salts.Count, index =>
            {
                int offset = 2 + index * BasisContentKeyProtocol.GrantEntryBytes;
                byte[] key = Key(password, salts[index]);
                Buffer.BlockCopy(salts[index], 0, grant, offset, BasisContentKeyProtocol.SaltBytes);
                Buffer.BlockCopy(key, 0, grant, offset + BasisContentKeyProtocol.SaltBytes, BasisContentKeyProtocol.KeyBytes);
                Array.Clear(key, 0, key.Length);
            });
            return grant;
        }
        public static byte[] Key(string password, byte[] salt)
        {
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            try
            {
                using Rfc2898DeriveBytes pbkdf2 = new Rfc2898DeriveBytes(passwordBytes, salt, IterationCount, HashAlgorithmName.SHA1);
                return pbkdf2.GetBytes(BasisContentKeyProtocol.KeyBytes);
            }
            finally
            {
                Array.Clear(passwordBytes, 0, passwordBytes.Length);
            }
        }
        public static byte[] Decrypt(string password, byte[] blob)
        {
            if (blob == null || blob.Length <= BasisContentKeyProtocol.SaltBytes + IvBytes) return null;
            byte[] key = Key(password, Slice(blob, 0, BasisContentKeyProtocol.SaltBytes));
            try
            {
                using Aes aes = Aes.Create();
                aes.Key = key;
                aes.IV = Slice(blob, BasisContentKeyProtocol.SaltBytes, IvBytes);
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using ICryptoTransform decryptor = aes.CreateDecryptor();
                int offset = BasisContentKeyProtocol.SaltBytes + IvBytes;
                return decryptor.TransformFinalBlock(blob, offset, blob.Length - offset);
            }
            catch (CryptographicException)
            {
                return null;
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }
        public static List<(long EndByte, List<long> Payloads)> ParseSections(byte[] json)
        {
            int position = 0;
            string text = Encoding.UTF8.GetString(json);
            if (!(ParseValue(text, ref position) is Dictionary<string, object> root)) return null;
            if (root.TryGetValue("Value", out object value) && value is Dictionary<string, object> wrapped) root = wrapped;
            if (!root.TryGetValue("BasisBundleGenerated", out object generated) || !(generated is List<object> list)) return null;
            List<(long, List<long>)> sections = new List<(long, List<long>)>(list.Count);
            foreach (object entry in list)
            {
                if (!(entry is Dictionary<string, object> section) || !TryLong(section, "EndByte", out long endByte) || endByte <= 0) return null;
                List<long> payloads = new List<long>();
                if (section.TryGetValue("GraphicsStatePayloads", out object payloadValue) && payloadValue is List<object> payloadList)
                {
                    foreach (object payloadEntry in payloadList)
                    {
                        if (payloadEntry is Dictionary<string, object> payload && TryLong(payload, "Length", out long length) && TryLong(payload, "Offset", out long offset) && length > 0 && offset > 0 && offset < endByte) payloads.Add(offset);
                    }
                }
                sections.Add((endByte, payloads));
            }
            return sections;
        }
        private static async Task<byte[]> ReadAsync(Uri uri, long start, int length, CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(start, start + length - 1);
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK) throw new IOException($"The content host answered {(int)response.StatusCode}.");
            long skip = response.StatusCode == HttpStatusCode.OK ? start : 0;
            if (skip > MaxSkipBytes) throw new IOException("The content host does not support partial downloads.");
            using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            byte[] buffer = new byte[Math.Max(length, 81920)];
            while (skip > 0)
            {
                int read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, skip), cancellationToken).ConfigureAwait(false);
                if (read <= 0) throw new IOException("The content file is shorter than its description.");
                skip -= read;
            }
            byte[] data = new byte[length];
            int total = 0;
            while (total < length)
            {
                int read = await stream.ReadAsync(data, total, length - total, cancellationToken).ConfigureAwait(false);
                if (read <= 0) throw new IOException("The content file is shorter than its description.");
                total += read;
            }
            return data;
        }
        private static byte[] Slice(byte[] source, int offset, int count)
        {
            byte[] result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }
        private static bool TryLong(Dictionary<string, object> map, string name, out long value)
        {
            value = 0;
            return map.TryGetValue(name, out object raw) && raw is string number && long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
        private static object ParseValue(string text, ref int position)
        {
            SkipSpace(text, ref position);
            if (position >= text.Length) throw new FormatException("Unexpected end of JSON.");
            char c = text[position];
            if (c == '{')
            {
                position++;
                Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipSpace(text, ref position);
                if (Peek(text, position) == '}') { position++; return map; }
                while (true)
                {
                    SkipSpace(text, ref position);
                    string key = ParseString(text, ref position);
                    SkipSpace(text, ref position);
                    Expect(text, ref position, ':');
                    map[key] = ParseValue(text, ref position);
                    SkipSpace(text, ref position);
                    if (Peek(text, position) == ',') { position++; continue; }
                    Expect(text, ref position, '}');
                    return map;
                }
            }
            if (c == '[')
            {
                position++;
                List<object> list = new List<object>();
                SkipSpace(text, ref position);
                if (Peek(text, position) == ']') { position++; return list; }
                while (true)
                {
                    list.Add(ParseValue(text, ref position));
                    SkipSpace(text, ref position);
                    if (Peek(text, position) == ',') { position++; continue; }
                    Expect(text, ref position, ']');
                    return list;
                }
            }
            if (c == '"') return new Text(ParseString(text, ref position));
            int begin = position;
            while (position < text.Length && "+-0123456789.eEtruefalsn".IndexOf(text[position]) >= 0) position++;
            string literal = text.Substring(begin, position - begin);
            if (literal.Length == 0) throw new FormatException("Unexpected character in JSON.");
            return literal == "null" ? null : literal;
        }
        private sealed class Text
        {
            public readonly string Value;
            public Text(string value) { Value = value; }
        }
        private static string ParseString(string text, ref int position)
        {
            Expect(text, ref position, '"');
            StringBuilder builder = null;
            int begin = position;
            while (position < text.Length)
            {
                char c = text[position];
                if (c == '"')
                {
                    string result = builder == null ? text.Substring(begin, position - begin) : builder.Append(text, begin, position - begin).ToString();
                    position++;
                    return result;
                }
                if (c == '\\')
                {
                    builder ??= new StringBuilder();
                    builder.Append(text, begin, position - begin);
                    position++;
                    char escape = Peek(text, position);
                    position++;
                    switch (escape)
                    {
                        case 'u':
                            if (position + 4 > text.Length) throw new FormatException("Bad JSON escape.");
                            builder.Append((char)Convert.ToInt32(text.Substring(position, 4), 16));
                            position += 4;
                            break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        default: builder.Append(escape); break;
                    }
                    begin = position;
                    continue;
                }
                position++;
            }
            throw new FormatException("Unterminated JSON string.");
        }
        private static void SkipSpace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        }
        private static char Peek(string text, int position) => position < text.Length ? text[position] : '\0';
        private static void Expect(string text, ref int position, char expected)
        {
            if (Peek(text, position) != expected) throw new FormatException($"Expected '{expected}' in JSON.");
            position++;
        }
    }
}
