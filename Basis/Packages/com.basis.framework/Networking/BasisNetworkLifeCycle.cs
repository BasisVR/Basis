using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.UI;
using Basis.Network.Core;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using static SerializableBasis;
public static class BasisNetworkLifeCycle
{
    /// <summary>
    /// boots up the network management
    /// </summary>
    public static void Initialize()
    {
        BasisDebug.Log($"Initializing Network Connection", BasisDebug.LogTag.Networking);
        BasisNetworkManagement.mainThreadId = Thread.CurrentThread.ManagedThreadId;
        // Invalidate anything the previous connection left mid-decode, then start (or re-arm) the
        // join decode thread. Runs here so the main-thread-only statics it touches are warmed from
        // the main thread rather than from the worker.
        BasisAvatarLoadThread.Flush();
        BasisAvatarLoadThread.Initialize();
        BasisRemoteNetworkDriver.Initialize(Unity.Collections.Allocator.Persistent);
        BasisAudioRemoteSource.Initialize();
        BasisNetworkIdResolver.KnownIdMap.Clear();
        BasisNetworkIdResolver.PendingResolutions.Clear();
        // Remote players spawn as scene roots (null parent) so each avatar's bone hierarchy has its
        // own Transform.root. IJobParallelForTransform batches by root, so a shared DeviceManagement
        // parent forced every remote avatar's bones onto a single worker thread; distinct roots let the
        // bone writes spread across all workers. DontDestroyOnLoad in CreateRemotePlayer keeps them alive
        // across additive scene switches the way the persistent DeviceManagement parent used to.
        BasisNetworkManagement.instantiationParameters = new InstantiationParameters(Vector3.zero, Quaternion.identity, null);
        // Reset & initialize metadata defaults
        BasisNetworkPlayers.ClearAllRegistries(); // new: central place
        BasisNetworkManagement.ServerMetaDataMessage = new ServerMetaDataMessage
        {
            ClientMetaDataMessage = new ClientMetaDataMessage(),
            SyncInterval = 50,
            BaseMultiplier = 1,
            IncreaseRate = 0.005f,
            SlowestSendRate = 2.5f,
            PeerLimit = 0
        };

        BasisJoinLeaveNotification.Create();
        BasisNetworkHandleTempBlock.Initialize();
        BasisNetworkHandleChatTyping.Initialize();
        Basis.Scripts.BasisSdk.Interactions.BasisJiggleGrabDriver.Initialize();
#if !UNITY_SERVER
        BasisNetworkPIPCameraDriver.Create();
#endif
        BasisNetworkManagement.IsInitialized = true;
        BasisNetworkManagement.OnEnableInstanceCreate?.Invoke();
        BasisNetworkManagement.OnIstanceCreated?.Invoke();
        BasisNetworkManagement.NetworkRunning = true;
    }
    private static int _rebootGuard = 0;
    /// <summary>
    /// allows us to reset before continuing on the operation.
    /// </summary>
    public static async Task RebootManagement(bool DisplayReason, NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _rebootGuard, 1, 0) == 0)
        {
            BasisDebug.Log($"Rebooting Network Connection", BasisDebug.LogTag.Networking);
            if (BasisNetworkConnection.LocalPlayerPeer != null && BasisNetworkPlayers.Players.TryGetValue((ushort)BasisNetworkConnection.LocalPlayerPeer.RemoteId, out var networkedPlayer))
            {
                if (networkedPlayer?.Player is BasisLocalPlayer local)
                {
                    BasisNetworkPlayer.OnLocalPlayerLeft?.Invoke(networkedPlayer, local);
                }
                BasisNetworkPlayer.OnPlayerLeft?.Invoke(networkedPlayer);
            }
            BasisNetworkManagement.Transmitter?.DeInitialize();
            if (BasisNetworkConnection.BasisNetworkServerRunner != null)
            {
                BasisNetworkConnection.BasisNetworkServerRunner.Stop();
                BasisNetworkConnection.BasisNetworkServerRunner = null;
            }

            BasisRemoteNetworkDriver.Apply();//complete in-flight jobs before clearing players
#if !UNITY_SERVER
            BasisNetworkPIPCameraDriver.ClearRemotePIPs();
#endif
            BasisAvatarLoadThread.Flush();//drop join packets decoded for the connection going away
            BasisNetworkPlayers.ClearAllRegistries();//remove players
            Basis.Scripts.Networking.Receivers.BasisAnnounceAudioDriver.DeInitialize();//remove announce audio sources
            Basis.Scripts.Networking.VoiceRecording.BasisVoiceRecording.DeInitialize();//remove voice recordings
            await BasisNetworkSpawnItem.Reset();//remove items
            BasisNetworkPreloadManager.Reset();//remove preloaded resources
            BasisContentShareManager.Reset();//remove content spheres
            BasisNetworkIdResolver.KnownIdMap.Clear();
            BasisNetworkIdResolver.PendingResolutions.Clear();
            BasisNetworkGenericMessages.ReleaseConnectionRegistrations();//message indices belong to the connection that assigned them
            BasisNetworkManagement.Transmitter = null;
            BasisNetworkConnection.NetworkClient?.Disconnect();//disconnect the local client last.
            BasisNetworkConnection.LocalPlayerIsConnected = false;
            Basis.Scripts.Avatar.BasisLocalAvatarNetworkNotice.Reset();//the next server warns for itself
            BasisNetworkManagement.LocalAccessTransmitter = null;
            BasisNetworkConnection.LocalPlayerPeer = null;
            if (DisplayReason)
            {
                BasisDebug.Log($"Client disconnected from server [{peer?.RemoteId}] [{disconnectInfo.Reason}]");
                BasisNetworkEvents.HandleDisconnectionReason(disconnectInfo);
            }
            BasisNetworkHandleChatTyping.ClearState();
            System.Threading.Interlocked.Exchange(ref _rebootGuard, 0);
        }
    }
    /// <summary>
    /// destroys all data related to network management
    /// </summary>
    public static async Task Destroy()
    {
        BasisDebug.Log($"Shutting Down Network Connection", BasisDebug.LogTag.Networking);
        BasisNetworkConnectionWatchdog.Reset();
        if (BasisNetworkConnection.LocalPlayerPeer != null && BasisNetworkPlayers.Players.TryGetValue((ushort)BasisNetworkConnection.LocalPlayerPeer.RemoteId, out var networkedPlayer))
        {
            if (networkedPlayer?.Player is BasisLocalPlayer local)
            {
                BasisNetworkPlayer.OnLocalPlayerLeft?.Invoke(networkedPlayer, local);
            }
            BasisNetworkPlayer.OnPlayerLeft?.Invoke(networkedPlayer);
        }
        // Reset instance-scoped configuration to safe defaults
        BasisNetworkManagement.Transmitter?.DeInitialize();

        if (BasisNetworkConnection.BasisNetworkServerRunner != null)
        {
            BasisNetworkConnection.BasisNetworkServerRunner.Stop();
            BasisNetworkConnection.BasisNetworkServerRunner = null;
        }
        BasisNetworkManagement.JoinPendingCompute();//join the pipelined compute task before buffers are freed
        BasisRemoteNetworkDriver.Shutdown();//complete in-flight jobs before disposing anything
        BasisAvatarLoadThread.Flush();//drop join packets decoded for the connection going away
        BasisNetworkPlayers.ClearAllRegistries();//remove players
        Basis.Scripts.Networking.Receivers.BasisAnnounceAudioDriver.DeInitialize();//remove announce audio sources
        Basis.Scripts.Networking.VoiceRecording.BasisVoiceRecording.DeInitialize();//remove voice recordings
        await BasisNetworkSpawnItem.Reset();//remove items
        BasisNetworkPreloadManager.Reset();//remove preloaded resources
        BasisContentShareManager.Reset();//remove content spheres
        BasisNetworkIdResolver.KnownIdMap.Clear();
        BasisNetworkIdResolver.PendingResolutions.Clear();
        BasisNetworkGenericMessages.ReleaseConnectionRegistrations();//message indices belong to the connection that assigned them
        BasisAudioRemoteSource.DeInitialize();//release memory for audio gameobject
        BasisNetworkManagement.Transmitter = null;
        // The player lifecycle delegates are owned by whoever subscribed, not by the connection, and they are
        // how a service learns the next connection has arrived. Clearing them here unhooked every subscriber
        // that arms once - a RuntimeInitializeOnLoadMethod or a one-shot Initialize - so from the second server
        // onwards nothing re-subscribed and those services were simply deaf: OnLocalPlayerJoined never reached
        // BasisImagePickupManager or BasisCameraDollyManager again, and OnRemotePlayerJoined/Left never reached
        // BasisTalkModeManager, BasisBodyFitNetworking or BasisRTAOIntegration. Nulling OnOwnershipTransfer also
        // stranded any ownership request awaiting a reply, which could then only end in its timeout. Every
        // subscriber in the tree unsubscribes from its own teardown, so there is nothing to clean up here.
        // OnEnableInstanceCreate is deliberately still cleared: it is a one-shot "the network layer exists"
        // arm whose subscribers connect on it, and Initialize raises it, so leaving it armed across a server
        // switch would fire a second connect underneath the one already being made.
        BasisNetworkManagement.OnEnableInstanceCreate = null;
        BasisNetworkConnection.LocalPlayerPeer = null;
        BasisNetworkManagement.LocalAccessTransmitter = null;
        BasisNetworkConnection.LocalPlayerIsConnected = false;
        Basis.Scripts.Avatar.BasisLocalAvatarNetworkNotice.Reset();//the next server warns for itself
        BasisNetworkManagement.NetworkRunning = false;
        BasisNetworkManagement.IsInitialized = false;
        BasisDebug.Log("BasisNetworkManagement has been successfully shutdown.", BasisDebug.LogTag.Networking);
        BasisJoinLeaveNotification.Shutdown();
        BasisNetworkHandleTempBlock.Shutdown();
        BasisNetworkHandleChatTyping.Shutdown();
        Basis.Scripts.BasisSdk.Interactions.BasisJiggleGrabDriver.Shutdown();
#if !UNITY_SERVER
        BasisNetworkPIPCameraDriver.Shutdown();
#endif
        BasisNetworkConnection.NetworkClient?.Disconnect();
    }
}
