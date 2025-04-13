using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Enums;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.UI.NamePlate;
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
            BasisNamePlate.ShowHandleEvent?.Invoke(true);
            BasisDebug.Log("burger");
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
            BasisUIManagement.Instance.CloseAllMenus();
            AddressableGenericResource resource = new AddressableGenericResource(MainMenuAddressableID, AddressableExpectedResult.SingleItem);
            await OpenThisMenu(resource);
        }
        public static void OpenHamburgerMenuNow()
        {
            BasisUIManagement.Instance.CloseAllMenus();
            AddressableGenericResource resource = new AddressableGenericResource(MainMenuAddressableID, AddressableExpectedResult.SingleItem);
            OpenMenuNow(resource);
        }
        public static async void OpenCamera(BasisHamburgerMenu BasisHamburgerMenu)
        {
            BasisHamburgerMenu.transform.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
            BasisUIManagement.Instance.CloseAllMenus();
            InstantiationParameters Pars = new InstantiationParameters(Position, Rotation, null);
            await BasisHandHeldCameraFactory.CreateCamera(Pars);
        }

        public override void DestroyEvent()
        {
            BasisCursorManagement.LockCursor(nameof(BasisHamburgerMenu));
            BasisUINeedsVisibleTrackers.Instance.Remove(this);
            BasisNamePlate.ShowHandleEvent?.Invoke(false);
            BasisDebug.Log("Destroy Burger");
        }
    }
}
