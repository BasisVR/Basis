using Basis.Scripts.BasisSdk.Players;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
namespace Basis.Scripts.Drivers
{
    public static class BasisSceneLoad
    {
        public static BasisProgressReport progressCallback = new BasisProgressReport();
        /// <summary>
        /// Loads a scene via Addressables, optionally reporting progress and spawning the player.
        /// </summary>
        /// <param name="sceneToLoad">Scene key or address to load.</param>
        /// <param name="spawnPlayerOnSceneLoad">If true, will spawn player after loading.</param>
        /// <param name="mode">Scene load mode (Additive or Single).</param>
        /// <param name="progress">Optional progress reporter (0-1).</param>
        public static async Task LoadSceneAddressables(string sceneToLoad, bool spawnPlayerOnSceneLoad = true, LoadSceneMode mode = LoadSceneMode.Additive)
        {
            SetIfPlayerShouldSpawnOnSceneLoad(spawnPlayerOnSceneLoad);
            BasisDebug.Log("Loading Scene " + sceneToLoad, BasisDebug.LogTag.Event);
            string UUID = BasisGenerateUniqueID.GenerateUniqueID();
            AsyncOperationHandle<SceneInstance> handle = Addressables.LoadSceneAsync(sceneToLoad, mode, activateOnLoad: true, priority: 100, SceneReleaseMode.ReleaseSceneWhenSceneUnloaded);

            progressCallback.ReportProgress(UUID, 0f, $"Preparing scene {sceneToLoad}");
            try
            {
                while (!handle.IsDone)
                {
                    DownloadStatus dl = handle.GetDownloadStatus();
                    float percent = System.Math.Min(handle.PercentComplete * 100f, 99f);
                    string stage = (dl.TotalBytes > 0 && dl.DownloadedBytes < dl.TotalBytes)
                        ? $"Downloading {sceneToLoad}: {FormatBytes(dl.DownloadedBytes)} / {FormatBytes(dl.TotalBytes)}"
                        : $"Activating scene {sceneToLoad}";
                    progressCallback.ReportProgress(UUID, percent, stage);
                    await Task.Yield();
                }

                await handle.Task;
            }
            finally
            {
                progressCallback.ReportProgress(UUID, 100f, $"Loaded scene {sceneToLoad}");
            }
            BasisDebug.Log($"Loaded Scene {sceneToLoad}", BasisDebug.LogTag.Event);
        }

        private static string FormatBytes(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;
            if (bytes >= GB) return $"{bytes / (double)GB:0.00} GB";
            if (bytes >= MB) return $"{bytes / (double)MB:0.0} MB";
            if (bytes >= KB) return $"{bytes / (double)KB:0.0} KB";
            return $"{bytes} B";
        }


        /// <summary>
        /// remote but can be used local.
        /// </summary>
        /// <returns></returns>
        public static async Task<Scene> LoadSceneAssetBundle(BasisLoadableBundle BasisLoadableBundle, bool SpawnPlayerOnSceneLoad = true, bool MakeSceneActiveScene = true)
        {
            SetIfPlayerShouldSpawnOnSceneLoad(SpawnPlayerOnSceneLoad);
            BasisDebug.Log("Loading Scene ", BasisDebug.LogTag.Scene);
            Scene Scene = await BasisLoadHandler.LoadSceneBundle(MakeSceneActiveScene, BasisLoadableBundle, progressCallback, new CancellationToken());
            BasisDebug.Log("Loaded Scene ", BasisDebug.LogTag.Scene);
            return Scene;
        }
        /// <summary>
        /// turning this off for loading in additional levels is recommended. :) 
        /// so first run its on after that off. unless your handling it yourself.
        /// </summary>
        public static void SetIfPlayerShouldSpawnOnSceneLoad(bool SpawnPlayerOnSceneLoad)
        {
            BasisLocalPlayer.SpawnPlayerOnSceneLoad = SpawnPlayerOnSceneLoad;
        }
    }
}
