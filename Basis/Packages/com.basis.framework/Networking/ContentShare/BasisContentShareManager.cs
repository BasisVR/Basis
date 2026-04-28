using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Windows;
using static SerializableBasis;

/// <summary>
/// Client-side manager for content share spheres.
/// Handles sending/receiving content share messages and managing sphere GameObjects.
/// Handles sending/receiving content share messages and managing sphere GameObjects.
/// </summary>
public static class BasisContentShareManager
{
    /// <summary>
    /// All active content share spheres keyed by SphereNetID.
    /// </summary>
    public static ConcurrentDictionary<string, BasisContentSphere> ActiveSpheres = new ConcurrentDictionary<string, BasisContentSphere>();

    /// <summary>
    /// Fired when a new content sphere is created (for UI hooks).
    /// </summary>
    public static Action<BasisContentSphere> OnSphereCreated;

    /// <summary>
    /// Fired when a content sphere is removed.
    /// </summary>
    public static Action<string> OnSphereRemoved;
    public static string AvatarOrb = "Packages/com.basis.sdk/Prefabs/AvatarOrb.prefab";
    public static string PropOrb = "Packages/com.basis.sdk/Prefabs/PropOrb.prefab";
    public static string WorldOrb = "Packages/com.basis.sdk/Prefabs/WorldOrb.prefab";
    /// <summary>
    /// Drops a content share sphere in front of the local player.
    /// </summary>
    public static async void DropContentSphere(string contentURL, string unlockPassword, ContentShareType contentType)
    {
        if (string.IsNullOrEmpty(contentURL) || string.IsNullOrEmpty(unlockPassword))
        {
            BasisDebug.LogError("Invalid content URL or password for content share.", BasisDebug.LogTag.Networking);
            return;
        }
        BasisDeviceManagement deviceInstance = BasisDeviceManagement.Instance;
        if (!deviceInstance.FindDevice(out BasisInput input, BasisDominantHand.DominantRole) &&
    !deviceInstance.FindDevice(out input, BasisDominantHand.NonDominantRole) &&
    !deviceInstance.FindDevice(out input, BasisBoneTrackedRole.CenterEye))
        {
            BasisDebug.LogError("LoadProp failed: no suitable device found (LeftHand/RightHand/CenterEye).");
            return;
        }
        BasisDebug.Log("Forcefully closing the main menu");
        BasisMainMenu.Close();

        (Vector3 spawnPos, Quaternion spawnRot, Vector3 spawnScale) placementResult;
        try
        {
            placementResult = await PlacementManager.BeginPlacement(input, new Vector3(0.5f,0.5f,0.5f), new Vector3());
        }
        catch (TaskCanceledException)
        {
            BasisDebug.Log("Placement was cancelled by the user or UI.");
            return;
        }
        catch (Exception ex)
        {
            BasisDebug.LogError(ex);
            return;
        }
       Vector3 finalPos = placementResult.spawnPos;

        ContentShareMessage msg = new ContentShareMessage
        {
            SphereNetID = BasisGenerateUniqueID.GenerateUniqueID(),
            ContentURL = contentURL,
            UnlockPassword = unlockPassword,
            ContentType = contentType,
            PositionX = finalPos.x,
            PositionY = finalPos.y,
            PositionZ = finalPos.z
        };

        NetDataWriter writer = new NetDataWriter();
        msg.Serialize(writer);

        BasisDebug.Log($"Dropping content sphere: {msg.SphereNetID} type={contentType}", BasisDebug.LogTag.Networking);

        BasisNetworkConnection.LocalPlayerPeer?.Send(
            writer,
            BasisNetworkCommons.ContentShareChannel,
            DeliveryMethod.ReliableOrdered
        );
    }

    /// <summary>
    /// Drops a content share sphere using an existing BasisLoadableBundle.
    /// </summary>
    public static void DropContentSphere(BasisLoadableBundle bundle, ContentShareType contentType)
    {
        DropContentSphere(
            bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation,
            bundle.UnlockPassword,
            contentType
        );
    }

    /// <summary>
    /// Request removal of a content share sphere.
    /// </summary>
    public static void RequestRemoveSphere(string sphereNetID)
    {
        if (string.IsNullOrEmpty(sphereNetID))
        {
            BasisDebug.LogError("Invalid sphere ID for cleanup.", BasisDebug.LogTag.Networking);
            return;
        }

        ContentShareCleanupMessage msg = new ContentShareCleanupMessage
        {
            SphereNetID = sphereNetID
        };

        NetDataWriter writer = new NetDataWriter();
        msg.Serialize(writer);

        BasisNetworkConnection.LocalPlayerPeer?.Send(
            writer,
            BasisNetworkCommons.ContentShareCleanupChannel,
            DeliveryMethod.ReliableOrdered
        );
    }

    /// <summary>
    /// Called when a content share message is received from the server.
    /// Creates the sphere locally.
    /// </summary>
    public static void HandleContentShareMessage(NetPacketReader reader)
    {
        ServerContentShareMessage serverMsg = new ServerContentShareMessage();
        serverMsg.Deserialize(reader);

        CreateSphere(serverMsg);
    }

    /// <summary>
    /// Called when a content share cleanup message is received from the server.
    /// Removes the sphere locally.
    /// </summary>
    public static void HandleContentShareCleanup(NetPacketReader reader)
    {
        ServerContentShareCleanupMessage serverMsg = new ServerContentShareCleanupMessage();
        serverMsg.Deserialize(reader);

        RemoveSphere(serverMsg.contentShareCleanupMessage.SphereNetID);
    }
    /// <summary>
    /// Creates a content sphere GameObject in the world.
    /// </summary>
    private static void CreateSphere(ServerContentShareMessage serverMsg)
    {
        ContentShareMessage msg = serverMsg.contentShareMessage;

        if (ActiveSpheres.ContainsKey(msg.SphereNetID))
        {
            BasisDebug.LogWarning($"Content sphere already exists locally: {msg.SphereNetID}");
            return;
        }

        Vector3 position = new Vector3(msg.PositionX, msg.PositionY, msg.PositionZ);
        var op = new UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject>();
        switch (serverMsg.contentShareMessage.ContentType)
        {
            case ContentShareType.Avatar:
                op = Addressables.LoadAssetAsync<GameObject>(AvatarOrb);
                break;
            case ContentShareType.Prop:
                op = Addressables.LoadAssetAsync<GameObject>(PropOrb);
                break;
            case ContentShareType.World:
                op = Addressables.LoadAssetAsync<GameObject>(WorldOrb);
                break;
        }
        var Orb = op.WaitForCompletion();
        var InSceneOrb = GameObject.Instantiate(Orb);
        InSceneOrb.transform.position = position;
        InSceneOrb.transform.parent = BasisDeviceManagement.Instance.transform;
        // Add the content sphere component
        if (InSceneOrb.TryGetComponent<BasisContentSphere>(out BasisContentSphere Sphere))
        {
            Sphere.Initialize(
                msg.SphereNetID,
                msg.ContentURL,
                msg.UnlockPassword,
                msg.ContentType,
                serverMsg.playerIdMessage.playerID,
                serverMsg.SharerUUID,
                serverMsg.SharerDisplayName
            );
            if (ActiveSpheres.TryAdd(msg.SphereNetID, Sphere))
            {
                BasisDebug.Log($"Content sphere created: {msg.SphereNetID} type={msg.ContentType}", BasisDebug.LogTag.Networking);
                OnSphereCreated?.Invoke(Sphere);
            }
        }
    }

    /// <summary>
    /// Removes a content sphere from the world.
    /// </summary>
    private static void RemoveSphere(string sphereNetID)
    {
        if (ActiveSpheres.TryRemove(sphereNetID, out BasisContentSphere sphere))
        {
            if (sphere != null && sphere.gameObject != null)
            {
                UnityEngine.Object.Destroy(sphere.gameObject);
            }
            BasisDebug.Log($"Content sphere removed: {sphereNetID}", BasisDebug.LogTag.Networking);
            OnSphereRemoved?.Invoke(sphereNetID);
        }
    }

    /// <summary>
    /// Cleans up all spheres (called on disconnect).
    /// </summary>
    public static void Reset()
    {
        foreach (var kvp in ActiveSpheres)
        {
            if (kvp.Value != null && kvp.Value.gameObject != null)
            {
                UnityEngine.Object.Destroy(kvp.Value.gameObject);
            }
        }
        ActiveSpheres.Clear();
    }
}
