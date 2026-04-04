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
    #if UNITY_SERVER
    [SerializeField]
    public bool IsBundleBackingStoreReleased = false;
    #endif
    private int _requestedTimes = 0;
    public bool DidErrorOccur = false;
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
        // You can implement your actual logic to check if the bundle is loaded here
        return AssetBundle != null; // Assuming AssetBundle being non-null means it's loaded
    }


    // TODO: Bug in here
    // when loading in mutliple same scenes and unloading one of them
    // it will remove other duplicate scenes?
    public async Task<bool> UnloadIfReady()
    {
        #if !UNITY_SERVER
        if (AssetBundle == null)
        {
            BasisDebug.LogError("Asset Bundle was null this should never occur");
            return false;
        }
        #endif
        if (Volatile.Read(ref _requestedTimes) <= 0)
        {
            await Task.Delay(TimeSpan);
            if (Volatile.Read(ref _requestedTimes) <= 0)
            {
                if (AssetBundle == null)
                {
                    #if UNITY_SERVER
                    if (IsBundleBackingStoreReleased)
                    {
                        return true;
                    }

                    BasisDebug.LogError("Asset Bundle was null this should never occur");
                    #else
                    BasisDebug.LogError("Already Unloaded this bundle, check logic could be ok if you loaded this a few times and unloaded it quickly aswell.");
                    #endif
                    return false;
                }
                BasisDebug.Log("Unloading Bundle " + AssetBundle.name);
                AssetBundle.Unload(true);
                #if UNITY_SERVER
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
    public bool Increment()
    {
        Interlocked.Increment(ref _requestedTimes);
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
#if UNITY_SERVER
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
