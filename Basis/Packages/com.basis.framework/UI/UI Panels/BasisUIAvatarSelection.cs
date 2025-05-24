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
        public Button GoBack;
        public Button ChangeIntoAvatar;
        public BasisLoadableBundle SelectedBundle;
        public TMP_InputField AvatarPassword;
        public TextMeshProUGUI Name;
        public TextMeshProUGUI Description;
        private async void Start()
        {
            BasisDataStoreAvatarKeys.DisplayKeys();
            AddAvatarApply.onClick.AddListener(AddAvatar);

            GoBack.onClick.AddListener(ShowAvatarSelectionPanel);
            DeleteAvatar.onClick.AddListener(SelectedDeleteAvatar);
            ShowAvatarPassword.onClick.AddListener(SelectedShowAvatarPassword);
            ShowAvatarSelectionPanel();
            AvatarPassword.gameObject.SetActive(false);
            await Initialize();
        }
        public async void SelectedDeleteAvatar()
        {
            BasisDataStoreAvatarKeys.AvatarKey Key = new BasisDataStoreAvatarKeys.AvatarKey()
            {
                Pass = SelectedBundle.UnlockPassword,
                Url = SelectedBundle.BasisRemoteBundleEncrypted.CombinedURL
            };
            await BasisDataStoreAvatarKeys.RemoveKey(Key);
            CloseThisMenu();
        }
        public void SelectedShowAvatarPassword()
        {
            AvatarPassword.gameObject.SetActive(!AvatarPassword.gameObject.activeSelf);
            AvatarPassword.text = SelectedBundle.UnlockPassword;
            AvatarPassword.readOnly = true;
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
            int PreloadedCount = preLoadedBundles.Count;
            for (int Index = 0; Index < PreloadedCount; Index++)
            {
                BasisLoadableBundle loadableBundle = preLoadedBundles[Index];
                BasisDataStoreAvatarKeys.AvatarKey Key = new BasisDataStoreAvatarKeys.AvatarKey()
                {
                    Pass = loadableBundle.UnlockPassword,
                    Url = loadableBundle.BasisRemoteBundleEncrypted.CombinedURL
                };

                // Prevent duplicate keys in the store
                if (!BasisDataStoreAvatarKeys.DisplayKeys().Exists(k => k.Url == Key.Url && k.Pass == Key.Pass))
                {
                    await BasisDataStoreAvatarKeys.AddNewKey(Key);
                }
            }

            List<BasisDataStoreAvatarKeys.AvatarKey> activeKeys = BasisDataStoreAvatarKeys.DisplayKeys();
            int AKcount = activeKeys.Count;
            for (int Index = 0; Index < AKcount; Index++)
            {
                if (!BasisLoadHandler.IsMetaDataOnDisc(activeKeys[Index].Url, out var info))
                {
                    if (activeKeys[Index].Url == BasisLocalPlayer.DefaultAvatar)
                    {

                    }
                    else
                    {
                        if (string.IsNullOrEmpty(activeKeys[Index].Url))
                        {
                            BasisDebug.LogError("Supplied URL was null or empty!");
                        }
                        else
                        {
                            BasisDebug.LogError("Missing File on Disc For " + activeKeys[Index].Url);
                        }
                    }
                    await BasisDataStoreAvatarKeys.RemoveKey(activeKeys[Index]);
                    continue;
                }

                // Prevent duplicates in avatarUrlsRuntime
                if (!avatarUrlsRuntime.Exists(b => b.BasisRemoteBundleEncrypted.CombinedURL == activeKeys[Index].Url))
                {
                    BasisLoadableBundle bundle = new BasisLoadableBundle
                    {
                        BasisRemoteBundleEncrypted = info.StoredRemote,
                        BasisBundleConnector = new BasisBundleConnector
                        {
                            BasisBundleDescription = new BasisBundleDescription(),
                            BasisBundleGenerated = new BasisBundleGenerated[] { new BasisBundleGenerated() },
                            UniqueVersion = ""
                        },
                        BasisLocalEncryptedBundle = info.StoredLocal,
                        UnlockPassword = activeKeys[Index].Pass
                    };
                    avatarUrlsRuntime.Add(bundle);
                }
            }

            await CreateAvatarButtons(activeKeys);
        }
        private async Task CreateAvatarButtons(List<BasisDataStoreAvatarKeys.AvatarKey> activeKeys)
        {
            foreach (BasisLoadableBundle bundle in avatarUrlsRuntime)
            {
                if (bundle == null)
                {
                    continue;
                }
                if (createdCopies.Exists(copy => copy != null && copy.name == bundle.BasisRemoteBundleEncrypted.CombinedURL))
                {
                    Debug.LogWarning("Button for this avatar already exists: " + bundle.BasisRemoteBundleEncrypted.CombinedURL);
                    continue;
                }
                GameObject buttonObject = Instantiate(ButtonPrefab, ParentedAvatarButtons);
                buttonObject.name = bundle.BasisRemoteBundleEncrypted.CombinedURL;
                buttonObject.SetActive(true);
                if (buttonObject.TryGetComponent<BasisUIAvatarSelectionButton>(out var SelectionButton))
                {
                    SelectionButton.Button.onClick.AddListener(() => ShowInformation(bundle));
                    BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper
                    {
                        LoadableBundle = bundle
                    };
                    try
                    {
                        if (bundle.UnlockPassword == BasisLocalPlayer.DefaultAvatar)
                        {
                            SelectionButton.Text.text = BasisLocalPlayer.DefaultAvatar;
                        }
                        else
                        {
                            await BasisLoadHandler.HandleBundleAndMetaLoading(wrapper, Report, CancellationToken);
                            SelectionButton.Text.text = wrapper.LoadableBundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName;
                        }
                    }
                    catch (Exception E)
                    {
                        BasisDebug.LogError(E);
                        BasisLoadHandler.RemoveDiscInfo(bundle.BasisRemoteBundleEncrypted.CombinedURL);
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
        public TextMeshProUGUI UniqueVersion;
        public TextMeshProUGUI SupportedPlatformsText;
        private void ShowInformation(BasisLoadableBundle avatarLoadRequest)
        {
            if (BasisLocalPlayer.Instance != null)
            {
                ChangeIntoAvatar.onClick.RemoveAllListeners();
                SelectedBundle = avatarLoadRequest;
                ChangeIntoAvatar.onClick.AddListener(async () => await LoadAvatar(avatarLoadRequest)); // Fix: Use lambda

                if (SelectedBundle.BasisBundleConnector.GetPlatform(out BasisBundleGenerated platformBundle))
                {
                    string assetMode = platformBundle.AssetMode;
                }
                Name.text = $"Avatar Name: {SelectedBundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName}";
                Description.text = $"Avatar Description: {SelectedBundle.BasisBundleConnector.BasisBundleDescription.AssetBundleDescription}";
                UniqueVersion.text = $"Version ID: {SelectedBundle.BasisBundleConnector.UniqueVersion}";
                string SupportedPlatforms = string.Join(", ", SelectedBundle.BasisBundleConnector.BasisBundleGenerated
                    .Select(pair => pair.Platform));
                SupportedPlatformsText.text = "Supported Platforms : " + SupportedPlatforms;
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
                    if (avatarLoadRequest.UnlockPassword == BasisLocalPlayer.DefaultAvatar)
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
