using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Basis.BasisUI
{
    /// <summary>
    /// Loads each UI prefab through Addressables once and instantiates plain clones from then on,
    /// instead of paying an Addressables handle + location lookup per element. Menu pages build
    /// dozens-to-hundreds of elements from a handful of prefab paths, so this removes almost all
    /// of the per-element load cost.
    /// <para>
    /// Instances created here are plain <see cref="UnityEngine.Object.Instantiate(UnityEngine.Object)"/>
    /// clones, which <see cref="Addressables.ReleaseInstance(GameObject)"/> does NOT destroy — release
    /// paths must call <see cref="DestroyInstance"/> instead or every released element leaks.
    /// </para>
    /// </summary>
    public static class BasisAddressablePrefabCache
    {
        private static readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, AsyncOperationHandle<GameObject>> Handles = new Dictionary<string, AsyncOperationHandle<GameObject>>();

        private static Func<string, GameObject> _loaderOverride;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnDomainReload()
        {
            Clear();
            _loaderOverride = null;
        }

        public static GameObject GetPrefab(string referencePath)
        {
            if (string.IsNullOrEmpty(referencePath))
            {
                return null;
            }

            if (Prefabs.TryGetValue(referencePath, out GameObject prefab) && prefab)
            {
                return prefab;
            }

            if (_loaderOverride != null)
            {
                prefab = _loaderOverride(referencePath);
                if (prefab == null)
                {
                    BasisDebug.LogError($"Failed to load Addressable at path:\n{referencePath}");
                    return null;
                }
                Prefabs[referencePath] = prefab;
                return prefab;
            }

            if (Handles.TryGetValue(referencePath, out AsyncOperationHandle<GameObject> stale))
            {
                if (stale.IsValid())
                {
                    Addressables.Release(stale);
                }
                Handles.Remove(referencePath);
            }

            AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(referencePath);
            prefab = handle.WaitForCompletion();
            if (prefab == null)
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
                BasisDebug.LogError($"Failed to load Addressable at path:\n{referencePath}");
                return null;
            }

            Prefabs[referencePath] = prefab;
            Handles[referencePath] = handle;
            return prefab;
        }

        public static GameObject Instantiate(string referencePath, Transform parent)
        {
            GameObject prefab = GetPrefab(referencePath);
            if (prefab == null)
            {
                return null;
            }

            return parent
                ? UnityEngine.Object.Instantiate(prefab, parent, false)
                : UnityEngine.Object.Instantiate(prefab);
        }

        public static void DestroyInstance(GameObject instance)
        {
            if (!instance)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(instance);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        public static void Clear()
        {
            foreach (AsyncOperationHandle<GameObject> handle in Handles.Values)
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
            }
            Handles.Clear();
            Prefabs.Clear();
        }

        public static void ResetForTests(Func<string, GameObject> loader)
        {
            Clear();
            _loaderOverride = loader;
        }
    }
}
