using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.UI;
namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisHamburgerMenu : BasisUIBase
    {
        public Button Settings;
        public Button Servers;
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
        public BasisUIMovementDriver BasisUIMovementDriver;
        public override void InitalizeEvent()
        {
            Instance = this;
            UpdateMirrorState();

            Settings.onClick.AddListener(SettingsPanel);
            Servers.onClick.AddListener(ServerButtonPanel);
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
            Servers.onClick.RemoveListener(ServerButtonPanel);
            AvatarButton.onClick.RemoveListener(AvatarButtonPanel);
            FullBody.onClick.RemoveListener(PutIntoCalibrationMode);
            Respawn.onClick.RemoveListener(RespawnLocalPlayer);
            Camera.onClick.RemoveAllListeners(); // Used lambda, must remove all
            PersonalMirror.onClick.RemoveAllListeners(); // Used lambda, must remove all

            BasisCursorManagement.LockCursor(nameof(BasisHamburgerMenu));
            BasisUINeedsVisibleTrackers.Instance.Remove(this);
            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            BasisUIMovementDriver.DeInitalize();
        }
        private void OnBootModeChanged(string obj)
        {
            if (FullBodyParent != null)
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
            string BasisBootedMode = BasisDeviceManagement.StaticCurrentMode;
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
        public static void ServerButtonPanel()
        {
            if (BasisUIServers.Instance != null)
            {
                BasisUIServers.Instance.CloseThisMenu();
                return;
            }
            else
            {
                BasisHamburgerMenu.Instance.CloseThisMenu();
                BasisUISettings.OpenMenuNow(BasisUIServers.ServerPanel);
            }
        }
        private static void AvatarButtonPanel()
        {
            BasisHamburgerMenu.Instance.CloseThisMenu();
            BasisUISettings.OpenMenuNow(BasisUIAvatarSelection.AvatarPanel);
        }

        public static void SettingsPanel()
        {
            BasisHamburgerMenu.Instance.CloseThisMenu();
            BasisUISettings.OpenMenuNow(BasisUISettings.SettingsPanel);
        }
        public static void OpenHamburgerMenuNow()
        {
            BasisUIManagement.CloseAllMenus();
            OpenMenuNow(MainMenuAddressableID);
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
        public static async void OpenCamera(BasisHamburgerMenu menu, string cameraPrefab = "Packages/com.basis.sdk/Prefabs/UI/Player Held Camera.prefab")
        {
            if (activeCameraInstance != null)
            {
                var cameraInteractable = activeCameraInstance.GetComponent<BasisHandHeldCameraInteractable>();
                if (cameraInteractable != null)
                {

                    cameraInteractable.ReleasePlayerLocks();
                }
                AddressableResourceProcess.ReleaseGameobject(activeCameraInstance.gameObject);
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
            GameObject data = await AddressableResourceProcess.LoadSystemGameobject(cameraPrefab, parameters);
            if (data.TryGetComponent(out BasisHandHeldCamera Camera))
            {
                activeCameraInstance = Camera.gameObject;
            }
        }
        public static async void OpenOrClosePersonalMirror(BasisHamburgerMenu menu, string Path = "Packages/com.basis.sdk/Prefabs/UI/Personal Mirror Prefab/PersonalMirror.prefab")
        {
            if (HasMirror)
            {
                HasMirror = false;
                if (personalMirrorInstance != null)
                {
                    AddressableResourceProcess.ReleaseGameobject(personalMirrorInstance.gameObject);
                    personalMirrorInstance = null;
                }
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
                    GameObject data = await AddressableResourceProcess.LoadSystemGameobject(Path, parameters);
                    if (data.TryGetComponent(out personalMirrorInstance))
                    {
                    }
                }
            }
            BasisUIManagement.CloseAllMenus();
        }
    }
}
