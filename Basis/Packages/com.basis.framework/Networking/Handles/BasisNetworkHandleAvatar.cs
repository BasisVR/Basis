using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Profiler;
using System.Collections.Concurrent;
using static SerializableBasis;
public static class BasisNetworkHandleAvatar
{
    public static ConcurrentQueue<ServerSideSyncPlayerMessage> Message = new ConcurrentQueue<ServerSideSyncPlayerMessage>();

    public static void HandleAvatarUpdate(NetDataReader reader, byte channel)
    {
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, reader.AvailableBytes);

        if (!Message.TryDequeue(out ServerSideSyncPlayerMessage ssm))
            ssm = new ServerSideSyncPlayerMessage();

        // Quality, additional-data presence, and ID size are all derived from the channel number.
        byte quality = BasisNetworkCommons.GetQualityFromChannel(channel);
        bool hasAdditionalData = BasisNetworkCommons.ChannelHasAdditionalData(channel);
        bool largeId = BasisNetworkCommons.IsLargePlayerIdChannel(channel);
        ssm.Deserialize(reader, quality, hasAdditionalData, largeId);

        ushort playerId = ssm.playerIdMessage.playerID;

        if (BasisNetworkPlayers.RemotePlayers.TryGetValue(playerId, out BasisNetworkReceiver player))
        {
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(player, ssm);
        }

        Message.Enqueue(ssm);
        TrimQueue();
    }

    private static void TrimQueue()
    {
        if (Message.Count > 256)
        {
            while (Message.TryDequeue(out _)) { }
            BasisDebug.LogError("Messages Exceeded 256! Resetting");
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
}
