using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.UI.UI_Panels;
using BasisNetworkClient;
using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Connection/session management, server runner, time utilities, and send helpers.
    /// </summary>
    public static class BasisNetworkConnection
    {
        public static NetPeer LocalPlayerPeer { get; set; }
        public static NetworkClient NetworkClient { get; set; } = new NetworkClient();
        public static bool LocalPlayerIsConnected { get; set; }
        public static BasisNetworkServerRunner BasisNetworkServerRunner = null;
        private static void LogErrorOutput(string msg) => BasisDebug.LogError(msg, BasisDebug.LogTag.Networking);
        private static void LogWarningOutput(string msg) => BasisDebug.LogWarning(msg);
        private static void LogOutput(string msg) => BasisDebug.Log(msg, BasisDebug.LogTag.Networking);
        public static bool TryGetLocalPlayerID(out ushort localId)
        {
            localId = 0;
            if (LocalPlayerPeer == null) return false;
            localId = (ushort)LocalPlayerPeer.RemoteId;
            return true;
        }
        public static void Connect(ushort port, string ipString, string primitivePassword, bool isHostMode)
        {
            BNL.LogOutput += LogOutput;
            BNL.LogWarningOutput += LogWarningOutput;
            BNL.LogErrorOutput += LogErrorOutput;

            var uuid = BasisDIDAuthIdentityClient.GetOrSaveDID();

            if (isHostMode)
            {
                ipString = "localhost";
                BasisNetworkServerRunner = new BasisNetworkServerRunner();
                var serverConfig = new Configuration
                {
                    IPv4Address = ipString,
                    HasFileSupport = false,
                    UseNativeSockets = false,
                    UseAuthIdentity = true,
                    UseAuth = true,
                    Password = primitivePassword,
                    EnableStatistics = false
                };
                BasisNetworkServerRunner.Initalize(serverConfig, string.Empty, uuid);
            }

            BasisDebug.Log($"Connecting with Port {port} IpString {ipString}");

            var basisLocalPlayer = BasisLocalPlayer.Instance;
            basisLocalPlayer.UUID = uuid;

            byte[] avatarBytes = BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(basisLocalPlayer.AvatarMetaData);

            var readyMessage = new ReadyMessage
            {
                clientAvatarChangeMessage = new ClientAvatarChangeMessage
                {
                    byteArray = avatarBytes,
                    loadMode = basisLocalPlayer.AvatarLoadMode,
                    LocalAvatarIndex = 0,
                },
                playerMetaDataMessage = new ClientMetaDataMessage
                {
                    playerUUID = basisLocalPlayer.UUID,
                    playerDisplayName = basisLocalPlayer.DisplayName
                }
            };

            BasisNetworkAvatarCompressor.InitalAvatarData(basisLocalPlayer.BasisAvatar.Animator, out var dataSet);
            readyMessage.localAvatarSyncMessage = dataSet.LASM;

            BasisDebug.Log("Network Starting Client");

            _ = Task.Run(() =>
            {
                try
                {
                    var serverConfig = new Configuration
                    {
                        IPv4Address = ipString,
                        HasFileSupport = false,
                        UseNativeSockets = false,
                        UseAuthIdentity = true,
                        UseAuth = true,
                        Password = primitivePassword,
                        EnableStatistics = false
                    };
                    // Pass the token into anything that supports cancellation
                    LocalPlayerPeer = NetworkClient.StartClient(
                        ipString, port, readyMessage,
                        Encoding.UTF8.GetBytes(primitivePassword), serverConfig);

                    NetworkClient.listener.PeerConnectedEvent += PeerConnectedEvent;
                    NetworkClient.listener.PeerDisconnectedEvent += BasisNetworkEvents.PeerDisconnectedEvent;
                    NetworkClient.listener.NetworkReceiveEvent += BasisNetworkEvents.NetworkReceiveEvent;

                    if (LocalPlayerPeer != null)
                    {
                        BasisDebug.Log("Network Client Started " + LocalPlayerPeer.RemoteId);

                    }
                    else
                    {
                        HandleDisconnection(null, new DisconnectInfo
                        {
                            Reason = DisconnectReason.ConnectionFailed
                        });
                    }
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError("Client task error: " + ex, BasisDebug.LogTag.Networking);
                    HandleDisconnection(null, new DisconnectInfo
                    {
                        Reason = DisconnectReason.UnknownHost
                    });
                }
            });
        }
        public static void OnDestroy()
        {
            BasisNetworkAvatarCompressor.Dispose();
        }
        private static void PeerConnectedEvent(NetPeer peer)
        {
            BasisDebug.Log("Success! Now setting up Networked Local Player");

            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                BasisDebug.Log("PeerConnectedEvent On MainThread");
                try
                {
                    LocalPlayerPeer = peer;
                    ushort localPlayerID = (ushort)peer.RemoteId;

                    BasisNetworkManagement.Instance.transform.GetPositionAndRotation(out Vector3 _, out Quaternion _);

                    var transmitter = new BasisNetworkTransmitter(localPlayerID);
                    BasisNetworkManagement.Transmitter = transmitter;
                    BasisNetworkManagement.Instance.LocalAccessTransmitter = transmitter;
                    transmitter.Player = BasisLocalPlayer.Instance;

                    if (BasisLocalPlayer.Instance.LocalAvatarDriver != null)
                    {
                        if (BasisLocalAvatarDriver.HasEvents == false)
                        {
                            BasisLocalAvatarDriver.CalibrationComplete += transmitter.OnAvatarCalibrationLocal;
                            BasisLocalAvatarDriver.HasEvents = true;
                        }
                        transmitter.TransmissionResults.BasisNetworkTransmitter = transmitter;
                    }
                    else
                    {
                        BasisDebug.LogError("Missing CharacterIKCalibration");
                    }

                    if (!BasisNetworkPlayers.AddPlayer(transmitter))
                    {
                        BasisDebug.LogError($"Cannot add player {localPlayerID}");
                    }

                    transmitter.Initialize();

                    BasisNetworkPlayer.OnLocalPlayerJoined?.Invoke(transmitter, BasisLocalPlayer.Instance);
                    BasisNetworkPlayer.OnPlayerJoined?.Invoke(transmitter);

                    LocalPlayerIsConnected = true;
                    if (BasisSetUserName.Instance != null)
                    {
                        BasisSetUserName.Instance.DestroyUserNamePanel();
                    }
                }
                catch (Exception ex)
                {
                    if (BasisSetUserName.Instance != null && BasisSetUserName.Instance.Ready != null)
                    {
                        BasisSetUserName.Instance.Ready.interactable = true;
                    }
                    BasisDebug.LogError($"Error setting up the local player: {ex.Message} {ex.StackTrace}");
                }
            });
        }
        public static void HandleDisconnection(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            BasisDeviceManagement.EnqueueOnMainThread(async () =>
            {
                BasisNetworkAvatarCompressor.Dispose();
                await BasisNetworkLifeCycle.RebootManagement(BasisNetworkManagement.Instance, true, peer, disconnectInfo);
            });
        }
    }
}
