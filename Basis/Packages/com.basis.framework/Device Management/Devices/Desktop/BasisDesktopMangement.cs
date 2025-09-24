using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using System;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Desktop
{
    /// <summary>
    /// Provides device management logic for desktop-based usage of the Basis SDK.  
    /// Handles initialization and cleanup of eye input simulation when running without XR hardware.
    /// </summary>
    [Serializable]
    public class BasisDesktopManagement : BasisBaseTypeManagement
    {
        /// <summary>
        /// Reference to the <see cref="BasisAvatarEyeInput"/> component 
        /// created for simulating desktop eye tracking input.
        /// </summary>
        public BasisAvatarEyeInput BasisAvatarEyeInput;

        /// <summary>
        /// Identifier string for the desktop eye device.
        /// </summary>
        public const string DesktopEye = "Desktop Eye";

        /// <summary>
        /// Starts the Basis SDK for desktop mode.  
        /// If no <see cref="BasisAvatarEyeInput"/> exists, it creates one and attaches it
        /// under the <see cref="BasisLocalPlayer"/> object (if present).  
        /// Also locks the cursor for desktop interaction.
        /// </summary>
        public override void StartSDK()
        {
            if (BasisAvatarEyeInput == null)
            {
                BasisLocalCameraDriver.AllowXRRenderering(false);

                GameObject gameObject = new GameObject(DesktopEye);
                if (BasisLocalPlayer.Instance != null)
                {
                    gameObject.transform.parent = BasisLocalPlayer.Instance.transform;
                }

                BasisAvatarEyeInput = gameObject.AddComponent<BasisAvatarEyeInput>();
                BasisAvatarEyeInput.Initialize(DesktopEye, nameof(BasisDesktopManagement));
                BasisDeviceManagement.Instance.TryAdd(BasisAvatarEyeInput);
            }

            BasisCursorManagement.LockCursor(nameof(BasisAvatarEyeInput));
        }

        /// <summary>
        /// Stops the Basis SDK for desktop mode.  
        /// Removes the desktop eye input device from <see cref="BasisDeviceManagement"/> and destroys its component.
        /// </summary>
        public override void StopSDK()
        {
            BasisDeviceManagement.Instance.RemoveDevicesFrom(nameof(BasisDesktopManagement), DesktopEye);

            if (BasisAvatarEyeInput != null)
            {
                GameObject.Destroy(BasisAvatarEyeInput);
            }

            BasisAvatarEyeInput.Instance = null;
            BasisAvatarEyeInput = null;
        }

        /// <summary>
        /// Determines whether the desktop device can boot based on the provided request string.
        /// </summary>
        /// <param name="BootRequest">A string representing the requested boot device type.</param>
        /// <returns>
        /// <c>true</c> if the boot request matches <see cref="BasisConstants.Desktop"/>; otherwise, <c>false</c>.
        /// </returns>
        public override bool IsDeviceBootable(string BootRequest)
        {
            if (BootRequest == BasisConstants.Desktop)
            {
                return true;
            }
            return false;
        }
    }
}
