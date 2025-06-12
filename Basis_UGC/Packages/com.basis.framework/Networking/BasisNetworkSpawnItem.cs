using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Assertions.Must;
using UnityEngine.SceneManagement;
using static BundledContentHolder;
using static SerializableBasis;

public static class BasisNetworkSpawnItem
{
    public static bool RequestSceneLoad(string UnlockPassword, string CombinedURL, bool Persist, out LocalLoadResource localLoadResource)
    {
        if (string.IsNullOrEmpty(CombinedURL) || string.IsNullOrEmpty(UnlockPassword))
        {
            BasisDebug.Log("Invalid parameters for scene load request.", BasisDebug.LogTag.Networking);
            localLoadResource = new LocalLoadResource();
            return false;
        }

        BasisDebug.Log("Requesting scene load...", BasisDebug.LogTag.Networking);

        localLoadResource = new LocalLoadResource
        {
            LoadedNetID = BasisGenerateUniqueID.GenerateUniqueID(),
            Mode = 1,
            CombinedURL = CombinedURL,
            UnlockPassword = UnlockPassword,
            Persist = Persist,
        };

        LiteNetLib.Utils.NetDataWriter writer = new LiteNetLib.Utils.NetDataWriter();
        localLoadResource.Serialize(writer);

        BasisDebug.Log($"Sending scene load request with NetID: {localLoadResource.LoadedNetID}", BasisDebug.LogTag.Networking);

        BasisNetworkManagement.LocalPlayerPeer?.Send(writer, BasisNetworkCommons.LoadResourceMessage, LiteNetLib.DeliveryMethod.ReliableOrdered);
        return true;
    }

    public static bool RequestGameObjectLoad(string UnlockPassword, string CombinedURL, Vector3 Position, Quaternion Rotation, Vector3 Scale, bool Persistent, bool ModifysScale, out LocalLoadResource LocalLoadResource)
    {
        if (string.IsNullOrEmpty(CombinedURL) || string.IsNullOrEmpty(UnlockPassword))
        {
            BasisDebug.Log("Invalid parameters for GameObject load request.", BasisDebug.LogTag.Networking);
            LocalLoadResource = new LocalLoadResource();
            return false;
        }

        BasisDebug.Log("Requesting GameObject load...", BasisDebug.LogTag.Networking);

        LocalLoadResource = new LocalLoadResource
        {
            LoadedNetID = BasisGenerateUniqueID.GenerateUniqueID(),
            Mode = 0,
            CombinedURL = CombinedURL,
            UnlockPassword = UnlockPassword,
            PositionX = Position.x,
            PositionY = Position.y,
            PositionZ = Position.z,
            QuaternionW = Rotation.w,
            QuaternionX = Rotation.x,
            QuaternionY = Rotation.y,
            QuaternionZ = Rotation.z,
            ScaleX = Scale.x,
            ScaleY = Scale.y,
            ScaleZ = Scale.z,
            Persist = Persistent,
            ModifyScale = ModifysScale,
        };

        LiteNetLib.Utils.NetDataWriter writer = new LiteNetLib.Utils.NetDataWriter();
        LocalLoadResource.Serialize(writer);

        BasisDebug.Log($"Sending GameObject load request with NetID: {LocalLoadResource.LoadedNetID}", BasisDebug.LogTag.Networking);

        BasisNetworkManagement.LocalPlayerPeer?.Send(writer, BasisNetworkCommons.LoadResourceMessage, LiteNetLib.DeliveryMethod.ReliableOrdered);
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

        LiteNetLib.Utils.NetDataWriter writer = new LiteNetLib.Utils.NetDataWriter();
        UnLoadResource.Serialize(writer);

        BasisDebug.Log($"Sending unload request with NetID: {UnLoadResource.LoadedNetID}", BasisDebug.LogTag.Networking);

        BasisNetworkManagement.LocalPlayerPeer?.Send(writer, BasisNetworkCommons.UnloadResourceMessage, LiteNetLib.DeliveryMethod.ReliableOrdered);
    }

    public static async Task<Scene> SpawnScene(LocalLoadResource localLoadResource)
    {
        BasisDebug.Log($"Spawning scene with NetID: {localLoadResource.LoadedNetID}", BasisDebug.LogTag.Networking);

        BasisLoadableBundle loadBundle = new BasisLoadableBundle
        {
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle()
            {
                RemoteBeeFileLocation = localLoadResource.CombinedURL
            },
            UnlockPassword = localLoadResource.UnlockPassword,
        };

        Scene scene = await BasisSceneLoadDriver.LoadSceneAssetBundle(loadBundle);
        BasisDebug.Log($"LoadSceneAssetBundle Complete now Starting Scene Traversal", BasisDebug.LogTag.Networking);
        SceneTraverseNetIdAssign(scene, localLoadResource);
        SpawnedScenes.TryAdd(localLoadResource.LoadedNetID, scene);
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
                BasisScene.NetworkID = localLoadResource.LoadedNetID;
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
        GameObject reference = await BasisLoadHandler.LoadGameObjectBundle(loadBundle, true, BasisProgressReport, new CancellationToken(),
            new Vector3(localLoadResource.PositionX, localLoadResource.PositionY, localLoadResource.PositionZ),
            new Quaternion(localLoadResource.QuaternionX, localLoadResource.QuaternionY, localLoadResource.QuaternionZ, localLoadResource.QuaternionW),
            new Vector3(localLoadResource.ScaleX, localLoadResource.ScaleY, localLoadResource.ScaleZ),
            localLoadResource.ModifyScale, Selector, BasisNetworkManagement.Instance.transform);

        if (reference.TryGetComponent<BasisContentBase>(out BasisContentBase BasisContentBase))
        {
            BasisContentBase.NetworkID = localLoadResource.LoadedNetID;
        }
        SpawnedGameobjects.TryAdd(localLoadResource.LoadedNetID, reference);
        BasisProgressReport.OnProgressReport -= BasisUILoadingBar.ProgressReport;
        return reference;
    }
    public static void DestroyScene(UnLoadResource resource)
    {
        if (string.IsNullOrEmpty(resource.LoadedNetID))
        {
            BasisDebug.Log("Invalid resource for destroying scene.", BasisDebug.LogTag.Networking);
            return;
        }

        if (SpawnedScenes.TryRemove(resource.LoadedNetID, out Scene value))
        {
            SceneManager.UnloadSceneAsync(value);
        }
    }

    public static void DestroyGameobject(UnLoadResource resource)
    {
        if (string.IsNullOrEmpty(resource.LoadedNetID))
        {
            BasisDebug.Log("Invalid resource for destroying GameObject.", BasisDebug.LogTag.Networking);
            return;
        }

        if (SpawnedGameobjects.TryRemove(resource.LoadedNetID, out GameObject value))
        {
            if (value != null)
                GameObject.Destroy(value);
        }
    }

    public static async Task Reset()
    {
        if (SpawnedScenes != null)
        {
            foreach (var reference in SpawnedScenes.Values)
            {
                if (reference != null && reference.IsValid())
                {
                    try
                    {
                        await SceneManager.UnloadSceneAsync(reference);
                    }
                    catch (Exception ex)
                    {
                        //bad dooly silent error
                         BasisDebug.Log($"Reset If Spawn Item {ex.Message}");
                    }
                }
            }
        }
        if (SpawnedGameobjects != null)
        {
            foreach (var reference in SpawnedGameobjects.Values)
            {
                if (reference != null)
                {
                    GameObject.Destroy(reference);
                }
            }
        }
        SpawnedGameobjects.Clear();
        SpawnedScenes.Clear();
        BasisDebug.Log("All spawned objects and scenes have been cleared.", BasisDebug.LogTag.Networking);
    }
    public static ConcurrentDictionary<string, GameObject> SpawnedGameobjects = new ConcurrentDictionary<string, GameObject>();
    public static ConcurrentDictionary<string, Scene> SpawnedScenes = new ConcurrentDictionary<string, Scene>();
}
