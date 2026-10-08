using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.Util;

namespace Basis.Scripts.Platform
{
    [NoAutoStaticsCleanup]
    public static class BasisWebAddressables
    {
        private const string RuntimePathToken = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}";
        private static readonly string[] SkippedBundles = { "mediapipemodels", "openlipsync" };
        private static readonly List<AsyncOperationHandle> HeldBundles = new List<AsyncOperationHandle>();
        private static Task _ready = Task.CompletedTask;

        public static bool IsReady => _ready == null || _ready.IsCompleted;

        public static Task WhenReady()
        {
            return _ready ?? Task.CompletedTask;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Install()
        {
            Addressables.ResourceManager.ResourceProviders.Add(new BasisSyncBundledAssetProvider());
            _ready = PreloadLocalBundles();
        }
#endif

        private static async Task PreloadLocalBundles()
        {
            try
            {
                await Addressables.InitializeAsync().Task;
                string localRoot = Addressables.RuntimePath;
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                List<Task> loads = new List<Task>();
                foreach (IResourceLocator locator in Addressables.ResourceLocators)
                {
                    foreach (IResourceLocation location in locator.AllLocations)
                    {
                        if (location.ResourceType != typeof(IAssetBundleResource) || !(location.InternalId.StartsWith(localRoot, StringComparison.Ordinal) || location.InternalId.StartsWith(RuntimePathToken, StringComparison.Ordinal)) || !seen.Add(location.InternalId) || IsSkipped(location.InternalId)) continue;
                        AsyncOperationHandle<IAssetBundleResource> handle = Addressables.ResourceManager.ProvideResource<IAssetBundleResource>(location);
                        HeldBundles.Add(handle);
                        loads.Add(handle.Task);
                    }
                }
                await Task.WhenAll(loads);
                BasisDebug.Log($"[BasisWebAddressables] Preloaded {loads.Count} local bundles.");
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"[BasisWebAddressables] Preloading local bundles failed: {e}");
            }
        }

        private static bool IsSkipped(string internalId)
        {
            for (int index = 0; index < SkippedBundles.Length; index++)
            {
                if (internalId.IndexOf(SkippedBundles[index], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }

    public sealed class BasisSyncBundledAssetProvider : BundledAssetProvider
    {
        private static readonly string BundledAssetProviderId = typeof(BundledAssetProvider).FullName;

        public override string ProviderId => BundledAssetProviderId;

        public override void Provide(ProvideHandle provideHandle)
        {
            List<object> dependencies = new List<object>();
            provideHandle.GetDependencies(dependencies);
            AssetBundle bundle = null;
            bool first = true;
            for (int index = 0; index < dependencies.Count; index++)
            {
                if (dependencies[index] is not IAssetBundleResource resource) continue;
                AssetBundle loaded = resource.GetAssetBundle();
                if (first) bundle = loaded;
                first = false;
            }
            if (bundle == null)
            {
                base.Provide(provideHandle);
                return;
            }
            Type type = provideHandle.Type;
            string path = provideHandle.ResourceManager.TransformInternalId(provideHandle.Location);
            object result = null;
            if (type.IsArray)
            {
                result = ResourceManagerConfig.CreateArrayResult(type, bundle.LoadAssetWithSubAssets(path, type.GetElementType()));
            }
            else if (type.IsGenericType && typeof(IList<>) == type.GetGenericTypeDefinition())
            {
                result = ResourceManagerConfig.CreateListResult(type, bundle.LoadAssetWithSubAssets(path, type.GetGenericArguments()[0]));
            }
            else if (ResourceManagerConfig.ExtractKeyAndSubKey(path, out string mainPath, out string subKey))
            {
                UnityEngine.Object[] assets = bundle.LoadAssetWithSubAssets(mainPath, type);
                for (int index = 0; index < assets.Length; index++)
                {
                    if (assets[index].name == subKey && type.IsAssignableFrom(assets[index].GetType()))
                    {
                        result = assets[index];
                        break;
                    }
                }
            }
            else
            {
                UnityEngine.Object asset = bundle.LoadAsset(path, type);
                if (asset != null && type.IsAssignableFrom(asset.GetType())) result = asset;
            }
            provideHandle.Complete(result, result != null, result == null ? new Exception($"Unable to load asset of type {type} from location {provideHandle.Location}.") : null);
        }
    }
}
