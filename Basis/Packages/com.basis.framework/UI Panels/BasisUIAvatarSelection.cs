using Basis.Scripts.BasisSdk.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisUIAvatarSelection : BasisUIBase
    {
        [SerializeField] public List<BasisLoadableBundle> preLoadedBundles = new List<BasisLoadableBundle>();
        [SerializeField] public RectTransform ParentedAvatarButtons;
        [SerializeField] public GameObject ButtonPrefab;

        public const string AvatarSelection = "BasisUIAvatarSelection";

        [SerializeField] public Button AddAvatarApply;
        [SerializeField] public BasisProgressReport Report = new BasisProgressReport();
        [SerializeField]
        public List<BasisLoadableBundle> avatarUrlsRuntime = new List<BasisLoadableBundle>();
        [SerializeField]
        public List<GameObject> createdCopies = new List<GameObject>();
        public CancellationToken CancellationToken = new CancellationToken();

        public GameObject AvatarSelectionPanel;
        public GameObject AvatarInformationPanel;

        public Button DeleteAvatar;
        public Button ShowAvatarPassword;
        public Button ShowAvatarURL;
        public Button GoBack;
        public Button ChangeIntoAvatar;
        public BasisLoadableBundle SelectedBundle;
        public TMP_InputField AvatarPassword;
        public TMP_InputField AvatarURL;
        public TextMeshProUGUI Name;
        public TextMeshProUGUI Description;
        public TextMeshProUGUI UniqueVersion;
        public RawImage AvatarBigImage;
        public Texture FallbackImage;
        public RectTransform Content;
        public GridLayoutGroup gridLayout;
        public List<Texture> AvatarImages = new List<Texture>();
        public GameObject WindowsIcon;
        public GameObject LinuxIcon;
        public GameObject AndroidIcon;
        public Sprite EyeOn;
        public Sprite EyeOff;
        public Image EyePasswordIcon;
        public Image EyeURLIcon;
        public bool ISShowingPassword = false;
        public bool ISShowingURL = false;
        private async void Start()
        {
            BasisDataStoreAvatarKeys.DisplayKeys();
            AddAvatarApply.onClick.AddListener(AddAvatar);

            GoBack.onClick.AddListener(ShowAvatarSelectionPanel);
            DeleteAvatar.onClick.AddListener(SelectedDeleteAvatar);
            ShowAvatarPassword.onClick.AddListener(SelectedShowAvatarPassword);
            ShowAvatarURL.onClick.AddListener(SelectedShowAvatarURL);

            EyePasswordIcon.sprite = EyeOn;
            EyeURLIcon.sprite = EyeOn;

            ShowAvatarSelectionPanel();
            await Initialize();
        }

        public void SelectedShowAvatarPassword()
        {
            ISShowingPassword = !ISShowingPassword;
            AvatarPassword.readOnly = true;
            if (ISShowingPassword)
            {
                AvatarPassword.text = SelectedBundle.UnlockPassword;
                EyePasswordIcon.sprite = EyeOn;
            }
            else
            {
                EyePasswordIcon.sprite = EyeOff;
                AvatarPassword.text = string.Empty;
            }
        }
        public void SelectedShowAvatarURL()
        {
            ISShowingURL = !ISShowingURL;
            AvatarURL.readOnly = true;
            if (ISShowingURL)
            { 
                AvatarURL.text = SelectedBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
                EyeURLIcon.sprite = EyeOn;
            }
            else
            {
                EyeURLIcon.sprite = EyeOff;
                AvatarURL.text = string.Empty;
            }
        }

        public void OnDestroy()
        {
            foreach (Texture image in AvatarImages)
            {
                Destroy(image);
            }
            AvatarImages.Clear();
        }
        public void SelectedDeleteAvatar()
        {

            BasisDataStoreAvatarKeys.AvatarKey Key = new BasisDataStoreAvatarKeys.AvatarKey()
            {
                Pass = SelectedBundle.UnlockPassword,
                Url = SelectedBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation
            };
            CloseThisMenu();
            BasisUIAcceptDenyPanel.OpenAcceptDenyPanel("do you want to delete this avatar?", (bool accepted) =>
            {
                if (accepted)
                {
                    RemoveKey(Key);
                    CloseThisMenu();
                }
            });
        }
        public async void RemoveKey(BasisDataStoreAvatarKeys.AvatarKey Key)
        {
            await BasisDataStoreAvatarKeys.RemoveKey(Key);
        }
        public override void InitalizeEvent()
        {
            BasisCursorManagement.UnlockCursor(AvatarSelection);
            BasisUINeedsVisibleTrackers.Instance.Add(this);
        }

        private void AddAvatar()
        {
            CloseThisMenu();
            BasisUIAddAvatar.OpenAddAvatarUI();
        }

        private async Task Initialize()
        {
            ClearCreatedCopies();
            avatarUrlsRuntime.Clear();
            avatarUrlsRuntime.AddRange(preLoadedBundles);
            await BasisDataStoreAvatarKeys.LoadKeys();

            int preloadedCount = preLoadedBundles.Count;
            for (int Index = 0; Index < preloadedCount; Index++)
            {
                BasisLoadableBundle loadableBundle = preLoadedBundles[Index];
                var key = new BasisDataStoreAvatarKeys.AvatarKey
                {
                    Pass = loadableBundle.UnlockPassword,
                    Url = loadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation
                };

                if (!BasisDataStoreAvatarKeys.DisplayKeys().Exists(k => k.Url == key.Url && k.Pass == key.Pass))
                {
                    await BasisDataStoreAvatarKeys.AddNewKey(key);
                }
            }

            // Work on a copy to prevent modification issues
            var activeKeys = new List<BasisDataStoreAvatarKeys.AvatarKey>(BasisDataStoreAvatarKeys.DisplayKeys());
            var validKeys = new List<BasisDataStoreAvatarKeys.AvatarKey>();
            var keysToRemove = new List<BasisDataStoreAvatarKeys.AvatarKey>();

            foreach (var key in activeKeys)
            {
                if (!BasisLoadHandler.IsMetaDataOnDisc(key.Url, out var info))
                {
                    switch (key.Url)
                    {
                        case BasisBeeConstants.DefaultAvatar:
                            break;
                        default:
                            if (string.IsNullOrEmpty(key.Url))
                            {
                                BasisDebug.LogError("Supplied URL was null or empty!");
                            }
                            else
                            {
                                BasisDebug.LogError("Missing File on Disc For " + key.Url);
                            }
                            break;
                    }

                    keysToRemove.Add(key);
                    continue;
                }

                validKeys.Add(key);

                // Prevent duplicates in avatarUrlsRuntime
                if (!avatarUrlsRuntime.Exists(b => b.BasisRemoteBundleEncrypted.RemoteBeeFileLocation == key.Url))
                {
                    var bundle = new BasisLoadableBundle
                    {
                        BasisRemoteBundleEncrypted = info.StoredRemote,
                        BasisBundleConnector = new BasisBundleConnector
                        {
                            BasisBundleDescription = new BasisBundleDescription(),
                            BasisBundleGenerated = new BasisBundleGenerated[] { new BasisBundleGenerated() },
                            UniqueVersion = ""
                        },
                        BasisLocalEncryptedBundle = info.StoredLocal,
                        UnlockPassword = key.Pass
                    };
                    avatarUrlsRuntime.Add(bundle);
                }
            }

            // Now remove all invalid keys
            foreach (var key in keysToRemove)
            {
                await BasisDataStoreAvatarKeys.RemoveKey(key);
            }

            await CreateAvatarButtons();
            UpdateHeight();
        }
        /// <summary>
        /// Call this if you already know the item count.
        /// </summary>
        public void UpdateHeight()
        {
            // Guard: no items -> just keep padding height.
            int count = createdCopies.Count;
            int rows, columns;
            GetGridDimensions(count, out rows, out columns);

            float cellH = gridLayout.cellSize.y;
            float spacingY = gridLayout.spacing.y;
            int padTop = gridLayout.padding.top;
            int padBot = gridLayout.padding.bottom;

            float totalHeight =
                (rows > 0 ? rows * cellH + (rows - 1) * spacingY : 0f) +
                padTop + padBot;

            // Use SetSizeWithCurrentAnchors so it behaves correctly even if the Content is vertically stretched.
            Content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, totalHeight);
        }
        /// <summary>
        /// Determines rows/columns based on GridLayoutGroup settings and item count.
        /// Handles FixedColumnCount, FixedRowCount, and Flexible.
        /// </summary>
        private void GetGridDimensions(int count, out int rows, out int columns)
        {
            rows = 0;
            columns = 0;

            if (count <= 0)
            {
                rows = 0;
                columns = 0;
                return;
            }

            switch (gridLayout.constraint)
            {
                case GridLayoutGroup.Constraint.FixedColumnCount:
                    {
                        columns = Mathf.Max(1, gridLayout.constraintCount);
                        rows = Mathf.CeilToInt((float)count / columns);
                        break;
                    }
                case GridLayoutGroup.Constraint.FixedRowCount:
                    {
                        rows = Mathf.Max(1, gridLayout.constraintCount);
                        columns = Mathf.CeilToInt((float)count / rows);
                        break;
                    }
                case GridLayoutGroup.Constraint.Flexible:
                default:
                    {
                        // Infer columns from available width if filling horizontally,
                        // otherwise fall back to 1 column.
                        bool fillHorizontal = gridLayout.startAxis == GridLayoutGroup.Axis.Horizontal;

                        if (fillHorizontal)
                        {
                            float availableWidth =
                                Content.rect.width
                                - gridLayout.padding.left
                                - gridLayout.padding.right;

                            float stepX = gridLayout.cellSize.x + gridLayout.spacing.x;
                            // columns = how many cells fit; add spacing back to allow exact fits
                            columns = Mathf.Max(1, Mathf.FloorToInt((availableWidth + gridLayout.spacing.x) / stepX));
                            rows = Mathf.CeilToInt((float)count / columns);
                        }
                        else
                        {
                            // Vertical fill + Flexible height is underdetermined without a target height,
                            // so default to one column.
                            columns = 1;
                            rows = count;
#if UNITY_EDITOR
                            Debug.LogWarning("[GridContentResizer] startAxis=Vertical with Flexible constraint: defaulting to 1 column. Consider using FixedColumnCount.");
#endif
                        }
                        break;
                    }
            }
        }
        private async Task CreateAvatarButtons()
        {
            for (int Index = 0; Index < avatarUrlsRuntime.Count; Index++)
            {
                BasisLoadableBundle bundle = avatarUrlsRuntime[Index];
                if (bundle == null)
                {
                    continue;
                }
                if (createdCopies.Exists(copy => copy != null && copy.name == bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
                {
                    Debug.LogWarning("Button for this avatar already exists: " + bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                    continue;
                }
                if (ParentedAvatarButtons == null)
                {
                    continue;
                }
                if (ButtonPrefab == null)
                {
                    continue;
                }

                GameObject buttonObject = Instantiate(ButtonPrefab, ParentedAvatarButtons);
                buttonObject.name = bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
                buttonObject.SetActive(true);
                if (buttonObject.TryGetComponent<BasisUIAvatarSelectionButton>(out BasisUIAvatarSelectionButton SelectionButton))
                {
                    SelectionButton.Button.onClick.AddListener(() => ShowInformation(bundle));
                    BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper
                    {
                        LoadableBundle = bundle
                    };
                    try
                    {
                        if (bundle.UnlockPassword == BasisBeeConstants.DefaultAvatar)
                        {
                            SelectionButton.Text.text = BasisBeeConstants.DefaultAvatar;
                        }
                        else
                        {
                            await BasisBeeManagement.HandleMetaOnlyLoad(wrapper, Report, CancellationToken);
                            SelectionButton.Text.text = wrapper.LoadableBundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName;
                            if (wrapper.LoadableBundle.BasisBundleConnector.ImageBytes != null)
                            {
                                SelectionButton.Image.texture = BasisTextureCompression.FromPngBytes(wrapper.LoadableBundle.BasisBundleConnector.ImageBytes);
                                AvatarImages.Add(SelectionButton.Image.texture);
                            }
                            else
                            {
                                SelectionButton.Image.texture = FallbackImage;
                            }
                        }
                    }
                    catch (Exception E)
                    {
                        BasisDebug.LogError(E);
                        BasisLoadHandler.RemoveDiscInfo(bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                        continue;
                    }
                }
                createdCopies.Add(buttonObject);
            }
        }
        private void ClearCreatedCopies()
        {
            foreach (var copy in createdCopies)
            {
                Destroy(copy);
            }
            createdCopies.Clear();
        }
        public void ShowAvatarSelectionPanel()
        {
            AvatarSelectionPanel.SetActive(true);
            AvatarInformationPanel.SetActive(false);
        }
        public void ShowInformationPanel()
        {
            AvatarSelectionPanel.SetActive(false);
            AvatarInformationPanel.SetActive(true);
        }
        private void ShowInformation(BasisLoadableBundle avatarLoadRequest)
        {
            if (BasisLocalPlayer.Instance != null)
            {
                ChangeIntoAvatar.onClick.RemoveAllListeners();
                SelectedBundle = avatarLoadRequest;

                ChangeIntoAvatar.onClick.AddListener(async () => await LoadAvatar(avatarLoadRequest));

                Name.text = SelectedBundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName;
                Description.text = SelectedBundle.BasisBundleConnector.BasisBundleDescription.AssetBundleDescription;
                UniqueVersion.text = SelectedBundle.BasisBundleConnector.UniqueVersion;

                string[] Platforms = SelectedBundle.BasisBundleConnector.BasisBundleGenerated.Select(pair => pair.Platform).ToArray();

                WindowsIcon.SetActive(false);
                AndroidIcon.SetActive(false);
                LinuxIcon.SetActive(false);

                foreach (string Platform in Platforms)
                {
                    switch (Platform)
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
                if (avatarLoadRequest.BasisBundleConnector.ImageBytes != null)
                {
                    AvatarBigImage.texture = BasisTextureCompression.FromPngBytes(avatarLoadRequest.BasisBundleConnector.ImageBytes);
                    AvatarImages.Add(AvatarBigImage.texture);
                }
                else
                {
                    AvatarBigImage.texture = FallbackImage;
                }


                ShowInformationPanel();
            }
            else
            {
                BasisDebug.LogError("Missing LocalPlayer!");
            }
        }
        private async Task LoadAvatar(BasisLoadableBundle avatarLoadRequest)
        {
            if (BasisLocalPlayer.Instance != null)
            {
                if (avatarLoadRequest.BasisBundleConnector.GetPlatform(out BasisBundleGenerated platformBundle))
                {
                    string assetMode = platformBundle.AssetMode;
                    byte mode = !string.IsNullOrEmpty(assetMode) && byte.TryParse(assetMode, out var result) ? result : (byte)0;
                    await BasisLocalPlayer.Instance.CreateAvatar(mode, avatarLoadRequest);
                }
                else
                {
                    if (avatarLoadRequest.UnlockPassword == BasisBeeConstants.DefaultAvatar)
                    {
                        await BasisLocalPlayer.Instance.CreateAvatar(1, avatarLoadRequest);
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
        public override void DestroyEvent()
        {
            BasisCursorManagement.LockCursor(AvatarSelection);
            BasisUINeedsVisibleTrackers.Instance.Remove(this);
        }
    }
}
