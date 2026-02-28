using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using static SerializableBasis;

namespace Basis.BasisUI
{
    /// <summary>
    /// Used by the LibraryProvider.cs to specifically load a type of content with a given BasisDataStoreItemKeys.ItemKey
    /// </summary>
    public static class ContentLoader
    {
        public static async Task LoadAvatar(BasisDataStoreItemKeys.ItemKey item)
        {
            if (BasisLocalPlayer.Instance)
            {
                CachedMetaData.CachedContent cachedMeta;
                if (CachedMetaData.TryGetMeta(item.Url, out cachedMeta))
                {
                    BasisLoadableBundle bundle = cachedMeta.BasisLoadableBundle;

                    BasisDebug.Log($"LoadAvatar({item.Url}) -> bundle = {bundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName}");

                    if (cachedMeta.BasisBundleConnector.GetPlatform(out BasisBundleGenerated platformBundle))
                    {
                        string assetMode = platformBundle.AssetMode;
                        byte mode = !string.IsNullOrEmpty(assetMode) && byte.TryParse(assetMode, out byte result)
                            ? result
                            : (byte)0;
                        await BasisLocalPlayer.Instance.CreateAvatar(mode, bundle);
                    }
                    else
                    {
                        if (bundle.UnlockPassword == BasisBeeConstants.DefaultAvatar)
                        {
                            await BasisLocalPlayer.Instance.CreateAvatar(1, bundle);
                        }
                        else
                        {
                            BasisDebug.LogError("LoadAvatar -> Missing Platform " + Application.platform);
                        }
                    }
                }
                else
                {
                    BasisDebug.LogError($"LoadAvatar({item.Url}) -> failed to get cached meta");
                }
            }
            else
            {
                BasisDebug.LogError("Attempted to LoadAvatar without a BasisLocalPlayer.Instance.");
            }
        }

        public static async Task LoadProp(BasisDataStoreItemKeys.ItemKey item, BundledContentHolder.NetworkType desiredNetworkType, bool persistent = false, bool modifyScale = false)
        {
            if (CachedMetaData.TryGetMeta(item.Url, out var cached) || (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable))
            {
                Vector3 finalPos = Vector3.zero;
                Quaternion finalRot = Quaternion.identity;
                Vector3 finalScale = Vector3.one;

                switch (item.PlacementType)
                {
                    case BundledContentHolder.PlacementType.SpawnAtRaycast:
                        BasisDeviceManagement deviceInstance = BasisDeviceManagement.Instance;

                        if (!deviceInstance.FindDevice(out BasisInput input, BasisBoneTrackedRole.LeftHand) &&
                            !deviceInstance.FindDevice(out input, BasisBoneTrackedRole.RightHand) &&
                            !deviceInstance.FindDevice(out input, BasisBoneTrackedRole.CenterEye))
                        {
                            BasisDebug.LogError("LoadProp failed: no suitable device found (LeftHand/RightHand/CenterEye).");
                            return;
                        }

                        BasisDebug.Log("Forcefully closing the main menu");
                        BasisMainMenu.Close();

                        BasisBounds FinalBounds = cached.BasisBundleConnector.Bounds;
                        if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
                        {
                            FinalBounds = EmbeddedItems.GetBoundsForEmbeddedItem(item);
                        }

                        BasisDebug.Log($"{item.Url} -> finalbounds = {FinalBounds.extents} max = {FinalBounds.max} center = {FinalBounds.center}");

                        (Vector3 spawnPos, Quaternion spawnRot, Vector3 spawnScale) placementResult;
                        try
                        {
                            placementResult = await PlacementManager.BeginPlacement(input, FinalBounds.extents, FinalBounds.center);
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

                        finalPos = placementResult.spawnPos;
                        finalRot = placementResult.spawnRot;
                        finalScale = placementResult.spawnScale;
                        break;
                    case BundledContentHolder.PlacementType.SpawnInFrontOfPlayer:
                        Vector3 playerPosReference = BasisLocalCameraDriver.Position;
                        Vector3 forward = BasisLocalCameraDriver.Forward();

                        finalPos = EmbeddedItems.GetOffsetForEmbeddedItem(item, playerPosReference, forward);
                        finalRot = Quaternion.LookRotation(forward, Vector3.up);

                        BasisMainMenu.Close();
                        break;
                    case BundledContentHolder.PlacementType.SpawnAtPlayerOrigin:
                        finalPos = BasisLocalPlayer.Instance.PlayerSelf.position;
                        BasisMainMenu.Close();
                        break;
                    default:
                        BasisDebug.LogError($"LoadProp was invoked for item = {item.Url} but has placementType = {item.PlacementType} which is not defined. Unable to spawn item");
                        break;
                }

                switch (desiredNetworkType)
                {
                    case BundledContentHolder.NetworkType.Local:
                        {
                            Transform parentTarget = BasisDeviceManagement.Instance.transform;

                            if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
                            {
                                // for the moment embedded items are one instance
                                // lets check if it already exists
                                bool exists = BasisRuntimeSpawnRegistry.HasAny(item.Url);

                                if (exists)
                                {
                                    // Get the actual SpawnInstance
                                    var singleInstance = BasisRuntimeSpawnRegistry.GetInstances(item.Url)[0];

                                    // Optionally get the actual GameObject
                                    if (BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(singleInstance.LoadedNetID, out var go) && go != null)
                                    {
                                        BasisDebug.Log("Personal Mirror already exists in the scene");

                                        // lets delete it
                                        // if the gameobject is not null then lets remove its registery
                                        bool success = await BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(singleInstance.LoadedNetID);
                                        if (success)
                                        {
                                            // we should delete the embedded item
                                            GameObject.Destroy(go);
                                        }
                                        else
                                        {
                                            BasisDebug.LogError($"failed to remove item = {singleInstance.InstanceId} that has itemKey.SpawnMethod = {singleInstance.SpawnMethod} from basis BasisRuntimeSpawnRegistry");
                                        }
                                    }

                                }
                                else
                                {
                                    AsyncOperationHandle<GameObject> op = Addressables.LoadAssetAsync<GameObject>(item.Url);
                                    GameObject CreatedObject = op.WaitForCompletion();
                                    GameObject instance = GameObject.Instantiate(CreatedObject, finalPos, finalRot, parentTarget);
                                    BasisRuntimeSpawnRegistry.AddGameObject(item.Url, instance.name, instance, false, BasisRuntimeSpawnRegistry.SpawnMethod.Embedded, out var embeddedinstance);
                                    BasisDebug.Log($"BasisRuntimeSpawnRegistry.AddGameObject instanceID = {embeddedinstance.InstanceId}, LoadedNetID = {embeddedinstance.LoadedNetID}");
                                }


                            }
                            else
                            {
                                if (cached.BasisBundleConnector != null)
                                {
                                    BasisLoadableBundle bundle = cached.BasisLoadableBundle;

                                    BasisProgressReport report = new BasisProgressReport();
                                    CancellationToken cancel = default;

                                    var selector = item.Mode switch
                                    {
                                        BundledContentHolder.Mode.Avatar => BundledContentHolder.Selector.Avatar,
                                        BundledContentHolder.Mode.Prop => BundledContentHolder.Selector.Prop,
                                        BundledContentHolder.Mode.World => BundledContentHolder.Selector.System,
                                        _ => BundledContentHolder.Selector.Prop
                                    };

                                    GameObject createdObject = await BasisLoadHandler.LoadGameObjectBundle(bundle, true, report, cancel, finalPos, finalRot, finalScale, modifyScale, selector, parentTarget);

                                    if (createdObject != null)
                                    {
                                        Debug.Log($"Library provider successfully created item {item.Url} with networking: {desiredNetworkType} at {createdObject.transform.position}.");
                                        BasisRuntimeSpawnRegistry.AddGameObject(
                                            item.Url,
                                            createdObject.name,
                                            createdObject,
                                            item.EmbeddedSettings.IsEmbedded,
                                            BasisRuntimeSpawnRegistry.SpawnMethod.Local
                                            , out var instance
                                        );
                                    }
                                    else
                                    {
                                        Debug.LogError($"Library provider failed to create desired with networking: {desiredNetworkType} with LoadSelectedItem of url {item.Url}");
                                    }
                                }
                                else
                                {
                                    BasisDebug.LogError($"LoadSelectedItem found cached meta for {item.Url} but BasisBundleConnector was null.");
                                }
                            }

                            break;
                        }

                    case BundledContentHolder.NetworkType.Networked:
                        {
                            try
                            {
                                bool ok = BasisNetworkSpawnItem.RequestGameObjectLoad(item.Pass, item.Url, finalPos, finalRot, finalScale, persistent, modifyScale, out LocalLoadResource loadedProp);

                                if (ok && !string.IsNullOrEmpty(loadedProp.LoadedNetID))
                                {
                                    BasisDebug.Log($"Requested networked load for {item.Url}, NetID={loadedProp.LoadedNetID}", BasisDebug.LogTag.Networking);
                                }
                                else
                                {
                                    BasisDebug.LogError($"Failed to request networked load for {item.Url}");
                                }
                            }
                            catch (Exception ex)
                            {
                                BasisDebug.LogError(ex);
                            }

                            break;
                        }

                    default:
                        BasisDebug.LogError($"Load selected item {item.Url} was loaded with an unknown network of {desiredNetworkType}! Nothing will happen.");
                        break;
                }
            }
            else
            {
                BasisDebug.LogError($"LoadSelectedItem failed to find cached meta for url {item.Url}, cannot load bundle without it!");
            }
        }

        public static async Task LoadWorld(BasisDataStoreItemKeys.ItemKey item)
        {
            BasisDebug.LogWarning("World loading not implemented yet, breaking out of load logic to prevent errors. Implement LoadWorld!");
            await Task.CompletedTask;
        }
    }
}
