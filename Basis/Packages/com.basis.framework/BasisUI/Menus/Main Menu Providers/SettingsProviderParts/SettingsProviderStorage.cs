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
        PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
            BasisLocalization.Get("settings.storage.downloadLimits.title"), () =>
        {
            PanelSlider avatarDownloadSize = PanelSlider.CreateEntryAndBind(
                container,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.storage.avatarDownloadSize"), 5, 1024, false, 0, ValueDisplayMode.MemorySize),
                BasisSettingsDefaults.AvatarDownloadSize);
            avatarDownloadSize.Descriptor.SetTooltip(BasisLocalization.Get("settings.storage.avatarDownloadSize.tooltip"));

            // Concurrency gates for avatar loading. Three separate gates because the
            // network / disc / in-memory paths each have a different bottleneck. Tuning
            // these higher helps crowded rooms catch up faster; tuning downloads too high
            // just splits bandwidth and makes everyone wait longer on the loading avatar.
            PanelSlider maxDownloads = PanelSlider.CreateEntryAndBind(
                container,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.storage.maxDownloads"), 1, 32, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MaxConcurrentAvatarDownloads);
            maxDownloads.Descriptor.SetTooltip(BasisLocalization.Get("settings.storage.maxDownloads.tooltip"));

            PanelSlider maxDiscLoads = PanelSlider.CreateEntryAndBind(
                container,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.storage.maxDiscLoads"), 1, 64, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MaxConcurrentAvatarDiscLoads);
            maxDiscLoads.Descriptor.SetTooltip(BasisLocalization.Get("settings.storage.maxDiscLoads.tooltip"));

            PanelSlider maxAddressables = PanelSlider.CreateEntryAndBind(
                container,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.storage.maxAddressables"), 1, 128, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.MaxConcurrentAvatarAddressables);
            maxAddressables.Descriptor.SetTooltip(BasisLocalization.Get("settings.storage.maxAddressables.tooltip"));
        }, false, _ => descriptor.ForceRebuild());

        // Cache size limit slider (lightweight, no file I/O) plus the on-demand
        // storage-data loader, all under the one collapsible Cache section.
        PanelSectionToggle cacheSection = null;
        List<PanelElementDescriptor> storageRows = new List<PanelElementDescriptor>();
        cacheSection = PanelSectionToggleHelpers.CreateCollapsibleFlatSection(container,
            BasisLocalization.Get("settings.storage.cache.title"), () =>
        {
            PanelSlider cacheSizeSlider = PanelSlider.CreateEntryAndBind(
                container,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.storage.maxCacheSize"), 1, 512, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.CacheMaxSizeGB);
            cacheSizeSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.storage.maxCacheSize.tooltip"));

            // Button to load and display all storage data on demand
            PanelButton loadDataButton = PanelButton.CreateNew(container);
            loadDataButton.Descriptor.SetTitle(BasisLocalization.Get("settings.storage.loadButton"));
            loadDataButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.storage.loadButton.tooltip"));
            loadDataButton.OnClicked += () =>
            {
                int insertAt = loadDataButton.transform.GetSiblingIndex() + 1;

                // Remove the load button itself
                cacheSection.UnregisterContentContainer(loadDataButton);
                loadDataButton.ReleaseInstance();

                PopulateStorageData(container, insertAt, storageRows);
                for (int i = 0; i < storageRows.Count; i++)
                {
                    cacheSection.RegisterContentContainer(storageRows[i]);
                }
                PanelSectionToggleHelpers.RebuildSectionLayout(cacheSection, container);
            };
        }, false, visible =>
        {
            for (int i = 0; i < storageRows.Count; i++)
            {
                if (storageRows[i] != null)
                {
                    storageRows[i].SetActive(visible);
                }
            }
            PanelSectionToggleHelpers.RebuildSectionLayout(cacheSection, container);
        });

        SettingsProviderTrustedUrls.Populate(container, TabKey, descriptor);

        // ---- Backup & Restore ----
        // Lazy, with lazy Create/Restore halves inside: a collapsed section costs nothing,
        // and the archive list is scanned (on a worker thread) only when Restore opens, so
        // reopening it picks up files written since.
        PanelSectionToggleHelpers.CreateLazyBoxedSection(container,
            BasisLocalization.Get("settings.developer.backup.title"),
            () => SettingsProviderBackup.BuildSection(container, descriptor),
            false, _ => descriptor.ForceRebuild());

        if (BasisSettingsDefaults.ShowDeveloperTab.RawValue)
        {
            PanelSectionToggle netStatsSection = null;
            netStatsSection = PanelSectionToggleHelpers.CreateLazyBoxedSection(container,
                BasisLocalization.Get("settings.developer.netStats"),
                () => SettingsProviderNetworkTab.BuildNetworkStatsGroup(container, out _),
                false, _ => PanelSectionToggleHelpers.RebuildSectionLayout(netStatsSection, container));
        }

        // One reset button for this whole page (download limits, cache, trusted URLs)
        SettingsProvider.RegisterPageReset(TabKey, ResetDefaults);

        descriptor.ForceRebuild();
        return tab;
    }

    /// <summary>
    /// Colour for the cache card: green while there is room, amber once the cache is mostly full,
    /// red once it is at or over the limit and evictions are about to start.
    /// </summary>
    private static BasisPanelSeverity CacheFillSeverity(long totalBytes)
    {
        long limit = BasisStorageManagement.MaxCacheSizeBytes;
        if (limit <= 0)
        {
            return BasisPanelSeverity.None;
        }

        double fill = (double)totalBytes / limit;
        if (fill >= 1.0) return BasisPanelSeverity.Hot;
        if (fill >= 0.85) return BasisPanelSeverity.Caution;
        return BasisPanelSeverity.Calm;
    }

    private static void PopulateStorageData(RectTransform container, int insertAt, List<PanelElementDescriptor> rows)
    {
        long totalBytes = BasisStorageManagement.GetTotalCacheSizeBytes();
        string sizeText = BasisStorageManagement.FormatBytes(totalBytes);
        string limitText = BasisStorageManagement.FormatBytes(BasisStorageManagement.MaxCacheSizeBytes);

        List<BasisStorageManagement.StoredBeeFileInfo> storedFiles = BasisStorageManagement.GetAllStoredFiles();

        void Place(PanelElementDescriptor row)
        {
            row.transform.SetSiblingIndex(insertAt++);
            rows.Add(row);
        }

        // Cache size info group
        PanelElementDescriptor infoGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        Place(infoGroup);
        infoGroup.SetTitle(BasisLocalization.Get("settings.storage.cacheInfo"));

        PanelPasswordField cacheInfoField = PanelPasswordField.CreateNew(infoGroup.ContentParent);
        cacheInfoField.Descriptor.SetTitle(BasisLocalization.Get("settings.storage.totalCacheSize"));
        cacheInfoField.SetPassword($"{sizeText} / {limitText}");

        PanelPasswordField fileCountField = PanelPasswordField.CreateNew(infoGroup.ContentParent);
        fileCountField.Descriptor.SetTitle(BasisLocalization.Get("settings.storage.storedFiles"));
        fileCountField.SetPassword(BasisLocalization.Get("settings.storage.fileCount", storedFiles.Count));

        BasisPanelTint.Apply(BasisPanelTint.Capture(infoGroup), CacheFillSeverity(totalBytes), false);

        // Clear all cache button
        PanelButton clearAllButton = PanelButton.CreateNew(container);
        Place(clearAllButton.Descriptor);
        clearAllButton.Descriptor.SetTitle(BasisLocalization.Get("settings.storage.clearAll"));
        clearAllButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.storage.clearAll.tooltip"));
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
            Place(filesGroup);
            filesGroup.SetTitle(BasisLocalization.Get("settings.storage.storedBeeFiles"));

            foreach (var file in storedFiles)
            {
                string fileName = file.UniqueVersion;
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
