using Basis.BasisUI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.XR.Management;

namespace Basis.Scripts.Device_Management.Devices
{
    /// <summary>
    /// Thin wrapper over Unity XR Management (XR Plug-in Management).
    /// Handles loader initialization/start/stop and integrates with
    /// <see cref="BasisDeviceManagement"/> for device boot orchestration.
    /// </summary>
    [System.Serializable]
    public class BasisXRManagement
    {
        /// <summary>
        /// Cached XR manager settings (from <see cref="XRGeneralSettings.Manager"/>).
        /// </summary>
        public XRManagerSettings xRManagerSettings;

        /// <summary>
        /// Cached global XR settings (<see cref="XRGeneralSettings.Instance"/>).
        /// </summary>
        public XRGeneralSettings xRGeneralSettings;

        /// <summary>
        /// Boot modes that should attempt XR loader initialization.
        /// </summary>
        public string[] ActiveOnModes = new string[] { BasisConstants.OpenVRLoader, BasisConstants.OpenXRLoader };

        /// <summary>
        /// Name of the loader that is currently initialized, or empty when no XR runtime is up.
        /// This stays set across a soft swap to Desktop, because the runtime is still alive then —
        /// which is what makes it the right signal for "can the other VR mode still be entered".
        /// </summary>
        public string ActiveLoaderName = string.Empty;

        /// <summary>
        /// Mode handed to the in-flight <see cref="TryBeginLoad"/>, so <see cref="LoadXR"/> can say
        /// what was asked for when reporting what actually started.
        /// </summary>
        private string RequestedMode = string.Empty;

        /// <summary>
        /// Attempts to begin XR loading if the provided boot <paramref name="Mode"/> is in <see cref="ActiveOnModes"/>.
        /// Kicks off a coroutine to initialize and start subsystems.
        /// </summary>
        /// <param name="Mode">Boot mode string (e.g., OpenXR/OpenVR/Desktop).</param>
        /// <returns>True if an XR load attempt was started; otherwise false.</returns>
        public bool TryBeginLoad(string Mode)
        {
            if (ActiveOnModes.Contains(Mode))
            {
                if (isLoading)
                {
                    BasisDebug.LogWarning($"XR load already in progress, ignoring request for {Mode}", BasisDebug.LogTag.Device);
                    return true;
                }
                isLoading = true;
                RequestedMode = Mode;
                BasisDebug.Log($"Starting Attempt of load LoadXR {Mode}", BasisDebug.LogTag.Device);

                if (!TryOrderLoadersFor(Mode))
                {
                    isLoading = false;
                    string noPlugin = BasisLocalization.Get("settings.platform.vrFailed.noPlugin");
                    StartDevice(BasisConstants.Desktop,
                        () => BasisXRRuntimeNotice.ReportFallback(Mode, BasisConstants.Desktop, noPlugin));
                    return true;
                }

                BasisDeviceManagement.Instance.StartCoroutine(LoadXR());
                return true;
            }
            return false;
        }

        /// <summary>
        /// Puts the requested loader at the front of the manager's list and leaves the remaining
        /// loaders behind it instead of removing them. Unity's <see cref="XRManagerSettings.InitializeLoader"/>
        /// walks the list and keeps the first loader that initializes, so ordering — rather than
        /// trimming — is what gives a runtime that isn't installed somewhere to hand off to.
        /// <para>
        /// Trimming was also one-way: the removed loaders were never put back while the app ran,
        /// so a second VR attempt found an empty list, initialized nothing, and dropped to Desktop
        /// with no explanation. Rewriting the whole list each time undoes any previous attempt.
        /// </para>
        /// Safe against the loader-list mutation race because the <see cref="isLoading"/> guard
        /// means no <see cref="XRManagerSettings.InitializeLoader"/> is enumerating the list here.
        /// </summary>
        /// <returns>False when this build has no loader matching <paramref name="Mode"/>.</returns>
        private bool TryOrderLoadersFor(string Mode)
        {
            if (xRManagerSettings == null || AvaliableLoaders == null)
            {
                return false;
            }

            List<XRLoader> ordered = new List<XRLoader>(AvaliableLoaders.Count);
            foreach (XRLoader loader in AvaliableLoaders)
            {
                if (loader != null && loader.name == Mode)
                {
                    ordered.Add(loader);
                }
            }

            if (ordered.Count == 0)
            {
                BasisDebug.LogError($"No XR loader named {Mode} is present in this build", BasisDebug.LogTag.Device);
                return false;
            }

            foreach (XRLoader loader in AvaliableLoaders)
            {
                if (loader != null && loader.name != Mode)
                {
                    ordered.Add(loader);
                }
            }

            if (!xRManagerSettings.TrySetLoaders(ordered))
            {
                BasisDebug.LogError($"Unable to order XR loaders for {Mode}; leaving the existing list in place", BasisDebug.LogTag.Device);
            }
            return true;
        }

        /// <summary>
        /// Coroutine that initializes the XR loader and starts subsystems if available.
        /// On success, calls <see cref="StartDevice(string)"/> with the active loader name;
        /// on failure, falls back to <c>Desktop</c>.
        /// </summary>
        public IEnumerator LoadXR()
        {
            string requested = RequestedMode;
            string result = BasisConstants.Desktop;
            bool noRuntime = false;

            try
            {
                // Initialize the XR loader
                IEnumerator initialize = xRManagerSettings.InitializeLoader();
                while (true)
                {
                    try
                    {
                        if (!initialize.MoveNext())
                        {
                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        BasisDebug.LogError($"XR loader failed to initialize for {requested}: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Device);
                        break;
                    }
                    yield return initialize.Current;
                }

                // Check the result
                if (xRManagerSettings.activeLoader != null)
                {
                    xRManagerSettings.StartSubsystems();
                    result = xRManagerSettings.activeLoader?.name;
                    ActiveLoaderName = result;
                }
                else
                {
                    BasisDebug.LogErrorUnreported("No Active Loader Present! falling back to desktop!");
                    ActiveLoaderName = string.Empty;
                    result = BasisConstants.Desktop;
                    noRuntime = true;
                }
            }
            finally
            {
                isLoading = false;
            }

            BasisDebug.Log($"Found Loader {result}", BasisDebug.LogTag.Device);

            // The user asked for one runtime and is getting another (or none). Say so — but only
            // once the devices for the mode we landed in exist, since the notice is a menu dialogue
            // and there would be nothing to click it with until desktop input has been created.
            Action report = null;
            if (noRuntime)
            {
                string noRuntimeReason = BasisLocalization.Get("settings.platform.vrFailed.noRuntime");
                report = () => BasisXRRuntimeNotice.ReportFallback(requested, result, noRuntimeReason);
            }
            else if (!string.Equals(result, requested, StringComparison.Ordinal))
            {
                report = () => BasisXRRuntimeNotice.ReportSubstituted(requested, result);
            }

            StartDevice(result, report);
        }

        /// <summary>
        /// Bridges the resolved loader/mode to <see cref="BasisDeviceManagement.StartDevices(string)"/>.
        /// </summary>
        /// <param name="result">Resolved loader name or <c>Desktop</c> fallback.</param>
        /// <param name="afterStart">Optional work to run once the mode's devices are up.</param>
        public async void StartDevice(string result, Action afterStart = null)
        {
            await BasisDeviceManagement.Instance.StartDevices(result);
            afterStart?.Invoke();
            if (BasisDeviceManagement.OnInitializationComplete)
            {
                BasisDeviceManagement.Instance.ReconcileAutoSwapWithPresence();
            }
        }

        /// <summary>
        /// Stops and deinitializes the current XR loader if initialization had completed.
        /// </summary>
        public void StopXR()
        {
            if (xRManagerSettings != null)
            {
                if (xRManagerSettings.isInitializationComplete)
                {
                    xRManagerSettings.DeinitializeLoader();
                }
            }
            ActiveLoaderName = string.Empty;
        }
        public List<XRLoader> AvaliableLoaders;

        private bool isLoading;
        public bool IsLoading => isLoading;

        public void Initialize()
        {
            // Serialized alongside the rest of this object, so clear it rather than trusting
            // whatever the scene asset carries — a stale name here would grey out the other VR
            // mode on a fresh boot with no runtime actually running.
            ActiveLoaderName = string.Empty;

            if (XRGeneralSettings.Instance != null)
            {
                xRGeneralSettings = XRGeneralSettings.Instance;
                if (xRGeneralSettings.Manager != null)
                {
                    xRManagerSettings = xRGeneralSettings.Manager;
                    AvaliableLoaders = xRManagerSettings.activeLoaders.ToList();
                }
            }
        }
        public void DeInitialize()
        {
#if UNITY_EDITOR
            xRManagerSettings.TrySetLoaders(AvaliableLoaders);
#endif
        }
    }
}
