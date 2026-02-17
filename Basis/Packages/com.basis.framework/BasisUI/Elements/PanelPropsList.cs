using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
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
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using static Basis.BasisUI.PanelPropsList;
using static BundledContentHolder;
using static SerializableBasis;
using Debug = UnityEngine.Debug;

namespace Basis.BasisUI
{
    /// <summary>
    /// Stores and reconciles cached PROP bundles + saved keys.
    /// </summary>
    public static class CachedPropData
    {
        public static List<BasisLoadableBundleWrapper> PropBundles = new();
        public static bool Initialized;

        public static async Task FillPreloadedBundles(List<BasisLoadableBundleWrapper> bundles)
        {
            PropBundles.Clear();
            PropBundles.AddRange(bundles);

            int preloadedCount = bundles.Count;
            for (int i = 0; i < preloadedCount; i++)
            {
                BasisLoadableBundleWrapper Wrapper = bundles[i];

                // Default persistent for preloaded: false unless you store it elsewhere.
                BasisDataStoreItemKeys.ItemKey key = new()
                {
                    Pass = Wrapper.BasisLoadableBundle.UnlockPassword,
                    Url = Wrapper.BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation,
                    ISEmbedded = Wrapper.ISEmbedded,
                };

                BasisDataStoreItemKeys.ItemKey[] keys = BasisDataStoreItemKeys.DisplayKeys();
                bool found = false;

                for (int index = 0; index < keys.Length; index++)
                {
                    var cur = keys[index];
                    if (cur != null && cur.Url == key.Url && cur.Pass == key.Pass)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    await BasisDataStoreItemKeys.AddNewKey(key);
                }
            }
        }

        public static async Task Initialize()
        {
            await BasisDataStoreItemKeys.LoadKeys();
            List<BasisDataStoreItemKeys.ItemKey> activeKeys = new(BasisDataStoreItemKeys.DisplayKeys());
            List<BasisDataStoreItemKeys.ItemKey> keysToRemove = new();

            int count = activeKeys.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisDataStoreItemKeys.ItemKey key = activeKeys[Index];

                // If the metadata is missing on disk, remove the key and DO NOT attempt to create a bundle from it.
                if (!BasisLoadHandler.IsMetaDataOnDisc(key.Url, out BasisBEEExtensionMeta info))
                {
                    Debug.Log($"PropData: Did NOT find {key.Url}");
                    keysToRemove.Add(key);
                    continue;
                }

                // If we already have a bundle entry for this url, do nothing.
                if (PropBundles.Exists(b => b.BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation == key.Url))
                {
                    continue;
                }
                BasisLoadableBundleWrapper Wrapper = new BasisLoadableBundleWrapper();
                // Otherwise create a bundle entry from stored meta.
                BasisLoadableBundle bundle = new()
                {
                    BasisRemoteBundleEncrypted = info.StoredRemote,
                    BasisLocalEncryptedBundle = info.StoredLocal,
                    UnlockPassword = key.Pass,
                    BasisBundleConnector = new BasisBundleConnector()
                    {
                        BasisBundleDescription = new BasisBundleDescription(),
                        BasisBundleGenerated = new BasisBundleGenerated[] { new() },
                        UniqueVersion = info.UniqueVersion,
                    },
                };
                Wrapper.BasisLoadableBundle = bundle;
                Wrapper.ISEmbedded = false;
                PropBundles.Add(Wrapper);
            }

            foreach (BasisDataStoreItemKeys.ItemKey key in keysToRemove)
            {
                await BasisDataStoreItemKeys.RemoveKey(key);
            }

            Initialized = true;
        }
    }

    /// <summary>
    /// UI panel that lists PROPS/WORLDS, and spawns/unspawns them.
    /// Supports MULTIPLE instances per URL + per-entry persistent.
    /// </summary>
    public class PanelPropsList : PanelSelectionGroup
    {
        public static class PropListStyles
        {
            public static string Default = "Packages/com.basis.sdk/Prefabs/Panel Elements/Prop List Page.prefab";
        }

        public static PanelPropsList CreateNew(Component parent) => CreateNew<PanelPropsList>(PropListStyles.Default, parent);

        public class PropMenuItem
        {
            public PanelButton Button;
            public bool IsEmbedded;
            public BasisTrackedBundleWrapper Wrapper;

            public Texture2D IconTexture;
            public Sprite IconSprite;
            public void Clear()
            {
                Button.ReleaseInstance();
                if (IconTexture) UnityEngine.Object.Destroy(IconTexture);
                if (IconSprite) UnityEngine.Object.Destroy(IconSprite);
            }

            public async Task LoadItemData(BasisProgressReport report, CancellationToken cancellationToken)
            {
                string title = "Prop";
                if (IsEmbedded)
                {
                    try
                    {
                        IconSprite = null;
                        title = Wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
                        Button.Descriptor.SetIcon(IconSprite);
                        Button.Descriptor.SetTitle(title);
                        return;
                    }
                    catch (Exception e)
                    {
                        BasisDebug.LogError(e);
                        BasisLoadHandler.RemoveDiscInfo(Wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                        return;
                    }
                }
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await BasisBeeManagement.HandleMetaOnlyLoad(Wrapper, report, cancellationToken);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    BasisBundleDescription desc = Wrapper.LoadableBundle.BasisBundleConnector?.BasisBundleDescription;
                    if (desc != null && !string.IsNullOrWhiteSpace(desc.AssetBundleName))
                    {
                        title = desc.AssetBundleName;
                    }

                    string imageBytes = Wrapper.LoadableBundle.BasisBundleConnector?.ImageBase64;
                    if (!string.IsNullOrEmpty(imageBytes))
                    {
                        IconTexture = BasisTextureCompression.FromPngBytes(imageBytes);
                        if (IconTexture)
                        {
                            IconSprite = Sprite.Create(IconTexture, new Rect(0, 0, IconTexture.width, IconTexture.height), Vector2.zero);
                        }
                    }

                    if (IconSprite)
                    {
                        Button.Descriptor.SetIcon(IconSprite);
                    }
                }
                catch (Exception e)
                {
                    BasisDebug.LogError(e);
                    BasisLoadHandler.RemoveDiscInfo(Wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                    return;
                }

                Button.Descriptor.SetTitle(title);
            }
        }
        [System.Serializable]
        public class BasisLoadableBundleWrapper
        {
            public bool ISEmbedded = false;
            public bool ForceSpawnAtEyeLevel = false;
            public BasisLoadableBundle BasisLoadableBundle;
        }
        // (renamed) PreLoaded props
        [SerializeField]
        public List<BasisLoadableBundleWrapper> PreLoadedProps = new List<BasisLoadableBundleWrapper>();
        public bool ToggleLoadUnload = true;
        public bool RemoveAlsoUnloads = true;
        public bool Persistent = false;
        public bool UseCustomSpawnPosition = false;
        public Vector3 CustomSpawnPosition;
        public Quaternion CustomSpawnRotation = Quaternion.identity;
        public bool ApplyCustomScale = false;
        public Vector3 CustomSpawnScale = Vector3.one;

        public BasisProgressReport Report = new();
        public CancellationTokenSource CancellationSource = new();

        public PropMenuItem SelectedProp;

        public TextMeshProUGUI CreationDateLabel;
        public TextMeshProUGUI FileSizeLabel;

        public PanelPasswordField PropIDField;
        public PanelPasswordField PropUrlField;
        public PanelPasswordField PropPasswordField;

        public PanelDropdown Mode;
        public PanelToggle PersistentToggle;

        public GameObject WindowsIcon;
        public GameObject MacIcon;
        public GameObject LinuxIcon;
        public GameObject AndroidIcon;
        public GameObject IOSIcon;

        public PanelPropAddNew NewPropPanel; // you might rename later, kept for compatibility
        public PanelButton NewPropButton;

        public PanelButton RemovePropButton; // Removes from saved list
        public PanelButton LoadPropButton;   // Spawn / Unload-all toggle
        public PanelButton UnloadLastButton;
        public PanelButton UnloadAllButton;

        public List<PropMenuItem> MenuItems = new();

        public override void OnCreateEvent()
        {
            base.OnCreateEvent();

            ClearPropInfo();

            NewPropButton.OnClicked += NewPropPanel.Show;

            // Main action: spawn or unload-all depending on ToggleLoadUnload + existing instances
            LoadPropButton.OnClicked += () => _ = LoadOrUnloadSelected();

            // Remove-from-list behaviour
            RemovePropButton.OnClicked += RemoveProp;

            if (UnloadLastButton != null)
                UnloadLastButton.OnClicked += UnloadLastInstanceSelected;

            if (UnloadAllButton != null)
                UnloadAllButton.OnClicked += UnloadAllInstancesSelected;

            NewPropPanel.Hide();
            _ = LoadPropBundles();
        }

        public override void OnReleaseEvent()
        {
            base.OnReleaseEvent();

            if (CancellationSource != null)
            {
                CancellationSource.Cancel();
                CancellationSource.Dispose();
            }
        }

        private async Task LoadPropBundles()
        {
            if (!CachedPropData.Initialized)
            {
                await CachedPropData.FillPreloadedBundles(PreLoadedProps);
                await CachedPropData.Initialize();
            }

            await CreateButtons();
        }

        private bool TryGetStoredKeyForUrlPass(string url, string pass, out BasisDataStoreItemKeys.ItemKey key)
        {
            key = null;
            var keys = BasisDataStoreItemKeys.DisplayKeys();
            if (keys == null) return false;

            for (int Index = 0; Index < keys.Length; Index++)
            {
                var cur = keys[Index];
                if (cur != null && cur.Url == url && cur.Pass == pass)
                {
                    key = cur;
                    return true;
                }
            }

            return false;
        }

        private async Task CreateButtons()
        {
            foreach (PanelButton button in SelectionButtons) button.ReleaseInstance();
            SelectionButtons.Clear();
            MenuItems.Clear();

            foreach (BasisLoadableBundleWrapper Wrapper in CachedPropData.PropBundles)
            {
                PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.Prop, TabButtonParent);
                SelectionButtons.Add(button);
                button.Descriptor.SetTitle("Prop");

                var BasisLoadableBundle = Wrapper.BasisLoadableBundle;
                var url = BasisLoadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation ?? string.Empty;
                var pass = BasisLoadableBundle?.UnlockPassword ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(url) && TryGetStoredKeyForUrlPass(url, pass, out var storedKey))
                {
                }
                BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper();
                wrapper.LoadableBundle = BasisLoadableBundle;

                PropMenuItem item = new PropMenuItem
                {
                    Button = button,
                    Wrapper = wrapper,
                     IsEmbedded = Wrapper.ISEmbedded,
                };

                MenuItems.Add(item);

                button.OnClicked += () => OnTabSelected(button);
                button.OnClicked += () => ShowPropInfo(item);
            }

            foreach (PropMenuItem item in MenuItems)
            {
                await item.LoadItemData(Report, CancellationSource.Token);
            }
        }

        public async Task AppendNewProp(BasisLoadableBundle bundle, bool selectAfterCreate)
        {
            PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.Prop, TabButtonParent);
            SelectionButtons.Add(button);
            button.Descriptor.SetTitle("Prop");

            BasisTrackedBundleWrapper wrapper = new() { LoadableBundle = bundle };

            // Pull persistent from stored key (it was saved in AddNewKey)
            var url = bundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation ?? string.Empty;
            var pass = bundle?.UnlockPassword ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(url) && TryGetStoredKeyForUrlPass(url, pass, out var storedKey))
            {
            }

            PropMenuItem item = new()
            {
                Button = button,
                Wrapper = wrapper,
                 IsEmbedded = false,
            };

            MenuItems.Add(item);

            button.OnClicked += () => OnTabSelected(button);
            button.OnClicked += () => ShowPropInfo(item);

            await item.LoadItemData(Report, CancellationSource.Token);

            CachedPropData.PropBundles.Add(new BasisLoadableBundleWrapper());

            if (selectAfterCreate) button.OnClick();
        }

        private void ClearPropInfo()
        {
            SelectedProp = null;

            CreationDateLabel.text = string.Empty;
            FileSizeLabel.text = string.Empty;

            Descriptor.SetIcon(string.Empty);
            Descriptor.SetTitle(string.Empty);
            Descriptor.SetDescription(string.Empty);

            PropIDField.SetValue(false);
            PropIDField.SetPassword(string.Empty);

            PropUrlField.SetValue(false);
            PropUrlField.SetPassword(string.Empty);

            PropPasswordField.SetValue(false);
            PropPasswordField.SetPassword(string.Empty);

            List<string> Modes = new List<string>
            {
                "Local",
                "Networked"
            };
            Mode.AssignEntries(Modes);
            Mode.SetValueWithoutNotify("Local");
            Mode.Descriptor.SetTitle("Is This Network Synced?");
            WindowsIcon.SetActive(false);
            MacIcon.SetActive(false);
            LinuxIcon.SetActive(false);
            AndroidIcon.SetActive(false);
            IOSIcon.SetActive(false);

            if (PersistentToggle != null)
            {
                PersistentToggle.SetValueWithoutNotify(Persistent);
            }
            PersistentToggle.Descriptor.SetTitle("Sync Mode");
            PersistentToggle.Descriptor.SetDescription("Can this Object Be Loaded by joining clients?");

            NewPropPanel.Hide();
            RefreshLoadButtonLabel();
        }

        private void ShowPropInfo(PropMenuItem item)
        {
            if (item == null)
            {
                BasisDebug.LogError("No prop menu item provided.");
                ClearPropInfo();
                return;
            }

            SelectedProp = item;

            BasisLoadableBundle bundle = item.Wrapper.LoadableBundle;
            if (bundle == null)
            {
                BasisDebug.LogError($"Bundle on PropMenuItem {item} not found.");
                RemovePropItem(item);
                ClearPropInfo();
                return;
            }

            BasisBundleDescription description = bundle.BasisBundleConnector?.BasisBundleDescription;
            if (description == null)
            {
                BasisDebug.LogError($"Bundle Description on PropMenuItem {item} not found.");
                RemovePropItem(item);
                ClearPropInfo();
                return;
            }

            Descriptor.SetIcon(item.IconSprite);
            Descriptor.SetTitle(description.AssetBundleName);
            Descriptor.SetDescription(description.AssetBundleDescription);

            PropIDField.SetValue(false);
            PropIDField.SetPassword(bundle.BasisBundleConnector.UniqueVersion);

            PropUrlField.SetValue(false);
            PropUrlField.SetPassword(bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);

            PropPasswordField.SetValue(false);
            PropPasswordField.SetPassword(bundle.UnlockPassword);
            Mode.Descriptor.SetTitle("Sync Mode");
            PersistentToggle.Descriptor.SetTitle("Is Network Persistent?");
            PersistentToggle.Descriptor.SetDescription("Can this Object Be Loaded by joining clients?");
            string creationDate = bundle.BasisBundleConnector.DateOfCreation;
            if (string.IsNullOrEmpty(creationDate))
            {
                creationDate = string.Empty;
            }
            else
            {
                creationDate = DateTime.Parse(creationDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToString(CultureInfo.InvariantCulture);
                creationDate += " UTC";
            }

            CreationDateLabel.text = creationDate;

            string[] platforms = bundle.BasisBundleConnector.BasisBundleGenerated.Select(pair => pair.Platform).ToArray();

            WindowsIcon.SetActive(false);
            MacIcon.SetActive(false);
            LinuxIcon.SetActive(false);
            AndroidIcon.SetActive(false);
            IOSIcon.SetActive(false);

            foreach (string platform in platforms)
            {
                switch (platform)
                {
                    case "StandaloneWindows64": WindowsIcon.SetActive(true); break;
                    case "StandaloneOSX": MacIcon.SetActive(true); break;
                    case "StandaloneLinux64": LinuxIcon.SetActive(true); break;
                    case "Android": AndroidIcon.SetActive(true); break;
                    case "iOS": IOSIcon.SetActive(true); break;
                }
            }

            NewPropPanel.Hide();
            RefreshLoadButtonLabel();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
        }

        private Vector3 GetSpawnPosition()
        {
            if (UseCustomSpawnPosition)
            {
                return CustomSpawnPosition;
            }

            if (BasisLocalPlayer.Instance != null)
            {
                return BasisLocalPlayer.Instance.transform.position;
            }

            if (Camera.main != null)
            {
                return Camera.main.transform.position;
            }

            return Vector3.zero;
        }

        private Quaternion GetSpawnRotation()
        {
            if (CustomSpawnRotation == new Quaternion(0, 0, 0, 0))
            {
                CustomSpawnRotation = Quaternion.identity;
            }

            return UseCustomSpawnPosition ? CustomSpawnRotation : Quaternion.identity;
        }

        private Vector3 GetSpawnScale()
        {
            if (!ApplyCustomScale)
            {
                return Vector3.one;
            }

            if (CustomSpawnScale == Vector3.zero)
            {
                CustomSpawnScale = Vector3.one;
            }

            return CustomSpawnScale;
        }

        private string SelectedUrl()
        {
            return SelectedProp?.Wrapper?.LoadableBundle?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation ?? string.Empty;
        }

        private void RefreshLoadButtonLabel()
        {
            if (LoadPropButton == null)
            {
                return;
            }

            string url = SelectedUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                LoadPropButton.Descriptor.SetTitle("Spawn");
                return;
            }

            int instanceCount = Basis.BasisRuntimeSpawnRegistry.Count(url);

            if (ToggleLoadUnload && instanceCount > 0)
            {
                LoadPropButton.Descriptor.SetTitle($"Unload All ({instanceCount})");
            }
            else
            {
                LoadPropButton.Descriptor.SetTitle("Spawn (+1)");
            }
        }
        public async Task LoadOrUnloadSelected()
        {
            if (SelectedProp == null)
            {
                BasisDebug.LogError("No selected bundle.");
                return;
            }

            var bundle = SelectedProp.Wrapper.LoadableBundle;
            if (bundle == null)
            {
                BasisDebug.LogError("Selected bundle is null.");
                return;
            }

            string url = bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
            if (string.IsNullOrWhiteSpace(url))
            {
                BasisDebug.LogError("Selected bundle URL is empty.");
                return;
            }

            // Toggle means: if anything is loaded, unload ALL; otherwise spawn a new instance.
            if (ToggleLoadUnload && Basis.BasisRuntimeSpawnRegistry.HasAny(url))
            {
                UnloadAllInstancesSelected();
                RefreshLoadButtonLabel();
                return;
            }

            await SpawnSelectedNewInstance();
            RefreshLoadButtonLabel();
        }

        /// <summary>
        /// Always spawns a NEW instance, even if already spawned before.
        /// </summary>
        public async Task SpawnSelectedNewInstance()
        {
            if (SelectedProp == null)
            {
                BasisDebug.LogError("No selected bundle.");
                return;
            }
            var Wrapper = SelectedProp.Wrapper;
            var bundle = Wrapper.LoadableBundle;
            if (bundle == null)
            {
                BasisDebug.LogError("Selected bundle is null.");
                return;
            }

            string url = bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
            string pass = bundle.UnlockPassword;

            if (string.IsNullOrWhiteSpace(url))
            {
                BasisDebug.LogError("Bundle URL is empty.");
                return;
            }

            Vector3 spawnPos = GetSpawnPosition();
            Quaternion spawnRot = GetSpawnRotation();
            Vector3 spawnScale = GetSpawnScale();

            bool persistent = PersistentToggle.Value;
            if (SelectedProp.IsEmbedded)
            {
                AsyncOperationHandle<GameObject> op = Addressables.LoadAssetAsync<GameObject>(bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                GameObject CreatedObject = op.WaitForCompletion();
                var inSceneLoadingAvatar = GameObject.Instantiate(CreatedObject, spawnPos, spawnRot, BasisDeviceManagement.Instance.transform);
            }
            else
            {
                if (Mode.Value == "Local")
                {
                    BasisProgressReport Report = new BasisProgressReport();
                    CancellationToken Cancel = new CancellationToken();
                    GameObject CreateObject = await BasisLoadHandler.LoadGameObjectBundle(bundle, true, Report, Cancel, spawnPos, spawnRot, spawnScale, ApplyCustomScale, Selector.Prop, BasisNetworkManagement.Instance.transform);
                }
                else
                {
                    if (Mode.Value == "Networked")
                    {
                        BasisNetworkSpawnItem.RequestGameObjectLoad(pass, url, spawnPos, spawnRot, spawnScale, persistent, ApplyCustomScale, out LocalLoadResource loadedProp);
                        // Store as a new instance (1-to-many)
                        Basis.BasisRuntimeSpawnRegistry.Add(url, loadedProp.LoadedNetID, persistent, out _);
                    }
                }
            }

            await Task.CompletedTask;
        }
        /// <summary>
        /// Unloads ONLY the most recently spawned instance for the selected URL.
        /// </summary>
        public void UnloadLastInstanceSelected()
        {
            string url = SelectedUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                BasisDebug.LogError("No selected bundle URL.");
                return;
            }

            RefreshLoadButtonLabel();
        }
        /// <summary>
        /// Unloads ALL spawned instances for the selected URL.
        /// </summary>
        public void UnloadAllInstancesSelected()
        {
            string url = SelectedUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                BasisDebug.LogError("No selected bundle URL.");
                return;
            }

            var instances = Basis.BasisRuntimeSpawnRegistry.GetInstances(url);
            if (instances == null || instances.Count == 0)
            {
                BasisDebug.Log("Nothing loaded for this item.");
                RefreshLoadButtonLabel();
                return;
            }

            for (int i = instances.Count - 1; i >= 0; i--)
            {
                var inst = instances[i];
                if (inst != null && !string.IsNullOrEmpty(inst.LoadedNetID))
                {
                    BasisNetworkSpawnItem.RequestGameObjectUnLoad(inst.LoadedNetID);
                }
            }

            Basis.BasisRuntimeSpawnRegistry.ClearAll(url);
            RefreshLoadButtonLabel();
        }

        public void RemoveProp()
        {
            if (SelectedProp == null)
            {
                BasisDebug.LogError("No selected bundle.");
                return;
            }

            BasisMainMenu.Instance.OpenDialogue(
                "Basis",
                "Are you sure you want to remove this item from your list?",
                "Cancel",
                "Remove",
                value =>
                {
                    if (value) return; // your dialog uses "value==true means cancel" pattern
                    RemovePropItem(SelectedProp);
                }
            );
        }

        public void RemovePropItem(PropMenuItem menuItem)
        {
            if (menuItem == null) return;

            var bundle = menuItem.Wrapper.LoadableBundle;

            if (bundle != null && RemoveAlsoUnloads)
            {
                string url = bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;

                // Unload ALL instances for this URL
                var instances = Basis.BasisRuntimeSpawnRegistry.GetInstances(url);
                for (int Index = instances.Count - 1; Index >= 0; Index--)
                {
                    var inst = instances[Index];
                    if (inst != null && !string.IsNullOrEmpty(inst.LoadedNetID))
                    {
                        BasisNetworkSpawnItem.RequestGameObjectUnLoad(inst.LoadedNetID);
                    }
                }
                Basis.BasisRuntimeSpawnRegistry.ClearAll(url);
            }

            // Remove saved key
            BasisDataStoreItemKeys.ItemKey key = new()
            {
                Pass = menuItem.Wrapper.LoadableBundle.UnlockPassword,
                Url = menuItem.Wrapper.LoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation,
            };

            MenuItems.Remove(menuItem);
            SelectionButtons.Remove(menuItem.Button);
            menuItem.Clear();

            _ = RemoveKey(key);

            ClearPropInfo();
        }

        public async Task RemoveKey(BasisDataStoreItemKeys.ItemKey key)
        {
            await BasisDataStoreItemKeys.RemoveKey(key);
        }
    }
}
