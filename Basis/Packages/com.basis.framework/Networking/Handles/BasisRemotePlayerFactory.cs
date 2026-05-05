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
            ushort playerId = ServerReadyMessage.playerIdMessage.playerID;
            BasisNetworkPlayers.JoiningPlayers.TryAdd(playerId, 0);

            try
            {
                BasisRemotePlayer remote = BasisPlayerFactory.CreateRemotePlayer(instantiationParameters, avatarID, ServerReadyMessage.localReadyMessage.playerMetaDataMessage);
                BasisNetworkReceiver BasisNetworkReceiver = new BasisNetworkReceiver(playerId);
                RemoteInitialization(BasisNetworkReceiver, remote, ServerReadyMessage, avatarID.LocalAvatarIndex);

                if (avatarID.byteArray != null && avatarID.byteArray.Length > 0)
                {
                    remote.LoadAvatarFromInitial(avatarID);
                }

                if (!BasisNetworkPlayers.AddPlayer(BasisNetworkReceiver))
                {
                    BasisNetworkHandleRemoval.HandleDisconnectId(playerId);
                    if (BasisNetworkPlayers.AddPlayer(BasisNetworkReceiver))
                    {
                        BasisDebug.LogError($"Player Forcefully removed and readded with new Identity : {playerId}");
                    }
                    else
                    {
                        BasisDebug.LogError("Critical issue this should never occur this is after the fallback system");
                        return null;
                    }
                }

                BasisNetworkPlayer.OnRemotePlayerJoined?.Invoke(BasisNetworkReceiver, remote);
                BasisNetworkPlayer.OnPlayerJoined?.Invoke(BasisNetworkReceiver);

                return BasisNetworkReceiver;
            }
            finally
            {
                BasisNetworkPlayers.JoiningPlayers.TryRemove(playerId, out _);
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
