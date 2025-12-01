using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Concurrent;
using static SerializableBasis;

public static class BasisNetworkHandleAvatar
{
    public static ConcurrentQueue<ServerSideSyncPlayerMessage> Message = new ConcurrentQueue<ServerSideSyncPlayerMessage>();

    // Baseline 176-byte array per player (fixed once per player)
    private static readonly ConcurrentDictionary<ushort, byte[]> AvatarBaselines = new ConcurrentDictionary<ushort, byte[]>();

    public static void HandleAvatarUpdate(NetPacketReader reader, DeliveryMethod deliveryMethod)
    {
        HandleFullAvatarUpdate(reader);
    }

    private static void HandleFullAvatarUpdate(NetPacketReader reader)
    {
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, reader.AvailableBytes);

        if (Message.TryDequeue(out ServerSideSyncPlayerMessage ssm) == false)
        {
            ssm = new ServerSideSyncPlayerMessage();
        }

        // Normal full deserialize – matches ServerSideSyncPlayerMessage.Serialize on server
        ssm.Deserialize(reader);

        ushort playerId = ssm.playerIdMessage.playerID;

        // Build / cache baseline ONLY on first full for this player
        var lav = ssm.avatarSerialization;
        if (lav.array != null && lav.array.Length == LocalAvatarSyncMessage.AvatarSyncSize)
        {
            if (!AvatarBaselines.ContainsKey(playerId))
            {
                byte[] baseline = new byte[LocalAvatarSyncMessage.AvatarSyncSize];
                Buffer.BlockCopy(lav.array, 0, baseline, 0, LocalAvatarSyncMessage.AvatarSyncSize);
                AvatarBaselines[playerId] = baseline;
            }
            // If a baseline already exists, we DON'T overwrite it.
        }

        if (BasisNetworkPlayers.RemotePlayers.TryGetValue(playerId, out BasisNetworkReceiver player))
        {
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(player, ssm);
        }
        else
        {
            // Still keep baseline; player may spawn later.
            // BasisDebug.Log($"Missing player for full avatar update {playerId}");
        }

        Message.Enqueue(ssm);
        TrimQueue();
    }

    private static void HandleDeltaAvatarUpdate(NetPacketReader reader)
    {
        // Add to profiler
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, reader.AvailableBytes);

        // 1) PlayerIdMessage
        PlayerIdMessage playerIdMsg = new PlayerIdMessage();
        playerIdMsg.Deserialize(reader);
        ushort playerId = playerIdMsg.playerID;

        // 2) interval (make sure type matches server field)
        byte interval = reader.GetByte(); // change to GetDouble/GetFloat if needed

        // 3) delta section for 176-byte avatar array
        byte changeCount = reader.GetByte();

        // We ONLY trust deltas if we already have a baseline for this player.
        if (!AvatarBaselines.TryGetValue(playerId, out byte[] baseline))
        {
            // No baseline yet: can't safely apply this delta. Drop it.
            // Optionally: drain remaining bytes if your reader needs it, but usually fine.
            // BasisDebug.Log($"Delta with no baseline for player {playerId}, dropping.");
            return;
        }

        // Clone baseline → new avatar bytes
        byte[] newAvatarArray = new byte[LocalAvatarSyncMessage.AvatarSyncSize];
        Buffer.BlockCopy(baseline, 0, newAvatarArray, 0, LocalAvatarSyncMessage.AvatarSyncSize);

        for (int Index = 0; Index < changeCount; Index++)
        {
            byte index = reader.GetByte();
            byte value = reader.GetByte();

            // Sanity check – server only writes 0..AvatarSyncSize-1
            if (index < LocalAvatarSyncMessage.AvatarSyncSize)
            {
                newAvatarArray[index] = value;
            }
            else
            {
                BasisDebug.LogError($"Delta index out of range: {index} for player {playerId}");
            }
        }

        // 4) AdditionalAvatarData section (mirrors server)
        LocalAvatarSyncMessage lav = new LocalAvatarSyncMessage();
        lav.array = newAvatarArray;

        byte additionalSize = reader.GetByte();
        if (additionalSize == 0)
        {
            lav.AdditionalAvatarDatas = Array.Empty<AdditionalAvatarData>();
            lav.LinkedAvatarIndex = 0;
        }
        else
        {
            lav.LinkedAvatarIndex = reader.GetByte();
            lav.AdditionalAvatarDatas = new AdditionalAvatarData[additionalSize];

            for (int i = 0; i < additionalSize; i++)
            {
                AdditionalAvatarData aad = new AdditionalAvatarData();
                aad.Deserialize(reader);
                lav.AdditionalAvatarDatas[i] = aad;
            }
        }

        // IMPORTANT:
        // We do NOT update the baseline here.
        // Baseline stays as the original full snapshot for this player.
        // Buffer.BlockCopy(newAvatarArray, 0, baseline, 0, LocalAvatarSyncMessage.AvatarSyncSize);

        // Reuse queue object to avoid GC churn, same as full path
        if (Message.TryDequeue(out ServerSideSyncPlayerMessage ssm) == false)
        {
            ssm = new ServerSideSyncPlayerMessage();
        }

        ssm.playerIdMessage = playerIdMsg;
        ssm.interval = interval;
        ssm.avatarSerialization = lav;

        if (BasisNetworkPlayers.RemotePlayers.TryGetValue(playerId, out BasisNetworkReceiver player))
        {
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(player, ssm);
        }
        else
        {
            // We applied delta to a valid baseline even if player object isn't live yet.
            // BasisDebug.Log($"Missing player for delta avatar update {playerId}");
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
        ServerAvatarChangeMessage ServerAvatarChangeMessage = new ServerAvatarChangeMessage();
        ServerAvatarChangeMessage.Deserialize(reader);
        ushort PlayerID = ServerAvatarChangeMessage.uShortPlayerId.playerID;
        if (BasisNetworkPlayers.Players.TryGetValue(PlayerID, out BasisNetworkPlayer Player))
        {
            BasisNetworkReceiver networkReceiver = (BasisNetworkReceiver)Player;
            networkReceiver.ReceiveAvatarChangeRequest(ServerAvatarChangeMessage);
        }
        else
        {
            BasisDebug.Log("Missing Player For Message " + ServerAvatarChangeMessage.uShortPlayerId.playerID);
        }
    }
}
