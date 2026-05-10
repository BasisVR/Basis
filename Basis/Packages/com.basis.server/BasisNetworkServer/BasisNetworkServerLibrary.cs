using Basis.Network.Core;
using BasisNetworking.InitalData;
using K4os.Compression.LZ4;
using System;
using System.Buffers;
using static SerializableBasis;

public static class BasisNetworkServerLibrary
{
    /// <summary>
    /// Wire format on <see cref="BasisNetworkCommons.ServerLibraryChannel"/>:
    ///   [u16 rawLen][u16 compressedLen][bytes payload]
    /// If <c>compressedLen == 0</c> the payload is the raw <see cref="ServerLibraryMessage"/>
    /// bytes. Otherwise the payload is LZ4-encoded and decompresses to <c>rawLen</c> bytes.
    ///
    /// The wire payload is cached and only rebuilt when the admin mutates the
    /// library (via <see cref="BroadcastLibraryToAll"/>). Per-peer joins just
    /// memcpy the cached bytes into a pooled writer — zero new allocations on
    /// the hot path.
    /// </summary>
    private static byte[] _cachedWire = Array.Empty<byte>();
    private static int _cachedWireLen;
    private static readonly object _cacheLock = new object();

    public static void SendLibraryToPeer(NetPeer peer)
    {
        if (!TryGetCachedWire(out byte[] wire, out int wireLen)) return;
        NetDataWriter writer = NetworkServer.RentWriter();
        writer.Put(wire, 0, wireLen);
        NetworkServer.TrySend(peer, writer, BasisNetworkCommons.ServerLibraryChannel, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
    }

    public static void BroadcastLibraryToAll()
    {
        // Library mutated — rebuild cache before broadcasting.
        lock (_cacheLock) RebuildCacheLocked();
        if (_cachedWireLen == 0) return;

        NetDataWriter writer = NetworkServer.RentWriter();
        writer.Put(_cachedWire, 0, _cachedWireLen);
        NetworkServer.BroadcastMessageToClients(
            writer,
            BasisNetworkCommons.ServerLibraryChannel,
            NetworkServer.PeerSnapshot,
            DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
    }

    private static bool TryGetCachedWire(out byte[] wire, out int wireLen)
    {
        lock (_cacheLock)
        {
            if (_cachedWireLen == 0)
            {
                RebuildCacheLocked();
            }
            wire = _cachedWire;
            wireLen = _cachedWireLen;
            return wireLen > 0;
        }
    }

    private static void RebuildCacheLocked()
    {
        var loaded = BasisDefaultLibraryLoader.LoadedItems;
        int count = loaded?.Count ?? 0;

        // Serialize directly from BasisDefaultLibraryLoader.LoadedItems into a
        // rented writer — avoids the ServerLibraryItem[] alloc the prior version
        // built. Mirrors ServerLibraryMessage.Serialize byte-for-byte.
        NetDataWriter raw = NetworkServer.RentWriter();
        try
        {
            raw.Put((ushort)count);
            for (int i = 0; i < count; i++)
            {
                var src = loaded[i];
                raw.Put(src.Mode);
                raw.Put(src.Url ?? string.Empty);
                raw.Put(src.Password ?? string.Empty);
            }
            int rawLen = raw.Length;
            if (rawLen <= 0 || rawLen > ushort.MaxValue)
            {
                _cachedWireLen = 0;
                return;
            }

            int maxOut = LZ4Codec.MaximumOutputSize(rawLen);
            byte[] compressedScratch = ArrayPool<byte>.Shared.Rent(maxOut);
            try
            {
                int compressedLen = LZ4Codec.Encode(
                    raw.Data.AsSpan(0, rawLen),
                    compressedScratch.AsSpan(0, maxOut),
                    LZ4Level.L00_FAST);

                bool useCompressed = compressedLen > 0
                    && compressedLen < rawLen
                    && compressedLen <= ushort.MaxValue;
                int payloadLen = useCompressed ? compressedLen : rawLen;
                int wireLen = 4 + payloadLen; // [u16 rawLen][u16 compressedLen]

                // Grow the cache buffer only when the wire format actually got bigger;
                // otherwise reuse the existing allocation.
                if (_cachedWire.Length < wireLen)
                {
                    _cachedWire = new byte[Math.Max(wireLen, 256)];
                }

                // [u16 rawLen] (little-endian — matches NetDataWriter.Put(ushort))
                _cachedWire[0] = (byte)rawLen;
                _cachedWire[1] = (byte)(rawLen >> 8);
                // [u16 compressedLen] — 0 means the payload is raw
                int compLenWire = useCompressed ? compressedLen : 0;
                _cachedWire[2] = (byte)compLenWire;
                _cachedWire[3] = (byte)(compLenWire >> 8);

                Buffer.BlockCopy(
                    useCompressed ? compressedScratch : raw.Data,
                    0,
                    _cachedWire,
                    4,
                    payloadLen);
                _cachedWireLen = wireLen;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressedScratch);
            }
        }
        finally
        {
            NetworkServer.ReturnWriter(raw);
        }
    }
}
