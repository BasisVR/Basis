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
    /// <summary>
    /// Central bootstrapping entry point for the Basis runtime.
    /// Initializes Addressables, spawns the framework GameObject, and (optionally) starts Unity Services/Analytics.
    /// Handles cleanup both on app quit and when leaving Play Mode in the Editor.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public static class BootSequence
    {
        /// <summary>
        /// Reference to the loaded boot manager instance (framework root) if created.
        /// </summary>
        public static GameObject LoadedBootManager;

        /// <summary>
        /// Addressables key (or label) for the framework prefab to instantiate.
        /// </summary>
        public static string BasisFramework = "BasisFramework";

        /// <summary>
        /// Guard to ensure we only hook global events once.
        /// </summary>
        public static bool HasEvents = false;

        /// <summary>
        /// If true, will instantiate the framework after Addressables initialization completes.
        /// </summary>
        public static bool WillBoot = true;

        /// <summary>
        /// If true, attempts to initialize Unity Services and enable Analytics.
        /// </summary>
        public static bool GrabUnityAnalytics = true;

        // Keep handles/refs so we can release them properly.
        /// <summary>
        /// Handle for Addressables.InitializeAsync() to release at teardown.
        /// </summary>
        private static AsyncOperationHandle<IResourceLocator> _addressablesInitHandle;

        /// <summary>
        /// Reference to the instantiated framework GameObject so we can ReleaseInstance().
        /// </summary>
        private static GameObject _basisInstance;

        /// <summary>
        /// Unity runtime hook invoked after a scene has loaded.
        /// Initializes Addressables and, if enabled, Unity Services/Analytics.
        /// Also wires up application/editor teardown callbacks.
        /// </summary>
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

        /// <summary>
        /// Event bridge for Addressables initialization completion.
        /// Forwards to the async boot method and logs exceptions.
        /// </summary>
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

        /// <summary>
        /// Performs the actual boot work after Addressables are ready:
        /// loads and instantiates the framework root via <see cref="AddressableResourceProcess.LoadSystemGameobject"/>.
        /// </summary>
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
        /// <summary>
        /// Editor-only hook to release resources when exiting Play Mode back to the Editor.
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // We only want to release when leaving Play Mode back to the Editor.
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                SafeReleaseAll();
            }
        }
#endif

        /// <summary>
        /// Application quit hook to release instantiated instances and Addressables handles safely.
        /// </summary>
        private static void OnApplicationQuitting()
        {
            SafeReleaseAll();
        }

        /// <summary>
        /// Releases the instantiated framework instance (if any) and the Addressables init handle.
        /// Swallows and logs exceptions to avoid teardown crashes.
        /// </summary>
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
