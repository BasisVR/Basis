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
    public static void Initalize(BasisNetworkManagement Management)
    {
        BasisDebug.Log($"Initalizing Network Connection", BasisDebug.LogTag.Networking);
        BasisNetworkManagement.mainThreadId = Thread.CurrentThread.ManagedThreadId;
        BasisRemoteNetworkDriver.Initialize(95, Unity.Collections.Allocator.Persistent);
        BasisAudioRemoteSource.Initalize();
        BasisNetworkIdResolver.KnownIdMap.Clear();
        BasisNetworkIdResolver.PendingResolutions.Clear();
        BasisNetworkManagement.instantiationParameters = new InstantiationParameters(Vector3.zero, Quaternion.identity, BasisDeviceManagement.Instance.transform);
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

        Management.transform.SetParent(BasisDeviceManagement.Instance.transform, false);

        Management.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        BasisJoinLeaveNotification.Create();
        BasisNetworkPIPCameraDriver.Create();
        BasisNetworkManagement.OnEnableInstanceCreate?.Invoke();
        BasisNetworkManagement.NetworkRunning = true;
    }
    private static int _rebootGuard = 0;
    /// <summary>
    /// allows us to reset before continuing on the operation.
    /// </summary>
    public static async Task RebootManagement(BasisNetworkManagement Management, bool DisplayReason, NetPeer peer, DisconnectInfo disconnectInfo)
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
            BasisObjectSyncDriver.Reset();//stop owned pickup sync before scene/object teardown
            BasisNetworkPlayers.ClearAllRegistries();//remove players
            Basis.Scripts.Networking.Receivers.BasisShoutAudioDriver.DeInitialize();//remove shout audio sources
            await BasisNetworkSpawnItem.Reset();//remove items
            BasisNetworkPreloadManager.Reset();//remove preloaded resources
            BasisContentShareManager.Reset();//remove content spheres
            BasisNetworkIdResolver.KnownIdMap.Clear();
            BasisNetworkIdResolver.PendingResolutions.Clear();
            BasisNetworkManagement.Transmitter = null;
            BasisNetworkConnection.NetworkClient?.Disconnect();//disconnect the local client last.
            BasisNetworkConnection.LocalPlayerIsConnected = false;
            Management.LocalAccessTransmitter = null;
            BasisNetworkConnection.LocalPlayerPeer = null;
            BasisNetworkManagement.OnRequestServerSideDatabaseItem = null;
            if (DisplayReason)
            {
                BasisDebug.Log($"Client disconnected from server [{peer?.RemoteId}] [{disconnectInfo.Reason}]");
                BasisNetworkEvents.HandleDisconnectionReason(disconnectInfo);
            }
            System.Threading.Interlocked.Exchange(ref _rebootGuard, 0);
        }
    }
    /// <summary>
    /// destroys all data related to network management
    /// </summary>
    public static async Task Destroy(BasisNetworkManagement Management)
    {
        BasisDebug.Log($"Shutting Down Network Connection", BasisDebug.LogTag.Networking);
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
        BasisRemoteNetworkDriver.Shutdown();//complete in-flight jobs before disposing anything
        BasisObjectSyncDriver.Reset();//stop owned pickup sync before scene/object teardown
        BasisNetworkPlayers.ClearAllRegistries();//remove players
        Basis.Scripts.Networking.Receivers.BasisShoutAudioDriver.DeInitialize();//remove shout audio sources
        await BasisNetworkSpawnItem.Reset();//remove items
        BasisNetworkPreloadManager.Reset();//remove preloaded resources
        BasisContentShareManager.Reset();//remove content spheres
        BasisNetworkIdResolver.KnownIdMap.Clear();
        BasisNetworkIdResolver.PendingResolutions.Clear();
        BasisAudioRemoteSource.DeInitalize();//release memory for audio gameobject
        BasisNetworkManagement.Transmitter = null;
        // Clear delegates / events
        BasisNetworkPlayer.OnOwnershipTransfer = null;
        BasisNetworkPlayer.OnLocalPlayerJoined = null;
        BasisNetworkPlayer.OnRemotePlayerJoined = null;
        BasisNetworkPlayer.OnLocalPlayerLeft = null;
        BasisNetworkPlayer.OnRemotePlayerLeft = null;
        BasisNetworkManagement.OnEnableInstanceCreate = null;
        BasisNetworkConnection.LocalPlayerPeer = null;
        BasisNetworkManagement.OnRequestServerSideDatabaseItem = null;
        Management.LocalAccessTransmitter = null;
        BasisNetworkConnection.LocalPlayerIsConnected = false;
        BasisNetworkManagement.NetworkRunning = false;
        // let the MonoBehaviour reset its Instance in OnDestroy; no direct assignment here
        BasisDebug.Log("BasisNetworkManagement has been successfully shutdown.", BasisDebug.LogTag.Networking);
        BasisJoinLeaveNotification.Shutdown();
        BasisNetworkPIPCameraDriver.Shutdown();
        BasisNetworkConnection.NetworkClient?.Disconnect();
    }
}
