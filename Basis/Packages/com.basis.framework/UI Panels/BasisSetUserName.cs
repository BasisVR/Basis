using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using System.Threading.Tasks;
using Basis.Scripts.Drivers;
using Basis.Scripts.Device_Management;
namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisSetUserName : MonoBehaviour
    {
        public TMP_InputField UserNameTMP_InputField;
        public Button Ready;
        public static string LoadFileName = "CachedUserName.BAS";
        public Button AdvancedSettings;
        public GameObject AdvancedSettingsPanel;
        [Header("Advanced Settings")]
        public TMP_InputField IPaddress;
        public TMP_InputField Port;
        public TMP_InputField Password;
        public Button UseLocalhost;
        public Toggle HostMode;
        public TextMeshProUGUI ConnectText;
        public Vector3 InitalScale;
        public void Start()
        {
            InitalScale = gameObject.transform.localScale;
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
                this.transform.SetParent(BasisDeviceManagement.Instance.transform,true);
            }

            ApplySize();
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += ApplySize;
        }
        public void ApplySize()
        {
            if (BasisLocalPlayer.Instance != null)
            {
                this.transform.localScale = InitalScale * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;
            }
        }
        public void OnDestroy()
        {
            if (AdvancedSettingsPanel != null)
            {
                AdvancedSettings.onClick.RemoveListener(ToggleAdvancedSettings);
                UseLocalhost.onClick.RemoveListener(UseLocalHost);
            }
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= ApplySize;
        }
        public void UseHostMode(bool IsDown)
        {
            if(IsDown)
            {
                ConnectText.text = "Host";
            }
            else
            {
                ConnectText.text = "Connect";
            }
        }
        public void UseLocalHost()
        {
            IPaddress.text = "localhost";
        }

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
        }
        public async void HasUserName()
        {
            // Set button to non-interactable immediately after clicking
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
                    Destroy(this.gameObject);
                }
            }
            else
            {
                BasisDebug.LogError("Name was empty, bailing");
                // Re-enable button interaction if username is empty
                Ready.interactable = true;
            }
        }
        public void ToggleAdvancedSettings()
        {
            if (AdvancedSettingsPanel != null)
            {
                AdvancedSettingsPanel.SetActive(!AdvancedSettingsPanel.activeSelf);
            }
        }
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
