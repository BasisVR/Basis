using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.UI.UI_Panels;
using TMPro;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Basis.BasisUI
{
    public static class CachedAvatarData
    {
        public static List<BasisLoadableBundle> AvatarBundles = new();
        public static bool Initialized;

        public static async Task FillPreloadedBundles(List<BasisLoadableBundle> bundles)
        {
            AvatarBundles.Clear();
            AvatarBundles.AddRange(bundles);

            int preloadedCount = bundles.Count;
            for (int i = 0; i < preloadedCount; i++)
            {
                BasisLoadableBundle loadableBundle = bundles[i];
                BasisDataStoreAvatarKeys.AvatarKey key = new()
                {
                    Pass = loadableBundle.UnlockPassword,
                    Url = loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation
                };

                if (!BasisDataStoreAvatarKeys.DisplayKeys().Exists(k => k.Url == key.Url && k.Pass == key.Pass))
                {
                    await BasisDataStoreAvatarKeys.AddNewKey(key);
                }
            }
        }

        public static async Task Initialize()
        {
            await BasisDataStoreAvatarKeys.LoadKeys();
            List<BasisDataStoreAvatarKeys.AvatarKey> activeKeys = new(BasisDataStoreAvatarKeys.DisplayKeys());
            List<BasisDataStoreAvatarKeys.AvatarKey> keysToRemove = new();

            foreach (BasisDataStoreAvatarKeys.AvatarKey key in activeKeys)
            {
                if (BasisLoadHandler.IsMetaDataOnDisc(key.Url, out BasisBEEExtensionMeta info))
                {
                    // Debug.Log($"AvatarData: Found {key.Url}");
                }
                else
                {
                    Debug.Log($"AvatarData: Did NOT find {key.Url}");
                    keysToRemove.Add(key);
                }

                if (AvatarBundles.Exists(b => b.BasisRemoteBundleEncrypted.RemoteBeeFileLocation == key.Url))
                {
                    // Debug.Log($"AvatarData: Found entry for {key.Url}");
                }
                else
                {
                    // Debug.Log($"AvatarData: Did not entry file for {key.Url}. Creating now.");

                    BasisLoadableBundle bundle = new()
                    {
                        BasisRemoteBundleEncrypted = info.StoredRemote,
                        BasisLocalEncryptedBundle = info.StoredLocal,
                        UnlockPassword = key.Pass,

                        BasisBundleConnector = new BasisBundleConnector()
                        {
                            BasisBundleDescription = new BasisBundleDescription(),
                            BasisBundleGenerated = new BasisBundleGenerated[] { new() },
                            UniqueVersion = "",
                        },
                    };

                    AvatarBundles.Add(bundle);
                }
            }

            foreach (BasisDataStoreAvatarKeys.AvatarKey key in keysToRemove)
            {
                await BasisDataStoreAvatarKeys.RemoveKey(key);
            }

            Initialized = true;
        }
    }

    public class PanelAvatarList : PanelSelectionGroup
    {

        public static class AvatarListStyles
        {
            public static string Default = "Packages/com.basis.sdk/Prefabs/Panel Elements/Avatar List Page.prefab";
        }


        public static PanelAvatarList CreateNew(Component parent)
            => CreateNew<PanelAvatarList>(AvatarListStyles.Default, parent);


        public class AvatarMenuItem
        {
            public PanelButton Button;
            public BasisTrackedBundleWrapper Wrapper;
            public Texture2D IconTexture;
            public Sprite IconSprite;

            public void Clear()
            {
                Button.ReleaseInstance();
                Destroy(IconTexture);
                Destroy(IconSprite);
            }

            public async Task LoadItemData(BasisProgressReport report, CancellationToken cancellationToken)
            {
                string title;
                if (Wrapper.LoadableBundle.UnlockPassword == BasisBeeConstants.DefaultAvatar)
                {
                    title = BasisBeeConstants.DefaultAvatar;
                }
                else
                {
                    // This catches for invalid meta file downloads.
                    try
                    {
                        await BasisBeeManagement.HandleMetaOnlyLoad(Wrapper, report, cancellationToken);
                        if (cancellationToken.IsCancellationRequested) return;
                        title = Wrapper.LoadableBundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName;
                        byte[] imageBytes = Wrapper.LoadableBundle.BasisBundleConnector.ImageBytes;
                        if (imageBytes != null)
                        {
                            IconTexture = BasisTextureCompression.FromPngBytes(Wrapper.LoadableBundle.BasisBundleConnector.ImageBytes);
                            IconSprite = Sprite.Create(IconTexture,new Rect(0, 0, IconTexture.width, IconTexture.height), Vector2.zero);
                        }

                        if (IconSprite) Button.Descriptor.SetIcon(IconSprite);
                    }
                    // TODO: This will trigger if the async task is continuing after the menu has been closed.
                    catch (Exception e)
                    {
                        BasisDebug.LogError(e);
                        BasisLoadHandler.RemoveDiscInfo(Wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                        return;
                    }
                }

                Button.Descriptor.SetTitle(title);
            }
        }

        public List<BasisLoadableBundle> PreLoadedAvatars = new();
        public BasisProgressReport Report = new();
        public CancellationToken CancellationToken = CancellationToken.None;
        public AvatarMenuItem SelectedAvatar;

        public TextMeshProUGUI CreationDateLabel;
        public TextMeshProUGUI FileSizeLabel;
        public PanelPasswordField AvatarIDField;
        public PanelPasswordField AvatarUrlField;
        public PanelPasswordField AvatarPasswordField;
        public GameObject WindowsIcon;
        public GameObject LinuxIcon;
        public GameObject AndroidIcon;
        public PanelAvatarAddNew NewAvatarPanel;
        public PanelButton NewAvatarButton;

        public PanelButton RemoveAvatarButton;
        public PanelButton LoadAvatarButton;


        public List<AvatarMenuItem> MenuItems = new();


        public override void OnCreateEvent()
        {
            base.OnCreateEvent();
            ClearAvatarInfo();

            NewAvatarButton.OnClicked += NewAvatarPanel.Show;
            LoadAvatarButton.OnClicked += () => _ = LoadAvatar();
            RemoveAvatarButton.OnClicked += RemoveAvatar;

            NewAvatarPanel.Hide();

            _ = LoadAvatarBundles();
        }

        private async Task LoadAvatarBundles()
        {
            if (!CachedAvatarData.Initialized)
            {
                await CachedAvatarData.FillPreloadedBundles(PreLoadedAvatars);
                await CachedAvatarData.Initialize();
            }

            await CreateButtons();
        }

        private async Task CreateButtons()
        {
            foreach (PanelButton button in SelectionButtons)
                button.ReleaseInstance();

            SelectionButtons.Clear();

            foreach (BasisLoadableBundle bundle in CachedAvatarData.AvatarBundles)
            {
                PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.Avatar, TabButtonParent);
                SelectionButtons.Add(button);
                button.Descriptor.SetTitle("Avatar");

                BasisTrackedBundleWrapper wrapper = new()
                {
                    LoadableBundle = bundle,
                };

                AvatarMenuItem item = new()
                {
                    Button = button,
                    Wrapper = wrapper
                };

                MenuItems.Add(item);

                button.OnClicked += () => OnTabSelected(button);
                button.OnClicked += () => ShowAvatarInfo(item);
            }

            foreach (AvatarMenuItem item in MenuItems)
            {
                await item.LoadItemData(Report, CancellationToken);
            }
        }

        public async Task AppendNewAvatar(BasisLoadableBundle bundle, bool selectAfterCreate)
        {
            PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.Avatar, TabButtonParent);
            SelectionButtons.Add(button);
            button.Descriptor.SetTitle("Avatar");

            BasisTrackedBundleWrapper wrapper = new()
            {
                LoadableBundle = bundle,
            };

            AvatarMenuItem item = new()
            {
                Button = button,
                Wrapper = wrapper
            };

            MenuItems.Add(item);

            button.OnClicked += () => OnTabSelected(button);
            button.OnClicked += () => ShowAvatarInfo(item);

            await item.LoadItemData(Report, CancellationToken);
            CachedAvatarData.AvatarBundles.Add(bundle);

            if (selectAfterCreate) button.OnClick();
        }

        private void ClearAvatarInfo()
        {
            SelectedAvatar = null;

            CreationDateLabel.text = string.Empty;
            FileSizeLabel.text = string.Empty;
            Descriptor.SetIcon(string.Empty);
            Descriptor.SetTitle(string.Empty);
            Descriptor.SetDescription(string.Empty);

            AvatarIDField.SetValue(false);
            AvatarIDField.SetPassword(string.Empty);
            AvatarUrlField.SetValue(false);
            AvatarUrlField.SetPassword(string.Empty);
            AvatarPasswordField.SetValue(false);
            AvatarPasswordField.SetPassword(string.Empty);

            WindowsIcon.SetActive(false);
            AndroidIcon.SetActive(false);
            LinuxIcon.SetActive(false);

            NewAvatarPanel.Hide();
        }

        private void ShowAvatarInfo(AvatarMenuItem item)
        {
            if (item == null)
            {
                BasisDebug.LogError($"No avatar menu item provided.");
                ClearAvatarInfo();
                return;
            }

            SelectedAvatar = item;

            BasisLoadableBundle bundle = item.Wrapper.LoadableBundle;
            if (bundle == null)
            {
                BasisDebug.LogError($"Bundle on AvatarMenuItem {item} not found.");
                RemoveAvatarItem(item);
                ClearAvatarInfo();
                return;
            }

            BasisBundleDescription description = bundle.BasisBundleConnector.BasisBundleDescription;
            if (description == null)
            {
                BasisDebug.LogError($"Bundle Description on AvatarMenuItem {item} not found.");
                RemoveAvatarItem(item);
                ClearAvatarInfo();
                return;
            }

            Descriptor.SetIcon(item.IconSprite);
            Descriptor.SetTitle(description.AssetBundleName);
            Descriptor.SetDescription(description.AssetBundleDescription);

            AvatarIDField.SetValue(false);
            AvatarIDField.SetPassword(bundle.BasisBundleConnector.UniqueVersion);
            AvatarUrlField.SetValue(false);
            AvatarUrlField.SetPassword(bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
            AvatarPasswordField.SetValue(false);
            AvatarPasswordField.SetPassword(bundle.UnlockPassword);

            string creationDate = bundle.BasisBundleConnector.DateOfCreation;
            if (string.IsNullOrEmpty(creationDate))
            {
                creationDate = string.Empty;
            }
            else
            {
                creationDate = DateTime.Parse(creationDate).ToString(CultureInfo.InvariantCulture);
                creationDate += " UTC";
            }

            CreationDateLabel.text = creationDate;

            string[] platforms = bundle.BasisBundleConnector.BasisBundleGenerated
                .Select(pair => pair.Platform).ToArray();

            WindowsIcon.SetActive(false);
            AndroidIcon.SetActive(false);
            LinuxIcon.SetActive(false);

            foreach (string platform in platforms)
            {
                switch (platform)
                {
                    case "StandaloneWindows64":
                        WindowsIcon.SetActive(true);
                        break;
                    case "StandaloneLinux64":
                        AndroidIcon.SetActive(true);
                        break;
                    case "Android":
                        LinuxIcon.SetActive(true);
                        break;
                }
            }

            NewAvatarPanel.Hide();
        }

        public void RemoveAvatar()
        {
            if (SelectedAvatar == null)
            {
                BasisDebug.LogError("No selected bundle.");
                return;
            }

            BasisMainMenu.Instance.OpenDialogue(
                "Basis VR",
                "Are you sure you want to remove this avatar?",
                "Cancel",
                "Remove Avatar",
                value =>
                {
                    if (value) return;
                    RemoveAvatarItem(SelectedAvatar);
                });
        }

        public void RemoveAvatarItem(AvatarMenuItem menuItem)
        {
            BasisDataStoreAvatarKeys.AvatarKey key = new()
            {
                Pass = SelectedAvatar.Wrapper.LoadableBundle.UnlockPassword,
                Url = SelectedAvatar.Wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation
            };
            MenuItems.Remove(SelectedAvatar);
            SelectionButtons.Remove(SelectedAvatar.Button);
            SelectedAvatar.Clear();
            _ = RemoveKey(key);
            ClearAvatarInfo();
        }

        public async Task RemoveKey(BasisDataStoreAvatarKeys.AvatarKey key)
        {
            await BasisDataStoreAvatarKeys.RemoveKey(key);
        }

        /// <summary>
        /// Apply the current avatar onto the player.
        /// </summary>
        public async Task LoadAvatar()
        {
            if (SelectedAvatar == null)
            {
                BasisDebug.LogError("No selected bundle.");
                return;
            }

            if (BasisLocalPlayer.Instance)
            {
                BasisLoadableBundle bundle = SelectedAvatar.Wrapper.LoadableBundle;

                if (bundle.BasisBundleConnector.GetPlatform(out BasisBundleGenerated platformBundle))
                {
                    string assetMode = platformBundle.AssetMode;
                    byte mode = !string.IsNullOrEmpty(assetMode) && byte.TryParse(assetMode, out byte result)
                        ? result
                        : (byte)0;
                    await BasisLocalPlayer.Instance.CreateAvatar(mode, bundle);
                }
                else
                {
                    if (bundle.UnlockPassword == BasisBeeConstants.DefaultAvatar)
                    {
                        await BasisLocalPlayer.Instance.CreateAvatar(1, bundle);
                    }
                    else
                    {
                        BasisDebug.LogError("Missing Platform " + Application.platform);
                    }
                }
            }
            else
            {
                BasisDebug.LogError("Missing LocalPlayer!");
            }
        }
    }
}
