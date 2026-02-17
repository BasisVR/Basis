using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static BasisHeightDriver;

namespace Basis.Scripts.UI.UI_Panels
{
    /// <summary>
    /// UI controller for setting the player's username and managing connection settings.
    /// Provides advanced networking configuration and hooks into Basis networking lifecycle.
    /// </summary>
    public class BasisSetUserName : MonoBehaviour
    {
        /// <summary>
        /// Input field for entering the player's display name.
        /// </summary>
        public TMP_InputField UserNameTMP_InputField;

        /// <summary>
        /// Button that confirms the username and attempts to connect.
        /// </summary>
        public Button Ready;

        /// <summary>
        /// File name used to cache the player's username locally.
        /// </summary>
        public static string LoadFileName = "CachedUserName.BAS";

        /// <summary>
        /// Button to show/hide advanced network settings.
        /// </summary>
        public Button AdvancedSettings;

        /// <summary>
        /// Panel containing advanced settings UI elements.
        /// </summary>
        public GameObject AdvancedSettingsPanel;

        [Header("Advanced Settings")]
        public TMP_InputField IPaddress;
        public TMP_InputField Port;
        public TMP_InputField Password;
        public Button UseLocalhost;
        public Toggle HostMode;
        public TextMeshProUGUI ConnectText;

        /// <summary>
        /// Initial scale of the UI element before height scaling is applied.
        /// </summary>
        public Vector3 InitalScale;

        /// <summary>
        /// Singleton instance of this UI controller.
        /// </summary>
        public static BasisSetUserName Instance;
        public float InitalY;
        /// <summary>
        /// Unity Start hook. Initializes UI, registers callbacks, and loads cached settings.
        /// </summary>
        public void Start()
        {
            Instance = this;
            InitalScale = gameObject.transform.localScale;
            InitalY = gameObject.transform.position.y;
            UserNameTMP_InputField.text = BasisDataStore.LoadString(LoadFileName, string.Empty);
            Ready.onClick.AddListener(HasUserName);
            AdvancedSettingsPanel.SetActive(false);
            if (AdvancedSettingsPanel != null)
            {
                AdvancedSettings.onClick.AddListener(ToggleAdvancedSettings);
                UseLocalhost.onClick.AddListener(UseLocalHost);
            }
            HostMode.onValueChanged.AddListener(UseHostMode);
            BasisNetworkManagement.OnEnableInstanceCreate += LoadCurrentSettings;
            if (BasisDeviceManagement.Instance != null)
            {
                this.transform.SetParent(BasisDeviceManagement.Instance.transform, true);
            }

            ApplySizeAndPosition();
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += ApplySizeAndPosition;
            if (BasisNetworkManagement.Instance != null)
            {
                LoadCurrentSettings();
            }
            BasisLocalPlayer.OnLocalPlayerInitalized += ApplySizeAndPosition;
            BasisLocalPlayer.OnLocalAvatarChanged += ApplySizeAndPosition;
            BasisLocalCameraDriver.InstanceExists += ApplySizeAndPosition;
        }
        public void ApplySizeAndPosition(HeightModeChange Mode)
        {
            ApplySizeAndPosition();
        }
        /// <summary>
        /// Rescales the UI panel based on the local player's avatar height.
        /// </summary>
        public void ApplySizeAndPosition()
        {
            if (BasisLocalPlayer.Instance != null)
            {
                this.transform.localScale = InitalScale * BasisHeightDriver.PlayerToDefaultRatioScaledWithAvatarScale;
                this.transform.position  = new Vector3(this.transform.position.x, -1.4f + BasisHeightDriver.SelectedScaledPlayerHeight, this.transform.position.z);
            }
        }

        /// <summary>
        /// Unity OnDestroy hook. Cleans up listeners.
        /// </summary>
        public void OnDestroy()
        {
            if (AdvancedSettingsPanel != null)
            {
                AdvancedSettings.onClick.RemoveListener(ToggleAdvancedSettings);
                UseLocalhost.onClick.RemoveListener(UseLocalHost);
            }
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= ApplySizeAndPosition;
            BasisLocalPlayer.OnLocalPlayerInitalized -= ApplySizeAndPosition;
            BasisLocalPlayer.OnLocalAvatarChanged -= ApplySizeAndPosition;
            BasisLocalCameraDriver.InstanceExists -= ApplySizeAndPosition;
        }

        /// <summary>
        /// Updates the connect text depending on whether host mode is enabled.
        /// </summary>
        public void UseHostMode(bool IsDown)
        {
            if (IsDown)
            {
                ConnectText.text = "Host";
            }
            else
            {
                ConnectText.text = "Connect";
            }
        }

        /// <summary>
        /// Sets the IP address input field to localhost.
        /// </summary>
        public void UseLocalHost()
        {
            IPaddress.text = "localhost";
        }

        /// <summary>
        /// Loads the current networking settings from <see cref="BasisNetworkManagement.Instance"/>.
        /// </summary>
        public void LoadCurrentSettings()
        {
            IPaddress.text = BasisNetworkManagement.Instance.Ip;
            Port.text = BasisNetworkManagement.Instance.Port.ToString();
            Password.text = BasisNetworkManagement.Instance.Password;
            HostMode.isOn = BasisNetworkManagement.Instance.IsHostMode;
            UseHostMode(HostMode.isOn);
            if (BasisDeviceManagement.Instance != null)
            {
                this.transform.SetParent(BasisDeviceManagement.Instance.transform);
            }
            ApplySizeAndPosition();
        }

        /// <summary>
        /// Triggered when the Ready button is clicked. Validates username, saves it,
        /// creates an asset bundle if needed, and attempts to connect.
        /// </summary>
        public async void HasUserName()
        {
            Ready.interactable = false;

            if (!string.IsNullOrEmpty(UserNameTMP_InputField.text))
            {
                BasisLocalPlayer.Instance.DisplayName = UserNameTMP_InputField.text;
                BasisLocalPlayer.Instance.SetSafeDisplayname();
                BasisDataStore.SaveString(BasisLocalPlayer.Instance.DisplayName, LoadFileName);
                if (BasisNetworkManagement.Instance != null)
                {
                    await CreateAssetBundle();
                    BasisNetworkManagement.Instance.Ip = IPaddress.text;
                    BasisNetworkManagement.Instance.Password = Password.text;
                    BasisNetworkManagement.Instance.IsHostMode = HostMode.isOn;
                    ushort.TryParse(Port.text, out BasisNetworkManagement.Instance.Port);
                    BasisNetworkManagement.Instance.Connect();
                    Ready.interactable = false;
                    BasisDebug.Log("connecting to default");
                }
            }
            else
            {
                BasisDebug.LogError("Name was empty, bailing");
                Ready.interactable = true;
            }
        }

        /// <summary>
        /// Destroys this username panel and clears the static <see cref="Instance"/> reference.
        /// </summary>
        public void DestroyUserNamePanel()
        {
            Destroy(this.gameObject);
            BasisSetUserName.Instance = null;
        }

        /// <summary>
        /// Toggles visibility of the advanced settings panel.
        /// </summary>
        public void ToggleAdvancedSettings()
        {
            if (AdvancedSettingsPanel != null)
            {
                AdvancedSettingsPanel.SetActive(!AdvancedSettingsPanel.activeSelf);
            }
        }

        /// <summary>
        /// Creates or loads the default asset bundle or addressable scene, depending on configuration.
        /// </summary>
        public async Task CreateAssetBundle()
        {
            if (BundledContentHolder.Instance.UseSceneProvidedHere)
            {
                BasisDebug.Log("using Local Asset Bundle or Addressable", BasisDebug.LogTag.Networking);
                if (BundledContentHolder.Instance.UseAddressablesToLoadScene)
                {
                    await BasisSceneLoad.LoadSceneAddressables(BundledContentHolder.Instance.DefaultScene.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                }
                else
                {
                    await BasisSceneLoad.LoadSceneAssetBundle(BundledContentHolder.Instance.DefaultScene);
                }
            }
        }
    }
}
