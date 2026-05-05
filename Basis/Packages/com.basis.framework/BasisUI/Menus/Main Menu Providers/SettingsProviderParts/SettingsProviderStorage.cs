using Basis.BasisUI;
using System.Collections.Generic;
using UnityEngine;

public static class SettingsProviderStorage
{
    private const string TabKey = "settings.tab.downloadsurls";

    public static PanelTabPage DownloadsUrlsTab(PanelTabGroup tabGroup)
    {
        PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
        PanelElementDescriptor descriptor = tab.Descriptor;
        descriptor.SetIcon(AddressableAssets.Sprites.Settings);
        descriptor.SetTitle(BasisLocalization.Get(TabKey));

        RectTransform container = descriptor.ContentParent;

        // Download limits
        PanelElementDescriptor downloadGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        downloadGroup.SetTitle(BasisLocalization.Get("settings.storage.downloadLimits.title"));
        downloadGroup.SetDescription(BasisLocalization.Get("settings.storage.downloadLimits.description"));

        PanelSlider avatarDownloadSize = PanelSlider.CreateEntryAndBind(
            downloadGroup.ContentParent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.storage.avatarDownloadSize"), 5, 1024, false, 0, ValueDisplayMode.MemorySize),
            BasisSettingsDefaults.AvatarDownloadSize);

        // Concurrency gates for avatar loading. Three separate gates because the
        // network / disc / in-memory paths each have a different bottleneck. Tuning
        // these higher helps crowded rooms catch up faster; tuning downloads too high
        // just splits bandwidth and makes everyone wait longer on the loading avatar.
        PanelSlider maxDownloads = PanelSlider.CreateEntryAndBind(
            downloadGroup.ContentParent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.storage.maxDownloads"), 1, 32, true, 0, ValueDisplayMode.Raw),
            BasisSettingsDefaults.MaxConcurrentAvatarDownloads);

        PanelSlider maxDiscLoads = PanelSlider.CreateEntryAndBind(
            downloadGroup.ContentParent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.storage.maxDiscLoads"), 1, 64, true, 0, ValueDisplayMode.Raw),
            BasisSettingsDefaults.MaxConcurrentAvatarDiscLoads);

        PanelSlider maxAddressables = PanelSlider.CreateEntryAndBind(
            downloadGroup.ContentParent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.storage.maxAddressables"), 1, 128, true, 0, ValueDisplayMode.Raw),
            BasisSettingsDefaults.MaxConcurrentAvatarAddressables);

        // Cache size limit slider (lightweight, no file I/O)
        PanelElementDescriptor limitGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        limitGroup.SetTitle(BasisLocalization.Get("settings.storage.cache.title"));
        limitGroup.SetDescription(BasisLocalization.Get("settings.storage.cache.description"));

        PanelSlider cacheSizeSlider = PanelSlider.CreateEntryAndBind(
            limitGroup.ContentParent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.storage.maxCacheSize"), 1, 512, true, 0, ValueDisplayMode.Raw),
            BasisSettingsDefaults.CacheMaxSizeGB);

        // Button to load and display all storage data on demand
        PanelButton loadDataButton = PanelButton.CreateNew(container);
        loadDataButton.Descriptor.SetTitle(BasisLocalization.Get("settings.storage.loadButton"));
        loadDataButton.Descriptor.SetDescription(BasisLocalization.Get("settings.storage.loadButton.description"));
        loadDataButton.OnClicked += () =>
        {
            // Remove the load button itself
            Object.Destroy(loadDataButton.gameObject);

            PopulateStorageData(container);
            descriptor.ForceRebuild();
        };

        SettingsProviderTrustedUrls.Populate(container, TabKey);

        // One reset button for this whole page (download limits, cache, trusted URLs)
        SettingsProvider.AddResetPageButton(container, TabKey, ResetDefaults);

        descriptor.ForceRebuild();
        return tab;
    }

    private static void PopulateStorageData(RectTransform container)
    {
        long totalBytes = BasisStorageManagement.GetTotalCacheSizeBytes();
        string sizeText = BasisStorageManagement.FormatBytes(totalBytes);
        string limitText = BasisStorageManagement.FormatBytes(BasisStorageManagement.MaxCacheSizeBytes);

        List<BasisStorageManagement.StoredBeeFileInfo> storedFiles = BasisStorageManagement.GetAllStoredFiles();

        // Cache size info group
        PanelElementDescriptor infoGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        infoGroup.SetTitle(BasisLocalization.Get("settings.storage.cacheInfo"));

        PanelPasswordField cacheInfoField = PanelPasswordField.CreateNew(infoGroup.ContentParent);
        cacheInfoField.Descriptor.SetTitle(BasisLocalization.Get("settings.storage.totalCacheSize"));
        cacheInfoField.SetPassword($"{sizeText} / {limitText}");

        PanelPasswordField fileCountField = PanelPasswordField.CreateNew(infoGroup.ContentParent);
        fileCountField.Descriptor.SetTitle(BasisLocalization.Get("settings.storage.storedFiles"));
        fileCountField.SetPassword(BasisLocalization.Get("settings.storage.fileCount", storedFiles.Count));

        // Clear all cache button
        PanelButton clearAllButton = PanelButton.CreateNew(container);
        clearAllButton.Descriptor.SetTitle(BasisLocalization.Get("settings.storage.clearAll"));
        clearAllButton.Descriptor.SetDescription(BasisLocalization.Get("settings.storage.clearAll.description"));
        clearAllButton.OnClicked += () =>
        {
            BasisMainMenu.Instance.OpenDialogue(
                BasisLocalization.Get("settings.storage.clearAll"),
                BasisLocalization.Get("settings.storage.clearAll.confirm", storedFiles.Count, sizeText),
                BasisLocalization.Get("settings.storage.clearAll.button"),
                BasisLocalization.Get("ui.cancel"),
                value =>
                {
                    if (!value) return;
                    BasisStorageManagement.ClearAllCache();
                    BasisMainMenu.Close();
                    BasisMainMenu.OpenWithProvider(SettingsProvider.StaticTitle);
                });
        };

        // Individual file list group
        if (storedFiles.Count > 0)
        {
            PanelElementDescriptor filesGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            filesGroup.SetTitle(BasisLocalization.Get("settings.storage.storedBeeFiles"));
            filesGroup.SetDescription(BasisLocalization.Get("settings.storage.storedBeeFiles.description"));

            foreach (var file in storedFiles)
            {
                string fileName = string.IsNullOrWhiteSpace(file.UniqueVersion)
                    ? (string.IsNullOrWhiteSpace(file.LocalPath) ? "Unknown Cache Entry" : System.IO.Path.GetFileName(file.LocalPath))
                    : file.UniqueVersion;
                string size = BasisStorageManagement.FormatBytes(file.FileSizeBytes);
                string loadedStatus = file.IsLoadedInMemory ? " [IN USE]" : "";
                string remoteUrl = file.RemoteUrl;

                PanelButton fileButton = PanelButton.CreateNew(filesGroup.ContentParent);
                fileButton.Descriptor.SetTitle($"{fileName} ({size}){loadedStatus}");
                fileButton.Descriptor.SetDescription(remoteUrl);
                fileButton.OnClicked += () =>
                {
                    BasisMainMenu.Instance.OpenDialogue(
                        BasisLocalization.Get("settings.storage.deleteFile"),
                        BasisLocalization.Get("settings.storage.deleteFile.confirm", fileName, size) +
                            (file.IsLoadedInMemory ? "\n\n" + BasisLocalization.Get("settings.storage.deleteFile.inUse") : ""),
                        BasisLocalization.Get("library.delete"),
                        BasisLocalization.Get("ui.cancel"),
                        value =>
                        {
                            if (!value) return;
                            BasisStorageManagement.DeleteStoredFile(remoteUrl);
                            BasisMainMenu.Close();
                            BasisMainMenu.OpenWithProvider(SettingsProvider.StaticTitle);
                        });
                };
            }
        }
    }

    private static void ResetDefaults()
    {
        BasisSettingsDefaults.AvatarDownloadSize.ResetToDefault();
        BasisSettingsDefaults.CacheMaxSizeGB.ResetToDefault();
        BasisSettingsDefaults.MaxConcurrentAvatarDownloads.ResetToDefault();
        BasisSettingsDefaults.MaxConcurrentAvatarDiscLoads.ResetToDefault();
        BasisSettingsDefaults.MaxConcurrentAvatarAddressables.ResetToDefault();
        SettingsProviderTrustedUrls.Reset();
    }
}
