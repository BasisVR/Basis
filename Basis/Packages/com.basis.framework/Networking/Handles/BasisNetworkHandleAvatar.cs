using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Concurrent;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static SerializableBasis;
public static class BasisNetworkHandleAvatar
{
    public static ConcurrentQueue<ServerSideSyncPlayerMessage> Message = new ConcurrentQueue<ServerSideSyncPlayerMessage>();

    // Baseline per player (payload bytes only). Size depends on DataQualityLevel.
    private static readonly ConcurrentDictionary<ushort, byte[]> AvatarBaselines = new ConcurrentDictionary<ushort, byte[]>();

    // Track which quality the baseline was built for (so we can decide whether to refresh).
    private static readonly ConcurrentDictionary<ushort, byte> BaselineQuality = new ConcurrentDictionary<ushort, byte>();

    public static void HandleAvatarUpdate(NetPacketReader reader, DeliveryMethod deliveryMethod)
    {
        HandleFullAvatarUpdate(reader);
    }

    private static void HandleFullAvatarUpdate(NetPacketReader reader)
    {
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, reader.AvailableBytes);

        if (!Message.TryDequeue(out ServerSideSyncPlayerMessage ssm))
            ssm = new ServerSideSyncPlayerMessage();

        // Normal full deserialize – matches ServerSideSyncPlayerMessage.Serialize on server
        ssm.Deserialize(reader);

        ushort playerId = ssm.playerIdMessage.playerID;

        // Cache baseline from first "full" message (or refresh if quality changed; policy below)
        var lav = ssm.avatarSerialization;

        if (lav.array != null)
        {
            var q = (BitQuality)lav.DataQualityLevel;

            if (BasisAvatarBitPacking.IsValidQuality(q))
            {
                int expectedSize = BasisAvatarBitPacking.ConvertToSize(q);

                if (lav.array.Length >= expectedSize)
                {
                    bool hasBaseline = AvatarBaselines.TryGetValue(playerId, out var existing);
                    bool hasQuality = BaselineQuality.TryGetValue(playerId, out var existingQ);

                    // --- Policy choice ---
                    // A) "First baseline only" (old behavior): only set baseline if missing.
                    // B) Refresh if quality changes (recommended now that payload size can change).
                    bool shouldRefresh =
                        !hasBaseline ||
                        !hasQuality ||
                        existingQ != lav.DataQualityLevel ||
                        existing == null ||
                        existing.Length != expectedSize;

                    if (shouldRefresh)
                    {
                        byte[] baseline = new byte[expectedSize];
                        Buffer.BlockCopy(lav.array, 0, baseline, 0, expectedSize);

                        AvatarBaselines[playerId] = baseline;
                        BaselineQuality[playerId] = lav.DataQualityLevel;
                    }
                    else
                    {
                        // If you truly want "never overwrite", comment out the refresh block above
                        // and just do TryAdd here.
                    }
                }
            }
        }

        if (BasisNetworkPlayers.RemotePlayers.TryGetValue(playerId, out BasisNetworkReceiver player))
        {
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(player, ssm);
        }
        else
        {
            // Still keep baseline; player may spawn later.
        }

        Message.Enqueue(ssm);
        TrimQueue();
    }

    private static void TrimQueue()
    {
        if (Message.Count > 256)
        {
            while (Message.TryDequeue(out _)) { }
            BasisDebug.LogError("Messages Exceeded 250! Resetting");
        }
    }

    public static void HandleAvatarChangeMessage(NetPacketReader reader)
    {
        ServerAvatarChangeMessage msg = new ServerAvatarChangeMessage();
        msg.Deserialize(reader);

        ushort playerId = msg.uShortPlayerId.playerID;
        if (BasisNetworkPlayers.Players.TryGetValue(playerId, out BasisNetworkPlayer player))
        {
            ((BasisNetworkReceiver)player).ReceiveAvatarChangeRequest(msg);
        }
        else
        {
            BasisDebug.Log("Missing Player For Message " + playerId);
        }
    }

    // Optional accessors if other systems need the baseline
    public static bool TryGetBaseline(ushort playerId, out byte[] baseline, out BitQuality quality)
    {
        baseline = null;
        quality = BitQuality.Medium;

        if (!AvatarBaselines.TryGetValue(playerId, out baseline))
            return false;

        if (!BaselineQuality.TryGetValue(playerId, out byte q))
            return true;

        quality = (BitQuality)q;
        return true;
    }
}
