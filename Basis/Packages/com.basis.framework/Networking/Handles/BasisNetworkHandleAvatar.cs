using Basis.IK;
using Basis.Network.Core;
using Basis.Network.Core.Compression;
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
        // Bytes on the wire for this player's avatar update, captured before the reader is consumed.
        int wireBytes = reader.AvailableBytes;

        if (!Message.TryDequeue(out ServerSideSyncPlayerMessage ssm))
            ssm = new ServerSideSyncPlayerMessage();

        // Quality, additional-data presence, and ID size are all derived from the channel number.
        byte quality = BasisNetworkCommons.GetQualityFromChannel(channel);
        bool hasAdditionalData = BasisNetworkCommons.ChannelHasAdditionalData(channel);
        bool largeId = BasisNetworkCommons.IsLargePlayerIdChannel(channel);
        try
        {
            ssm.Deserialize(reader, quality, hasAdditionalData, largeId);

            ushort playerId = ssm.playerIdMessage.playerID;

            if (BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(playerId, out BasisNetworkReceiver player))
            {
                player.AccountReceivedBytes(wireBytes);
                // Capture this full keyframe as the delta baseline (used to reconstruct later deltas on
                // DeltaAvatarChannel). Cheap copy; P2P full frames land here too, which is exactly what
                // v42 P2P delta senders rebaseline against.
                byte[] arr = ssm.avatarSerialization.array;
                if (arr != null)
                {
                    int payloadSize = BasisAvatarBitPacking.ConvertToSize((BasisAvatarBitPacking.BitQuality)quality);
                    if (arr.Length >= payloadSize)
                        player.CaptureKeyframeBaseline(quality, ssm.sequence, arr, payloadSize);
                }
                BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(player, ssm);
            }
        }
        catch (System.Exception ex)
        {
            BasisDebug.LogError($"Malformed avatar packet on channel {channel}: {ex.Message}", BasisDebug.LogTag.Networking);
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
        // Leading kind byte multiplexes this channel — see BasisNetworkCommons.AvatarChangeKind*.
        byte kind = reader.GetByte();
        if (kind == BasisNetworkCommons.AvatarChangeKindBodyFit)
        {
            ServerBodyFitMessage fitMsg = new ServerBodyFitMessage();
            fitMsg.Deserialize(reader);
            BasisBodyFitNetworking.Receive(fitMsg.uShortPlayerId.playerID, fitMsg.bodyFit);
            return;
        }

        ServerAvatarChangeMessage msg = new ServerAvatarChangeMessage();
        msg.Deserialize(reader);

        ushort playerId = msg.uShortPlayerId.playerID;
        if (BasisNetworkPlayers.Players.TryGetValue(playerId, out BasisNetworkPlayer player))
        {
            ((BasisNetworkReceiver)player).ReceiveAvatarChangeRequest(msg);
        }
        else if (!BasisNetworkPlayers.JoiningPlayers.ContainsKey(playerId))
        {
            // Joining players race their own creation — silent; their spawn payload carries
            // the avatar as of spawn time. Anything else is worth a line.
            BasisDebug.Log("Missing Player For Message " + playerId);
        }
    }
}
