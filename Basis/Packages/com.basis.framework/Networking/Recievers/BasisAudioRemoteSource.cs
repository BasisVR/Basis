using Basis.Scripts.Drivers;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
namespace Basis.Scripts.Networking.Receivers
{
    public static class BasisAudioRemoteSource
    {
        public const string AudioSourcePath = "Packages/com.basis.sdk/Prefabs/Players/AudioSource.prefab";
        private static GameObject LoadableAudioSource;
        public static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> Loadable;

        private static readonly Stack<GameObject> pool = new Stack<GameObject>();
        private const int MaxPoolSize = 16;
        private static Transform poolRoot;

        public static int PoolCount => pool.Count;

        public static void Initalize()
        {
            Loadable = Addressables.LoadAssetAsync<GameObject>(AudioSourcePath);
            if (Loadable.IsValid() == false)
            {
                BasisDebug.LogError("Cant Find Audio Source!");
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
        public static void DeInitalize()
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
