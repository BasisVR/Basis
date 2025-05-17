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
        public Button PersonalMirror;
        public static string MainMenuAddressableID = "MainMenu";
        public static BasisHamburgerMenu Instance;
        internal static GameObject activeCameraInstance;
        internal static GameObject personalMirrorInstance;
        public bool OverrideForceCalibration;
        public override void InitalizeEvent()
        {
            Instance = this;
            Settings.onClick.AddListener(SettingsPanel);
            AvatarButton.onClick.AddListener(AvatarButtonPanel);
            FullBody.onClick.AddListener(PutIntoCalibrationMode);
            Respawn.onClick.AddListener(RespawnLocalPlayer);
            Camera.onClick.AddListener(() => OpenCamera(this));
            PersonalMirror.onClick.AddListener(() => OpenPersonalMirror(this));
            BasisCursorManagement.UnlockCursor(nameof(BasisHamburgerMenu));
            BasisUINeedsVisibleTrackers.Instance.Add(this);
        }
        private Dictionary<BasisInput, Action> TriggerDelegates = new Dictionary<BasisInput, Action>();
        public void RespawnLocalPlayer()
        {
            if (BasisLocalPlayer.Instance != null)
            {
                BasisSceneFactory.SpawnPlayer(BasisLocalPlayer.Instance);
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
            if (activeCameraInstance != null)
            {
                GameObject.Destroy(activeCameraInstance);
                BasisDebug.Log("[OpenCamera] Destroyed previous camera instance.");
                activeCameraInstance = null;
            }
            else
            {
                BasisDebug.LogWarning("[OpenCamera] Tried to destroy camera, but none existed.");
            }

            menu.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            BasisUIManagement.CloseAllMenus();

            InstantiationParameters parameters = new InstantiationParameters(position, rotation, null);
            BasisHandHeldCamera cameraComponent = await BasisHandHeldCameraFactory.CreateCamera(parameters);
            activeCameraInstance = cameraComponent.gameObject;
        }
        public static async void OpenPersonalMirror(BasisHamburgerMenu menu)
        {
            if (personalMirrorInstance != null)
            {
                GameObject.Destroy(personalMirrorInstance);
                BasisDebug.Log("[OpenPersonalMirror] Destroyed previous mirror instance.");
                personalMirrorInstance = null;
            }
            else
            {
                BasisDebug.LogWarning("[OpenPersonalMirror] Tried to destroy mirror, but none existed.");
            }
            menu.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            BasisUIManagement.CloseAllMenus();

            InstantiationParameters parameters = new InstantiationParameters(position, rotation, null);
            BasisPersonalMirror mirrorComponent = await BasisPersonalMirrorFactory.CreateMirror(parameters);
            personalMirrorInstance = mirrorComponent.gameObject;
        }
        public override void DestroyEvent()
        {
            BasisCursorManagement.LockCursor(nameof(BasisHamburgerMenu));
            BasisUINeedsVisibleTrackers.Instance.Remove(this);
        }
    }
}
