using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisPermissions;
using BasisServerHandle;
using System;
using System.Collections.Concurrent;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static BasisPermissions.PermissionManager;

public static class BasisNetworkMessageProcessor
{
    private const int MaxErrorsBeforeWarning = 50;
    /// <summary>Protocol errors tolerated from one peer before it is dropped.</summary>
    private const int MaxErrorsBeforeDisconnect = 500;
    private static readonly ConcurrentDictionary<int, int> _peerErrorCounts = new();

    public static void ClearPeerErrors(int peerId) => _peerErrorCounts.TryRemove(peerId, out _);
    public static void ProcessMessage(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        BasisNetworkStatistics.RecordInbound(channel, reader.AvailableBytes);
        if (channel != BasisNetworkCommons.AuthIdentityChannel && !ReferenceEquals(peer.Tag, NetworkServer.AuthenticatedPeerTag))
        {
            reader.Recycle();
            int preAuthErrors = _peerErrorCounts.AddOrUpdate(peer.Id, 1, (_, c) => c + 1);
            if (preAuthErrors <= 5 || preAuthErrors % 100 == 0)
            {
                BNL.LogError($"Pre-auth message on channel {channel} from peer {peer.Id} before authentication (error #{preAuthErrors}).");
            }
            return;
        }
        try
        {
            BasisServerMessageHandler handler = BasisServerMessageRegistry.ResolveCore(channel);
            if (handler != null)
            {
                handler(peer, reader, channel, deliveryMethod);
            }
            else if (BasisNetworkCommons.IsPluginChannel(channel))
            {
                if (!BasisServerMessageRegistry.DispatchPlugin(peer, reader, channel, deliveryMethod))
                {
                    HandleUnknown(peer, reader, channel, "plugin id");
                }
            }
            else
            {
                HandleUnknown(peer, reader, channel, "channel");
            }
        }
        catch (Exception ex)
        {
            int errorCount = _peerErrorCounts.AddOrUpdate(peer.Id, 1, (_, c) => c + 1);
            if (errorCount <= 5 || errorCount % 100 == 0)
            {
                BNL.LogError(
                    $"[Error] Exception in ProcessMessage (error #{errorCount})\nPeer: {peer.Id}, Channel: {channel}, Delivery: {deliveryMethod}\nMessage: {ex.Message}\nStackTrace: {ex.StackTrace}"
                );
            }
            reader.Recycle();
            HandleErrorEscalation(peer, errorCount);
        }
    }

    private static void HandleUnknown(NetPeer peer, NetPacketReader reader, byte channel, string kind)
    {
        int errorCount = _peerErrorCounts.AddOrUpdate(peer.Id, 1, (_, c) => c + 1);
        if (errorCount <= 5 || errorCount % 100 == 0)
        {
            BNL.LogError($"Unknown {kind}: {channel} ({reader.AvailableBytes} bytes remaining) from peer {peer.Id} (error #{errorCount})");
        }
        reader.Recycle();
        HandleErrorEscalation(peer, errorCount);
    }

    /// <summary>
    /// Warns once at the warning threshold and disconnects at the hard limit. The counter must
    /// not be cleared on warning, or the limit can never be exceeded.
    /// </summary>
    private static void HandleErrorEscalation(NetPeer peer, int errorCount)
    {
        if (errorCount == MaxErrorsBeforeWarning)
        {
            BNL.LogError($"Peer {peer.Id} has reached {errorCount} protocol errors. The server has detected an issue with this client or its connection.");
            BasisPlayerModeration.SendBackMessage(peer, "The server has detected an issue with your client or connection. You may experience problems.");
        }
        else if (errorCount >= MaxErrorsBeforeDisconnect)
        {
            BNL.LogError($"Peer {peer.Id} exceeded {MaxErrorsBeforeDisconnect} protocol errors; disconnecting.");
            _peerErrorCounts.TryRemove(peer.Id, out _);
            peer.Disconnect();
        }
    }
}
