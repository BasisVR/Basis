using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.BasisUI
{
    public class ServersProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new ServersProvider());
        }
        public static string TitleStatic = "Servers";
        public override string Title => TitleStatic;
        public override string IconAddress => AddressableAssets.Sprites.Servers;
        public override int Order => 1;

        public override bool Hidden => false;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.Instance.ActiveMenu.ReleaseInstance();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                new BasisMenuPanel.PanelData
                {
                    Title = this.Title,
                    PanelSize = new Vector2(650, 900),
                    PanelPosition = default
                },
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            RectTransform container = panel.Descriptor.ContentParent;

            PanelElementDescriptor layout =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVertical, container);
            container = layout.ContentParent;

            usernameField = PanelTextField.CreateNewEntry(container);
            usernameField.Descriptor.SetTitle("Username");
            usernameField.SetValueWithoutNotify(BasisDataStore.LoadString(LoadFileName, string.Empty));

            ipAddressField = PanelTextField.CreateNewEntry(container);
            ipAddressField.Descriptor.SetTitle("IP Address");



            useLocalhost = PanelButton.CreateNew(container);
            useLocalhost.Descriptor.SetTitle("Use \"Localhost\"");
            useLocalhost.OnClicked += () => ipAddressField.SetValueWithoutNotify("localhost");

            portField = PanelTextField.CreateNewEntry(container);
            portField.Descriptor.SetTitle("Port");

            passwordField = PanelPasswordField.CreateNewEntry(container);
            passwordField.Descriptor.SetTitle("Password");
            passwordField.SetPassword("default_password");

            hostModeToggle = PanelToggle.CreateNewEntry(container);
            hostModeToggle.Descriptor.SetTitle("Host Mode");
            hostModeToggle.OnValueChanged += UseHostMode;

            connectButton = PanelButton.CreateNew(container);
            connectButton.Descriptor.SetTitle("Connect");
            connectButton.Descriptor.SetHeight(80);
            connectButton.OnClicked += () => _ = OnConnectButton();

            ShowAdvancedSettings = PanelButton.CreateNew(container);

            ShowAdvancedSettings.Descriptor.SetTitle("Show Advanced Settings");
            ShowAdvancedSettings.OnClicked += ShowAdvancedOptions;

            Info = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group,container);

            portField.gameObject.SetActive(false);
            useLocalhost.gameObject.SetActive(false);
            passwordField.gameObject.SetActive(false);
            hostModeToggle.gameObject.SetActive(false);
            ipAddressField.gameObject.SetActive(false);
            if (BasisNetworkManagement.Instance)
            {
                LoadCurrentSettings();
            }
            else
            {
                BasisNetworkManagement.OnIstanceCreated += LoadCurrentSettings;
            }
        }
        public void ShowAdvancedOptions()
        {

            bool State = useLocalhost.gameObject.activeSelf;
            bool Opposite = !State;

            if(Opposite)
            {
                ShowAdvancedSettings.Descriptor.SetTitle("Hide Advanced Settings");
            }
            else
            {
                ShowAdvancedSettings.Descriptor.SetTitle("Show Advanced Settings");
            }
            useLocalhost.gameObject.SetActive(Opposite);
            passwordField.gameObject.SetActive(Opposite);
            hostModeToggle.gameObject.SetActive(Opposite);
            portField.gameObject.SetActive(Opposite);
            ipAddressField.gameObject.SetActive(Opposite);
        }
        private PanelElementDescriptor Info;
        private PanelButton ShowAdvancedSettings;
        private PanelTextField usernameField;
        private PanelTextField ipAddressField;
        private PanelButton useLocalhost;
        private PanelTextField portField;
        private PanelPasswordField passwordField;
        private PanelToggle hostModeToggle;
        private PanelButton connectButton;

        public static string LoadFileName = "CachedUserName.BAS";

        public void LoadCurrentSettings()
        {
            ipAddressField.SetValueWithoutNotify(BasisNetworkManagement.Instance.Ip);
            portField.SetValueWithoutNotify(BasisNetworkManagement.Instance.Port.ToString());
            passwordField.SetPassword(BasisNetworkManagement.Instance.Password);
            hostModeToggle.SetValueWithoutNotify(BasisNetworkManagement.Instance.IsHostMode);
            UseHostMode(hostModeToggle.Value);
        }

        public async Task OnConnectButton()
        {
            connectButton.ButtonComponent.interactable = false;

            try
            {
                Info.SetTitle("Connecting");
                Info.SetDescription("Initializing...");

                string userName = usernameField._inputField.text;
                if (string.IsNullOrEmpty(userName))
                {
                    Info.SetTitle("Error");
                    Info.SetDescription("Display Name Was Empty");
                    return;
                }
                if (ushort.TryParse(portField.Value, out var port))
                {

                }
                else
                {
                    BasisDebug.LogError($"Unable to pass Port! {portField.Value}");
                }

                // If currently connected, disconnect + wait for reboot completion
                if (BasisNetworkConnection.LocalPlayerIsConnected)
                {
                    Info.SetTitle("Disconnecting");
                    Info.SetDescription("Disconnecting...");
                    BasisDebug.Log("Disconnecting From Current Connection", BasisDebug.LogTag.Networking);

                    // Start waiting BEFORE you trigger the chain that will raise the event.
                    // (If it fires instantly, you won't miss it.)
                    using var cts = new CancellationTokenSource(); // optionally store and cancel on UI close
                    Task rebootWait = BasisNetworkConnection.WaitForRebootCompleteAsync(cts.Token);

                    await BasisNetworkLifeCycle.Destroy(BasisNetworkManagement.Instance);

                    // Wait until HandleDisconnection -> RebootManagement -> OnRebootComplete happens
                    await rebootWait;

                    // Now safe to continue
                    BasisNetworkLifeCycle.Initalize(BasisNetworkManagement.Instance);
                }

                Info.SetTitle("Connecting");
                Info.SetDescription("Preparing...");

                BasisLocalPlayer.Instance.DisplayName = userName;
                BasisLocalPlayer.Instance.SetSafeDisplayname();
                BasisDataStore.SaveString(BasisLocalPlayer.Instance.DisplayName, LoadFileName);

                if (BasisNetworkManagement.Instance == null)
                {
                    Info.SetTitle("Error");
                    Info.SetDescription("Networking Layer was Not Created!");
                    BasisDebug.LogError("Missing Networking layer!");
                    return;
                }

                Info.SetTitle("Connecting");
                Info.SetDescription("Loading Asset Bundle...");

                BasisNetworkManagement.Instance.Port = port;
                BasisNetworkManagement.Instance.Ip = ipAddressField.Value;
                BasisNetworkManagement.Instance.Password = passwordField.Password;
                BasisNetworkManagement.Instance.IsHostMode = hostModeToggle.Value;
                BasisMainMenu.Close();//no ui from here
                BasisCursorManagement.OnReset();
                await CreateAssetBundle();
                BasisNetworkManagement.Instance.Connect();
                if (BasisDesktopEye.Instance != null)
                {
                    BasisDesktopEye.Instance.LockEye();
                }
            }
            catch (TimeoutException tex)
            {
                Info.SetTitle("Error");
                Info.SetDescription("Disconnect/reboot timed out.");
                BasisDebug.LogError(tex.ToString());
            }
            catch (System.Exception ex)
            {
                Info.SetTitle("Error");
                Info.SetDescription("Connection failed.");
                BasisDebug.LogError(ex.ToString());
            }
            finally
            {
                // If you successfully connected and you don't want the button back, remove this.
                // Otherwise it's nice for retry on failure.
                connectButton.ButtonComponent.interactable = true;
            }
        }

        public async Task CreateAssetBundle()
        {
            if (BundledContentHolder.Instance.UseSceneProvidedHere)
            {
                BasisDebug.Log("using Local Asset Bundle or Addressable", BasisDebug.LogTag.Networking);
                if (BundledContentHolder.Instance.UseAddressablesToLoadScene)
                {
                    await BasisSceneLoad.LoadSceneAddressables(
                        BundledContentHolder.Instance.DefaultScene
                        .BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                }
                else
                {
                    await BasisSceneLoad.LoadSceneAssetBundle(BundledContentHolder.Instance.DefaultScene);
                }
            }
        }

        public void UseHostMode(bool value)
        {
            connectButton.Descriptor.SetTitle(value ? "Host" : "Connect");
        }
    }
}
