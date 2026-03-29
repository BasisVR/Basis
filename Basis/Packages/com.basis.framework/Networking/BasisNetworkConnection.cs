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
using System.Threading;
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
        public static bool SuppressNextDisconnectUi { get; set; }
        public static event Action<NetPeer> OnConnectedToServer;
        public static event Action<NetPeer, DisconnectInfo> OnDisconnectedFromServer;
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
        public static void Connect(BasisNetworkManagement networkManagement)
        {
            if (networkManagement == null)
            {
                BasisDebug.LogError("Missing BasisNetworkManagement during connect.", BasisDebug.LogTag.Networking);
                return;
            }

            Connect(
                networkManagement.Port,
                networkManagement.Ip,
                networkManagement.Password,
                networkManagement.IsHostMode,
                networkManagement.Transport,
                networkManagement.UseSteamRelay,
                networkManagement.CurrentSteamLobbyId,
                networkManagement.CurrentHostSteamId,
                networkManagement.CurrentSteamVirtualPort
            );
        }
        public static bool HasActiveClient() => NetworkClient != null && NetworkClient.HasActiveClient;
        public static void DisconnectActiveClient()
        {
            NetworkClient?.Disconnect();
            LocalPlayerPeer = null;
            LocalPlayerIsConnected = false;
        }
        public static async Task ResetConnectionStateAsync(BasisNetworkManagement management)
        {
            if (!LocalPlayerIsConnected && !HasActiveClient())
            {
                return;
            }

            if (management == null)
            {
                SuppressNextDisconnectUi = true;
                DisconnectActiveClient();
                return;
            }

            if (!LocalPlayerIsConnected)
            {
                SuppressNextDisconnectUi = true;
                DisconnectActiveClient();
                return;
            }

            using var cts = new CancellationTokenSource();
            Task rebootWait = WaitForRebootCompleteAsync(cts.Token);

            SuppressNextDisconnectUi = true;
            await BasisNetworkLifeCycle.Destroy(management);
            await rebootWait;

            if (management != null)
            {
                BasisNetworkLifeCycle.Initalize(management);
            }
        }
        public static void Connect(ushort port, string ipString, string primitivePassword, bool isHostMode)
        {
            Connect(port, ipString, primitivePassword, isHostMode, NetworkTransportType.LiteNetLib, true, 0, 0, 0);
        }
        public static void Connect(ushort port, string ipString, string primitivePassword, bool isHostMode, NetworkTransportType transportType, bool useSteamRelay, ulong steamLobbyId, ulong steamHostSteamId, int steamVirtualPort)
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
                    EnableStatistics = false,
                    TransportType = transportType,
                    UseSteamRelay = useSteamRelay,
                    SteamLobbyId = steamLobbyId,
                    SteamHostSteamId = steamHostSteamId,
                    SteamVirtualPort = steamVirtualPort
                };
                BasisDebug.Log($"Initializing host server with transport {transportType} relay={useSteamRelay} virtualPort={steamVirtualPort}", BasisDebug.LogTag.Networking);
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
                    playerDisplayName = basisLocalPlayer.DisplayName,
                    playerPlatform = basisLocalPlayer.PlayerPlatform,
                }
            };

            BasisNetworkAvatarCompressor.InitalAvatarData(basisLocalPlayer.BasisAvatar.Animator, out var dataSet);
            readyMessage.localAvatarSyncMessage = dataSet.LASM;

            BasisDebug.Log("Network Starting Client");

            void StartClientConnection()
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
                        EnableStatistics = false,
                        TransportType = transportType,
                        UseSteamRelay = useSteamRelay,
                        SteamLobbyId = steamLobbyId,
                        SteamHostSteamId = steamHostSteamId,
                        SteamVirtualPort = steamVirtualPort
                    };
                    NetworkClient.OnPeerConnected = PeerConnectedEvent;
                    NetworkClient.OnPeerDisconnected = BasisNetworkConnection.HandleDisconnection;
                    NetworkClient.OnNetworkReceive = BasisNetworkEvents.NetworkReceiveEvent;

                    LocalPlayerPeer = NetworkClient.StartClient(
                        ipString, port, readyMessage,
                        Encoding.UTF8.GetBytes(primitivePassword), serverConfig);

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
            }

            if (transportType == NetworkTransportType.Steam)
            {
                StartClientConnection();
            }
            else
            {
                _ = Task.Run(StartClientConnection);
            }
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

                    LocalPlayerIsConnected = true;

                    OnConnectedToServer?.Invoke(peer);
                    BasisNetworkPlayer.OnLocalPlayerJoined?.Invoke(transmitter, BasisLocalPlayer.Instance);
                    BasisNetworkPlayer.OnPlayerJoined?.Invoke(transmitter);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Error setting up the local player: {ex.Message} {ex.StackTrace}");
                }
            });
        }
        public static Action OnRebootComplete;
        public static void HandleDisconnection(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            BasisDeviceManagement.EnqueueOnMainThread(async () =>
            {
                OnDisconnectedFromServer?.Invoke(peer, disconnectInfo);
                BasisNetworkAvatarCompressor.Dispose();
                bool displayReason = !SuppressNextDisconnectUi;
                SuppressNextDisconnectUi = false;
                await BasisNetworkLifeCycle.RebootManagement(BasisNetworkManagement.Instance, displayReason, peer, disconnectInfo);
                OnRebootComplete?.Invoke();
            });
        }
        public static Task WaitForRebootCompleteAsync(CancellationToken ct = default)
        {
            // Run continuations asynchronously to avoid executing awaiting code inside the event invoke call stack.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler()
            {
                OnRebootComplete -= Handler;
                tcs.TrySetResult(true);
            }

            OnRebootComplete += Handler;

            // Cancellation support
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
            {
                ctr = ct.Register(() =>
                {
                    OnRebootComplete -= Handler;
                    tcs.TrySetCanceled(ct);
                });
            }
            // No timeout; still dispose registration when done
            _ = tcs.Task.ContinueWith(_ => ctr.Dispose(), TaskScheduler.Default);

            return tcs.Task;
        }
    }
}
