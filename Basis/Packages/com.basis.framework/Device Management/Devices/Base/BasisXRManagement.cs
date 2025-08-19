using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.Management;

namespace Basis.Scripts.Device_Management.Devices
{
    [System.Serializable]
    public class BasisXRManagement
    {
        public XRManagerSettings xRManagerSettings;
        public XRGeneralSettings xRGeneralSettings;
        public string[] ActiveOnModes = new string[] { BasisConstants.OpenVRLoader, BasisConstants.OpenXRLoader };
        // Store the initial list of loaders
        [SerializeField]
        public List<XRLoader> initialLoaders = new List<XRLoader>();
        public void ReInitalizeCheck()
        {
            if (XRGeneralSettings.Instance != null)
            {
                xRGeneralSettings = XRGeneralSettings.Instance;
                if (xRGeneralSettings.Manager != null)
                {
                    xRManagerSettings = xRGeneralSettings.Manager;
                }
            }
        }
        public bool TryBeginLoad(string Mode)
        {
            if (ActiveOnModes.Contains(Mode))
            {
                BasisDebug.Log("Starting LoadXR", BasisDebug.LogTag.Device);
                // BasisDebug.Log("Begin Load of XR");
                ReInitalizeCheck();
                BasisDeviceManagement.Instance.StartCoroutine(LoadXR());
                return true;
            }
            return false;
        }
        public void DisableDeviceManagerSolution(string BasisBootedMode)
        {
            ReInitalizeCheck();
            IReadOnlyList<XRLoader> Loaders = xRManagerSettings.activeLoaders;
            foreach (XRLoader loader in Loaders)
            {
                if (loader?.name == BasisBootedMode.ToString())
                {
                    xRManagerSettings.TryRemoveLoader(loader);
                    return;
                }
            }
        }
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
            BasisDebug.Log($"Found Loader {result}", BasisDebug.LogTag.Device);
            BasisDebug.Log($"Loading {result}", BasisDebug.LogTag.Device);
            BasisDeviceManagement.Instance.StartDevices(result);
            BasisDeviceManagement.StaticCurrentMode = result;
        }
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
    }
}
