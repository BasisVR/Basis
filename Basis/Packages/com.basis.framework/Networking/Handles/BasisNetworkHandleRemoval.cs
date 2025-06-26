using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;
public static class BasisNetworkHandleRemoval
{
    public static void HandleDisconnection(LiteNetLib.NetPacketReader reader)
    {
        if (reader.TryGetUShort(out ushort DisconnectValue))
        {
            BasisDebug.Log($"trying to remove Networked Player {DisconnectValue}");
            if (BasisNetworkManagement.Players.TryGetValue(DisconnectValue, out BasisNetworkPlayer NetworkedPlayer))
            {
                if (NetworkedPlayer.Player.IsLocal == false)
                {
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkManagement.RemovePlayer(DisconnectValue);
                        BasisNetworkPlayer.OnRemotePlayerLeft?.Invoke(NetworkedPlayer, (Basis.Scripts.BasisSdk.Players.BasisRemotePlayer)NetworkedPlayer.Player);//tell scripts delete time
                        NetworkedPlayer.DeInitialize();//shutdown the networking
                        if (NetworkedPlayer.Player.BasisAvatar != null)//nuke avatar first
                        {
                            GameObject.Destroy(NetworkedPlayer.Player.BasisAvatar.gameObject);
                        }
                        if (NetworkedPlayer.Player != null)//remove the player
                        {
                            GameObject.Destroy(NetworkedPlayer.Player.gameObject);
                        }

                    }, null);
                }
                else
                {
                    BasisDebug.LogError("network used wrong api to remove local player!");
                }
            }
            else
            {
                BasisDebug.LogError("Removal Requested but no one was found with id " + DisconnectValue);
            }
        }
        else
        {
            BasisDebug.LogError("Tried To Read Disconnect Message Missing Data!");
        }
    }
}
