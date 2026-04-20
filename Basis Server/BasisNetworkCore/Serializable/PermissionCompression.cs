using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Basis.Network.Core
{
    /// <summary>
    /// Deflate compression for extra permission strings sent over the wire.
    /// Wire format: [byte flag][payload...]
    ///   flag 0 = raw UTF8, flag 1 = Deflate compressed.
    /// Strings are NUL-joined before compression.
    /// </summary>
    public static class PermissionCompression
    {
        /// <summary>
        /// Compresses an array of permission strings into a single byte payload.
        /// Uses Deflate when it saves space, otherwise sends raw UTF8.
        /// </summary>
        public static byte[] CompressExtras(string[] strings)
        {
            if (strings == null || strings.Length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] raw = Encoding.UTF8.GetBytes(string.Join("\0", strings));

            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    ds.Write(raw, 0, raw.Length);
                }
                deflated = ms.ToArray();
            }

            // Pick whichever is smaller (1 byte flag overhead)
            if (deflated.Length < raw.Length)
            {
                byte[] result = new byte[1 + deflated.Length];
                result[0] = 1; // compressed
                Buffer.BlockCopy(deflated, 0, result, 1, deflated.Length);
                return result;
            }
            else
            {
                byte[] result = new byte[1 + raw.Length];
                result[0] = 0; // raw
                Buffer.BlockCopy(raw, 0, result, 1, raw.Length);
                return result;
            }
        }

        /// <summary>
        /// Decompresses a byte payload back into the original string array.
        /// </summary>
        public static string[] DecompressExtras(byte[] data, int expectedCount)
        {
            if (data == null || data.Length < 1 || expectedCount == 0)
            {
                return Array.Empty<string>();
            }

            byte flag = data[0];
            byte[] payload;

            if (flag == 1)
            {
                using (var ms = new MemoryStream(data, 1, data.Length - 1))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    ds.CopyTo(output);
                    payload = output.ToArray();
                }
            }
            else
            {
                payload = new byte[data.Length - 1];
                Buffer.BlockCopy(data, 1, payload, 0, payload.Length);
            }

            string joined = Encoding.UTF8.GetString(payload);
            string[] result = joined.Split('\0');

            return result;
        }
    }
}
