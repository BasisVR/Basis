using System;
using System.IO;
using System.IO.Compression;
using Basis.Network.Core;
public static partial class SerializableBasis
{
    /// <summary>
    /// A run of <see cref="ServerReadyMessage"/>s delivered as one packet, used for the join fill.
    ///
    /// A joining client is told about every player already present. Sending one reliable packet each
    /// meant 1999 packets at a 2000-player scale — all queued to a single peer in one tight loop, which
    /// costs per-packet overhead and stalls that client behind its own backlog. Worse, each avatar blob
    /// was deflated INDIVIDUALLY, which recovers almost nothing: the redundancy is across players (the
    /// same CDN host on every URL, and one popular avatar worn by hundreds), not inside a single record.
    /// Batching first and compressing the batch turns that around — measured ~24x on the avatar bytes.
    ///
    /// Wire: [count:ushort][compressed:1][payloadLength:int][payload]
    /// where payload is count ServerReadyMessages back to back, optionally raw-Deflate'd. The flag is
    /// per batch because compression is skipped when it does not pay (small batches inflate).
    /// </summary>
    public struct ServerReadyBatchMessage
    {
        /// <summary>Uncompressed payload bytes per batch. Keeps a batch inside a sane reassembly cost
        /// and well under the 64 KB ushort-length ceiling used elsewhere in this protocol.</summary>
        public const int MaxPayloadBytes = 32 * 1024;

        /// <summary>
        /// Hard ceiling the RECEIVER enforces on an inflated payload, deliberately larger than
        /// <see cref="MaxPayloadBytes"/>. Producers append a whole record and flush once the buffer
        /// has REACHED the cap, and JoinBroadcast.Flush always takes at least one record however
        /// large, so a real batch runs one record past it. The headroom covers the biggest record
        /// the protocol can express — a 64 KB avatar blob plus a 64 KB additional-data section and
        /// their framing — while still refusing a bomb long before it costs anything.
        /// </summary>
        public const int MaxInflatedBytes = 256 * 1024;

        /// <summary>Below this a Deflate block header costs more than it saves.</summary>
        public const int MinCompressBytes = 256;

        public ushort Count;
        public byte[] Payload;          // uncompressed concatenation
        public bool WasCompressed;      // what the last Serialize/Deserialize actually did

        public void Serialize(NetDataWriter writer)
        {
            byte[] framed = Compress(Payload ?? Array.Empty<byte>(), out bool compressed);
            SerializePreCompressed(writer, framed, compressed);
        }

        /// <summary>
        /// The Deflate half of <see cref="Serialize"/> on its own, so a join fill with many
        /// batches can compress them in parallel and then write the frames out in order.
        /// Returns the bytes to frame; <paramref name="compressed"/> says whether they won.
        /// </summary>
        public static byte[] Compress(byte[] body, out bool compressed)
        {
            compressed = false;
            if (body.Length >= MinCompressBytes)
            {
                byte[] deflated = Deflate(body);
                // Only pay for compression when it actually wins; a high-entropy batch can grow.
                if (deflated.Length < body.Length)
                {
                    compressed = true;
                    return deflated;
                }
            }
            return body;
        }

        /// <summary>Writes the wire frame for a body <see cref="Compress"/> already produced.</summary>
        public void SerializePreCompressed(NetDataWriter writer, byte[] framed, bool compressed)
        {
            WasCompressed = compressed;
            writer.Put(Count);
            writer.Put(compressed);
            writer.Put(framed.Length);
            writer.Put(framed);
        }

        public void Deserialize(NetDataReader reader)
        {
            Count = reader.GetUShort();
            WasCompressed = reader.GetBool();
            int length = reader.GetInt();

            if (length < 0 || length > reader.AvailableBytes)
            {
                throw new ArgumentException($"Ready batch length {length} exceeds available data ({reader.AvailableBytes} bytes).");
            }
            if (!WasCompressed && length > MaxInflatedBytes)
            {
                throw new ArgumentException($"Ready batch payload {length} exceeds the {MaxInflatedBytes} byte cap.");
            }

            byte[] framed = new byte[length];
            reader.GetBytes(framed, 0, length);
            Payload = WasCompressed ? Inflate(framed) : framed;
        }

        static byte[] Deflate(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
            {
                deflate.Write(raw, 0, raw.Length);
            }
            return output.ToArray();
        }

        static byte[] Inflate(byte[] compressed)
        {
            using var input = new MemoryStream(compressed);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            return BasisBoundedStream.ReadAllBounded(deflate, MaxInflatedBytes, "Ready batch");
        }
    }

    public struct ServerReadyMessage
    {
        public PlayerIdMessage playerIdMessage;//who this came from
        public ReadyMessage localReadyMessage;
        public void Deserialize(NetDataReader Writer)
        {
            playerIdMessage.Deserialize(Writer);
            localReadyMessage.Deserialize(Writer);
        }
        public void Serialize(NetDataWriter Writer)
        {
            playerIdMessage.Serialize(Writer);
            localReadyMessage.Serialize(Writer);
        }
    }
}
