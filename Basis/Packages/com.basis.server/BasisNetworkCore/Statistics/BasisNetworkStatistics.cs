using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading;
namespace BasisNetworkServer.BasisNetworking
{
    /// <summary>
    /// High-throughput, thread-safe network statistics with striped counters to minimize contention.
    /// Tracks inbound/outbound counts and bytes per 0..255 message index,
    /// and supports compact encoding/decoding to/from byte arrays (optionally using Brotli).
    /// </summary>
    public static class BasisNetworkStatistics
    {
        private const int Indices = 256;

        // More stripes -> less contention. Two per core, from the shared sizing helper so every
        // contention table in the server is derived the same way. The old upper clamp of 128 was
        // the problem case: on a 256-core host it silently became fewer stripes than threads,
        // which is the situation striping exists to avoid.
        private static readonly int StripeCount = Basis.Network.Core.BasisCpuBudget.ConcurrencyWidth(perCore: 2, min: 16, max: 1024);

        // Jagged arrays so Interlocked can take ref long (elements are referenceable).
        private static readonly long[][] _inCountStripes;
        private static readonly long[][] _inBytesStripes;
        private static readonly long[][] _outCountStripes;
        private static readonly long[][] _outBytesStripes;

        // Thread-local stripe selection. 0 means "uninitialized".
        [ThreadStatic] private static int _stripePlusOne;

        static BasisNetworkStatistics()
        {
            _inCountStripes = new long[StripeCount][];
            _inBytesStripes = new long[StripeCount][];
            _outCountStripes = new long[StripeCount][];
            _outBytesStripes = new long[StripeCount][];

            for (int s = 0; s < StripeCount; s++)
            {
                _inCountStripes[s] = new long[Indices];
                _inBytesStripes[s] = new long[Indices];
                _outCountStripes[s] = new long[Indices];
                _outBytesStripes[s] = new long[Indices];
            }
        }
        public static bool IsRecordingData = false;
        // ===== Recording API =====

        /// <summary>Record one inbound message for <paramref name="index"/>, adding its encoded byte length.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordInbound(byte index, int bytesEncoded)
        {
            if(!IsRecordingData)
            {
                return;
            }

            int s = EnsureStripe();

            Interlocked.Increment(ref _inCountStripes[s][index]);
            Interlocked.Add(ref _inBytesStripes[s][index], bytesEncoded);
        }

        /// <summary>Record one outbound message for <paramref name="index"/>, adding its encoded byte length.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordOutbound(byte index, int bytesEncoded)
        {
            if (!IsRecordingData)
            {
                return;
            }

            int s = EnsureStripe();

            Interlocked.Increment(ref _outCountStripes[s][index]);
            Interlocked.Add(ref _outBytesStripes[s][index], bytesEncoded);
        }

        /// <summary>
        /// Record a batch of N outbound messages on the same <paramref name="index"/>. Caller
        /// is expected to have accumulated <paramref name="count"/> messages totaling
        /// <paramref name="bytesEncoded"/> bytes within one logical scope (e.g. one receiver's
        /// tick in the BSR send loop). Folds N×(LOCK XADD + LOCK ADD) into 2×LOCK ADD —
        /// at 1k+ players this is several percent of total CPU saved in the BSR hot path.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecordOutboundBatch(byte index, long count, long bytesEncoded)
        {
            if (!IsRecordingData || count <= 0)
            {
                return;
            }

            int s = EnsureStripe();

            Interlocked.Add(ref _outCountStripes[s][index], count);
            Interlocked.Add(ref _outBytesStripes[s][index], bytesEncoded);
        }

        // ===== Snapshot API =====

        /// <summary>
        /// Non-destructive snapshot. Values may change during read, but each read is atomic.
        /// Back-compat note:
        ///   - Snapshot.PerIndex and Snapshot.TotalCalls are inbound.
        ///   - Snapshot.OutPerIndex and Snapshot.OutTotalCalls are outbound.
        /// </summary>
        public static Snapshot GetSnapshot()
        {
            var inPerIndex = new Dictionary<byte, IndexStats>(capacity: 64);
            var outPerIndex = new Dictionary<byte, IndexStats>(capacity: 64);

            for (int i = 0; i < Indices; i++)
            {
                long inCount = 0, inBytes = 0;
                long outCount = 0, outBytes = 0;

                for (int s = 0; s < StripeCount; s++)
                {
                    inCount += Volatile.Read(ref _inCountStripes[s][i]);
                    inBytes += Volatile.Read(ref _inBytesStripes[s][i]);
                    outCount += Volatile.Read(ref _outCountStripes[s][i]);
                    outBytes += Volatile.Read(ref _outBytesStripes[s][i]);
                }

                if ((inCount | inBytes) != 0)
                    inPerIndex[(byte)i] = new IndexStats(unchecked((ulong)inCount), unchecked((ulong)inBytes));

                if ((outCount | outBytes) != 0)
                    outPerIndex[(byte)i] = new IndexStats(unchecked((ulong)outCount), unchecked((ulong)outBytes));
            }
            return new Snapshot(inPerIndex, outPerIndex);
        }

        /// <summary>
        /// Atomic cut: collect and reset all counters without losing increments.
        /// Back-compat note:
        ///   - Snapshot.PerIndex/TotalCalls are inbound; OutPerIndex/OutTotalCalls are outbound.
        /// </summary>
        public static Snapshot SnapshotAndReset()
        {
            var inPerIndex = new Dictionary<byte, IndexStats>(capacity: 64);
            var outPerIndex = new Dictionary<byte, IndexStats>(capacity: 64);

            for (int i = 0; i < Indices; i++)
            {
                long inCount = 0, inBytes = 0;
                long outCount = 0, outBytes = 0;

                for (int s = 0; s < StripeCount; s++)
                {
                    inCount += Interlocked.Exchange(ref _inCountStripes[s][i], 0);
                    inBytes += Interlocked.Exchange(ref _inBytesStripes[s][i], 0);
                    outCount += Interlocked.Exchange(ref _outCountStripes[s][i], 0);
                    outBytes += Interlocked.Exchange(ref _outBytesStripes[s][i], 0);
                }

                if ((inCount | inBytes) != 0)
                    inPerIndex[(byte)i] = new IndexStats(unchecked((ulong)inCount), unchecked((ulong)inBytes));

                if ((outCount | outBytes) != 0)
                    outPerIndex[(byte)i] = new IndexStats(unchecked((ulong)outCount), unchecked((ulong)outBytes));
            }
            return new Snapshot(inPerIndex, outPerIndex);
        }

        /// <summary>Zero everything.</summary>
        public static void Clear()
        {
            for (int s = 0; s < StripeCount; s++)
            {
                for (int i = 0; i < Indices; i++)
                {
                    Interlocked.Exchange(ref _inCountStripes[s][i], 0);
                    Interlocked.Exchange(ref _inBytesStripes[s][i], 0);
                    Interlocked.Exchange(ref _outCountStripes[s][i], 0);
                    Interlocked.Exchange(ref _outBytesStripes[s][i], 0);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int EnsureStripe()
        {
            int sPlusOne = _stripePlusOne;
            if (sPlusOne == 0)
            {
                int stripe = PickStripe();
                _stripePlusOne = sPlusOne = stripe + 1;
            }
            return sPlusOne - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PickStripe()
        {
            // Stable, cheap spread of threads across stripes.
            int id = Thread.CurrentThread.ManagedThreadId;
            unchecked
            {
                uint x = (uint)id;
                x ^= x >> 17; x *= 0xED5AD4BBu;
                x ^= x >> 11; x *= 0xAC4C1B51u;
                x ^= x >> 15; x *= 0x31848BABu;
                x ^= x >> 14;
                return (int)(x % (uint)StripeCount);
            }
        }
        public readonly struct IndexStats
        {
            public readonly ulong Count;
            public readonly ulong Bytes;
            public IndexStats(ulong count, ulong bytes) { Count = count; Bytes = bytes; }
        }

        public sealed class Snapshot
        {
            public const int MaxDecompressedBytes = 64 * 1024;
            // Back-compat (inbound):
            public readonly Dictionary<byte, IndexStats> PerIndex;
            // New (outbound):
            public readonly Dictionary<byte, IndexStats> OutPerIndex;

            public Snapshot( Dictionary<byte, IndexStats> inPerIndex, Dictionary<byte, IndexStats> outPerIndex)
            {
                PerIndex = inPerIndex;
                OutPerIndex = outPerIndex;
            }

            /// <summary>
            /// Take an atomic cut *and* reset the live counters, then encode & (optionally) compress.
            /// </summary>
            public static byte[] SnapshotResetEncode(bool compress = true, int brotliQuality = 6)
            {
                var snap = BasisNetworkStatistics.SnapshotAndReset();
                var raw = EncodeSnapshot(snap);
                return compress ? BrotliCompress(raw, brotliQuality) : raw;
            }

            /// <summary>
            /// Encode a non-destructive snapshot (no reset). Useful for debugging.
            /// </summary>
            public static byte[] EncodeCurrent(bool compress = true, int brotliQuality = 6)
            {
                var snap = BasisNetworkStatistics.GetSnapshot();
                var raw = EncodeSnapshot(snap);
                return compress ? BrotliCompress(raw, brotliQuality) : raw;
            }

            /// <summary>
            /// Decode snapshot bytes (after optional decompression).
            /// </summary>
            public static Snapshot Decode(ReadOnlySpan<byte> data, bool compressed = true)
            {
                ReadOnlySpan<byte> raw = compressed ? BrotliDecompressToSpan(data) : data;
                return DecodeSnapshot(raw);
            }

            // --- Encoding/Decoding core ---

            private static byte[] EncodeSnapshot(Snapshot s)
            {
                using var ms = new MemoryStream(512); // small default; grows as needed

                // Inbound map
                WriteMap(ms, s.PerIndex);
                // Outbound map
                WriteMap(ms, s.OutPerIndex);

                return ms.ToArray();
            }

            private static Snapshot DecodeSnapshot(ReadOnlySpan<byte> raw)
            {
                var r = new SpanReader(raw);

                var inPer = ReadMap(ref r);
                var outPer = ReadMap(ref r);

                return new Snapshot(inPer, outPer);
            }

            private static void WriteMap(Stream s, Dictionary<byte, IndexStats> map)
            {
                int n = 0;
                foreach (var kvp in map) if ((kvp.Value.Count | kvp.Value.Bytes) != 0) n++;
                WriteUVar(s, (uint)n);
                foreach (var (index, stats) in map)
                {
                    if ((stats.Count | stats.Bytes) == 0) continue;
                    s.WriteByte(index);
                    WriteUVar(s, stats.Count);
                    WriteUVar(s, stats.Bytes);
                }
            }

            private static Dictionary<byte, IndexStats> ReadMap(ref SpanReader r)
            {
                uint n = r.ReadUVar32();
                var dict = new Dictionary<byte, IndexStats>((int)Math.Min(n, 256));
                for (uint i = 0; i < n; i++)
                {
                    byte index = r.ReadByte();
                    ulong count = r.ReadUVar();
                    ulong bytes = r.ReadUVar();
                    dict[index] = new IndexStats(count, bytes);
                }
                return dict;
            }
            private static void WriteUVar(Stream s, ulong value)
            {
                // 10 bytes max for ulong
                while (value >= 0x80)
                {
                    s.WriteByte((byte)((value & 0x7Fu) | 0x80u));
                    value >>= 7;
                }
                s.WriteByte((byte)value);
            }

            private static void WriteUVar(Stream s, uint value) => WriteUVar(s, (ulong)value);

            private ref struct SpanReader
            {
                private ReadOnlySpan<byte> _span;
                private int _pos;
                public SpanReader(ReadOnlySpan<byte> span) { _span = span; _pos = 0; }
                public byte ReadByte()
                {
                    if (_pos >= _span.Length) throw new EndOfStreamException();
                    return _span[_pos++];
                }
                public ulong ReadUVar()
                {
                    ulong result = 0;
                    int shift = 0;
                    while (true)
                    {
                        byte b = ReadByte();
                        result |= (ulong)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0) return result;
                        shift += 7;
                        if (shift > 63) throw new InvalidDataException("Varint too long");
                    }
                }
                public uint ReadUVar32()
                {
                    ulong v = ReadUVar();
                    if (v > uint.MaxValue) throw new InvalidDataException("uvar32 overflow");
                    return (uint)v;
                }
            }

            private static byte[] BrotliCompress(ReadOnlySpan<byte> raw, int quality)
            {
                using var ms = new MemoryStream(raw.Length / 2);
                using (var bs = new BrotliStream(ms, quality >= 7 ? CompressionLevel.Optimal : CompressionLevel.Fastest, leaveOpen: true))
                {
                    bs.Write(raw);
                }
                return ms.ToArray();
            }

            private static ReadOnlySpan<byte> BrotliDecompressToSpan(ReadOnlySpan<byte> comp)
            {
                using var input = new MemoryStream(comp.ToArray());
                using var bs = new BrotliStream(input, CompressionMode.Decompress);
                return Basis.Network.Core.BasisBoundedStream.ReadAllBounded(bs, MaxDecompressedBytes, "Statistics snapshot");
            }
        }
    }
}
