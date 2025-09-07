using Basis.Scripts.Addressable_Driver.Resource;
using System;
using System.Threading.Tasks;
using Unity.Services.Analytics;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Basis.Scripts.Boot_Sequence
{
    [DefaultExecutionOrder(-50)]
    public static class BootSequence
    {
        public static GameObject LoadedBootManager;
        public static string BasisFramework = "BasisFramework";
        public static bool HasEvents = false;
        public static bool WillBoot = true;
        public static bool GrabUnityAnalytics = true;

        // Keep handles/refs so we can release them properly.
        private static AsyncOperationHandle<IResourceLocator> _addressablesInitHandle;
        private static GameObject _basisInstance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static async Task OnAfterSceneLoadRuntimeMethod()
        {
            // Subscribe once to teardown hooks.
            if (!HasEvents)
            {
                HasEvents = true;
                Application.quitting += OnApplicationQuitting;

#if UNITY_EDITOR
                // Release Addressables when leaving Play Mode.
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
            }

            // Initialize Addressables and optionally boot.
            _addressablesInitHandle = Addressables.InitializeAsync(false);
            if (WillBoot)
            {
                _addressablesInitHandle.Completed += OnAddressablesInitializationComplete;
            }

            // Unity Services / Analytics (optional).
            if (GrabUnityAnalytics)
            {
                try
                {
                    await UnityServices.InitializeAsync();
#pragma warning disable CS0618 // Type or member is obsolete
                    AnalyticsService.Instance.StartDataCollection();
#pragma warning restore CS0618
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BootSequence] Unity Services init/analytics failed: {e.Message}");
                }
            }
        }

        // Completed callback (event) -> bridge to Task method
        private static async void OnAddressablesInitializationComplete(AsyncOperationHandle<IResourceLocator> _)
        {
            try
            {
                await OnAddressablesInitializationComplete();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // Actual boot work after Addressables are ready.
        public static async Task OnAddressablesInitializationComplete()
        {
            // Instantiate the system GameObject via your driver. Keep a reference so we can ReleaseInstance later.
            var go = await AddressableResourceProcess.LoadSystemGameobject(
                BasisFramework,
                new UnityEngine.ResourceManagement.ResourceProviders.InstantiationParameters());

            if (go != null)
            {
                go.name = BasisFramework;
                _basisInstance = go;
                LoadedBootManager = go;
            }
            else
            {
                Debug.LogWarning("[BootSequence] AddressableResourceProcess returned null instance.");
            }
        }

        // === Cleanup paths ===

#if UNITY_EDITOR
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // We only want to release when leaving Play Mode back to the Editor.
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                SafeReleaseAll();
            }
        }
#endif

        private static void OnApplicationQuitting()
        {
            SafeReleaseAll();
        }

        private static void SafeReleaseAll()
        {
            // Release the instantiated Addressable instance if we created one.
            if (_basisInstance != null)
            {
                try
                {
                    Addressables.ReleaseInstance(_basisInstance);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BootSequence] ReleaseInstance failed: {e.Message}");
                }
                _basisInstance = null;
                LoadedBootManager = null;
            }

            // Release the Addressables initialization handle if valid.
            if (_addressablesInitHandle.IsValid())
            {
                try
                {
                    Addressables.Release(_addressablesInitHandle);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BootSequence] Release(init handle) failed: {e.Message}");
                }
                _addressablesInitHandle = default;
            }
        }
    }
}
