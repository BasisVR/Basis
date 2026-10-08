using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Drivers;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
namespace Basis.Scripts.Networking.Receivers
{
    [AutoStaticsCleanup]
    public static partial class BasisAudioRemoteSource
    {
        public const string AudioSourcePath = "Packages/com.basis.sdk/Prefabs/Players/AudioSource.prefab";
        private static GameObject LoadableAudioSource;
        public static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> Loadable;

        private static readonly Stack<GameObject> pool = new Stack<GameObject>();

        /// <summary>
        /// How many voice objects to keep parked for reuse. Sized for the audible crowd, not for
        /// a handful of speakers: a miss is dramatically more expensive than a hit, and in a busy
        /// instance the in/out-of-hearing-range set churns continuously.
        /// </summary>
        /// <remarks>
        /// A hit is <c>SetParent</c> + <c>SetActive</c>. A miss Instantiates the prefab, which
        /// runs Steam Audio's native source creation and — because <c>UnityAudioEngineSource</c>
        /// wipes its parameter cache to NaN on Initialize — pushes all 31 spatializer parameters,
        /// each one a DSP graph mutation. A pooled object keeps its audio engine source alive
        /// across the disable (only OnDestroy tears it down) and its parameter hash unchanged, so
        /// reuse pushes none of them. At 16 this pool was smaller than a single join burst, so
        /// every start paid the miss.
        /// </remarks>
        public static int MaxPoolSize = 64;
        private static Transform poolRoot;

        public static int PoolCount => pool.Count;

        public static void Initialize()
        {
            Loadable = Addressables.LoadAssetAsync<GameObject>(AudioSourcePath);
            if (Loadable.IsValid() == false)
            {
                BasisDebug.LogError("Can't Find Audio Source!");
                return;
            }
            LoadableAudioSource = Loadable.WaitForCompletion();
            if (LoadableAudioSource.TryGetComponent<AudioSource>(out AudioSource v))
            {
            }
            else
            {
                BasisDebug.LogError("Loaded Audio Source does not have a audio source!");
            }

            if (poolRoot == null)
            {
                var rootGo = new GameObject("[AudioSource Pool]");
                rootGo.SetActive(false);
                Object.DontDestroyOnLoad(rootGo);
                poolRoot = rootGo.transform;
            }
        }
        public static void DeInitialize()
        {
            Clear();

            if (poolRoot != null)
            {
                Object.Destroy(poolRoot.gameObject);
                poolRoot = null;
            }

            if (Loadable.IsValid())
            {
                Loadable.Release();
            }
        }
        public static GameObject RequestAudio(Transform Parent)
        {
            if (pool.Count > 0)
            {
                GameObject obj = pool.Pop();
                obj.transform.SetParent(Parent, false);
                obj.SetActive(true);
                return obj;
            }

            return GameObject.Instantiate(LoadableAudioSource, Parent);
        }
        public static void Return(GameObject obj)
        {
            if (obj == null) return;

            // Clean up the audio driver so it removes itself from the static Drivers list
            if (obj.TryGetComponent<BasisRemoteAudioDriver>(out var driver))
            {
                driver.ResetForPool();
            }

            // Reset AudioSource state so pooled objects don't hold stale references
            if (obj.TryGetComponent<AudioSource>(out var audioSource))
            {
                audioSource.Stop();
                audioSource.clip = null;
                audioSource.enabled = true;
            }

            if (pool.Count < MaxPoolSize && poolRoot != null)
            {
                obj.SetActive(false);
                obj.transform.SetParent(poolRoot, false);
                pool.Push(obj);
            }
            else
            {
                GameObject.Destroy(obj);
            }
        }
        public static void Clear()
        {
            while (pool.Count > 0)
            {
                var obj = pool.Pop();
                if (obj != null)
                {
                    GameObject.Destroy(obj);
                }
            }
        }
    }
}
