using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.UI.UI_Panels;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class BasisAvatarSelection : PanelComponent
    {
        [field: SerializeField] public RawImage ThumbnailImage { get; protected set; }
        [field: SerializeField] public PanelGridList AvatarList { get; protected set; }
        [field: SerializeField] public Texture2D FallbackAvatarIcon { get; protected set; }
        [field: SerializeField] public TextMeshProUGUI AvatarNameLabel { get; protected set; }
        [field: SerializeField] public TextMeshProUGUI AvatarCreatorField { get; protected set; }
        [field: SerializeField] public TextMeshProUGUI AvatarDescriptionLabel { get; protected set; }
        [field: SerializeField] public PanelButton AddNewButton { get; protected set; }
        [field: SerializeField] public PanelButton LoadAvatarButton { get; protected set; }
        [field: SerializeField] public PanelPasswordField AvatarUrlField { get; protected set; }
        [field: SerializeField] public PanelPasswordField AvatarPasswordField { get; protected set; }

        public BasisProgressReport Report = new();
        public CancellationToken CancellationToken = new CancellationToken();


        public override async void OnCreateEvent()
        {
            base.OnCreateEvent();
            AvatarList.OnValueChanged += OnItemSelected;
           await LoadAvatarsFromDisc();
        }

        private void OnItemSelected(int index)
        {

        }

        #region Avatar Data Loading


        public List<BasisLoadableBundle> RuntimeAvatarBundles = new();


        public async Task LoadAvatarsFromDisc()
        {
            RuntimeAvatarBundles.Clear();
            await BasisDataStoreAvatarKeys.LoadKeys();
            Debug.Log("Keys Loaded");

            // Work on a copy to prevent modification issues
            List<BasisDataStoreAvatarKeys.AvatarKey> activeKeys = new(BasisDataStoreAvatarKeys.DisplayKeys());
            List<BasisDataStoreAvatarKeys.AvatarKey> validKeys = new();
            List<BasisDataStoreAvatarKeys.AvatarKey> keysToRemove = new();

            foreach (BasisDataStoreAvatarKeys.AvatarKey key in activeKeys)
            {
                if (!BasisLoadHandler.IsMetaDataOnDisc(key.Url, out BasisBEEExtensionMeta info))
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
                if (!RuntimeAvatarBundles.Exists(b => b.BasisRemoteBundleEncrypted.RemoteBeeFileLocation == key.Url))
                {
                    BasisLoadableBundle bundle = new()
                    {
                        BasisRemoteBundleEncrypted = info.StoredRemote,
                        BasisBundleConnector = new BasisBundleConnector
                        {
                            BasisBundleDescription = new BasisBundleDescription(),
                            BasisBundleGenerated = new BasisBundleGenerated[] { new() },
                            UniqueVersion = ""
                        },
                        BasisLocalEncryptedBundle = info.StoredLocal,
                        UnlockPassword = key.Pass
                    };
                    RuntimeAvatarBundles.Add(bundle);
                }
            }

            // Now remove all invalid keys
            foreach (BasisDataStoreAvatarKeys.AvatarKey key in keysToRemove)
            {
                await BasisDataStoreAvatarKeys.RemoveKey(key);
            }

            for (int Index = 0; Index < RuntimeAvatarBundles.Count; Index++)
            {
                BasisLoadableBundle bundle = RuntimeAvatarBundles[Index];
                BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper
                {
                    LoadableBundle = bundle
                };

                try
                {
                    string title = string.Empty;
                    Texture2D texture = null;
                    if (bundle.UnlockPassword == BasisBeeConstants.DefaultAvatar)
                    {
                        title = BasisBeeConstants.DefaultAvatar;
                    }
                    else
                    {
                        await BasisBeeManagement.HandleMetaOnlyLoad(wrapper, Report, CancellationToken);
                        title = wrapper.LoadableBundle.BasisBundleConnector.BasisBundleDescription.AssetBundleName;
                        if (wrapper.LoadableBundle.BasisBundleConnector.ImageBase64 != null)
                        {
                            texture = BasisTextureCompression.FromPngBytes(wrapper.LoadableBundle.BasisBundleConnector.ImageBase64);
                        }
                        else
                        {
                            texture = FallbackAvatarIcon;
                        }
                    }

                    AvatarList.AddItem(new PanelGridItem.Data(title, texture));
                }
                catch (Exception e)
                {
                    BasisDebug.LogError(e);
                    BasisLoadHandler.RemoveDiscInfo(bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                    continue;
                }
            }

        }

        #endregion
    }
}
