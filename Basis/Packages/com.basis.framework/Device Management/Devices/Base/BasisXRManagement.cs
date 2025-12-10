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
        /// Attempts to begin XR loading if the provided boot <paramref name="Mode"/> is in <see cref="ActiveOnModes"/>.
        /// Kicks off a coroutine to initialize and start subsystems.
        /// </summary>
        /// <param name="Mode">Boot mode string (e.g., OpenXR/OpenVR/Desktop).</param>
        /// <returns>True if an XR load attempt was started; otherwise false.</returns>
        public bool TryBeginLoad(string Mode)
        {
            if (ActiveOnModes.Contains(Mode))
            {
                BasisDebug.Log($"Starting Attempt of load LoadXR {Mode}", BasisDebug.LogTag.Device);
                List<XRLoader> Loaders = AvaliableLoaders;

                foreach (XRLoader loader in Loaders)
                {
                    if (loader == null)
                    {
                        continue;
                    }
                    if (loader.name != Mode)
                    {
                        xRManagerSettings.TryRemoveLoader(loader);
                    }
                }
                BasisDeviceManagement.Instance.StartCoroutine(LoadXR());
                return true;
            }
            return false;
        }

        /// <summary>
        /// Coroutine that initializes the XR loader and starts subsystems if available.
        /// On success, calls <see cref="StartDevice(string)"/> with the active loader name;
        /// on failure, falls back to <c>Desktop</c>.
        /// </summary>
        public IEnumerator LoadXR()
        {
            // Initialize the XR loader
            yield return xRManagerSettings.InitializeLoader();

            string result = BasisConstants.Desktop;

            // Check the result
            if (xRManagerSettings.activeLoader != null)
            {
                xRManagerSettings.StartSubsystems();
                result = xRManagerSettings.activeLoader?.name;
            }
            else
            {
                BasisDebug.LogError("No Active Loader Present! falling back to desktop!");
                result = BasisConstants.Desktop;
            }

            BasisDebug.Log($"Found Loader {result}", BasisDebug.LogTag.Device);
            StartDevice(result);
        }

        /// <summary>
        /// Bridges the resolved loader/mode to <see cref="BasisDeviceManagement.StartDevices(string)"/>.
        /// </summary>
        /// <param name="result">Resolved loader name or <c>Desktop</c> fallback.</param>
        public async void StartDevice(string result)
        {
            await BasisDeviceManagement.Instance.StartDevices(result);
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
        }
        public List<XRLoader> AvaliableLoaders;
        public void Initalize()
        {
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
        public void DeInitalize()
        {
#if UNITY_EDITOR
            xRManagerSettings.TrySetLoaders(AvaliableLoaders);
#endif
        }
    }
}
