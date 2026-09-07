using Basis.Network.Core;
using Basis.Scripts.Avatar;
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

        /// <summary>
        /// Spawn path for a record the avatar load thread already decoded. Everything left here is
        /// Unity-affine: the mouth marker, the nameplate, the receiver, and the fallback avatar.
        /// </summary>
        public static BasisNetworkPlayer CreateRemotePlayer(BasisPreparedJoin prepared, InstantiationParameters instantiationParameters)
        {
            if (prepared == null)
            {
                return null;
            }
            if (!BasisAvatarLoadThread.IsCurrent(prepared))
            {
                // Decoded for a connection that has since torn down. The pose buffer is pooled and
                // no receiver will ever take ownership of it, so hand it back here.
                ReleaseSpawnPose(prepared);
                BasisNetworkPlayers.JoiningPlayers.TryRemove(prepared.PlayerId, out _);
                return null;
            }
            return CreateRemotePlayer(prepared.Ready, instantiationParameters, prepared);
        }

        public static BasisNetworkPlayer CreateRemotePlayer(ServerReadyMessage ServerReadyMessage, InstantiationParameters instantiationParameters)
        {
            return CreateRemotePlayer(ServerReadyMessage, instantiationParameters, null);
        }

        private static BasisNetworkPlayer CreateRemotePlayer(ServerReadyMessage ServerReadyMessage, InstantiationParameters instantiationParameters, BasisPreparedJoin prepared)
        {
            ClientAvatarChangeMessage avatarID = ServerReadyMessage.localReadyMessage.clientAvatarChangeMessage;
            ushort playerId = ServerReadyMessage.playerIdMessage.playerID;
            if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId) && localPlayerId == playerId)
            {
                BasisDebug.LogError($"Ignoring a remote spawn record that carries the local player's own id {playerId}.", BasisDebug.LogTag.Networking);
                if (prepared != null)
                {
                    ReleaseSpawnPose(prepared);
                }
                BasisNetworkPlayers.JoiningPlayers.TryRemove(playerId, out _);
                return null;
            }
            BasisNetworkPlayers.JoiningPlayers.TryAdd(playerId, 0);

            try
            {
                BasisRemotePlayer remote = BasisPlayerFactory.CreateRemotePlayer(instantiationParameters, avatarID, ServerReadyMessage.localReadyMessage.playerMetaDataMessage, prepared?.SafeDisplayName);
                BasisNetworkReceiver BasisNetworkReceiver = new BasisNetworkReceiver(playerId);
                RemoteInitialization(BasisNetworkReceiver, remote, ServerReadyMessage, avatarID.LocalAvatarIndex, prepared);

                if (avatarID.byteArray != null && avatarID.byteArray.Length > 0)
                {
                    if (prepared != null && prepared.AvatarDecodeError != null)
                    {
                        // The load thread already tried and failed to decode the blob; re-running
                        // the same decode here would only fail again, on the frame thread.
                        remote.AvatarLoadErrorMessage = prepared.AvatarDecodeError;
                    }
                    else
                    {
                        remote.LoadAvatarFromInitial(avatarID, prepared?.InitialAvatar);
                    }
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
                        Discard(BasisNetworkReceiver, remote);
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

        public static void RemoteInitialization(BasisNetworkReceiver BasisNetworkReceiver, BasisRemotePlayer RemotePlayer, ServerReadyMessage ServerReadyMessage, byte LocalAvatarIndex)
        {
            RemoteInitialization(BasisNetworkReceiver, RemotePlayer, ServerReadyMessage, LocalAvatarIndex, null);
        }

        private static void RemoteInitialization(BasisNetworkReceiver BasisNetworkReceiver, BasisRemotePlayer RemotePlayer, ServerReadyMessage ServerReadyMessage, byte LocalAvatarIndex, BasisPreparedJoin prepared)
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
            BasisNetworkReceiver.Initialize();//fires events and makes us network compatible

            LocalAvatarSyncMessage spawnSync = ServerReadyMessage.localReadyMessage.localAvatarSyncMessage;
            if (prepared != null && prepared.SpawnPose != null)
            {
                // Bit-unpacked on the avatar load thread. Ownership of the pooled buffer transfers
                // to the receiver here, so clear it either way — a second apply would double-free.
                BasisAvatarBuffer spawnPose = prepared.SpawnPose;
                prepared.SpawnPose = null;
                BasisNetworkAvatarDecompressor.ApplyDecodedSpawnPose(BasisNetworkReceiver, spawnPose, spawnSync);
            }
            else if (prepared == null)
            {
                BasisNetworkAvatarDecompressor.DecompressAndProcessAvatar(BasisNetworkReceiver, spawnSync);
            }
        }

        private static void Discard(BasisNetworkReceiver receiver, BasisRemotePlayer remote)
        {
            receiver.DeInitialize();
            BasisAvatarFactory.CancelPlayerLoad(remote);
            remote.OnDestroy();
            BasisAvatarFactory.DeleteLastAvatar(remote);
        }

        /// <summary>
        /// Returns an unconsumed spawn-pose buffer to the pool. Only reached when a prepared
        /// record is discarded before a receiver takes ownership of it.
        /// </summary>
        private static void ReleaseSpawnPose(BasisPreparedJoin prepared)
        {
            BasisAvatarBuffer spawnPose = prepared.SpawnPose;
            if (spawnPose == null)
            {
                return;
            }
            prepared.SpawnPose = null;
            BasisAvatarBufferPool.Release(spawnPose);
        }
    }
}
