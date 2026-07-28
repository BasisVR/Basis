using Basis.BasisUI;
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
        public static BasisNetworkServerRunner BasisNetworkServerRunner = null;
#if UNITY_SERVER
        public static bool HeadlessReconnectSuppressed { get; set; }
        public static Action<DisconnectInfo> OnDisconnectedAfterReboot;
#endif
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
        public static void Connect(ushort port, string ipString, string primitivePassword, bool isHostMode, string networkStackId = "")
        {
            BNL.LogOutput -= LogOutput;
            BNL.LogOutput += LogOutput;
            BNL.LogWarningOutput -= LogWarningOutput;
            BNL.LogWarningOutput += LogWarningOutput;
            BNL.LogErrorOutput -= LogErrorOutput;
            BNL.LogErrorOutput += LogErrorOutput;

            PlayerIdentity identity = BasisPlayerIdentityRegistry.ResolveActive();
            string uuid = identity?.Uuid ?? string.Empty;

            BasisTransportConfigStore.Get<LNLTransportConfig>(BasisNetworkStackRegistry.LiteNetLibId).UseNativeSockets = false;

            if (isHostMode)
            {
                ipString = "localhost";
                BasisNetworkServerRunner = new BasisNetworkServerRunner();
                var serverConfig = new Configuration
                {
                    IPv4Address = ipString,
                    SetPort = port,
                    HasFileSupport = false,
                    UseAuthIdentity = true,
                    UseAuth = BasisNetworkManagement.HostUseAuth,
                    Password = primitivePassword,
                    EnableStatistics = BasisSettingsDefaults.EnableStatistics.RawValue,
                    NetworkStackId = networkStackId ?? string.Empty,
                    ServerName = string.IsNullOrWhiteSpace(BasisNetworkManagement.HostServerName) ? "Basis Server" : BasisNetworkManagement.HostServerName,
                    ServerMotd = BasisNetworkManagement.HostServerMotd ?? string.Empty,
                    PeerLimit = BasisNetworkManagement.HostPeerLimit <= 0 ? ushort.MaxValue : BasisNetworkManagement.HostPeerLimit,
                    EnableConsole = BasisNetworkManagement.HostEnableConsole,
                    AvatarsLocked = BasisNetworkManagement.HostAvatarsLocked,
                    PropsLocked = BasisNetworkManagement.HostPropsLocked,
                    WorldsLocked = BasisNetworkManagement.HostWorldsLocked,
                    ThirdPersonDisabled = BasisNetworkManagement.HostThirdPersonDisabled,
                };
                BasisNetworkServerRunner.Initialize(serverConfig, string.Empty, uuid);
            }

            BasisDebug.Log($"Connecting with Port {port} IpString {ipString}");
            BasisP2PManager.StampServerEndpoint(ipString, port);

            var basisLocalPlayer = BasisLocalPlayer.Instance;
            basisLocalPlayer.UUID = uuid;

            byte[] avatarBytes = BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(basisLocalPlayer.AvatarMetaData);

            // Calibration usually happens before ever connecting, so the body fit has to ride the very
            // first record — otherwise the server stores identity for us and nobody sees the fitted
            // proportions until the next recalibration happens to produce a different answer.
            Basis.IK.BasisBodyFitResult bodyFit = Basis.Scripts.Drivers.BasisLocalRigDriver.AppliedBodyFit;

            var readyMessage = new ReadyMessage
            {
                clientAvatarChangeMessage = new ClientAvatarChangeMessage
                {
                    byteArray = avatarBytes,
                    loadMode = basisLocalPlayer.AvatarLoadMode,
                    LocalAvatarIndex = 0,
                    ArmScale = bodyFit.HasArmFit ? bodyFit.ArmScale : 1f,
                    LegScale = bodyFit.HasBodyFit ? bodyFit.LegScale : 1f,
                    TorsoScale = bodyFit.HasBodyFit ? bodyFit.TorsoScale : 1f,
                },
                playerMetaDataMessage = new ClientMetaDataMessage
                {
                    playerUUID = basisLocalPlayer.UUID,
                    playerDisplayName = basisLocalPlayer.DisplayName,
                    playerPlatform = basisLocalPlayer.PlayerPlatform,
                }
            };

            BasisNetworkAvatarCompressor.InitialAvatarData(basisLocalPlayer.BasisAvatar.Animator, out var dataSet);
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
                        UseAuthIdentity = true,
                        UseAuth = true,
                        Password = primitivePassword,
                        EnableStatistics = BasisSettingsDefaults.EnableStatistics.RawValue,
                        NetworkStackId = networkStackId ?? string.Empty,
                    };
                    // Pass the token into anything that supports cancellation
                    LocalPlayerPeer = NetworkClient.StartClient(
                        ipString, port, readyMessage,
                        Encoding.UTF8.GetBytes(primitivePassword), serverConfig);

                    NetworkClient.listener.PeerConnectedEvent -= PeerConnectedEvent;
                    NetworkClient.listener.PeerConnectedEvent += PeerConnectedEvent;
                    NetworkClient.listener.PeerDisconnectedEvent -= BasisNetworkConnection.HandleDisconnection;
                    NetworkClient.listener.PeerDisconnectedEvent += BasisNetworkConnection.HandleDisconnection;
                    BasisNetworkEvents.EnsureInitialized();
                    NetworkClient.listener.NetworkReceiveEvent -= BasisNetworkEvents.NetworkReceiveEvent;
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
            BasisDebug.Log("Transport connected; awaiting authentication before local player setup.");
            LocalPlayerPeer = peer;
        }

        public static void SetupLocalPlayer(NetPeer peer)
        {
            BasisDebug.Log("Authentication confirmed! Now setting up Networked Local Player");
#if UNITY_SERVER
            BasisHeadlessRuntimeStatus.MarkConnected();
#endif

            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                if (LocalPlayerIsConnected)
                {
                    return;
                }
                BasisDebug.Log("SetupLocalPlayer On MainThread");
                try
                {
#if UNITY_SERVER
                    Basis.Scripts.Device_Management.Devices.Headless.BasisHeadlessInput.Instance?.ResumeMovement();
#endif
                    LocalPlayerPeer = peer;
                    ushort localPlayerID = (ushort)peer.RemoteId;

                    var transmitter = new BasisNetworkTransmitter(localPlayerID);
                    BasisNetworkManagement.Transmitter = transmitter;
                    BasisNetworkManagement.LocalAccessTransmitter = transmitter;
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
#if UNITY_SERVER
                if (disconnectInfo.Reason == DisconnectReason.Timeout)
                {
                    string peerId = peer == null ? "null" : peer.RemoteId.ToString();
                    BasisDebug.LogWarning($"Headless timeout diagnostic: peer={peerId}, localConnected={LocalPlayerIsConnected}, playerReady={BasisLocalPlayer.PlayerReady}, realtime={Time.realtimeSinceStartup:F1}s", BasisDebug.LogTag.Networking);
                }
                Basis.Scripts.Device_Management.Devices.Headless.BasisHeadlessInput.Instance?.StopMovement();
#endif
                BasisNetworkConnectionWatchdog.NotifyDisconnected(disconnectInfo);
                BasisNetworkAvatarCompressor.Dispose();
                BasisP2PManager.Shutdown();
                BasisAvatarRateRegistry.Reset();
                await BasisNetworkLifeCycle.RebootManagement(true, peer, disconnectInfo);
                BasisNetworkConnectionWatchdog.NotifyRebootComplete();
#if UNITY_SERVER
                if (!HeadlessReconnectSuppressed)
                {
                    OnDisconnectedAfterReboot?.Invoke(disconnectInfo);
                }
#endif
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
