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
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using static SerializableBasis;

namespace Basis.BasisUI
{
    /// <summary>
    /// Used by the LibraryProvider.cs to specifically load a type of content with a given BasisDataStoreItemKeys.ItemKey
    /// </summary>
    public static class ContentLoader
    {
        /// <summary>
        /// A reachability warning from the last avatar load attempt, if any.
        /// Set when the remote file could not be reached but the load was still attempted.
        /// Cleared on each new load attempt.
        /// </summary>
        public static string LastAvatarReachabilityWarning { get; private set; }

        /// <summary>
        /// Static progress report forwarded during library content loading.
        /// Subscribe to OnProgressReport to receive loading progress updates.
        /// </summary>
        public static readonly BasisProgressReport LibraryLoadProgress = new BasisProgressReport();

        private static void ForwardProgress(string uniqueID, float progress, string eventDescription)
        {
            LibraryLoadProgress.ReportProgress(uniqueID, progress, eventDescription);
        }

        public static async Task LoadAvatar(BasisDataStoreItemKeys.ItemKey item)
        {
            LastAvatarReachabilityWarning = null;

            if (!BasisLocalPlayer.Instance)
            {
                BasisDebug.LogError("Attempted to LoadAvatar without a BasisLocalPlayer.Instance.");
                return;
            }

            if (!CachedMetaData.TryGetMeta(item.Url, out CachedMetaData.CachedContent cachedMeta))
            {
                BasisDebug.LogError($"LoadAvatar({item.Url}) -> failed to get cached meta");
                return;
            }

            BasisLocalPlayer.Instance.ProgressReportAvatarLoad.OnProgressReport += ForwardProgress;

            BasisLoadableBundle bundle = cachedMeta.BasisLoadableBundle;
            BasisDebug.Log($"LoadAvatar({item.Url}) -> bundle = {bundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName}");

            // Fire reachability check in parallel with the avatar load
            Task<BeeResult<bool>> reachabilityTask = null;
            string remoteUrl = bundle.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation;
            if (!string.IsNullOrEmpty(remoteUrl) && Uri.TryCreate(remoteUrl, UriKind.Absolute, out _))
            {
                BasisDebug.Log($"Checking avatar file reachability: {remoteUrl}", BasisDebug.LogTag.Avatar);
                reachabilityTask = BasisIOManagement.CheckRemoteFileReachable(remoteUrl);
            }

            // Load the avatar immediately without waiting for the reachability check
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

            // Collect the reachability result after the load finishes
            if (reachabilityTask != null)
            {
                try
                {
                    var reachResult = await reachabilityTask;
                    if (!reachResult.IsSuccess)
                    {
                        LastAvatarReachabilityWarning = $"Could not reach avatar file at URL: {reachResult.Error}";
                        BasisDebug.LogWarning(LastAvatarReachabilityWarning);
                    }
                }
                catch (Exception ex)
                {
                    LastAvatarReachabilityWarning = $"Could not reach avatar file at URL: {ex.Message}";
                    BasisDebug.LogWarning(LastAvatarReachabilityWarning);
                }
            }

            BasisLocalPlayer.Instance.ProgressReportAvatarLoad.OnProgressReport -= ForwardProgress;
        }

        public static async Task<GameObject> HandleLoadGameObjectWithBundle(BasisDataStoreItemKeys.ItemKey item, CachedMetaData.CachedContent cached, BundledContentHolder.NetworkType desiredNetworkType, Vector3 finalPos, Quaternion finalRot, Vector3 finalScale, Transform parentTarget, bool admin = false, bool modifyScale = false, bool local = false,bool ChangeColidersToCorrectLayer = false)
        {
            BasisLoadableBundle bundle = cached.BasisLoadableBundle;

            BasisProgressReport report = new BasisProgressReport();
            report.OnProgressReport += ForwardProgress;
            BasisRuntimeSpawnRegistry.PendingLoad pending = BasisRuntimeSpawnRegistry.BeginPendingLoad(
                item.Url,
                BasisRuntimeSpawnRegistry.SpawnMode.GameObject,
                BasisRuntimeSpawnRegistry.SpawnMethod.Local,
                BasisLocalPlayer.Instance.UUID,
                false,
                item.EmbeddedSettings.IsEmbedded);
            void ForwardPendingProgress(string uniqueId, float progress, string info) =>
                BasisRuntimeSpawnRegistry.ReportPendingLoadProgress(pending.PendingId, progress, info);
            report.OnProgressReport += ForwardPendingProgress;
            CancellationToken cancel = default;

            var selector = item.Mode switch
            {
                BundledContentHolder.Mode.Avatar => BundledContentHolder.Selector.Avatar,
                BundledContentHolder.Mode.Prop => BundledContentHolder.Selector.Prop,
                BundledContentHolder.Mode.World => BundledContentHolder.Selector.System,
                _ => BundledContentHolder.Selector.Prop
            };

            try
            {
                GameObject createdObject = await BasisLoadHandler.LoadGameObjectBundle(BasisDeviceManagement.Instance.CreationGameobject, bundle, true, report, cancel, finalPos, finalRot, finalScale, modifyScale, selector, parentTarget ,false, ChangeColidersToCorrectLayer);

                if (createdObject != null)
                {
                    BasisDebug.Log($"Library provider successfully created item {item.Url} with networking: {desiredNetworkType} at {createdObject.transform.position}. local = {local}");
                    if (!local) // if we are not local register it
                    {
                        BasisRuntimeSpawnRegistry.EndPendingLoad(pending.PendingId);
                        BasisRuntimeSpawnRegistry.AddGameObject(
                            item.Url,
                            createdObject.name,
                            createdObject,
                            BasisLocalPlayer.Instance.UUID,
                            false, // local items should not consider admin check
                            item.EmbeddedSettings.IsEmbedded, // persistent
                            BasisRuntimeSpawnRegistry.SpawnMethod.Local,
                            bundle.BasisBundleConnector
                            , out var instance
                        );
                    }
                }
                else
                {
                    BasisDebug.LogError($"Library provider failed to create desired with networking: {desiredNetworkType} with LoadSelectedItem of url {item.Url}");
                }

                return createdObject;
            }
            finally
            {
                report.OnProgressReport -= ForwardPendingProgress;
                report.OnProgressReport -= ForwardProgress;
                BasisRuntimeSpawnRegistry.EndPendingLoad(pending.PendingId);
            }
        }

        /// <summary>
        /// Raised before a prop is spawned, with the item's Url. A handler returning true has
        /// taken responsibility for the request and no prop is spawned.
        /// <para>
        /// This exists so packages the framework cannot reference can claim their own item —
        /// the handheld camera uses it to bring a hidden instance back rather than let a
        /// second one be created alongside it.
        /// </para>
        /// </summary>
        public static event Func<string, bool> OnBeforePropSpawn;

        /// <summary>
        /// Offers the spawn to each handler in turn. Multicast delegates only surface the last
        /// return value, so they are invoked one at a time and the first claim wins.
        /// </summary>
        private static bool PropSpawnClaimed(string url)
        {
            Func<string, bool> handlers = OnBeforePropSpawn;
            if (handlers == null) return false;

            Delegate[] list = handlers.GetInvocationList();
            for (int Index = 0; Index < list.Length; Index++)
            {
                if (((Func<string, bool>)list[Index]).Invoke(url)) return true;
            }
            return false;
        }

        public static async Task LoadProp(BasisDataStoreItemKeys.ItemKey item, BundledContentHolder.NetworkType desiredNetworkType, bool persistent = false, bool admin = false, bool modifyScale = false)
        {
            if (PropSpawnClaimed(item.Url)) return;

            if (CachedMetaData.TryGetMeta(item.Url, out var cached) || (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable))
            {
                if (desiredNetworkType == BundledContentHolder.NetworkType.Predownload)
                {
                    bool requested = BasisNetworkSpawnItem.RequestGameObjectLoad(item.Pass, item.Url, Vector3.zero, Quaternion.identity, Vector3.one, persistent, admin, modifyScale, out _, loadStrategy: 3);
                    if (requested)
                    {
                        BasisDebug.Log($"Requested predownload for prop {item.Url}", BasisDebug.LogTag.Networking);
                    }
                    else
                    {
                        BasisDebug.LogError($"Failed to request predownload for prop {item.Url}");
                    }
                    return;
                }

                Vector3 finalPos = Vector3.zero;
                Quaternion finalRot = Quaternion.identity;
                Vector3 finalScale = Vector3.one;

                switch (item.PlacementType)
                {
                    case BundledContentHolder.PlacementType.SpawnAtRaycast:
                        BasisDeviceManagement deviceInstance = BasisDeviceManagement.Instance;

                        if (!deviceInstance.FindDevice(out BasisInput input, BasisDominantHand.DominantRole) &&
                            !deviceInstance.FindDevice(out input, BasisDominantHand.NonDominantRole) &&
                            !deviceInstance.FindDevice(out input, BasisBoneTrackedRole.CenterEye))
                        {
                            BasisDebug.LogError("LoadProp failed: no suitable device found (LeftHand/RightHand/CenterEye).");
                            return;
                        }

                        BasisBounds FinalBounds = cached.BasisBundleConnector.Bounds;
                        if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
                        {
                            FinalBounds = EmbeddedItems.GetBoundsForEmbeddedItem(item);
                        }
                        else
                        {
                            if (FinalBounds.extents == Vector3.zero) // if for whatever reason the basis bounds is zero spawn the object in
                            {
                                GameObject tempOject = await HandleLoadGameObjectWithBundle(
                                    item,
                                    cached,
                                    desiredNetworkType,
                                    Vector3.zero,
                                    Quaternion.identity,
                                    finalScale,
                                    BasisDeviceManagement.Instance.transform,
                                    admin,
                                    modifyScale,
                                    true // we are using this one for local purposes
                                );

                                // disable it
                                tempOject.SetActive(false);

                                // calculate the bounds
                                Bounds calculatedBounds = PlacementManager.CalculateLocalRenderBounds(tempOject);
                                BasisBounds newBounds = new BasisBounds(calculatedBounds.center, calculatedBounds.size);
                                cached.BasisBundleConnector.Bounds = newBounds;
                                FinalBounds = newBounds;

                                GameObject.Destroy(tempOject);

                            }
                        }
                        //BasisDebug.Log($"{item.Url} -> finalbounds = {FinalBounds.extents} max = {FinalBounds.max} center = {FinalBounds.center}");

                        BasisDebug.Log("Forcefully closing the main menu");
                        BasisMainMenu.Close();

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
                        // Spawn at the player's head ("eye height and in front of them", per the
                        // PlacementType doc). HeadPosition/HeadForward equal the camera pose in
                        // first-person, but in third-person they stay on the head bone instead of
                        // the orbiting camera — otherwise the prop lands out where the third-person
                        // camera is rather than in front of the player.
                        Vector3 playerPosReference = BasisLocalCameraDriver.HeadPosition;
                        Vector3 forward = BasisLocalCameraDriver.HeadForward();

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
                    case BundledContentHolder.NetworkType.Synchronized:
                        {
                            try
                            {
                                bool ok = BasisNetworkSpawnItem.RequestGameObjectLoad(item.Pass, item.Url, finalPos, finalRot, finalScale, persistent, admin, modifyScale, out LocalLoadResource syncResource, loadStrategy: 2);
                                if (ok)
                                {
                                    BasisDebug.Log($"Requested synchronized load for {item.Url}, NetID={syncResource.LoadedNetID}", BasisDebug.LogTag.Networking);
                                }
                                else
                                {
                                    BasisDebug.LogError($"Failed to request synchronized load for {item.Url}");
                                }
                            }
                            catch (Exception ex)
                            {
                                BasisDebug.LogError(ex);
                            }
                            break;
                        }

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
                                        BasisDebug.Log($"{item.Url} already exists in the scene!");

                                        // lets delete it
                                        // if the gameobject is not null then lets remove its registry
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
                                    AsyncOperationHandle<GameObject> op = Addressables.InstantiateAsync(item.Url, finalPos, finalRot, parentTarget);
                                    GameObject instance = op.WaitForCompletion();
                                    BasisRuntimeSpawnRegistry.AddGameObject(
                                        item.Url,
                                        instance.name,
                                        instance,
                                        BasisLocalPlayer.Instance.UUID,
                                        false, // embedded items should not consider admin check
                                        false,
                                        BasisRuntimeSpawnRegistry.SpawnMethod.Embedded,
                                        null, // no metadata for embedded items
                                         out var embeddedinstance
                                    );
                                    BasisDebug.Log($"BasisRuntimeSpawnRegistry.AddGameObject instanceID = {embeddedinstance.InstanceId}, LoadedNetID = {embeddedinstance.LoadedNetID}");
                                }


                            }
                            else
                            {
                                if (cached.BasisBundleConnector != null)
                                {
                                    await HandleLoadGameObjectWithBundle(
                                        item,
                                        cached,
                                        desiredNetworkType,
                                        finalPos,
                                        finalRot,
                                        finalScale,
                                        parentTarget,
                                        admin,
                                        modifyScale
                                    );
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
                                bool ok = BasisNetworkSpawnItem.RequestGameObjectLoad(item.Pass, item.Url, finalPos, finalRot, finalScale, persistent, admin, modifyScale, out LocalLoadResource loadedProp);

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
                BasisDebug.LogError($"LoadProp failed to find cached meta for url {item.Url}, cannot load bundle without it!");
            }
        }

        public static async Task LoadWorld(BasisDataStoreItemKeys.ItemKey item, BundledContentHolder.NetworkType desiredNetworkType, bool persistent = false, bool admin = false)
        {
            if (CachedMetaData.TryGetMeta(item.Url, out var cached))
            {
                switch (desiredNetworkType)
                {
                    case BundledContentHolder.NetworkType.Local:

                        if (cached.BasisBundleConnector != null)
                        {
                            BasisLoadableBundle bundle = cached.BasisLoadableBundle;

                            BasisProgressReport report = new BasisProgressReport();
                            report.OnProgressReport += ForwardProgress;
                            BasisRuntimeSpawnRegistry.PendingLoad pending = BasisRuntimeSpawnRegistry.BeginPendingLoad(
                                item.Url,
                                BasisRuntimeSpawnRegistry.SpawnMode.Scene,
                                BasisRuntimeSpawnRegistry.SpawnMethod.Local,
                                BasisLocalPlayer.Instance.UUID,
                                admin,
                                persistent);
                            void ForwardPendingProgress(string uniqueId, float progress, string info) =>
                                BasisRuntimeSpawnRegistry.ReportPendingLoadProgress(pending.PendingId, progress, info);
                            report.OnProgressReport += ForwardPendingProgress;
                            CancellationToken cancel = default;

                            try
                            {
                                Scene scene = await BasisLoadHandler.LoadSceneBundle(
                                    true, // set as active scene so skybox and RenderSettings apply
                                    bundle,
                                    report,
                                    cancel
                                );


                                if (scene.IsValid())
                                {
                                    string uniqueID = BasisGenerateUniqueID.GenerateUniqueID();
                                    BasisDebug.Log($"Library provider successfully created scene {item.Url} with loadedNetId = {uniqueID} networking: {desiredNetworkType}");

                                    BasisRuntimeSpawnRegistry.EndPendingLoad(pending.PendingId);
                                    BasisRuntimeSpawnRegistry.AddScene(
                                        item.Url,
                                        uniqueID,//scene.name + $"_{desiredNetworkType}_{scene.handle}",
                                        scene,
                                        BasisLocalPlayer.Instance.UUID,
                                        admin,
                                        persistent,
                                        BasisRuntimeSpawnRegistry.SpawnMethod.Local,
                                        bundle.BasisBundleConnector,
                                        out BasisRuntimeSpawnRegistry.SpawnInstance instance
                                    );
                                }
                                else
                                {
                                    BasisDebug.LogError($"Library provider failed to create desired scene with networking: {desiredNetworkType} with of url {item.Url}");
                                }
                            }
                            finally
                            {
                                report.OnProgressReport -= ForwardPendingProgress;
                                report.OnProgressReport -= ForwardProgress;
                                BasisRuntimeSpawnRegistry.EndPendingLoad(pending.PendingId);
                            }
                        }
                        else
                        {
                            BasisDebug.LogError($"LoadWorld local: BasisBundleConnector is null for {item.Url}");
                        }

                        break;
                    case BundledContentHolder.NetworkType.Networked:
                        try
                        {
                            bool ok = BasisNetworkSpawnItem.RequestSceneLoad(item.Pass, item.Url, persistent, admin, out LocalLoadResource loadedProp);

                            if (ok && !string.IsNullOrEmpty(loadedProp.LoadedNetID))
                            {
                                BasisDebug.Log($"Requested networked SceneLoad for {item.Url}, NetID={loadedProp.LoadedNetID}", BasisDebug.LogTag.Networking);
                            }
                            else
                            {
                                BasisDebug.LogError($"Failed to request networked SceneLoad for {item.Url}");
                            }
                        }
                        catch (Exception ex)
                        {
                            BasisDebug.LogError(ex);
                        }
                        break;
                    case BundledContentHolder.NetworkType.Synchronized:
                        try
                        {
                            bool ok = BasisNetworkSpawnItem.RequestSceneLoad(item.Pass, item.Url, persistent, admin, out LocalLoadResource syncResource, loadStrategy: 2);
                            if (ok)
                            {
                                BasisDebug.Log($"Requested synchronized SceneLoad for {item.Url}, NetID={syncResource.LoadedNetID}", BasisDebug.LogTag.Networking);
                            }
                            else
                            {
                                BasisDebug.LogError($"Failed to request synchronized SceneLoad for {item.Url}");
                            }
                        }
                        catch (Exception ex)
                        {
                            BasisDebug.LogError(ex);
                        }
                        break;
                    case BundledContentHolder.NetworkType.Predownload:
                        try
                        {
                            bool ok = BasisNetworkSpawnItem.RequestSceneLoad(item.Pass, item.Url, persistent, admin, out _, loadStrategy: 3);
                            if (ok)
                            {
                                BasisDebug.Log($"Requested predownload for world {item.Url}", BasisDebug.LogTag.Networking);
                            }
                            else
                            {
                                BasisDebug.LogError($"Failed to request predownload for world {item.Url}");
                            }
                        }
                        catch (Exception ex)
                        {
                            BasisDebug.LogError(ex);
                        }
                        break;
                    default:
                        BasisDebug.LogError($"LoadWorld {item.Url} was loaded with an unknown network type of {desiredNetworkType}!");
                        break;
                }
            }
            else
            {
                BasisDebug.LogError($"LoadWorld failed to find the cached meta for url {item.Url}");
            }

        }
    }
}
