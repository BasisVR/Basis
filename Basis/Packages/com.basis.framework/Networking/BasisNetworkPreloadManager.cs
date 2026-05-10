using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static BundledContentHolder;
using static SerializableBasis;

/// <summary>
/// Client-side manager for preloaded and synchronized resource loading.
/// Handles downloading content without spawning, tracking readiness,
/// and responding to server spawn signals.
/// </summary>
public static class BasisNetworkPreloadManager
{
    /// <summary>
    /// Timeout duration for synchronized loads. If a client hasn't finished
    /// downloading within this window, it reports failure to the server.
    /// </summary>
    public static readonly TimeSpan SynchronizedTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Stores preloaded resources that have been downloaded but not yet spawned.
    /// Key is LoadedNetID.
    /// </summary>
    public static readonly ConcurrentDictionary<string, PreloadedResource> PreloadedResources = new();

    private static CancellationTokenSource _cts = new CancellationTokenSource();

    public class PreloadedResource
    {
        public LocalLoadResource LoadResource;
        public BasisTrackedBundleWrapper BundleWrapper;
        public bool IsReady;
        public DateTime PreloadStartUtc;
    }

    /// <summary>
    /// Downloads the full BEE file to disk so it is ready to be loaded from disc later.
    /// Does NOT create an AssetBundle - that happens when the spawn signal arrives
    /// through the normal load path which will find the file already on disk.
    /// Used internally by HandleSynchronizedPreload.
    /// </summary>
    private static async Task HandlePreload(LocalLoadResource resource)
    {
        string netId = resource.LoadedNetID;
        BasisDebug.Log($"PreloadManager: Beginning preload for {resource.CombinedURL} (NetID={netId})", BasisDebug.LogTag.Networking);

        var preloaded = new PreloadedResource
        {
            LoadResource = resource,
            IsReady = false,
            PreloadStartUtc = DateTime.UtcNow,
        };

        PreloadedResources[netId] = preloaded;

        try
        {
            BasisLoadableBundle loadBundle = new BasisLoadableBundle
            {
                BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle()
                {
                    RemoteBeeFileLocation = resource.CombinedURL
                },
                UnlockPassword = resource.UnlockPassword,
            };

            BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper
            {
                LoadableBundle = loadBundle,
            };

            BasisProgressReport report = new BasisProgressReport();
            CancellationToken cancel = _cts.Token;

            await BasisLoadHandler.EnsureInitializationComplete();

            // Check if the full BEE file is already on disk
            bool isOnDisc = BasisLoadHandler.IsMetaDataOnDisc(resource.CombinedURL, out BasisBEEExtensionMeta metaInfo);

            (BasisBundleGenerated Generated, byte[] BundleBytes, string ErrorMessage) output;

            if (isOnDisc)
            {
                // Already downloaded - just read connector + bundle bytes to verify integrity
                output = await BasisBundleManagement.LocalLoadBundleConnector(wrapper, metaInfo.StoredLocal, report, cancel);
            }
            else
            {
                // Download the full BEE file to disk
                output = await BasisBundleManagement.DownloadLoadBundleConnector(wrapper, report, cancel);
            }

            // Retry if local read returned empty data
            if (output.BundleBytes == null || output.BundleBytes.Length == 0)
            {
                output = await BasisBundleManagement.DownloadLoadBundleConnector(wrapper, report, cancel);
                isOnDisc = false; // was re-downloaded
            }

            if (output.Generated == null || output.ErrorMessage != string.Empty)
            {
                throw new Exception($"Failed to download BEE file: {output.ErrorMessage}");
            }

            // Save metadata to disk so the normal load path finds the file later
            if (!isOnDisc)
            {
                BasisBEEExtensionMeta newDiscInfo = new BasisBEEExtensionMeta
                {
                    StoredRemote = wrapper.LoadableBundle.BasisRemoteBundleEncrypted,
                    StoredLocal = wrapper.LoadableBundle.BasisLocalEncryptedBundle,
                    UniqueVersion = wrapper.LoadableBundle.BasisBundleConnector.UniqueVersion,
                };
                await BasisLoadHandler.AddDiscInfo(newDiscInfo);
                BasisStorageManagement.EnforceCacheSizeLimit();
            }

            // Do NOT create an AssetBundle here - the file is on disk and ready.
            // The normal load path will find it and load from disk when spawn signal arrives.

            preloaded.BundleWrapper = wrapper;
            preloaded.IsReady = true;

            BasisDebug.Log($"PreloadManager: Successfully downloaded {resource.CombinedURL} to disk (NetID={netId})", BasisDebug.LogTag.Networking);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"PreloadManager: Failed to preload {resource.CombinedURL} (NetID={netId}): {ex.Message}");
            preloaded.IsReady = false;
        }
    }

    /// <summary>
    /// Called when the client receives a LoadResource message with LoadStrategy = 2 (Synchronized).
    /// Downloads content, then reports readiness to the server. Spawning happens later
    /// when the server sends a SpawnPreloaded signal.
    /// </summary>
    public static async Task HandleSynchronizedPreload(LocalLoadResource resource)
    {
        string netId = resource.LoadedNetID;
        BasisDebug.Log($"PreloadManager: Beginning synchronized preload for {resource.CombinedURL} (NetID={netId})", BasisDebug.LogTag.Networking);

        // First preload the content
        await HandlePreload(resource);

        bool isReady = PreloadedResources.TryGetValue(netId, out var preloaded) && preloaded.IsReady;

        // Report readiness to the server
        SendReadyMessage(netId, isReady);

        if (!isReady)
        {
            BasisDebug.LogError($"PreloadManager: Synchronized preload failed for {resource.CombinedURL} (NetID={netId}), reported failure to server");
        }
        else
        {
            BasisDebug.Log($"PreloadManager: Synchronized preload ready for {resource.CombinedURL} (NetID={netId}), reported ready to server");
        }
    }

    /// <summary>
    /// Sends a readiness message to the server indicating whether this client
    /// has successfully preloaded the resource.
    /// </summary>
    private static void SendReadyMessage(string loadedNetId, bool isReady)
    {
        PreloadReadyMessage readyMsg = new PreloadReadyMessage
        {
            LoadedNetID = loadedNetId,
            IsReady = isReady,
        };

        NetDataWriter writer = new NetDataWriter();
        readyMsg.Serialize(writer);

        BasisNetworkConnection.LocalPlayerPeer?.Send(writer, BasisNetworkCommons.PreloadReadyChannel, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Called when the server signals that a preloaded resource should now be spawned.
    /// Unloads all existing scene-type content first, then spawns the new resource.
    /// Done client-side in one step so unload and spawn can't race.
    /// </summary>
    public static async Task HandleSpawnPreloaded(SpawnPreloadedMessage spawnMsg)
    {
        string netId = spawnMsg.LoadedNetID;

        if (!PreloadedResources.TryRemove(netId, out PreloadedResource preloaded))
        {
            BasisDebug.LogError($"PreloadManager: Received spawn signal for {netId} but no preloaded resource found");
            return;
        }

        if (!preloaded.IsReady || preloaded.BundleWrapper == null)
        {
            BasisDebug.LogError($"PreloadManager: Received spawn signal for {netId} but resource was not ready");
            return;
        }

        // Only unload existing scene content when the synchronized resource is a scene.
        // Props (Mode == 0) should never cause scene unloads.
        if (preloaded.LoadResource.Mode == 1)
        {
            await UnloadAllSceneContent();
        }

        BasisDebug.Log($"PreloadManager: Spawning preloaded resource {preloaded.LoadResource.CombinedURL} (NetID={netId})", BasisDebug.LogTag.Networking);

        // Now spawn using the existing spawn infrastructure
        // The LoadStrategy is set back to 0 (Immediate) so SpawnGameObject/SpawnScene
        // treats it as a normal load
        LocalLoadResource spawnResource = preloaded.LoadResource;
        spawnResource.LoadStrategy = 0;

        switch (spawnResource.Mode)
        {
            case 0: // GameObject
                await BasisNetworkSpawnItem.SpawnGameObject(spawnResource, Selector.Prop);
                break;
            case 1: // Scene
                await BasisNetworkSpawnItem.SpawnScene(spawnResource);
                break;
            default:
                BasisDebug.LogError($"PreloadManager: Unknown mode {spawnResource.Mode} for spawn");
                break;
        }
    }

    /// <summary>
    /// Unloads all scene-type content from the local spawn registry.
    /// Called before a synchronized spawn so the new content replaces existing scenes.
    /// </summary>
    private static async Task UnloadAllSceneContent()
    {
        var allInstances = BasisRuntimeSpawnRegistry.GetAll();
        var sceneInstances = new System.Collections.Generic.List<BasisRuntimeSpawnRegistry.SpawnInstance>();

        foreach (var instance in allInstances)
        {
            if (instance.SpawnMode == BasisRuntimeSpawnRegistry.SpawnMode.Scene)
            {
                sceneInstances.Add(instance);
            }
        }

        if (sceneInstances.Count == 0) return;

        BasisDebug.Log($"PreloadManager: Unloading {sceneInstances.Count} existing scene(s) before synchronized spawn", BasisDebug.LogTag.Networking);

        foreach (var scene in sceneInstances)
        {
            await BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(scene.LoadedNetID);
        }
    }

    /// <summary>
    /// Cleans up all preloaded resources. Called on disconnect or reset.
    /// </summary>
    public static void Reset()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        PreloadedResources.Clear();
    }
}
