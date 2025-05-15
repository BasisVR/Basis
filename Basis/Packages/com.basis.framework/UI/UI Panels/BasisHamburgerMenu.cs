using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Enums;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.UI;

namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisHamburgerMenu : BasisUIBase
    {
        public Button Settings;
        public Button AvatarButton;
        public Button FullBody;
        public Button Respawn;
        public Button Camera;
        public static string MainMenuAddressableID = "MainMenu";
        public static BasisHamburgerMenu Instance;
        internal static GameObject activeCameraInstance;
        public bool OverrideForceCalibration;
        public override void InitalizeEvent()
        {
            Instance = this;
            Settings.onClick.AddListener(SettingsPanel);
            AvatarButton.onClick.AddListener(AvatarButtonPanel);
            FullBody.onClick.AddListener(PutIntoCalibrationMode);
            Respawn.onClick.AddListener(RespawnLocalPlayer);
            Camera.onClick.AddListener(() => OpenCamera(this));
            BasisCursorManagement.UnlockCursor(nameof(BasisHamburgerMenu));
            BasisUINeedsVisibleTrackers.Instance.Add(this);
        }
        private Dictionary<BasisInput, Action> TriggerDelegates = new Dictionary<BasisInput, Action>();
        public void RespawnLocalPlayer()
        {
            if (BasisLocalPlayer.Instance != null && BasisSceneFactory.Instance != null)
            {
                BasisSceneFactory.Instance.SpawnPlayer(BasisLocalPlayer.Instance);
            }
            BasisHamburgerMenu.Instance.CloseThisMenu();
        }
        public void PutIntoCalibrationMode()
        {
            BasisDebug.Log("Attempting" + nameof(PutIntoCalibrationMode));
            string BasisBootedMode = BasisDeviceManagement.Instance.CurrentMode;
            if (OverrideForceCalibration || BasisBootedMode == "OpenVRLoader" || BasisBootedMode == "OpenXRLoader")
            {
                BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();

                foreach (BasisInput BasisInput in BasisDeviceManagement.Instance.AllInputDevices)
                {
                    Action triggerDelegate = () => OnTriggerChanged(BasisInput);
                    TriggerDelegates[BasisInput] = triggerDelegate;
                    BasisInput.InputState.OnTriggerChanged += triggerDelegate;
                }
            }
        }

        public void OnTriggerChanged(BasisInput FiredOff)
        {
            if (FiredOff.InputState.Trigger >= 0.9f)
            {
                foreach (var entry in TriggerDelegates)
                {
                    entry.Key.InputState.OnTriggerChanged -= entry.Value;
                }
                TriggerDelegates.Clear();
                BasisAvatarIKStageCalibration.FullBodyCalibration();
            }
        }
        private static void AvatarButtonPanel()
        {
            BasisHamburgerMenu.Instance.CloseThisMenu();
            AddressableGenericResource resource = new AddressableGenericResource(BasisUIAvatarSelection.AvatarSelection, AddressableExpectedResult.SingleItem);
            BasisUISettings.OpenMenuNow(resource);
        }

        public static void SettingsPanel()
        {
            BasisHamburgerMenu.Instance.CloseThisMenu();
            AddressableGenericResource resource = new AddressableGenericResource(BasisUISettings.SettingsPanel, AddressableExpectedResult.SingleItem);
            BasisUISettings.OpenMenuNow(resource);
        }
        public static async Task OpenHamburgerMenu()
        {
            BasisUIManagement.CloseAllMenus();
            AddressableGenericResource resource = new AddressableGenericResource(MainMenuAddressableID, AddressableExpectedResult.SingleItem);
            await OpenThisMenu(resource);
        }
        public static void OpenHamburgerMenuNow()
        {
            BasisUIManagement.CloseAllMenus();
            AddressableGenericResource resource = new AddressableGenericResource(MainMenuAddressableID, AddressableExpectedResult.SingleItem);
            OpenMenuNow(resource);
        }
        public static async void OpenCamera(BasisHamburgerMenu menu)
        {
            // Destroy existing camera if any
            if (activeCameraInstance != null)
            {
                GameObject.Destroy(activeCameraInstance);
                activeCameraInstance = null;
            }

            // Get position/rotation for spawn
            menu.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            BasisUIManagement.CloseAllMenus();

            InstantiationParameters parameters = new InstantiationParameters(position, rotation, null);
            BasisHandHeldCamera cameraComponent = await BasisHandHeldCameraFactory.CreateCamera(parameters);
            activeCameraInstance = cameraComponent.gameObject;
        }

        public override void DestroyEvent()
        {
            BasisCursorManagement.LockCursor(nameof(BasisHamburgerMenu));
            BasisUINeedsVisibleTrackers.Instance.Remove(this);
        }
    }
}
