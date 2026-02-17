using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using static Basis.BasisUI.PanelButton;

namespace Basis.BasisUI
{
    public partial class ItemsProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
          //  BasisMenuBase<BasisMainMenu>.AddProvider(new ItemsProvider());
        }

        public override string Title => "Items";
        public override string IconAddress => AddressableAssets.Sprites.Items;
        public override int Order => 1; // after Settings

        public override bool Hidden => false;

        public static BasisMenuPanel panel;
        public override async void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) return;

            panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);

            var titleLabel = panel.Descriptor.TitleLabel;
            titleLabel.text = Title;

            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            PanelTabGroup tabGroup = PanelTabGroup.CreateNew(panel.Descriptor.ContentParent, LayoutDirection.Vertical);

            await BasisDataStoreItemKeys.LoadKeys();
            BasisDataStoreItemKeys.ItemKey[] data = BasisDataStoreItemKeys.DisplayKeys();

            List<BasisDataStoreItemKeys.ItemKey> props = new();
            List<BasisDataStoreItemKeys.ItemKey> worlds = new();
            List<BasisDataStoreItemKeys.ItemKey> avatars = new();
            BasisDebug.Log($"Stored Item Keys were {data.Length}");
            for (int i = 0; i < data.Length; i++)
            {
                var k = data[i];
                switch (k.Mode)
                {
                    case BundledContentHolder.Mode.Prop: props.Add(k); break;
                    case BundledContentHolder.Mode.World: worlds.Add(k); break;
                    case BundledContentHolder.Mode.Avatar: avatars.Add(k); break;
                    default:
                        BasisDebug.LogError($"Mode Not Implented! {k.Mode}");
                        break;
                }
            }

            tabGroup.AddTab("Props", null, PropsTab(tabGroup, props));
            tabGroup.AddTab("Worlds", null, WorldsTab(tabGroup, worlds));
            tabGroup.AddTab("Avatars", null, AvatarsTab(tabGroup, avatars));

            tabGroup.AddExtraAction("Add New Item", AddNewItem);

            panel.Descriptor.ForceRebuild();
        }
        // Keep refs so you can close/destroy the UI you created.
        private static PanelElementDescriptor _background;
        private static PanelElementDescriptor _descriptor;

        // If you need to prevent double-click spam.
        private static bool _isSubmitting;
        public static PanelPasswordField URL;
        public static PanelPasswordField Password;
        // Prefer Task-returning async methods over async void.
        public static void AddNewItem()
        {
            // Build overlay
            _background = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Overlay, panel);
            _descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.BaseOverlay, _background);

            _descriptor.rectTransform.localPosition = Vector3.zero;
            _descriptor.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            _descriptor.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _descriptor.rectTransform.anchoredPosition = Vector2.zero;
            _descriptor.SetSize(new Vector2(700, 500));
            _descriptor.SetTitle("Add New Item");

            var Mode = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.OverlayEntry, _descriptor);
            string[] modeNames = Enum.GetNames(typeof(BundledContentHolder.Mode));
            Mode.Descriptor.SetTitle("Item Type");
            Mode.AssignEntries(modeNames.ToList());
            Mode.SetValueWithoutNotify(BundledContentHolder.Mode.Avatar.ToString());

            CreateText("Add your BEE File URL:", _descriptor);
            URL = PanelPasswordField.CreateNew(_descriptor);
            URL._placeholderField.text = "URL";
            URL._inputField.contentType = TMP_InputField.ContentType.Standard;
            URL.DisableIcons();

            CreateText("Add your generated BEE file password:", _descriptor);
            Password = PanelPasswordField.CreateNew(_descriptor);
            Password._placeholderField.text = "Enter password";
            PanelTabGroup acceptOrDenyPanel = PanelTabGroup.CreateNew(_descriptor, LayoutDirection.HorizontalNoBackground);

            PanelButton yesPanel = PanelButton.CreateNew(ButtonStyles.AcceptButton, acceptOrDenyPanel.TabButtonParent);
            PanelButton noPanel = PanelButton.CreateNew(ButtonStyles.CancelButton, acceptOrDenyPanel.TabButtonParent);

            noPanel.Descriptor.SetTitle("Cancel");
            yesPanel.Descriptor.SetTitle("Add");

            noPanel.Descriptor.SetWidth(270);
            noPanel.Descriptor.SetHeight(60);
            yesPanel.Descriptor.SetWidth(270);
            yesPanel.Descriptor.SetHeight(60);

            // Cancel just closes.
            noPanel.OnClicked += () =>
            {
                CloseOverlayAndLoad(false, Mode.SelectedString, URL.Password, Password.Password);
            };

            // Add does the async work, then closes.
            yesPanel.OnClicked += () =>
            {
                if (_isSubmitting) return;
                _isSubmitting = true;

                try
                {

                    CloseOverlayAndLoad(true, Mode.SelectedString, URL.Password, Password.Password);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                    _isSubmitting = false;
                }
            };
        }
        public static TMP_Text CreateText(string content, Component Parent)
        {
            GameObject go = new GameObject("RuntimeText");
            go.transform.SetParent(Parent.transform, false);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = 22;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Center;

            // Optional sizing
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(400, 100);

            return text;
        }

        public static async void CloseOverlayAndLoad(bool doLoad, string Mode, string URL, string Password)
        {
            if (doLoad)
            {
                if (Enum.TryParse<BundledContentHolder.Mode>(Mode, out var mode))
                {
                    var key = new BasisDataStoreItemKeys.ItemKey
                    {
                        Pass = Password,
                        Url = URL,
                        Mode = mode
                    };

                    await BasisDataStoreItemKeys.AddNewKey(key);
                }
                else
                {
                    CloseOverlay();
                    BasisDebug.LogError("Coudnt Parse Mode!");
                }
            }
            CloseOverlay();
        }

        public static void CloseOverlay()
        {
            _isSubmitting = false;

            // Destroy / hide whatever your UI framework expects.
            // If PanelElementDescriptor has a Dispose/Destroy method, use that instead.
            if (_descriptor != null)
            {
                UnityEngine.Object.Destroy(_descriptor.gameObject);
                _descriptor = null;
            }

            if (_background != null)
            {
                UnityEngine.Object.Destroy(_background.gameObject);
                _background = null;
            }
        }
        public static PanelTabPage PropsTab(PanelTabGroup tabGroup, List<BasisDataStoreItemKeys.ItemKey> items)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(20, 0);
            var d = tab.Descriptor;
            d.SetTitle("Props");
            BuildItemsList(items, tab);
            d.ForceRebuild();
            return tab;
        }
        public static PanelTabPage WorldsTab(PanelTabGroup tabGroup, List<BasisDataStoreItemKeys.ItemKey> items)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(20, 0);
            var d = tab.Descriptor;
            d.SetTitle("Worlds");
            BuildItemsList(items, tab);
            d.ForceRebuild();
            return tab;
        }
        public static PanelTabPage AvatarsTab(PanelTabGroup tabGroup, List<BasisDataStoreItemKeys.ItemKey> items)
        {
            PanelTabPage tab = PanelTabPage.CreateGrid(tabGroup.Descriptor.ContentParent);
            tab.rectTransform.offsetMin = new Vector2(20, 0);
            var d = tab.Descriptor;
            d.SetTitle("Avatars");
            BuildItemsList(items, tab);
            d.ForceRebuild();
            return tab;
        }
        private static void BuildItemsList(List<BasisDataStoreItemKeys.ItemKey> items, PanelTabPage tab)
        {
            RectTransform container = tab.Descriptor.ContentParent;
            // List entries
            for (int Index = 0; Index < items.Count; Index++)
            {
                var item = items[Index];
                CreateItemCard(item, container);
            }
        }

        private static async void CreateItemCard(BasisDataStoreItemKeys.ItemKey item, RectTransform container)
        {
            PanelButton buttonPanel = PanelButton.CreateNew(ButtonStyles.Prop, container);

            // Meta-only load that will fill title/icon/description
            BasisTrackedBundleWrapper wrapperForMeta = BuildWrapper(item);
            var reportForMeta = new BasisProgressReport();
            Task<Sprite> Data = LoadItemMetaIntoGroup(wrapperForMeta, reportForMeta, CancellationToken.None, buttonPanel);
            Sprite sprite = await Data;
            // NEW: clicking the item opens the info overlay
            buttonPanel.OnClicked += async () =>
            {
              await  ShowItemOverlay(item, sprite, wrapperForMeta);
            };
        }
        private static BasisTrackedBundleWrapper BuildWrapper(BasisDataStoreItemKeys.ItemKey item)
        {
            var wrapper = new BasisTrackedBundleWrapper();
            var loadable = new BasisLoadableBundle
            {
                BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle(),
                BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle(),
                BasisBundleConnector = new BasisBundleConnector(),
                UnlockPassword = item.Pass
            };
            loadable.BasisRemoteBundleEncrypted.RemoteBeeFileLocation = item.Url;
            wrapper.LoadableBundle = loadable;
            return wrapper;
        }
        private static async Task<Sprite> LoadItemMetaIntoGroup(BasisTrackedBundleWrapper wrapper, BasisProgressReport report, CancellationToken cancellationToken, PanelButton Buttonpanel)
        {
            var descripter = Buttonpanel.Descriptor;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                await BasisBeeManagement.HandleMetaOnlyLoad(wrapper, report, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                var desc = wrapper.LoadableBundle.BasisBundleConnector?.BasisBundleDescription;

                string title = "Unknown Bundle";

                if (desc != null)
                {
                    if (!string.IsNullOrWhiteSpace(desc.AssetBundleName))
                        title = desc.AssetBundleName;
                }

                Sprite iconSprite = null;
                string imageBase64 = wrapper.LoadableBundle.BasisBundleConnector?.ImageBase64;
                if (!string.IsNullOrEmpty(imageBase64))
                {
                    var tex = BasisTextureCompression.FromPngBytes(imageBase64);
                    if (tex != null)
                    {
                        iconSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                    }
                }
                Buttonpanel.SetIcon(iconSprite, false);
                descripter.SetTitle(title);
                string metaLine = string.Empty;
                descripter.SetDescription(wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);

                descripter.ForceRebuild();
                return iconSprite;
            }
            catch (Exception e)
            {
                BasisDebug.LogError(e);
                BasisLoadHandler.RemoveDiscInfo(wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);

                descripter.SetTitle("Failed to load meta");
                descripter.SetDescription(e.Message);
                descripter.ForceRebuild();
                return null;
            }
        }
        private static BasisDataStoreItemKeys.ItemKey _activeItem;
        public static PanelElementDescriptor CreateBaseOverlay(Vector2 Anchor, Vector2 Scale,string Name)//= new Vector2(0.5f, 0.5f) new Vector2(800, 720)
        {
            PanelElementDescriptor _descriptor = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.BaseOverlay, _background);

            _descriptor.rectTransform.localPosition = Vector3.zero;
            _descriptor.rectTransform.anchorMin = Anchor;
            _descriptor.rectTransform.anchorMax = Anchor;
            _descriptor.rectTransform.anchoredPosition = Vector2.zero;
            _descriptor.SetSize(Scale);
            _descriptor.SetTitle(Name);
            return _descriptor;
        }
        public static async Task ShowItemOverlay(BasisDataStoreItemKeys.ItemKey item, Sprite Sprite, BasisTrackedBundleWrapper Wrapper)
        {
            // Prevent stacking overlays
            CloseOverlay();

            var bundle = Wrapper.LoadableBundle;

            BasisBundleDescription description = bundle.BasisBundleConnector.BasisBundleDescription;
            if (description == null)
            {
                BasisDebug.LogError($"Bundle Description on AvatarMenuItem {item} not found.");
                return;
            }

            _activeItem = item;

            _background = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Overlay, panel);

            _descriptor = CreateBaseOverlay(new Vector2(0.5f, 0.5f), new Vector2(800, 000), description.AssetBundleName);

            var button = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButtonOverlay, _descriptor.Header);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            button.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            button.OnClicked += () => CloseOverlay();

            string creationDate = bundle.BasisBundleConnector.DateOfCreation;
            if (string.IsNullOrEmpty(creationDate))
            {
                creationDate = string.Empty;
            }
            else
            {
                creationDate = DateTime
                    .Parse(creationDate, CultureInfo.InvariantCulture,
                           DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                    .ToString(CultureInfo.InvariantCulture);

                creationDate += " UTC";
            }

            // Wrapper
            var Descriptor = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.GroupLargeIcon, _descriptor);

            Descriptor.SetIcon(Sprite);
            Descriptor.SetTitle(description.AssetBundleDescription);

            PanelTabGroup actionsSupportedPlatforms =  PanelTabGroup.CreateNew(_descriptor, LayoutDirection.HorizontalNoBackground);
            if (actionsSupportedPlatforms.TryGetComponent<LayoutElement>(out LayoutElement LayoutElement))
            {
                LayoutElement.minHeight = 50;
            }

            Descriptor.SetDescription("\n<size=80%>Created: " + creationDate + "</size>");

            var IDField = PanelPasswordField.CreateNew(PanelPasswordField.PasswordFieldStyles.Entry, _descriptor);
            IDField._placeholderField.text = "";//Wrapper
            IDField.SetPassword(bundle.BasisBundleConnector.UniqueVersion);
            IDField._inputField.interactable = false;
            IDField.Descriptor.SetTitle("URL:");
            IDField.LayoutElement.minWidth = 500;

            var urlField = PanelPasswordField.CreateNew(PanelPasswordField.PasswordFieldStyles.Entry, _descriptor);
            urlField._placeholderField.text = "";
            urlField.SetPassword(item.Url);
            urlField._inputField.interactable = false;
            urlField.Descriptor.SetTitle("URL:");
            urlField.LayoutElement.minWidth = 500;

            var passField = PanelPasswordField.CreateNew(PanelPasswordField.PasswordFieldStyles.Entry, _descriptor);
            passField._placeholderField.text = "";
            passField.SetPassword(item.Pass); // if supported
            passField._inputField.interactable = false;
            passField.Descriptor.SetTitle("Password:");
            passField.LayoutElement.minWidth = 500;

            string[] platforms = bundle.BasisBundleConnector.BasisBundleGenerated
                .Select(pair => pair.Platform)
                .ToArray();

            foreach (string platform in platforms)
            {
                string address = null;

                switch (platform)
                {
                    case "StandaloneWindows64":
                        address = "Packages/com.basis.sdk/Prefabs/Panel Elements/Platform Panel - Windows.prefab";
                        break;

                    case "StandaloneOSX":
                        address = "Packages/com.basis.sdk/Prefabs/Panel Elements/Platform Panel - Mac.prefab";
                        break;

                    case "StandaloneLinux64":
                        address = "Packages/com.basis.sdk/Prefabs/Panel Elements/Platform Panel - Linux.prefab";
                        break;

                    case "Android":
                        address = "Packages/com.basis.sdk/Prefabs/Panel Elements/Platform Panel - Android.prefab";
                        break;

                    case "iOS":
                        address = "Packages/com.basis.sdk/Prefabs/Panel Elements/Platform Panel - iOS.prefab";
                        break;
                }

                if (string.IsNullOrEmpty(address))
                {
                    continue;
                }

                var handle = Addressables.LoadAssetAsync<GameObject>(address);
                var prefab = await handle.Task;

                GameObject.Instantiate(prefab, actionsSupportedPlatforms.TabButtonParent.transform);
            }

            // Buttons row
            PanelTabGroup actions = PanelTabGroup.CreateNew(_descriptor, LayoutDirection.HorizontalNoBackground);

            PanelButton DeleteBtn = PanelButton.CreateNew(ButtonStyles.CancelButton, actions.TabButtonParent);
            PanelButton loadBtn = PanelButton.CreateNew(ButtonStyles.AcceptButton, actions.TabButtonParent);

            DeleteBtn.Descriptor.SetTitle("Delete");
            loadBtn.Descriptor.SetTitle("Load");

            DeleteBtn.SetSize(new Vector2(200, 60));
            loadBtn.SetSize(new Vector2(530, 60));

            DeleteBtn.OnClicked += async () =>
            {
                await BasisDataStoreItemKeys.RemoveKey(item);
                CloseOverlay();
            };

            loadBtn.OnClicked += () =>
            {
                if (_isSubmitting) return;
                _isSubmitting = true;

                try
                {
                    //await LoadSelectedItem(item);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(ex);
                }
                finally
                {
                    _isSubmitting = false;
                    CloseOverlay();
                }
            };
        }
    }
}
