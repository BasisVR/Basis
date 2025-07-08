using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using BasisNetworkClient;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using static DarkRift.Basis_Common.Serializable.SerializableBasis;
using static SerializableBasis;
namespace Basis.Scripts.Networking
{
    [DefaultExecutionOrder(15001)]
    public class BasisNetworkManagement : MonoBehaviour
    {
        public string Ip = "170.64.184.249";
        public ushort Port = 4296;
        [HideInInspector]
        public string Password = "default_password";
        public bool IsHostMode = false;
        /// <summary>
        /// fire when ownership is changed for a unique string
        /// </summary>
        public static ConcurrentDictionary<ushort, BasisNetworkPlayer> Players = new ConcurrentDictionary<ushort, BasisNetworkPlayer>();
        public static ConcurrentDictionary<ushort, BasisNetworkReceiver> RemotePlayers = new ConcurrentDictionary<ushort, BasisNetworkReceiver>();
        public static HashSet<ushort> JoiningPlayers = new HashSet<ushort>();

        [SerializeField]
        public static BasisNetworkReceiver[] ReceiversSnapshot;
        public static int ReceiverCount = 0;

        public static SynchronizationContext MainThreadContext;
        public static NetPeer LocalPlayerPeer;
        public static BasisNetworkTransmitter Transmitter;
        [SerializeField]
        public BasisNetworkTransmitter SerTransmitter;
        [SerializeField]
        public NetworkClient NetworkClient = new NetworkClient();
        public static bool LocalPlayerIsConnected { get; private set; }
        public static Action OnEnableInstanceCreate;
        public static BasisNetworkManagement Instance;
        public static Dictionary<string, ushort> OwnershipPairing = new Dictionary<string, ushort>();
       public static InstantiationParameters instantiationParameters;
        /// <summary>
        /// allows a local host to be the server
        /// </summary>
        public BasisNetworkServerRunner BasisNetworkServerRunner = null;
        public static bool NetworkRunning = false;
        public bool IsRunning = true;


        public static bool AddPlayer(BasisNetworkPlayer NetPlayer)
        {
            if (Instance != null)
            {
                if (NetPlayer.Player != null)
                {
                    if (NetPlayer.Player.IsLocal == false)
                    {
                        RemotePlayers.TryAdd(NetPlayer.NetId, (BasisNetworkReceiver)NetPlayer);
                        BasisNetworkManagement.ReceiversSnapshot = RemotePlayers.Values.ToArray();
                        ReceiverCount = ReceiversSnapshot.Length;
                      //  BasisDebug.Log("ReceiverCount was " + ReceiverCount);
                    }
                }
                else
                {
                    BasisDebug.LogError("Player was Null!");
                }
                return Players.TryAdd(NetPlayer.NetId, NetPlayer);
            }
            else
            {
                BasisDebug.LogError("No network Instance Existed!");
            }
            return false;
        }
        public static bool RemovePlayer(ushort NetID)
        {
            if (Instance != null)
            {
                RemotePlayers.TryRemove(NetID, out BasisNetworkReceiver A);
                BasisNetworkManagement.ReceiversSnapshot = RemotePlayers.Values.ToArray();
                ReceiverCount = ReceiversSnapshot.Length;
                //BasisDebug.Log("ReceiverCount was " + ReceiverCount);
                return Players.Remove(NetID, out var B);
            }
            return false;
        }
        public static ServerMetaDataMessage ServerMetaDataMessage = new ServerMetaDataMessage();
        public void OnEnable()
        {
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }
            instantiationParameters = new InstantiationParameters(Vector3.zero, Quaternion.identity, BasisDeviceManagement.Instance.transform);
            BasisAvatarMuscleRange.Initalize();
            MainThreadContext = SynchronizationContext.Current;
            // Initialize AvatarBuffer
            BasisAvatarBufferPool.AvatarBufferPool(30);
            OwnershipPairing.Clear();
            if (BasisScene.Instance != null)
            {
                SetupSceneEvents(BasisScene.Instance);
            }



            ServerMetaDataMessage = new ServerMetaDataMessage();
            ServerMetaDataMessage.ClientMetaDataMessage = new ClientMetaDataMessage();
            ServerMetaDataMessage.SyncInterval = 50;
            ServerMetaDataMessage.BaseMultiplier = 1;
            ServerMetaDataMessage.IncreaseRate = 0.005f;
            ServerMetaDataMessage.SlowestSendRate = 2.5f;

            BasisScene.Ready += SetupSceneEvents;
            if (BasisDeviceManagement.Instance != null)
            {
                this.transform.parent = BasisDeviceManagement.Instance.transform;
            }
            this.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            OnEnableInstanceCreate?.Invoke();
            NetworkRunning = true;
        }
        private void LogErrorOutput(string obj)
        {
            BasisDebug.LogError(obj, BasisDebug.LogTag.Networking);
        }
        private void LogWarningOutput(string obj)
        {
            BasisDebug.LogWarning(obj);
        }
        private void LogOutput(string obj)
        {
            BasisDebug.Log(obj, BasisDebug.LogTag.Networking);
        }
        public async void OnDestroy()
        {
            Players.Clear();
            await Shutdown();
            BasisAvatarBufferPool.Clear();
            NetworkClient.Disconnect();
            NetworkRunning = false;
        }
        public async Task Shutdown()
        {
            // Reset static fields
            Ip = "0.0.0.0";
            Port = 0;
            Password = string.Empty;
            IsHostMode = false;
            BasisNetworkPlayer.OnOwnershipTransfer = null;
            Players.Clear();
            RemotePlayers.Clear();
            JoiningPlayers.Clear();
            await BasisNetworkSpawnItem.Reset();
            ReceiverCount = 0;
            MainThreadContext = null;
            LocalPlayerPeer = null;
            Transmitter = null;
            SerTransmitter = null;
            BasisNetworkPlayer.OnLocalPlayerJoined = null;
            LocalPlayerIsConnected = false;
            BasisNetworkPlayer.OnRemotePlayerJoined = null;
            BasisNetworkPlayer.OnLocalPlayerLeft = null;
            BasisNetworkPlayer.OnRemotePlayerLeft = null;
            OnEnableInstanceCreate = null;

            // Reset instance fields
            Instance = null;
            OwnershipPairing.Clear();
            BasisNetworkServerRunner = null;

            if (BasisNetworkManagement.ReceiversSnapshot != null)
            {
                Array.Clear(BasisNetworkManagement.ReceiversSnapshot, 0, BasisNetworkManagement.ReceiversSnapshot.Length);
                BasisNetworkManagement.ReceiversSnapshot = null;
            }

            BasisDebug.Log("BasisNetworkManagement has been successfully shutdown.", BasisDebug.LogTag.Networking);
        }
        public static void SimulateNetworkCompute()
        {
            if (NetworkRunning)
            {
                double TimeAsDouble = Time.timeAsDouble;

                // Schedule multithreaded tasks
                for (int Index = 0; Index < ReceiverCount; Index++)
                {
                    if (ReceiversSnapshot[Index] != null)
                    {
                        ReceiversSnapshot[Index].Compute(TimeAsDouble);
                    }
                }
                BasisNetworkProfiler.Update();
            }
        }
        public static void SimulateNetworkApply()
        {
            if (NetworkRunning)
            {
                double TimeAsDouble = Time.timeAsDouble;
                // Complete tasks and apply results
                for (int Index = 0; Index < ReceiverCount; Index++)
                {
                    if (ReceiversSnapshot[Index] != null)
                    {
                        ReceiversSnapshot[Index].Apply(TimeAsDouble);
                    }
                }
            }
        }
        public static bool TryGetLocalPlayerID(out ushort LocalID)
        {
            if (Instance != null)
            {
                LocalID = (ushort)LocalPlayerPeer.RemoteId;
                return true;
            }
            LocalID = 0;
            return false;
        }
        public void SetupSceneEvents(BasisScene BasisScene)
        {
            BasisScene.OnNetworkMessageSend += BasisNetworkGenericMessages.OnNetworkMessageSend;
        }
        public void Connect()
        {
            Connect(Port, Ip, Password, IsHostMode);
        }
        public void Connect(ushort Port, string IpString, string PrimitivePassword, bool IsHostMode)
        {
            BNL.LogOutput += LogOutput;
            BNL.LogWarningOutput += LogWarningOutput;
            BNL.LogErrorOutput += LogErrorOutput;

            string UUID = BasisDIDAuthIdentityClient.GetOrSaveDID();
            if (IsHostMode)
            {
                IpString = "localhost";
                BasisNetworkServerRunner = new BasisNetworkServerRunner();
                Configuration ServerConfig = new Configuration() { IPv4Address = IpString, HasFileSupport = false, UseNativeSockets = false, UseAuthIdentity = true, UseAuth = true, Password = PrimitivePassword, EnableStatistics = false  };
                BasisNetworkServerRunner.Initalize(ServerConfig, string.Empty, UUID);
            }

            BasisDebug.Log("Connecting with Port " + Port + " IpString " + IpString);
            //   string result = BasisNetworkIPResolve.ResolveHosttoIP(IpString);
            //   BasisDebug.Log($"DNS call: {IpString} resolves to {result}");
            BasisLocalPlayer BasisLocalPlayer = BasisLocalPlayer.Instance;
            BasisLocalPlayer.UUID = UUID;
            byte[] Information = BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(BasisLocalPlayer.AvatarMetaData);
            ReadyMessage readyMessage = new ReadyMessage
            {
                clientAvatarChangeMessage = new ClientAvatarChangeMessage
                {
                    byteArray = Information,
                    loadMode = BasisLocalPlayer.AvatarLoadMode,
                },
                playerMetaDataMessage = new ClientMetaDataMessage
                {
                    playerUUID = BasisLocalPlayer.UUID,
                    playerDisplayName = BasisLocalPlayer.DisplayName
                }
            };
            BasisNetworkAvatarCompressor.InitalAvatarData(BasisLocalPlayer.Instance.BasisAvatar.Animator, out readyMessage.localAvatarSyncMessage);
            BasisDebug.Log("Network Starting Client");
            // BasisDebug.Log("Size is " + BasisNetworkClient.AuthenticationMessage.Message.Length);
            LocalPlayerPeer = NetworkClient.StartClient(IpString, Port, readyMessage, Encoding.UTF8.GetBytes(PrimitivePassword), true);
            NetworkClient.listener.PeerConnectedEvent += PeerConnectedEvent;
            NetworkClient.listener.PeerDisconnectedEvent += PeerDisconnectedEvent;
            NetworkClient.listener.NetworkReceiveEvent += NetworkReceiveEvent;
            if (LocalPlayerPeer != null)
            {
                BasisDebug.Log("Network Client Started " + LocalPlayerPeer.RemoteId);
            }
            else
            {
                ForceShutdown();
            }
        }
        private async void PeerConnectedEvent(NetPeer peer)
        {
            await PeerConnectedEventAsync(peer);
        }
        private async Task PeerConnectedEventAsync(NetPeer peer)
        {
            BasisDebug.Log("Success! Now setting up Networked Local Player");

            // Wrap the main logic in a task for thread safety and asynchronous execution.
            await Task.Run((Action)(() =>
            {
                BasisNetworkManagement.MainThreadContext.Post((SendOrPostCallback)(_ =>
                {
                    try
                    {
                        LocalPlayerPeer = peer;
                        ushort LocalPlayerID = (ushort)peer.RemoteId;
                        // Create the local networked player asynchronously.
                        this.transform.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                        Transmitter = new BasisNetworkTransmitter(LocalPlayerID);
                        SerTransmitter = Transmitter;
                        // Initialize the local networked player.
                        LocalInitalize(Transmitter, BasisLocalPlayer.Instance);
                        if (AddPlayer(Transmitter))
                        {
                          //  BasisDebug.Log($"Added local player {LocalPlayerID}");
                        }
                        else
                        {
                            BasisDebug.LogError($"Cannot add player {LocalPlayerID}");
                        }
                        Transmitter.Initialize();
                        // Notify listeners about the local player joining.
                        BasisNetworkPlayer.OnLocalPlayerJoined?.Invoke(Transmitter, BasisLocalPlayer.Instance);
                        LocalPlayerIsConnected = true;
                    }
                    catch (Exception ex)
                    {
                        BasisDebug.LogError($"Error setting up the local player: {ex.Message} {ex.StackTrace}");
                    }
                }), null);
            }));
        }
        public static void LocalInitalize(BasisNetworkTransmitter BasisNetworkPlayer, BasisLocalPlayer BasisLocalPlayer)
        {
            BasisNetworkPlayer.Player = BasisLocalPlayer;
            if (BasisLocalPlayer.LocalAvatarDriver != null)
            {
                if (BasisLocalAvatarDriver.HasEvents == false)
                {
                    BasisLocalAvatarDriver.CalibrationComplete += BasisNetworkPlayer.OnAvatarCalibrationLocal;
                    BasisLocalAvatarDriver.HasEvents = true;
                }
                BasisLocalPlayer.LocalBoneDriver.FindBone(out BasisNetworkPlayer.MouthBone, BasisBoneTrackedRole.Mouth);
            }
            else
            {
                BasisDebug.LogError("Missing CharacterIKCalibration");
            }
        }
        private async void PeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            BasisDebug.Log($"Client disconnected from server [{peer.Id}]");
            if (peer == LocalPlayerPeer)
            {
                await Task.Run(() =>
                {
                    MainThreadContext.Post(async _ =>
                    {
                        if (LocalPlayerPeer != null && Players.TryGetValue((ushort)LocalPlayerPeer.RemoteId, out BasisNetworkPlayer NetworkedPlayer))
                        {
                            BasisNetworkPlayer.OnLocalPlayerLeft?.Invoke(NetworkedPlayer, (BasisLocalPlayer)NetworkedPlayer.Player);
                        }
                        if (BasisNetworkServerRunner != null)
                        {
                            BasisNetworkServerRunner.Stop();
                            BasisNetworkServerRunner = null;
                        }
                        Players.Clear();
                        OwnershipPairing.Clear();
                        ReceiverCount = 0;
                        BasisDebug.Log($"Client disconnected from server [{peer.RemoteId}] [{disconnectInfo.Reason}]");
                        SceneManager.LoadScene(0, LoadSceneMode.Single);//reset
                        await Boot_Sequence.BootSequence.OnAddressablesInitializationComplete();
                        HandleDisconnectionReason(disconnectInfo);
                    }, null);
                });
            }
        }
        public void HandleDisconnectionReason(DisconnectInfo disconnectInfo)
        {
            if (disconnectInfo.Reason == DisconnectReason.RemoteConnectionClose)
            {
                if (disconnectInfo.AdditionalData.TryGetString(out string Reason))
                {
                    BasisUINotification.OpenNotification(Reason, true, new Vector3(0, 0, 0.9f));
                    BasisDebug.LogError(Reason);
                }
            }
            else
            {
                BasisUINotification.OpenNotification(disconnectInfo.Reason.ToString(), true, new Vector3(0, 0, 0.9f));
            }
        }
        public async void ForceShutdown()
        {
            await Task.Run(() =>
            {
                MainThreadContext.Post(async _ =>
                 {
                     if (LocalPlayerPeer != null && Players.TryGetValue((ushort)LocalPlayerPeer.RemoteId, out BasisNetworkPlayer NetworkedPlayer))
                     {
                         BasisNetworkPlayer.OnLocalPlayerLeft?.Invoke(NetworkedPlayer, (BasisLocalPlayer)NetworkedPlayer.Player);
                     }
                     if (BasisNetworkServerRunner != null)
                     {
                         BasisNetworkServerRunner.Stop();
                         BasisNetworkServerRunner = null;
                     }
                     Players.Clear();
                     OwnershipPairing.Clear();
                     ReceiverCount = 0;
                     SceneManager.LoadScene(0, LoadSceneMode.Single);//reset
                     await Boot_Sequence.BootSequence.OnAddressablesInitializationComplete();
                     BasisUINotification.OpenNotification("Unable to connect to Address!", true, new Vector3(0, 0, 0.9f));
                 }, null);
            });
        }
        public static bool ValidateSize(NetPacketReader reader, NetPeer peer, byte channel)
        {
            if (reader.AvailableBytes == 0)
            {
                BasisDebug.LogError($"Missing Data from peer! {peer.Id} with channel ID {channel}");
                return false;
            }
            return true;
        }
        public void AuthIdentityMessage(NetPeer peer, NetPacketReader Reader, byte channel)
        {
            BasisDebug.Log("Auth is being requested by server!");
            if (ValidateSize(Reader, peer, channel) == false)
            {
                BasisDebug.Log("Auth Failed");
                Reader.Recycle();
                return;
            }
            BasisDebug.Log("Validated Size " + Reader.AvailableBytes);
            if (BasisDIDAuthIdentityClient.IdentityMessage(peer, Reader, out NetDataWriter Writer))
            {
                BasisDebug.Log("Sent Identity To Server!");
                LocalPlayerPeer.Send(Writer, BasisNetworkCommons.AuthIdentityMessage, DeliveryMethod.ReliableSequenced);
                Reader.Recycle();
            }
            else
            {
                BasisDebug.LogError("Failed Identity Message!");
                Reader.Recycle();
                DisconnectInfo info = new DisconnectInfo
                {
                    Reason = DisconnectReason.ConnectionRejected,
                    SocketErrorCode = System.Net.Sockets.SocketError.AccessDenied,
                    AdditionalData = null
                };
                PeerDisconnectedEvent(peer, info);
            }
            BasisDebug.Log("Completed");
        }
        private async void NetworkReceiveEvent(NetPeer peer, NetPacketReader Reader, byte channel, LiteNetLib.DeliveryMethod deliveryMethod)
        {
            switch (channel)
            {
                case BasisNetworkCommons.FallChannel:
                    if (deliveryMethod == DeliveryMethod.Unreliable)
                    {
                        if (Reader.TryGetByte(out byte Byte))
                        {
                            NetworkReceiveEvent(peer, Reader, Byte, deliveryMethod);
                        }
                        else
                        {
                            BNL.LogError($"Unknown channel no data remains: {channel} " + Reader.AvailableBytes);
                            Reader.Recycle();
                        }
                    }
                    else
                    {
                        BNL.LogError($"Unknown channel: {channel} " + Reader.AvailableBytes);
                        Reader.Recycle();
                    }
                    break;
                case BasisNetworkCommons.AuthIdentityMessage:
                    AuthIdentityMessage(peer, Reader, channel);
                    break;
                case BasisNetworkCommons.Disconnection:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkHandleRemoval.HandleDisconnection(Reader);
                    Reader.Recycle();
                    break;
                case BasisNetworkCommons.AvatarChangeMessage:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkHandleAvatar.HandleAvatarChangeMessage(Reader);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.CreateRemotePlayer:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(async _ =>
                    {
                        await BasisRemotePlayerFactory.HandleCreateRemotePlayer(Reader, instantiationParameters);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.CreateRemotePlayersForNewPeer:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    //same as remote player but just used at the start
                    BasisNetworkManagement.MainThreadContext.Post(async _ =>
                    {
                        //this one is called first and is also generally where the issues are.
                        await BasisRemotePlayerFactory.HandleCreateRemotePlayer(Reader, instantiationParameters);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.GetCurrentOwnerRequest:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkGenericMessages.HandleOwnershipResponse(Reader);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.ChangeCurrentOwnerRequest:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkGenericMessages.HandleOwnershipTransfer(Reader);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.RemoveCurrentOwnerRequest:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkGenericMessages.HandleOwnershipRemove(Reader);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.VoiceChannel:
                    await BasisNetworkHandleVoice.HandleAudioUpdate(Reader);
                    Reader.Recycle();
                    break;
                case BasisNetworkCommons.MovementChannel:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkHandleAvatar.HandleAvatarUpdate(Reader);
                    Reader.Recycle();
                    break;
                case BasisNetworkCommons.SceneChannel:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkGenericMessages.HandleServerSceneDataMessage(Reader, deliveryMethod);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.AvatarChannel:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkGenericMessages.HandleServerAvatarDataMessage(Reader, deliveryMethod);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.NetIDAssigns:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkGenericMessages.MassNetIDAssign(Reader, deliveryMethod);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.netIDAssign:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkGenericMessages.NetIDAssign(Reader, deliveryMethod);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.LoadResourceMessage:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(async _ =>
                    {
                        await BasisNetworkGenericMessages.LoadResourceMessage(Reader, deliveryMethod);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.UnloadResourceMessage:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkGenericMessages.UnloadResourceMessage(Reader, deliveryMethod);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.AdminMessage:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    BasisNetworkManagement.MainThreadContext.Post(_ =>
                    {
                        BasisNetworkModeration.AdminMessage(Reader);
                        Reader.Recycle();
                    }, null);
                    break;
                case BasisNetworkCommons.metaDataMessage:
                    if (ValidateSize(Reader, peer, channel) == false)
                    {
                        Reader.Recycle();
                        return;
                    }
                    ServerMetaDataMessage ServerMetaDataMessage = new ServerMetaDataMessage();
                    ServerMetaDataMessage.Deserialize(Reader);
                    Reader.Recycle();

                    BasisLocalPlayer.Instance.UUID = ServerMetaDataMessage.ClientMetaDataMessage.playerUUID;
                    BasisLocalPlayer.Instance.DisplayName = ServerMetaDataMessage.ClientMetaDataMessage.playerDisplayName;
                    BasisNetworkManagement.ServerMetaDataMessage = ServerMetaDataMessage;

                    break;
                default:
                    BNL.LogError($"this Channel was not been implemented {channel}");
                    Reader.Recycle();
                    break;
            }
        }
        public static async Task<(bool, ushort)> RemoveOwnershipAsync(string UniqueNetworkId, int timeoutMs = 5000)
        {
            var tcs = new TaskCompletionSource<(bool, ushort)>();
            using var cancellationTokenSource = new CancellationTokenSource();

            void OnOwnershipTransferred(string ownershipID, ushort playerID, bool isLocalOwner)
            {
                if (ownershipID == UniqueNetworkId)
                {
                    BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                    cancellationTokenSource.Cancel(); // Stop timeout countdown
                    tcs.TrySetResult((true, playerID));
                }
            }

            cancellationTokenSource.Token.Register(() =>
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                tcs.TrySetResult((false, 0));
            });

            BasisNetworkPlayer.OnOwnershipTransfer += OnOwnershipTransferred;

            var ownershipTransferMessage = new OwnershipTransferMessage
            {
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)BasisNetworkManagement.LocalPlayerPeer.RemoteId,
                },
                ownershipID = UniqueNetworkId
            };

            var netDataWriter = new NetDataWriter();
            ownershipTransferMessage.Serialize(netDataWriter);

            var peer = BasisNetworkManagement.LocalPlayerPeer;
            if (peer != null)
            {
                peer.Send(netDataWriter, BasisNetworkCommons.RemoveCurrentOwnerRequest, DeliveryMethod.ReliableSequenced);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.OwnershipTransfer, netDataWriter.Length);
            }
            else
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                return (false, 0);
            }

            cancellationTokenSource.CancelAfter(timeoutMs);
            return await tcs.Task;
        }
        public static async Task<(bool, ushort)> TakeOwnershipAsync(string UniqueNetworkId, ushort NewOwner, int timeoutMs = 5000)
        {
            var tcs = new TaskCompletionSource<(bool, ushort)>();
            using var cancellationTokenSource = new CancellationTokenSource();

            void OnOwnershipTransferred(string ownershipID, ushort playerID, bool isLocalOwner)
            {
                if (ownershipID == UniqueNetworkId && playerID == NewOwner)
                {
                    BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                    cancellationTokenSource.Cancel(); // Stop timeout countdown
                    tcs.TrySetResult((true, playerID));
                }
            }

            cancellationTokenSource.Token.Register(() =>
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                tcs.TrySetResult((false, 0));
            });

            BasisNetworkPlayer.OnOwnershipTransfer += OnOwnershipTransferred;

            var ownershipTransferMessage = new OwnershipTransferMessage
            {
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = NewOwner
                },
                ownershipID = UniqueNetworkId
            };

            var netDataWriter = new NetDataWriter();
            ownershipTransferMessage.Serialize(netDataWriter);

            var peer = BasisNetworkManagement.LocalPlayerPeer;
            if (peer != null)
            {
                peer.Send(netDataWriter, BasisNetworkCommons.ChangeCurrentOwnerRequest, DeliveryMethod.ReliableSequenced);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.OwnershipTransfer, netDataWriter.Length);
            }
            else
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                return (false, 0);
            }

            cancellationTokenSource.CancelAfter(timeoutMs);
            return await tcs.Task;
        }
        public static async Task<(bool, ushort)> RequestCurrentOwnershipAsync(string UniqueNetworkId, int timeoutMs = 5000)
        {
            if (OwnershipPairing.TryGetValue(UniqueNetworkId, out ushort Unique))
            {
                return (true, Unique);
            }

            var tcs = new TaskCompletionSource<(bool, ushort)>();
            using var cancellationTokenSource = new CancellationTokenSource();

            void OnOwnershipTransferred(string ownershipID, ushort playerID, bool isLocalOwner)
            {
                if (ownershipID == UniqueNetworkId)
                {
                    BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                    cancellationTokenSource.Cancel(); // Stop timeout countdown
                    tcs.TrySetResult((true, playerID));
                }
            }

            cancellationTokenSource.Token.Register(() =>
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                tcs.TrySetResult((false, 0));
            });

            BasisNetworkPlayer.OnOwnershipTransfer += OnOwnershipTransferred;

            var ownershipTransferMessage = new OwnershipTransferMessage
            {
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)BasisNetworkManagement.LocalPlayerPeer.RemoteId,
                },
                ownershipID = UniqueNetworkId
            };

            var netDataWriter = new NetDataWriter();
            ownershipTransferMessage.Serialize(netDataWriter);

            // Ensure the peer is valid before sending
            var peer = BasisNetworkManagement.LocalPlayerPeer;
            if (peer != null)
            {
                peer.Send(netDataWriter, BasisNetworkCommons.GetCurrentOwnerRequest, DeliveryMethod.ReliableSequenced);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.RequestOwnershipTransfer, netDataWriter.Length);
            }
            else
            {
                BasisNetworkPlayer.OnOwnershipTransfer -= OnOwnershipTransferred;
                return (false, 0);
            }

            cancellationTokenSource.CancelAfter(timeoutMs);

            return await tcs.Task;
        }

        public static bool AvatarToPlayer(BasisAvatar Avatar, out BasisPlayer BasisPlayer, out BasisNetworkPlayer NetworkedPlayer)
        {
            if (Avatar == null)
            {
                BasisDebug.LogError("Missing Avatar! Make sure your not sending in a null item");
                NetworkedPlayer = null;
                BasisPlayer = null;
                return false;
            }
            if (Avatar.TryGetLinkedPlayer(out ushort id))
            {
                BasisNetworkPlayer output = Players[id];
                NetworkedPlayer = output;
                BasisPlayer = output.Player;
                return true;
            }
            else
            {
                BasisDebug.LogError("the player was not assigned at this time!");
            }
            NetworkedPlayer = null;
            BasisPlayer = null;
            return false;
        }
        /// <summary>
        /// on the remote player this will only work...
        /// </summary>
        /// <param name="Avatar"></param>
        /// <param name="BasisPlayer"></param>
        /// <returns></returns>
        public static bool AvatarToPlayer(BasisAvatar Avatar, out BasisPlayer BasisPlayer)
        {
            if (Avatar == null)
            {
                BasisDebug.LogError("Missing Avatar! Make sure your not sending in a null item");
                BasisPlayer = null;
                return false;
            }
            if (Avatar.TryGetLinkedPlayer(out ushort id))
            {
                if (GetPlayerById(id, out BasisNetworkPlayer player))
                {
                    BasisNetworkPlayer output = Players[id];
                    BasisPlayer = output.Player;
                    return true;
                }
                else
                {
                    if (JoiningPlayers.Contains(id))
                    {
                        BasisDebug.LogError("Player was still Connecting when this was called!");
                    }
                    else
                    {
                        BasisDebug.LogError("Player was not found, this also includes joining list, something is very wrong!");
                    }
                }
            }
            else
            {
                BasisDebug.LogError("the player was not assigned at this time!");
            }
            BasisPlayer = null;
            return false;
        }
        public static bool PlayerToNetworkedPlayer(BasisPlayer BasisPlayer, out BasisNetworkPlayer NetworkedPlayer)
        {
            if (BasisPlayer == null)
            {
                BasisDebug.LogError("Missing Player! make sure your not sending in a null item");
                NetworkedPlayer = null;
                return false;
            }
            int BasisPlayerInstance = BasisPlayer.GetInstanceID();
            foreach (BasisNetworkPlayer NPlayer in Players.Values)
            {
                if (NPlayer == null)
                {
                    continue;
                }
                if (NPlayer.Player == null)
                {
                    continue;
                }
                if (NPlayer.Player.GetInstanceID() == BasisPlayerInstance)
                {
                    NetworkedPlayer = NPlayer;
                    return true;
                }
            }
            NetworkedPlayer = null;
            return false;
        }
        public static bool GetPlayerById(ushort PlayerId, out BasisNetworkPlayer Player)
        {
            return Players.TryGetValue(PlayerId, out Player);
        }
        public static bool IsMainThread()
        {
            // Check if the current synchronization context matches the main thread's context
            return SynchronizationContext.Current == BasisNetworkManagement.MainThreadContext;
        }
        public static long RemoteTimeDelta()
        {
            return BasisNetworkManagement.LocalPlayerPeer.RemoteTimeDelta;
        }
        public static DateTime RemoteUtcTime()
        {
            return BasisNetworkManagement.LocalPlayerPeer.RemoteUtcTime;
        }
        public static float TimeSinceLastPacket()
        {
            return BasisNetworkManagement.LocalPlayerPeer.TimeSinceLastPacket;
        }
        public static NetStatistics Statistics()
        {
            return BasisNetworkManagement.LocalPlayerPeer.Statistics;
        }
        public static int DateTimeToSeconds(DateTime dateTime)
        {
            TimeSpan timeSpan = dateTime - DateTime.Now;

            // Convert to seconds and cast to int
            return (int)timeSpan.TotalSeconds;
        }

        public static int GetServerTimeInMilliseconds()
        {
            DateTime ServerTime = BasisNetworkManagement.RemoteUtcTime();
            int SecondsAhead = BasisNetworkManagement.DateTimeToSeconds(ServerTime);
            return SecondsAhead;
        }
        /// <summary>
        /// this message goes to the server and no where else, requires logic on the server todo things with it.
        /// </summary>
        public static void SendServerSideMessage(byte[] netDataWriter, DeliveryMethod Mode)
        {
            if (LocalPlayerPeer != null)
            {
                LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.ServerBoundMessage, Mode);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.SceneData, netDataWriter.Length);
            }
            else
            {
                BasisDebug.LogError("Local NetPeer was null!", BasisDebug.LogTag.Networking);
            }
        }
    }
}
