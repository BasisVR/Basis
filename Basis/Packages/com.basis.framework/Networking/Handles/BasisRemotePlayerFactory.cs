using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Player;
using Basis.Scripts.Profiler;
using UnityEngine.ResourceManagement.ResourceProviders;
using static SerializableBasis;

namespace Basis.Scripts.Networking
{
    public static class BasisRemotePlayerFactory
    {
        public static void HandleCreateRemotePlayer(NetPacketReader reader, InstantiationParameters Parent)
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, reader.AvailableBytes);
            // BasisDebug.Log($"Handling Create Remote Player! {reader.AvailableBytes}");
            ServerReadyMessage ServerReadyMessage = new ServerReadyMessage();
            ServerReadyMessage.Deserialize(reader);

             CreateRemotePlayer(ServerReadyMessage, Parent);
        }
        public static BasisNetworkPlayer CreateRemotePlayer(ServerReadyMessage ServerReadyMessage, InstantiationParameters instantiationParameters)
        {

            ClientAvatarChangeMessage avatarID = ServerReadyMessage.localReadyMessage.clientAvatarChangeMessage;

            if (avatarID.byteArray != null)
            {
                BasisNetworkPlayers.JoiningPlayers.Add(ServerReadyMessage.playerIdMessage.playerID);

                // Start both tasks simultaneously
                BasisRemotePlayer remote = BasisPlayerFactory.CreateRemotePlayer(instantiationParameters, avatarID, ServerReadyMessage.localReadyMessage.playerMetaDataMessage);
                BasisNetworkReceiver BasisNetworkReceiver = new BasisNetworkReceiver(ServerReadyMessage.playerIdMessage.playerID);
                // Continue with the rest of the code
                RemoteInitialization(BasisNetworkReceiver, remote, ServerReadyMessage, avatarID.LocalAvatarIndex);
                remote.LoadAvatarFromInitial(avatarID);
                if (BasisNetworkPlayers.AddPlayer(BasisNetworkReceiver))
                {
                    //    BasisDebug.Log("Added Player AT " + BasisNetworkReceiver.NetId);
                }
                else
                {
                    BasisNetworkHandleRemoval.HandleDisconnectId(ServerReadyMessage.playerIdMessage.playerID);
                    if (BasisNetworkPlayers.AddPlayer(BasisNetworkReceiver))
                    {
                        BasisDebug.LogError($"Player Forcefully removed and readded with new Identity : {ServerReadyMessage.playerIdMessage.playerID}");
                    }
                    else
                    {
                        BasisDebug.LogError("Critical issue this should never occur this is after the fallback system");
                    }
                    return null;
                }
                //  BasisDebug.Log("Added Player " + ServerReadyMessage.playerIdMessage.playerID);
                BasisNetworkPlayer.OnRemotePlayerJoined?.Invoke(BasisNetworkReceiver, remote);
                BasisNetworkPlayer.OnPlayerJoined?.Invoke(BasisNetworkReceiver);

                BasisNetworkPlayers.JoiningPlayers.Remove(ServerReadyMessage.playerIdMessage.playerID);

                return BasisNetworkReceiver;
            }
            else
            {
                BasisDebug.LogError("Empty Avatar ID for Player fatal error! " + ServerReadyMessage.playerIdMessage.playerID);
                return null;
            }
        }
        public static void RemoteInitialization(BasisNetworkReceiver BasisNetworkReceiver, BasisRemotePlayer RemotePlayer, ServerReadyMessage ServerReadyMessage,byte LocalAvatarIndex)
        {
            BasisNetworkReceiver.Player = RemotePlayer;
            RemotePlayer.NetworkReceiver = BasisNetworkReceiver;
            BasisNetworkReceiver.LastLinkedAvatarIndex = LocalAvatarIndex;
            if (RemotePlayer.RemoteAvatarDriver != null)
            {
                if (RemotePlayer.RemoteAvatarDriver.HasEvents == false)
                {
                    RemotePlayer.RemoteAvatarDriver.CalibrationComplete += BasisNetworkReceiver.OnAvatarCalibrationRemote;
                    RemotePlayer.RemoteAvatarDriver.HasEvents = true;
                }
            }
            else
            {
                BasisDebug.LogError("Missing CharacterIKCalibration");
            }
            if (RemotePlayer.RemoteAvatarDriver != null)
            {
            }
            else
            {
                BasisDebug.LogError("Missing CharacterIKCalibration");
            }
            BasisNetworkReceiver.Initialize();//fires events and makes us network compatible
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(BasisNetworkReceiver, ServerReadyMessage.localReadyMessage.localAvatarSyncMessage);
        }
    }
}
