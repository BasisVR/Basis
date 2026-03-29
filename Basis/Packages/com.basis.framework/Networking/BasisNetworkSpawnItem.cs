using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using static BundledContentHolder;
using static SerializableBasis;
public static class BasisNetworkSpawnItem
{
    public static bool RequestSceneLoad(string UnlockPassword, string CombinedURL, bool Persist, bool Admin, out LocalLoadResource localLoadResource, byte loadStrategy = 0)
    {
        if (string.IsNullOrEmpty(CombinedURL) || string.IsNullOrEmpty(UnlockPassword))
        {
            BasisDebug.Log("Invalid parameters for scene load request.", BasisDebug.LogTag.Networking);
            localLoadResource = new LocalLoadResource();
            return false;
        }

        BasisDebug.Log($"Requesting scene load (strategy={loadStrategy})...", BasisDebug.LogTag.Networking);

        localLoadResource = new LocalLoadResource
        {
            LoadedNetID = BasisGenerateUniqueID.GenerateUniqueID(),
            Mode = 1,
            CombinedURL = CombinedURL,
            UnlockPassword = UnlockPassword,
            UUIDOfCreator = BasisLocalPlayer.Instance.UUID,
            IsAdminLocked = Admin,
            Persist = Persist,
            LoadStrategy = loadStrategy,
        };

        NetDataWriter writer = new NetDataWriter();
        localLoadResource.Serialize(writer);

        BasisDebug.Log($"Sending scene load request with NetID: {localLoadResource.LoadedNetID}", BasisDebug.LogTag.Networking);

        BasisNetworkConnection.LocalPlayerPeer?.Send(writer, BasisNetworkCommons.LoadResourceChannel, DeliveryMethod.ReliableOrdered);
        return true;
    }

    public static bool RequestGameObjectLoad(string UnlockPassword, string CombinedURL, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool Persistent, bool Admin, bool ModifysScale, out LocalLoadResource LocalLoadResource, byte loadStrategy = 0)
    {
        if (string.IsNullOrEmpty(CombinedURL) || string.IsNullOrEmpty(UnlockPassword))
        {
            BasisDebug.Log("Invalid parameters for GameObject load request.", BasisDebug.LogTag.Networking);
            LocalLoadResource = new LocalLoadResource();
            return false;
        }

        BasisDebug.Log($"Requesting GameObject load (strategy={loadStrategy})...", BasisDebug.LogTag.Networking);

        LocalLoadResource = new LocalLoadResource
        {
            LoadedNetID = BasisGenerateUniqueID.GenerateUniqueID(),
            Mode = 0,
            CombinedURL = CombinedURL,
            UnlockPassword = UnlockPassword,
            UUIDOfCreator = BasisLocalPlayer.Instance.UUID,
            IsAdminLocked = Admin,
            Persist = Persistent,
            ModifyScale = ModifysScale,
            LoadStrategy = loadStrategy,
            PositionX = Position.x,
            PositionY = Position.y,
            PositionZ = Position.z,
            QuaternionW = Rotation.w,
            QuaternionX = Rotation.x,
            QuaternionY = Rotation.y,
            QuaternionZ = Rotation.z,
            ScaleX = Scale.x,
            ScaleY = Scale.y,
            ScaleZ = Scale.z
        };

        NetDataWriter writer = new NetDataWriter();
        LocalLoadResource.Serialize(writer);

        BasisDebug.Log($"Sending GameObject load request with NetID: {LocalLoadResource.LoadedNetID}", BasisDebug.LogTag.Networking);

        BasisNetworkConnection.LocalPlayerPeer?.Send(writer, BasisNetworkCommons.LoadResourceChannel, DeliveryMethod.ReliableOrdered);
        return true;
    }

    public static void RequestGameObjectUnLoad(string LoadedNetID)
    {
        if (string.IsNullOrEmpty(LoadedNetID))
        {
            BasisDebug.Log("Invalid LoadedNetID for GameObject unload.", BasisDebug.LogTag.Networking);
            return;
        }

        UnLoadResource localLoadResource = new UnLoadResource { LoadedNetID = LoadedNetID, Mode = 0 };
        RequestUnload(localLoadResource);
    }

    public static void RequestSceneUnLoad(string LoadedNetID)
    {
        if (string.IsNullOrEmpty(LoadedNetID))
        {
            BasisDebug.Log("Invalid LoadedNetID for scene unload.", BasisDebug.LogTag.Networking);
            return;
        }

        BasisDebug.Log("Requesting scene unload...", BasisDebug.LogTag.Networking);

        UnLoadResource localLoadResource = new UnLoadResource { LoadedNetID = LoadedNetID, Mode = 1 };
        RequestUnload(localLoadResource);
    }

    public static void RequestUnload(UnLoadResource UnLoadResource)
    {
        if (string.IsNullOrEmpty(UnLoadResource.LoadedNetID))
        {
            BasisDebug.Log("Invalid unload request.", BasisDebug.LogTag.Networking);
            return;
        }

        NetDataWriter writer = new NetDataWriter();
        UnLoadResource.Serialize(writer);

        BasisDebug.Log($"Sending unload request with NetID: {UnLoadResource.LoadedNetID}", BasisDebug.LogTag.Networking);

        BasisNetworkConnection.LocalPlayerPeer?.Send(writer, BasisNetworkCommons.UnloadResourceChannel, DeliveryMethod.ReliableOrdered);
    }

    public static async Task<Scene> SpawnScene(LocalLoadResource localLoadResource)
    {
        BasisLoadableBundle loadBundle = new BasisLoadableBundle
        {
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle()
            {
                RemoteBeeFileLocation = localLoadResource.CombinedURL
            },
            UnlockPassword = localLoadResource.UnlockPassword,
        };

        Scene scene = await BasisSceneLoad.LoadSceneAssetBundle(loadBundle);
        BasisDebug.Log($"LoadSceneAssetBundle Complete now Starting Scene Traversal", BasisDebug.LogTag.Networking);
        SceneTraverseNetIdAssign(scene, localLoadResource);

        BasisRuntimeSpawnRegistry.AddScene(
            localLoadResource.CombinedURL, 
            localLoadResource.LoadedNetID,
            scene, 
            localLoadResource.UUIDOfCreator,
            localLoadResource.IsAdminLocked,
            localLoadResource.Persist, 
            BasisRuntimeSpawnRegistry.SpawnMethod.Network, 
            loadBundle.BasisBundleConnector, 
            out var created
        );
        BasisDebug.Log($"Scene Load From Server Complete ", BasisDebug.LogTag.Networking);
        return scene;
    }

    public static void SceneTraverseNetIdAssign(Scene scene, LocalLoadResource localLoadResource)
    {
        GameObject[] Root = scene.GetRootGameObjects();
        foreach (GameObject root in Root)
        {
            BasisScene BasisScene = root.GetComponentInChildren<BasisScene>();
            if (BasisScene != null)
            {
                BasisScene.AssignNetworkGUIDIdentifier(localLoadResource.LoadedNetID);
                return;
            }
        }
    }
    public static async Task<GameObject> SpawnGameObject(LocalLoadResource localLoadResource, Selector Selector)
    {
        BasisDebug.Log($"Spawning GameObject with NetID: {localLoadResource.LoadedNetID}", BasisDebug.LogTag.Networking);

        BasisLoadableBundle loadBundle = new BasisLoadableBundle
        {
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle()
            {
                RemoteBeeFileLocation = localLoadResource.CombinedURL
            },
            UnlockPassword = localLoadResource.UnlockPassword,

        };
        BasisProgressReport BasisProgressReport = new BasisProgressReport();
        BasisProgressReport.OnProgressReport += BasisUILoadingBar.ProgressReport;
        var position = new Vector3(localLoadResource.PositionX, localLoadResource.PositionY, localLoadResource.PositionZ);
        var rotation = new Quaternion(localLoadResource.QuaternionX, localLoadResource.QuaternionY, localLoadResource.QuaternionZ, localLoadResource.QuaternionW);
        var scale = new Vector3(localLoadResource.ScaleX, localLoadResource.ScaleY, localLoadResource.ScaleZ);
        GameObject reference = await BasisLoadHandler.LoadGameObjectBundle(BasisDeviceManagement.Instance.CreationGameobject, loadBundle, true, BasisProgressReport, new CancellationToken(),
            position,
            rotation,
            scale,
            localLoadResource.ModifyScale, Selector, BasisNetworkManagement.Instance.transform);

        if (reference == null)
        {
            BasisDebug.LogError($"Unable to load {loadBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation}", BasisDebug.LogTag.Networking);
            BasisProgressReport.OnProgressReport -= BasisUILoadingBar.ProgressReport;
            return null;
        }


        reference.name = localLoadResource.LoadedNetID;
        if (reference.TryGetComponent<BasisNetworkContentBase>(out BasisNetworkContentBase BasisContentBase))
        {
            BasisContentBase.AssignNetworkGUIDIdentifier(localLoadResource.LoadedNetID);
        }
        else
        {
            BasisDebug.LogWarning($"Gameobject Did not have a class deriving from {nameof(BasisNetworkContentBase)} on it!");
        }
        //BasisDebug.Log( $"SpawnGameObject -> was spawned does it have metadata? asset bundle name = {loadBundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName}" );
        BasisRuntimeSpawnRegistry.AddGameObject(
            localLoadResource.CombinedURL, 
            localLoadResource.LoadedNetID, 
            reference, 
            localLoadResource.UUIDOfCreator,
            localLoadResource.IsAdminLocked, 
            localLoadResource.Persist, 
            BasisRuntimeSpawnRegistry.SpawnMethod.Network, 
            loadBundle.BasisBundleConnector, 
            out var data
        );
        BasisProgressReport.OnProgressReport -= BasisUILoadingBar.ProgressReport;
        return reference;
    }
    public static async Task DestroyScene(UnLoadResource resource)
    {
        if (string.IsNullOrEmpty(resource.LoadedNetID))
        {
            BasisDebug.Log("Invalid resource for destroying scene.", BasisDebug.LogTag.Networking);
            return;
        }
      await  BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(resource.LoadedNetID);
    }

    public static async Task DestroyGameobject(UnLoadResource resource)
    {
        if (string.IsNullOrEmpty(resource.LoadedNetID))
        {
            BasisDebug.Log("Invalid resource for destroying GameObject.", BasisDebug.LogTag.Networking);
            return;
        }
      await  BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(resource.LoadedNetID);
    }

    public static async Task Reset()
    {
        await BasisRuntimeSpawnRegistry.ClearAllNetworking();
    }
}
