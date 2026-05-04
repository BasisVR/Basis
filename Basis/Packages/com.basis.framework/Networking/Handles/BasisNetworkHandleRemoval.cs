using System;
using System.Collections.Concurrent;
using Basis.Scripts.Avatar;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Network.Core;
using UnityEngine;

public static class BasisNetworkHandleRemoval
{
    // Pending player-lifecycle work (joins + leaves), drained on the main thread
    // with a per-frame budget so a mass join/leave event can't stall the renderer.
    // Single queue preserves temporal order between joins and leaves.
    public static readonly ConcurrentQueue<Action> LifecycleQueue = new();

    /// <summary>
    /// Maximum number of join/leave actions to process per main-thread frame.
    /// Tuned for ~1ms of work at ~60fps; raise for faster catch-up, lower for smoother frames.
    /// </summary>
    public static int LifecycleBudgetPerFrame = 20;

    /// <summary>
    /// Drains up to <paramref name="maxPerFrame"/> queued lifecycle actions on the main thread.
    /// Called every Update from BasisEventDriver.
    /// </summary>
    public static int ProcessLifecycleQueue(int maxPerFrame)
    {
        int processed = 0;
        while (processed < maxPerFrame && LifecycleQueue.TryDequeue(out Action action))
        {
            try { action.Invoke(); }
            catch (Exception ex) { BasisDebug.LogError($"Lifecycle action failed: {ex}"); }
            processed++;
        }
        return processed;
    }

    public static void HandleDisconnection(NetPacketReader reader)
    {
        while (reader.AvailableBytes >= sizeof(ushort))
        {
            if (!reader.TryGetUShort(out ushort disconnectValue))
            {
                BasisDebug.LogError("Tried to read disconnect message but data was missing!");
                break;
            }

            HandleDisconnectId(disconnectValue);
        }
    }

    public static void HandleDisconnectId(ushort disconnectedID)
    {
        if (BasisNetworkPlayer.LocalPlayer != null && disconnectedID == BasisNetworkPlayer.LocalPlayer.playerId)
        {
            BasisDebug.LogError("LocalPlayer Matched Disconnected ID returning early");
            return;
        }

        // Defer to the budgeted main-thread queue so a burst of disconnects doesn't
        // chain N synchronous GameObject.Destroy / avatar-unload calls in one frame.
        LifecycleQueue.Enqueue(() => HandleDisconnectIdImmediate(disconnectedID));
    }

    public static void HandleDisconnectIdImmediate(ushort disconnectedID)
    {
        if (BasisNetworkPlayer.LocalPlayer != null && disconnectedID == BasisNetworkPlayer.LocalPlayer.playerId)
        {
           // BasisDebug.LogError("LocalPlayer Matched Disconnected ID returning early");
            return;
        }

        // Remove from network manager
        if (BasisNetworkPlayers.RemovePlayer(disconnectedID, out BasisNetworkPlayer network))
        {
            if (network == null)
            {
                BasisDebug.LogError($"Missing Networked Player for removing ID {disconnectedID}");
                return;
            }

            // Notify scripts about remote player leaving
            if (network.Player != null)
            {
                BasisNetworkPlayer.OnRemotePlayerLeft?.Invoke(network, (Basis.Scripts.BasisSdk.Players.BasisRemotePlayer)network.Player);
            }
            else
            {
                BasisDebug.LogError($"Missing Player for removing ID {disconnectedID}");
            }
            BasisNetworkPlayer.OnPlayerLeft?.Invoke(network);

            // Clean up any shout audio for this player
            BasisShoutAudioDriver.RemovePlayer(disconnectedID);

            // Notify avatar BasisAvatarMonoBehaviours that the network is going away
            // before the avatar GameObject is destroyed below.
            network.NotifyNetworkBehavioursTerminated();

            // Shutdown networking
            network.DeInitialize();

            // Cancel any in-flight async avatar load to prevent orphaned avatar GameObjects
            if (network.Player != null)
            {
                BasisAvatarFactory.CancelPlayerLoad(network.Player);
            }

            if (network.Player != null)
            {
                BasisAvatarFactory.DeleteLastAvatar(network.Player);
            }
            else
            {
                BasisDebug.LogError($"B Missing Player for removing ID {disconnectedID}");
            }

            // Destroy the player GameObject
            if (network.Player != null)
            {
                GameObject.Destroy(network.Player.gameObject);
            }
        }
        else
        {
            BasisDebug.LogError($"C Missing Player for removing ID {disconnectedID}");
        }
    }
}
