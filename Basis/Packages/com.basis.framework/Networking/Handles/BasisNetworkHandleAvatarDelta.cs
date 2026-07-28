using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using System;
using static SerializableBasis;

/// <summary>
/// Decodes one avatar delta on <see cref="BasisNetworkCommons.DeltaAvatarChannel"/> — server-repacked
/// (any quality) or straight from a P2P peer (High) — reconstructs the full pose payload against the
/// player's last keyframe baseline, and feeds the result through the same decode path as a keyframe.
/// Keyframes (and P2P full frames) still arrive on the per-quality avatar channels and are handled by
/// <see cref="BasisNetworkHandleAvatar"/>. Control frames (header bit 7) carry keyframe requests.
///
/// Wire (must match BasisServerReductionSystemEvents.PreSerializeDelta and the P2P splice in
/// BasisP2PManager.BroadcastAvatarViaP2P):
///   [header:1][playerId:1|2][interval:1][sequence:1][baseSeq:1][delta body][additional?]
/// header bits: quality(2) | hasAdditional&lt;&lt;2 | largeId&lt;&lt;3 | control&lt;&lt;7.
/// </summary>
public static class BasisNetworkHandleAvatarDelta
{
    [ThreadStatic] private static byte[] _reconstruct;
    [ThreadStatic] private static ServerSideSyncPlayerMessage _ssm;
    [ThreadStatic] private static bool _ssmInit;

    // Rate limit for DeltaControlKeyframeRequest per sender: dropped deltas keep arriving at the
    // sender's cadence while our baseline is stale, but one reliable request per second is enough
    // (the server re-keys on the first one; the rest would be redundant). Stopwatch ticks — Unity's
    // profile has no Environment.TickCount64.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, long> _lastKeyframeRequestTicks = new();
    private static readonly long KeyframeRequestMinIntervalTicks = System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Asks the server for a fresh keyframe of <paramref name="playerId"/> after a delta had to be
    /// dropped for lack of a matching baseline. With the v42 adaptive keyframe stretch, periodic
    /// keyframes can be seconds apart on idle senders — recovery is request-driven instead.
    /// </summary>
    private static void RequestKeyframeFromServer(ushort playerId)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastKeyframeRequestTicks.TryGetValue(playerId, out long last) && now - last < KeyframeRequestMinIntervalTicks) return;
        _lastKeyframeRequestTicks[playerId] = now;

        // A P2P-linked sender's avatar data bypasses the server, so ask the peer itself.
        if (BasisP2PManager.GetSessionState(playerId) == BasisP2PManager.P2PSessionState.Connected)
        {
            NetDataWriter p2pWriter = new NetDataWriter(true, 1);
            p2pWriter.Put(BasisNetworkCommons.DeltaControlUplinkKeyframeRequest);
            BasisP2PManager.SendDirectTo(playerId, p2pWriter, BasisNetworkCommons.DeltaAvatarChannel, DeliveryMethod.ReliableOrdered);
            return;
        }

        var peer = BasisNetworkConnection.LocalPlayerPeer;
        if (peer == null) return;
        NetDataWriter writer = new NetDataWriter(true, 3);
        writer.Put(BasisNetworkCommons.DeltaControlKeyframeRequest);
        writer.Put(playerId);
        peer.Send(writer, BasisNetworkCommons.DeltaAvatarChannel, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Decodes one delta frame.
    /// </summary>
    /// <returns>
    /// True when unread bytes are left DELIBERATELY — the frame was dropped for a reason this code
    /// expects (no receiver yet, no matching baseline, a control frame). The caller passes this to
    /// Recycle so the "bytes remaining, is this a parsing bug?" warning stays meaningful: those drops
    /// are routine, and at join they are the common case, which is why that warning used to fire
    /// constantly on this channel. A false return means the frame was consumed to the end, so anything
    /// left really is a parsing bug.
    /// </returns>
    public static bool Handle(NetDataReader reader)
    {
        int wireBytes = reader.AvailableBytes;
        if (!reader.TryGetByte(out byte header)) return true;

        if (BasisNetworkCommons.IsDeltaControlHeader(header))
        {
            // Server (or a P2P peer, via the P2P demux) reports our uplink baseline is missing —
            // make the next avatar send a full keyframe.
            if (header == BasisNetworkCommons.DeltaControlUplinkKeyframeRequest)
            {
                BasisNetworkAvatarCompressor.ForceUplinkKeyframe();
            }
            return true;
        }

        byte quality = BasisNetworkCommons.DeltaHeaderQuality(header);
        var q = (BasisAvatarBitPacking.BitQuality)quality;
        if (!BasisAvatarBitPacking.IsValidQuality(q)) return true;
        bool hasAdditional = BasisNetworkCommons.DeltaHeaderHasAdditionalData(header);
        bool largeId = BasisNetworkCommons.DeltaHeaderLargeId(header);

        ushort playerId;
        if (largeId)
        {
            if (!reader.TryGetUShort(out playerId)) return true;
        }
        else
        {
            if (!reader.TryGetByte(out byte b)) return true;
            playerId = b;
        }

        if (!reader.TryGetByte(out byte interval)) return true;
        if (!reader.TryGetByte(out byte sequence)) return true;
        if (!reader.TryGetByte(out byte baseSeq)) return true;

        // Routine while joining: the join fill creates receivers asynchronously off the lifecycle
        // queue, so deltas for a player we have not built yet arrive first and are simply dropped.
        if (!BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(playerId, out BasisNetworkReceiver player))
            return true;

        // Drop the delta unless we hold the exact keyframe baseline it references (quality + baseSeq).
        // A keyframe re-baselines us; ask for one instead of waiting out the (possibly stretched)
        // periodic cadence — this is the correctness guarantee over unreliable UDP. A fresh joiner
        // holds no baselines at all, so this is the other path that fires on every delta at join.
        if (!player.TryGetKeyframeBaseline(quality, baseSeq, out byte[] baseline))
        {
            RequestKeyframeFromServer(playerId);
            return true;
        }

        int bodyLen = BasisAvatarDeltaCompression.DeltaBodyLength(reader.RawData, reader.Position, reader.AvailableBytes, q);
        if (bodyLen < 0 || bodyLen > reader.AvailableBytes) return true;

        int payloadSize = BasisAvatarDeltaCompression.PayloadSize(q);
        byte[] recon = _reconstruct;
        if (recon == null || recon.Length < payloadSize)
        {
            recon = new byte[Math.Max(payloadSize, 256)];
            _reconstruct = recon;
        }

        if (!BasisAvatarDeltaCompression.TryApplyDelta(baseline, reader.RawData, reader.Position, bodyLen, q, recon))
            return true;
        reader.SkipBytes(bodyLen);

        if (!_ssmInit)
        {
            _ssm = new ServerSideSyncPlayerMessage();
            _ssmInit = true;
        }
        ServerSideSyncPlayerMessage ssm = _ssm;
        ssm.playerIdMessage.playerID = playerId;
        ssm.interval = interval;
        ssm.sequence = sequence;
        ssm.avatarSerialization.array = recon;
        ssm.avatarSerialization.DataQualityLevel = quality;
        // Size 0 is what gates dispatch; keeping the entries array lets DeserializeAdditionalData
        // reuse it (and each entry's retained payload buffer) on frames that do carry data.
        ssm.avatarSerialization.AdditionalAvatarDataSize = 0;
        if (hasAdditional)
            ssm.avatarSerialization.DeserializeAdditionalData(reader);
        _ssm = ssm;

        player.AccountReceivedBytes(wireBytes);
        BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(player, ssm);
        return false;
    }
}
