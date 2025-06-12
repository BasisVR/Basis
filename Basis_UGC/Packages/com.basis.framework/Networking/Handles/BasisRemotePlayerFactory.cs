using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Player;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using static SerializableBasis;

namespace Basis.Scripts.Networking
{
    public static class BasisRemotePlayerFactory
    {
        public static async Task HandleCreateRemotePlayer(LiteNetLib.NetPacketReader reader,Transform Parent)
        {
            BasisDebug.Log($"Handling Create Remote Player! {reader.AvailableBytes}");
            ServerReadyMessage ServerReadyMessage = new ServerReadyMessage();
            ServerReadyMessage.Deserialize(reader);

            await CreateRemotePlayer(ServerReadyMessage, Parent);
        }
        public static async Task<BasisNetworkPlayer> CreateRemotePlayer(ServerReadyMessage ServerReadyMessage, InstantiationParameters instantiationParameters)
        {

            ClientAvatarChangeMessage avatarID = ServerReadyMessage.localReadyMessage.clientAvatarChangeMessage;

            if (avatarID.byteArray != null)
            {
                BasisNetworkManagement.JoiningPlayers.Add(ServerReadyMessage.playerIdMessage.playerID);

                // Start both tasks simultaneously
                Task<BasisRemotePlayer> createRemotePlayerTask = BasisPlayerFactory.CreateRemotePlayer(instantiationParameters, avatarID, ServerReadyMessage.localReadyMessage.playerMetaDataMessage);
                BasisNetworkReceiver BasisNetworkReceiver = new BasisNetworkReceiver();

                BasisNetworkReceiver.ProvideNetworkKey(ServerReadyMessage.playerIdMessage.playerID);
                // Retrieve the results
                BasisRemotePlayer remote = await createRemotePlayerTask;
                // Continue with the rest of the code
                RemoteInitialization(BasisNetworkReceiver, remote, ServerReadyMessage);
                if (BasisNetworkManagement.AddPlayer(BasisNetworkReceiver))
                {
                    BasisDebug.Log("Added Player AT " + BasisNetworkReceiver.NetId);
                }
                else
                {
                    BasisDebug.LogError("Critical issue could not add player to data");
                    return null;
                }
                BasisDebug.Log("Added Player " + ServerReadyMessage.playerIdMessage.playerID);
                BasisNetworkPlayer.OnRemotePlayerJoined?.Invoke(BasisNetworkReceiver, remote);

                BasisNetworkManagement.JoiningPlayers.Remove(ServerReadyMessage.playerIdMessage.playerID);
                remote.LoadAvatarFromInitial(avatarID);

                return BasisNetworkReceiver;
            }
            else
            {
                BasisDebug.LogError("Empty Avatar ID for Player fatal error! " + ServerReadyMessage.playerIdMessage.playerID);
                return null;
            }
        }
        public static void RemoteInitialization(BasisNetworkReceiver BasisNetworkReceiver, BasisRemotePlayer RemotePlayer, ServerReadyMessage ServerReadyMessage)
        {
            BasisNetworkReceiver.Player = RemotePlayer;
            RemotePlayer.NetworkReceiver = BasisNetworkReceiver;
            if (RemotePlayer.RemoteAvatarDriver != null)
            {
                if (RemotePlayer.RemoteAvatarDriver.HasEvents == false)
                {
                    RemotePlayer.RemoteAvatarDriver.CalibrationComplete += BasisNetworkReceiver.OnAvatarCalibrationRemote;
                    RemotePlayer.RemoteAvatarDriver.HasEvents = true;
                }
                RemotePlayer.RemoteBoneDriver.FindBone(out BasisNetworkReceiver.MouthBone, BasisBoneTrackedRole.Mouth);
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
            BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(BasisNetworkReceiver, ServerReadyMessage.localReadyMessage.localAvatarSyncMessage, ServerReadyMessage.playerIdMessage.playerID);
        }
        public static async Task<BasisNetworkPlayer> CreateRemotePlayer(ServerReadyMessage ServerReadyMessage,Transform Parent)
        {
            InstantiationParameters instantiationParameters = new InstantiationParameters(Vector3.zero, Quaternion.identity, Parent);
            return await CreateRemotePlayer(ServerReadyMessage, instantiationParameters);
        }
    }
}
