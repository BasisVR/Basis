using Basis.Scripts.Avatar;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using UnityEngine;

public static class BasisNetworkHandleRemoval
{
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
        if (disconnectedID == BasisNetworkPlayer.LocalPlayer.playerId)
        {
            BasisDebug.LogError("LocalPlayer Matched Disconnected ID returning early");
            return;
        }

        // Queue removal on Unity's main thread
        BasisDeviceManagement.EnqueueOnMainThread(() =>
        {
            HandleDisconnectIdImmediate(disconnectedID);
        });
    }

    public static void HandleDisconnectIdImmediate(ushort disconnectedID)
    {
        if (disconnectedID == BasisNetworkPlayer.LocalPlayer.playerId)
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

            // Shutdown networking
            network.DeInitialize();

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
