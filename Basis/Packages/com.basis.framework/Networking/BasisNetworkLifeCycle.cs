using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
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
            SlowestSendRate = 2.5f
        };

        Management.transform.SetParent(BasisDeviceManagement.Instance.transform, false);

        Management.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        BasisNetworkManagement.OnEnableInstanceCreate?.Invoke();
        BasisNetworkManagement.NetworkRunning = true;
    }
    public static bool GoingThroughReboot = false;
    /// <summary>
    /// allows us to reset before continuing on the operation.
    /// </summary>
    public static async Task RebootManagement(BasisNetworkManagement Management, bool DisplayReason, NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (GoingThroughReboot == false)
        {
            GoingThroughReboot = true;
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

            BasisNetworkPlayers.ClearAllRegistries();//remove players
            await BasisNetworkSpawnItem.Reset();//remove items
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
            GoingThroughReboot = false;
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
        BasisNetworkPlayers.ClearAllRegistries();//remove players
        await BasisNetworkSpawnItem.Reset();//remove items
        BasisNetworkIdResolver.KnownIdMap.Clear();
        BasisNetworkIdResolver.PendingResolutions.Clear();
        BasisAudioRemoteSource.DeInitalize();//release memory for audio gameobject
        BasisRemoteNetworkDriver.Shutdown();
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
        BasisNetworkConnection.NetworkClient?.Disconnect();
    }
}
