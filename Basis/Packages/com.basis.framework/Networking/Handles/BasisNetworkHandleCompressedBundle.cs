using Basis.Network.Core;
using Basis.Scripts.Profiler;
using K4os.Compression.LZ4;
using System;

/// <summary>
/// Decodes one server-emitted avatar bundle on <see cref="BasisNetworkCommons.CompressedAvatarBundleChannel"/>
/// and dispatches each inner message back through <see cref="BasisNetworkHandleAvatar.HandleAvatarUpdate"/>
/// as if it had been received on its original quality channel.
///
/// Wire format (must match BasisServerReductionSystemEvents on the server):
///   [count:1][rawLen:2-LE][LZ4 block( [origChannel:1][msgLen:2-LE][bytes]* )]
///
/// rawLen is authoritative — count is just a sanity hint. Each inner [bytes] is exactly
/// what the server would have sent on origChannel individually, so quality / additional-data
/// presence / id-size are all derived from origChannel by the existing handler.
/// </summary>
public static class BasisNetworkHandleCompressedBundle
{
    // Per-thread scratch buffers. The Unity receive path runs on the LiteNetLib listener
    // thread (UnsyncedEvents = true on the server, equivalent on client), but async void
    // continuations may hop threads, so ThreadStatic keeps each thread's scratch isolated.
    [ThreadStatic] private static byte[] _scratch;
    [ThreadStatic] private static NetDataReader _scratchReader;

    public static void Handle(NetDataReader reader)
    {
        if (!reader.TryGetByte(out _)) return;          // count — ignored, rawLen is authoritative
        if (!reader.TryGetUShort(out ushort rawLen)) return;
        if (rawLen == 0) return;

        int compressedLen = reader.AvailableBytes;
        if (compressedLen <= 0) return;

        byte[] scratch = _scratch;
        if (scratch == null || scratch.Length < rawLen)
        {
            scratch = new byte[System.Math.Max((int)rawLen, 8192)];
            _scratch = scratch;
        }

        int decoded = LZ4Codec.Decode(
            reader.RawData.AsSpan(reader.Position, compressedLen),
            scratch.AsSpan(0, rawLen));

        // Advance the source reader past the bytes we just consumed so the caller's
        // Reader.Recycle() doesn't warn about unread payload (we read the compressed
        // span via RawData/Position rather than via reader API, which leaves _position
        // unchanged). Done unconditionally — even on a corrupt bundle we want to
        // mark the whole datagram as fully consumed.
        reader.SkipBytes(compressedLen);

        if (decoded != rawLen)
        {
            // Corrupt or truncated bundle — drop it.
            return;
        }

        NetDataReader inner = _scratchReader;
        if (inner == null)
        {
            inner = new NetDataReader();
            _scratchReader = inner;
        }

        // Walk [origChannel:1][msgLen:2-LE][bytes] entries.
        int offset = 0;
        while (offset + 3 <= decoded)
        {
            byte innerChannel = scratch[offset];
            ushort msgLen = (ushort)(scratch[offset + 1] | (scratch[offset + 2] << 8));
            offset += 3;
            if (msgLen == 0 || offset + msgLen > decoded) break;

            // Window the scratch buffer over just this entry's bytes; SetSource is alloc-free.
            inner.SetSource(scratch, offset, offset + msgLen);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, msgLen);
            if (innerChannel == BasisNetworkCommons.DeltaAvatarChannel)
                BasisNetworkHandleAvatarDelta.Handle(inner);
            else
                BasisNetworkHandleAvatar.HandleAvatarUpdate(inner, innerChannel);
            offset += msgLen;
        }
    }
}
