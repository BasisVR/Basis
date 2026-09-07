using System.Buffers;
using System.IO;

namespace Basis.Network.Core
{
    /// <summary>
    /// Reads a decompression stream to its end while refusing to buffer more than the protocol
    /// says the payload may be.
    ///
    /// Deflate and Brotli both reach ratios in the thousands on repetitive input, and the frame's
    /// declared length is the COMPRESSED length, so a receiver cannot know the real size before it
    /// starts copying. Stream.CopyTo has no ceiling at all, which is what makes a few kilobytes on
    /// the wire able to ask for gigabytes of heap.
    /// </summary>
    public static class BasisBoundedStream
    {
        /// <summary>
        /// Drains <paramref name="source"/> into a new array, throwing once the output would pass
        /// <paramref name="maxBytes"/>. <paramref name="what"/> names the payload in that message.
        /// </summary>
        public static byte[] ReadAllBounded(Stream source, int maxBytes, string what)
        {
            using MemoryStream output = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > maxBytes)
                    {
                        throw new InvalidDataException($"{what} inflated past its {maxBytes} byte cap.");
                    }
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return output.ToArray();
        }
    }
}
