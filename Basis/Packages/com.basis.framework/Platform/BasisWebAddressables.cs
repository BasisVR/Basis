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
        private const int SettleFrames = 2;
        private static readonly string[] SkippedBundles = { "mediapipemodels", "openlipsync" };
        private static readonly List<AsyncOperationHandle<IAssetBundleResource>> HeldBundles = new List<AsyncOperationHandle<IAssetBundleResource>>();
        private static TaskCompletionSource<bool> _readySource;
        private static int _pendingBundles;
        private static int _failedBundles;

        public static bool IsReady => _readySource == null || _readySource.Task.IsCompleted;

        public static Task WhenReady()
        {
            return _readySource == null ? Task.CompletedTask : _readySource.Task;
        }

        public static AsyncOperationHandle<IList<T>> LoadPreloadedAssetsNow<T>(object key)
        {
            List<T> assets = new List<T>();
            IList<IResourceLocation> found;
            AsyncOperationHandle<IList<IResourceLocation>> locations = Addressables.LoadResourceLocationsAsync(key, typeof(T));
            try
            {
                found = locations.WaitForCompletion();
            }
            catch (Exception e)
            {
                Addressables.Release(locations);
                return Addressables.ResourceManager.CreateCompletedOperation<IList<T>>(assets, $"Locations for {key} are not available yet: {e.Message}");
            }
            if (found != null)
            {
                for (int index = 0; index < found.Count; index++)
                {
                    AsyncOperationHandle<T> load = Addressables.LoadAssetAsync<T>(found[index]);
                    try
                    {
                        T asset = load.WaitForCompletion();
                        if (asset != null) assets.Add(asset);
                    }
                    catch (Exception e)
                    {
                        BasisDebug.LogError($"[BasisWebAddressables] {found[index].PrimaryKey} is not preloaded: {e.Message}");
                    }
                    Addressables.Release(load);
                }
            }
            Addressables.Release(locations);
            return Addressables.ResourceManager.CreateCompletedOperation<IList<T>>(assets, string.Empty);
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Install()
        {
            _readySource = new TaskCompletionSource<bool>();
            try
            {
                Addressables.ResourceManager.ResourceProviders.Add(new BasisSyncBundledAssetProvider());
                Addressables.InitializeAsync().Completed += OnInitialized;
            }
            catch (Exception e)
            {
                Finish($"Addressables could not start: {e}", true);
            }
        }
#endif

        private static void OnInitialized(AsyncOperationHandle<IResourceLocator> initialization)
        {
            try
            {
                if (initialization.Status != AsyncOperationStatus.Succeeded)
                {
                    Finish($"Addressables failed to initialize: {initialization.OperationException}", true);
                    return;
                }
                string localRoot = Addressables.RuntimePath;
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                List<IResourceLocation> bundles = new List<IResourceLocation>();
                foreach (IResourceLocator locator in Addressables.ResourceLocators)
                {
                    foreach (IResourceLocation location in locator.AllLocations)
                    {
                        if (location.ResourceType != typeof(IAssetBundleResource) || !IsLocal(location.InternalId, localRoot) || IsSkipped(location.InternalId) || !seen.Add(location.InternalId)) continue;
                        bundles.Add(location);
                    }
                }
                _pendingBundles = bundles.Count + 1;
                for (int index = 0; index < bundles.Count; index++)
                {
                    AsyncOperationHandle<IAssetBundleResource> handle = Addressables.ResourceManager.ProvideResource<IAssetBundleResource>(bundles[index]);
                    HeldBundles.Add(handle);
                    handle.Completed += OnBundleLoaded;
                }
                CountDown();
            }
            catch (Exception e)
            {
                Finish($"Preloading local bundles failed: {e}", true);
            }
        }

        private static void OnBundleLoaded(AsyncOperationHandle<IAssetBundleResource> handle)
        {
            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                _failedBundles++;
                BasisDebug.LogError($"[BasisWebAddressables] Could not preload {handle.DebugName}: {handle.OperationException}");
            }
            CountDown();
        }

        private static void CountDown()
        {
            if (--_pendingBundles == 0) Finish($"Preloaded {HeldBundles.Count - _failedBundles} of {HeldBundles.Count} local bundles.", _failedBundles > 0);
        }

        private static async void Finish(string message, bool failed)
        {
            if (failed) BasisDebug.LogError($"[BasisWebAddressables] {message}");
            else BasisDebug.Log($"[BasisWebAddressables] {message}");
            for (int frame = 0; frame < SettleFrames; frame++)
            {
                await Awaitable.NextFrameAsync();
            }
            _readySource?.TrySetResult(true);
        }

        private static bool IsLocal(string internalId, string localRoot)
        {
            return internalId.StartsWith(localRoot, StringComparison.Ordinal) || internalId.StartsWith(RuntimePathToken, StringComparison.Ordinal);
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
