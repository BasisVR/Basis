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
        public Image PersonalMirrorIcon;
        public GameObject FullBodyParent;
        public static string MainMenuAddressableID = "MainMenu";
        public static BasisHamburgerMenu Instance;
        internal static GameObject activeCameraInstance;
        internal static BasisPersonalMirror personalMirrorInstance;

        public bool OverrideForceCalibration;
        public static bool HasMirror;
        public override void InitalizeEvent()
        {
            Instance = this;
            UpdateMirrorState();

            Settings.onClick.AddListener(SettingsPanel);
            AvatarButton.onClick.AddListener(AvatarButtonPanel);
            FullBody.onClick.AddListener(PutIntoCalibrationMode);
            Respawn.onClick.AddListener(RespawnLocalPlayer);
            Camera.onClick.AddListener(() => OpenCamera(this));

            PersonalMirror.onClick.AddListener(() => OpenOrClosePersonalMirror(this));

            BasisCursorManagement.UnlockCursor(nameof(BasisHamburgerMenu));
            BasisUINeedsVisibleTrackers.Instance.Add(this);
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
            FullBodyParent.SetActive(!BasisDeviceManagement.IsUserInDesktop());
        }

        public override void DestroyEvent()
        {
            // Remove listeners
            Settings.onClick.RemoveListener(SettingsPanel);
            AvatarButton.onClick.RemoveListener(AvatarButtonPanel);
            FullBody.onClick.RemoveListener(PutIntoCalibrationMode);
            Respawn.onClick.RemoveListener(RespawnLocalPlayer);
            Camera.onClick.RemoveAllListeners(); // Used lambda, must remove all
            PersonalMirror.onClick.RemoveAllListeners(); // Used lambda, must remove all

            BasisCursorManagement.LockCursor(nameof(BasisHamburgerMenu));
            BasisUINeedsVisibleTrackers.Instance.Remove(this);
            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        }
        private void OnBootModeChanged(string obj)
        {
            if(FullBodyParent != null)
                FullBodyParent.SetActive(!BasisDeviceManagement.IsUserInDesktop());
        }

        public void UpdateMirrorState()
        {
            PersonalMirrorIcon.color = HasMirror ? Color.red : Color.white;
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
            string BasisBootedMode = BasisDeviceManagement.CurrentMode;
            if (OverrideForceCalibration || BasisBootedMode == "OpenVRLoader" || BasisBootedMode == "OpenXRLoader")
            {
                BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();

                foreach (BasisInput BasisInput in BasisDeviceManagement.Instance.AllInputDevices)
                {
                    Action triggerDelegate = () => OnTriggerChanged(BasisInput);
                    TriggerDelegates[BasisInput] = triggerDelegate;
                    BasisInput.CurrentInputState.OnTriggerChanged += triggerDelegate;
                }
            }
        }

        public void OnTriggerChanged(BasisInput FiredOff)
        {
            if (FiredOff.CurrentInputState.Trigger >= 0.9f)
            {
                foreach (var entry in TriggerDelegates)
                {
                    entry.Key.CurrentInputState.OnTriggerChanged -= entry.Value;
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

        public static void ToggleHamburgerMenu()
        {
            if (Instance == null)
            {
                OpenHamburgerMenuNow();
            }
            else
            {
                Instance.CloseThisMenu();
                Instance = null;
            }
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
        public static async void OpenOrClosePersonalMirror(BasisHamburgerMenu menu)
        {
            if (HasMirror)
            {
                HasMirror = false;
                if (personalMirrorInstance != null)
                {
                    GameObject.Destroy(personalMirrorInstance.gameObject);
                }
                personalMirrorInstance = null;
                menu.UpdateMirrorState();
            }
            else
            {
                if (HasMirror == false)
                {
                    HasMirror = true;
                    menu.UpdateMirrorState();
                    menu.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                    InstantiationParameters parameters = new InstantiationParameters(position, rotation, null);
                    personalMirrorInstance = await BasisPersonalMirrorFactory.CreateMirror(parameters);
                }
            }
        }
    }
}
