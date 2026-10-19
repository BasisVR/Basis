using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.Profiler;
using K4os.Compression.LZ4;
using System;

/// <summary>
/// Decodes one server-emitted avatar bundle on <see cref="BasisNetworkCommons.CompressedAvatarBundleChannel"/>
/// and dispatches each inner message back through <see cref="BasisNetworkHandleAvatar.HandleAvatarUpdate"/>
/// as if it had been received on its original quality channel.
///
/// Wire format (must match BasisServerReductionSystemEvents on the server):
///   [flags:1][rawLen:2-LE][compressed( group* )]
///   group := [origChannel:1][n:1][msgLen:2-LE] x n [bodies]
///
/// rawLen is authoritative. Each inner body is exactly what the server would have sent on
/// origChannel individually, so quality / additional-data presence / id-size are all derived
/// from origChannel by the existing handler.
///
/// v53 replaced byte 0 — a message count this decoder always ignored — with the codec id and
/// dictionary generation (see BasisAvatarBundleZstd). The server picks the codec per bundle by
/// traffic class: delta-only bundles are LZ4, keyframe/full bundles are Zstd against a trained
/// dictionary. Because a v52 client would have silently LZ4-decoded a Zstd bundle into garbage
/// rather than rejecting it, the server version was bumped so those clients cannot connect at
/// all; there is no in-band fallback here by design.
///
/// v50 groups entries by channel: one channel byte per RUN rather than per entry. The server
/// channel-sorts a receiver's pending messages first, so runs are long, but nothing here assumes
/// that — a stream of one-entry groups decodes identically.
///
/// The DeltaAvatarChannel group's bodies are COLUMN-TRANSPOSED (byte j of every body, then byte
/// j+1 of every body). Delta bodies are short and field-aligned, so interleaving them puts the
/// same field from different players adjacent and gives LZ4 something to match — worth -13.9%
/// on the wire. Only that group is transposed; doing it to the fixed-size quality groups is a
/// net loss. See BundleCompressionExperiment in the server tests.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisNetworkHandleCompressedBundle
{
    // Per-thread scratch buffers. The Unity receive path runs on the LiteNetLib listener
    // thread (UnsyncedEvents = true on the server, equivalent on client), but async void
    // continuations may hop threads, so ThreadStatic keeps each thread's scratch isolated.
    [ThreadStatic] private static byte[] _scratch;
    [ThreadStatic] private static NetDataReader _scratchReader;

    public static void Handle(NetDataReader reader)
    {
        if (!reader.TryGetByte(out byte flags)) return;
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

        byte codec = BasisAvatarBundleZstd.CodecOf(flags);
        int decoded;
        if (codec == BasisAvatarBundleZstd.CodecZstdDict)
        {
            // Refuse a generation this build has no dictionary for rather than decoding against
            // the wrong one: frames are written with the dictionary id suppressed, so zstd would
            // not catch the mismatch itself and would hand back plausible-looking garbage.
            if (BasisAvatarBundleZstd.DictGenerationOf(flags) != BasisAvatarBundleZstd.DictionaryGeneration)
            {
                reader.SkipBytes(compressedLen);
                return;
            }
            BasisAvatarBundleZstd.TryDecompress(
                reader.RawData.AsSpan(reader.Position, compressedLen),
                scratch.AsSpan(0, rawLen),
                out decoded);
        }
        else
        {
            decoded = LZ4Codec.Decode(
                reader.RawData.AsSpan(reader.Position, compressedLen),
                scratch.AsSpan(0, rawLen));
        }

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

        // Ungroup and un-transpose into the flat [channel][len:2][body]* stream this walk expects.
        int flatCap = BasisAvatarBundleCodec.MaxFlatSize(decoded);
        byte[] flat = _flat;
        if (flat == null || flat.Length < flatCap)
        {
            flat = new byte[System.Math.Max(flatCap, 8192)];
            _flat = flat;
        }
        if (!BasisAvatarBundleCodec.TryFlatten(scratch.AsSpan(0, decoded), flat.AsSpan(0, flatCap), out int flatLen))
        {
            // Corrupt or truncated bundle — drop it.
            return;
        }

        // Walk [origChannel:1][msgLen:2-LE][bytes] entries.
        int offset = 0;
        while (offset + 3 <= flatLen)
        {
            byte innerChannel = flat[offset];
            ushort msgLen = (ushort)(flat[offset + 1] | (flat[offset + 2] << 8));
            offset += 3;
            if (msgLen == 0 || offset + msgLen > flatLen) break;

            // Window the flat buffer over just this entry's bytes; SetSource is alloc-free.
            inner.SetSource(flat, offset, offset + msgLen);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, msgLen);
            if (innerChannel == BasisNetworkCommons.DeltaAvatarChannel)
                BasisNetworkHandleAvatarDelta.Handle(inner);
            else
                BasisNetworkHandleAvatar.HandleAvatarUpdate(inner, innerChannel);
            offset += msgLen;
        }
    }

    [ThreadStatic] private static byte[] _flat;
}
