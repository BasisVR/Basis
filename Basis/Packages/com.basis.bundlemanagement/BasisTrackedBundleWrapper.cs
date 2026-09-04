using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
[System.Serializable]
public class BasisTrackedBundleWrapper
{
    [SerializeField]
    public BasisLoadableBundle LoadableBundle;
    [SerializeField]
    public AssetBundle AssetBundle;
    /// <summary>
    /// Generic (glTF) content loads produce a hidden template instance instead of an
    /// AssetBundle: an inactive DontDestroyOnLoad holder owning the imported avatar with its
    /// rebuilt humanoid rig and wired BasisAvatar. Clones are instantiated from the template
    /// the same way prefabs are instantiated from a bundle. Disposing the GltfImport destroys
    /// the meshes/textures/materials it created — the glTF analog of AssetBundle.Unload(true).
    /// </summary>
    public GameObject GltfTemplateHolder;
    public GameObject GltfTemplateAvatarRoot;
    [System.NonSerialized]
    public GLTFast.GltfImport GltfImport;
    public UnityEngine.Avatar GltfBuiltAvatar;
    /// <summary>
    /// Content validator the HTTP host reported for the bytes this wrapper actually downloaded
    /// (ETag, else Last-Modified). Set by the download path and consumed when the cache meta is
    /// written, so the recorded version is one the server asserted rather than one a peer claimed.
    /// Empty for cache reads, local bee files, and hosts that publish no validator.
    /// </summary>
    [System.NonSerialized]
    public string ObservedVersionTag;
    public bool HasGltfTemplate => GltfTemplateAvatarRoot != null;
    #if UNITY_BUNDLEUNLOAD
    [SerializeField]
    public bool IsBundleBackingStoreReleased = false;
    #endif
    /// <summary>
    /// Sentinel parked in <see cref="_requestedTimes"/> by <see cref="TryClaimUnload"/>. Reaching
    /// zero is not enough to unload: the old code re-read the count and then called Unload(true),
    /// so an Increment landing in that gap got a reservation on a bundle whose assets were about to
    /// be destroyed. Claiming the count instead makes "nobody holds this" and "nobody may take it"
    /// one atomic step, and Increment refuses once the claim is in.
    /// </summary>
    private const int UnloadClaimed = int.MinValue;
    private int _requestedTimes = 0;
    public bool IsInUse => Volatile.Read(ref _requestedTimes) > 0;
    public bool DidErrorOccur = false;
    /// <summary>
    /// Set the moment Unload(true) destroys this wrapper's assets. The wrapper can outlive
    /// the unload in the registry for a continuation gap — lookups must treat a flagged
    /// wrapper as a MISS (drop it, load fresh), never instantiate from it.
    /// </summary>
    [System.NonSerialized]
    public volatile bool IsUnloaded;
    /// <summary>
    /// The registry key this wrapper was actually filed under, captured at registration.
    /// <para>Removal must use this rather than recomputing from the loadable bundle: the key
    /// includes the content version tag, and that tag lives on a record other systems hold and
    /// write to. A recomputed key that has drifted removes nothing, leaving a dead or orphaned
    /// wrapper in the registry under its original key.</para>
    /// </summary>
    [System.NonSerialized]
    public string RegisteredKey;
    public static TimeSpan TimeSpan = TimeSpan.FromSeconds(BasisBeeConstants.TimeUntilMemoryRemoval);
    /// <summary>
    /// for example this is the scene path. we can use this to see 
    /// if this scene is unloaded so we can remove the memory.
    /// </summary>
    public string MetaLink;
    // Method to await the completion of the bundle loading
    public async Task WaitForBundleLoadAsync()
    {
        // Simulating the bundle loading process - this can be replaced by your actual loading logic
        while (!IsBundleCompleteAndLoaded())
        {
            if (DidErrorOccur)
            {
                return;
            }
            await Task.Yield(); // Yield to avoid blocking the main thread
        }
    }
    // Method to check if the bundle is fully loaded
    private bool IsBundleCompleteAndLoaded()
    {
        // Either backing store counts as loaded: an AssetBundle or a generic glTF template.
        return AssetBundle != null || HasGltfTemplate;
    }

    /// <summary>
    /// Tears down generic (glTF) content: the template hierarchy, the runtime-built humanoid
    /// Avatar asset, and the import (which destroys the meshes/textures/materials it created).
    /// Safe to call when nothing was loaded.
    /// </summary>
    public void UnloadGltfTemplate()
    {
        if (GltfTemplateHolder != null)
        {
            UnityEngine.Object.Destroy(GltfTemplateHolder);
            GltfTemplateHolder = null;
        }
        GltfTemplateAvatarRoot = null;
        if (GltfBuiltAvatar != null)
        {
            UnityEngine.Object.Destroy(GltfBuiltAvatar);
            GltfBuiltAvatar = null;
        }
        if (GltfImport != null)
        {
            GltfImport.Dispose();
            GltfImport = null;
        }
    }

    // TODO: Bug in here
    // when loading in multiple same scenes and unloading one of them
    // it will remove other duplicate scenes?
    public async Task<bool> UnloadIfReady()
    {
        if (IsUnloaded)
        {
            return true;
        }
        bool isGltfContent = HasGltfTemplate || GltfImport != null;
        #if !UNITY_SERVER
        if (AssetBundle == null && !isGltfContent)
        {
            BasisDebug.LogError("Asset Bundle was null this should never occur");
            return false;
        }
        #endif
        if (Volatile.Read(ref _requestedTimes) <= 0)
        {
            await Task.Delay(TimeSpan);
            if (TryClaimUnload())
            {
                if (isGltfContent)
                {
                    BasisDebug.Log("Unloading generic (glTF) template " + (GltfTemplateHolder != null ? GltfTemplateHolder.name : "<destroyed>"));
                    IsUnloaded = true;
                    UnloadGltfTemplate();
                    return true;
                }
                if (AssetBundle == null)
                {
                    #if UNITY_BUNDLEUNLOAD
                    if (IsBundleBackingStoreReleased)
                    {
                        return true;
                    }

                    BasisDebug.LogError("Asset Bundle was null this should never occur");
                    #endif
                    ReleaseUnloadClaim();
                    return false;
                }
                BasisDebug.Log("Unloading Bundle " + AssetBundle.name);
                // Flagged BEFORE the unload: the wrapper stays in the registry until the
                // caller's continuation removes it, and a lookup in that gap must see a dead
                // wrapper, not a loadable one — Unload(true) destroys every asset instances
                // depend on (an instantiate from this wrapper afterwards produces an avatar
                // whose Animator.avatar is null).
                if (!BasisLoadHandler.TryUnloadBundleAssets(this))
                {
                    ReleaseUnloadClaim();
                    return false;
                }
                #if UNITY_BUNDLEUNLOAD
                AssetBundle = null;
                IsBundleBackingStoreReleased = true;
                #endif
                return true;
            }
            else
            {
                BasisDebug.Log("Stopping Unload Process, Bundle was Incremented again after Requested Time");
                return false;
            }
        }
        else
        {
            return false;
        }
    }
    /// <summary>
    /// Takes a reservation. Returns false when an unload has already been claimed on this wrapper,
    /// in which case the caller must treat the lookup as a MISS and load fresh: the assets are gone
    /// or about to be.
    /// </summary>
    public bool Increment()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _requestedTimes);
            if (current == UnloadClaimed)
            {
                return false;
            }
        } while (Interlocked.CompareExchange(ref _requestedTimes, current + 1, current) != current);

     //   BasisDebug.Log($"Incremented Asset Load {LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation}");
        return true;
    }
    public bool DeIncrement()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _requestedTimes);
            if (current <= 0)
            {
                BasisDebug.LogError("Trying to DeIncrement more than what was loaded, please check Increment and DeIncrement Logic");
                return false;
            }
        } while (Interlocked.CompareExchange(ref _requestedTimes, current - 1, current) != current);

       // BasisDebug.Log($"DeIncremented Asset Load {LoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation}");
        return true;
    }
    /// <summary>
    /// Atomically moves an unreserved wrapper into the unloading state. Succeeds only from exactly
    /// zero reservations, so it cannot race an Increment; pair a failed unload with
    /// <see cref="ReleaseUnloadClaim"/> so the wrapper stays loadable.
    /// </summary>
    public bool TryClaimUnload()
    {
        return Interlocked.CompareExchange(ref _requestedTimes, UnloadClaimed, 0) == 0;
    }
    public void ReleaseUnloadClaim()
    {
        Interlocked.CompareExchange(ref _requestedTimes, 0, UnloadClaimed);
    }
#if UNITY_BUNDLEUNLOAD
    public void ReleaseBundleBackingStore()
    {

        if (AssetBundle == null)
        {
            return;
        }

        BasisDebug.Log("Releasing bundle backing store " + AssetBundle.name);
        AssetBundle.Unload(false);
        AssetBundle = null;
        IsBundleBackingStoreReleased = true;
        BasisDebug.Log("Bundle backing store released for headless scene bundle.");

    }
    #endif
}
