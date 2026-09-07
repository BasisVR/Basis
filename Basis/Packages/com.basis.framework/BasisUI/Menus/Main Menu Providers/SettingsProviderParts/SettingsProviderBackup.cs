using System.Collections.Generic;
using System.IO;
using Basis.BasisUI;
using Basis.Scripts.Networking;
using UnityEngine;

/// <summary>
/// General-tab "Backup &amp; Restore" section. Creating an archive is offered everywhere; restoring
/// is Windows/Linux only (<see cref="BasisUserDataBackup.RestoreSupported"/>) and lists the archives
/// found in the backups folder, plus a field for a path copied in from elsewhere. Backup and Restore
/// are each their own collapsible sub-section nested inside the outer Backup &amp; Restore toggle, so
/// either half can be tucked away independently.
/// </summary>
public static class SettingsProviderBackup
{
    private static bool _busy;

    public static void BuildSection(RectTransform container, PanelElementDescriptor tabDescriptor)
    {
        // Toggling one of the nested Backup/Restore sections changes a box several levels
        // below tabDescriptor's own root. A single top-down ForceRebuild there measures each
        // nested box before it has resized itself, so walk outward from the box that actually
        // changed instead — see PanelElementDescriptor.RebuildLayoutChain.
        void RebuildFrom(RectTransform changed) =>
            PanelElementDescriptor.RebuildLayoutChain(changed, container);

        PanelToggle includeIdentity = null;
        PanelToggle includeCache = null;
        PanelButton createButton = null;
        PanelElementDescriptor createInfo = null;

        PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
            BasisLocalization.Get("settings.developer.backup.create.title"), () =>
        {
            createInfo =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            createInfo.SetBackgroundVisible(false);
            createInfo.SetTitle(string.Empty);
            createInfo.SetDescription(BasisLocalization.Get("settings.developer.backup.create.description"));

            includeIdentity = PanelToggle.CreateNewEntry(container);
            includeIdentity.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.includeIdentity"));
            includeIdentity.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.includeIdentity.tooltip"));
            includeIdentity.SetValueWithoutNotify(true);

            includeCache = PanelToggle.CreateNewEntry(container);
            includeCache.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.includeCache"));
            includeCache.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.includeCache.tooltip"));
            includeCache.SetValueWithoutNotify(false);

            createButton = PanelButton.CreateNew(container);
            createButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.create"));
            createButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.create.tooltip"));

            PanelButton revealButton = PanelButton.CreateNew(container);
            revealButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.openFolder"));
            revealButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.openFolder.tooltip"));
            revealButton.OnClicked += RevealBackupsFolder;
        }, true, _ => RebuildFrom(createInfo.rectTransform));

        if (!BasisUserDataBackup.RestoreSupported)
        {
            createButton.OnClicked += () => CreateBackup(createButton, includeCache.Value, includeIdentity.Value, null);

            PanelElementDescriptor unsupported = null;
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
                BasisLocalization.Get("settings.developer.backup.restore.title"), () =>
            {
                unsupported =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                unsupported.SetBackgroundVisible(false);
                unsupported.SetTitle(string.Empty);
                unsupported.SetDescription(BasisLocalization.Get("settings.developer.backup.restore.unsupported"));
            }, true, _ => RebuildFrom(unsupported.rectTransform));
            return;
        }

        System.Action refresh = null;
        PanelElementDescriptor restoreInfo = null;

        PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(container,
            BasisLocalization.Get("settings.developer.backup.restore.title"), () =>
        {
            restoreInfo =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            restoreInfo.SetBackgroundVisible(false);
            restoreInfo.SetTitle(string.Empty);
            restoreInfo.SetDescription(BasisLocalization.Get("settings.developer.backup.restore.description"));

            PanelToggle restoreIdentity = PanelToggle.CreateNewEntry(container);
            restoreIdentity.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.restoreIdentity"));
            restoreIdentity.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.restoreIdentity.tooltip"));
            restoreIdentity.SetValueWithoutNotify(true);

            PanelTextField pathField = PanelTextField.CreateNewEntry(container);
            pathField.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.path"));
            pathField.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.path.tooltip"));
            pathField.SetValueWithoutNotify(string.Empty);

            PanelButton pathRestoreButton = PanelButton.CreateNew(container);
            pathRestoreButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.restoreFromPath"));
            pathRestoreButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.restoreFromPath.tooltip"));
            pathRestoreButton.OnClicked += () =>
            {
                string path = ReadField(pathField).Trim().Trim('"');
                if (string.IsNullOrEmpty(path))
                {
                    Notify(BasisLocalization.Get("settings.developer.backup.path.missing"));
                    return;
                }
                ConfirmRestore(path, Path.GetFileName(path), restoreIdentity.Value);
            };

            PanelElementDescriptor listGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            listGroup.SetTitle(BasisLocalization.Get("settings.developer.backup.available"));

            PopulateArchiveList(listGroup, restoreIdentity);

            refresh = () =>
            {
                PopulateArchiveList(listGroup, restoreIdentity);
                RebuildFrom(listGroup.rectTransform);
            };

            PanelButton refreshButton = PanelButton.CreateNew(container);
            refreshButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.refresh"));
            refreshButton.OnClicked += refresh;
        }, true, _ => RebuildFrom(restoreInfo.rectTransform));

        createButton.OnClicked += () => CreateBackup(createButton, includeCache.Value, includeIdentity.Value, refresh);
    }

    private static void PopulateArchiveList(PanelElementDescriptor listGroup, PanelToggle restoreIdentity)
    {
        RectTransform parent = listGroup.ContentParent;
        if (parent == null) return;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            child.SetParent(null, false);
            Object.Destroy(child.gameObject);
        }

        List<BasisUserDataBackup.ArchiveInfo> archives = BasisUserDataBackup.ListArchives();

        if (archives.Count == 0)
        {
            PanelPasswordField empty = PanelPasswordField.CreateNew(parent);
            empty.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.available"));
            empty.SetPassword(BasisLocalization.Get("settings.developer.backup.none"));
            return;
        }

        foreach (BasisUserDataBackup.ArchiveInfo archive in archives)
        {
            PanelButton entry = PanelButton.CreateNew(parent);
            entry.Descriptor.SetTitle($"{archive.FileName}  ({BasisUserDataBackup.FormatBytes(archive.SizeBytes)})");
            entry.Descriptor.SetDescription(DescribeArchive(archive));

            string path = archive.Path;
            string name = archive.FileName;
            entry.OnClicked += () =>
                ConfirmRestore(path, name, restoreIdentity == null || restoreIdentity.Value);
        }
    }

    private static string DescribeArchive(BasisUserDataBackup.ArchiveInfo archive)
    {
        BasisUserDataBackup.Manifest manifest = BasisUserDataBackup.ReadManifest(archive.Path);
        if (manifest == null) return BasisLocalization.Get("settings.developer.backup.unreadable");

        string extras = string.Empty;
        if (manifest.IncludesIdentity) extras += "  " + BasisLocalization.Get("settings.developer.backup.tag.identity");
        if (manifest.IncludesCachedContent) extras += "  " + BasisLocalization.Get("settings.developer.backup.tag.cache");

        return BasisLocalization.Get(
            "settings.developer.backup.entry.summary",
            archive.WrittenLocal.ToString("g"),
            manifest.FileCount,
            manifest.PrefCount,
            manifest.AppVersion) + extras;
    }

    private static async void CreateBackup(
        PanelButton button, bool includeCache, bool includeIdentity, System.Action refresh)
    {
        if (_busy) return;
        _busy = true;

        if (button != null)
        {
            button.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.working"));
        }

        BasisUserDataBackup.BackupResult result;
        try
        {
            result = await BasisUserDataBackup.CreateAsync(includeCache, includeIdentity);
        }
        finally
        {
            _busy = false;
            if (button != null)
            {
                button.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.create"));
            }
        }

        if (!result.Success)
        {
            Notify(BasisLocalization.Get("settings.developer.backup.create.failed", result.Error));
            return;
        }

        refresh?.Invoke();

        Notify(BasisLocalization.Get(
            "settings.developer.backup.create.done",
            Path.GetFileName(result.ArchivePath),
            result.FileCount,
            result.PrefCount,
            BasisUserDataBackup.FormatBytes(result.ArchiveBytes)));
    }

    private static void ConfirmRestore(string archivePath, string displayName, bool restoreIdentity)
    {
        if (_busy) return;

        ShowDialogue(
            BasisLocalization.Get("settings.developer.backup.restore.title"),
            BasisLocalization.Get("settings.developer.backup.restore.confirm", displayName),
            BasisLocalization.Get("settings.developer.backup.restore.button"),
            BasisLocalization.Get("ui.cancel"),
            accepted =>
            {
                if (!accepted) return;
                RunRestore(archivePath, restoreIdentity);
            });
    }

    private static async void RunRestore(string archivePath, bool restoreIdentity)
    {
        if (_busy) return;
        _busy = true;

        BasisUserDataBackup.RestoreResult result;
        try
        {
            result = await BasisUserDataBackup.RestoreAsync(archivePath, restoreIdentity);
        }
        finally
        {
            _busy = false;
        }

        if (!result.Success)
        {
            Notify(BasisLocalization.Get("settings.developer.backup.restore.failed", result.Error));
            return;
        }

        string message = BasisLocalization.Get(
            "settings.developer.backup.restore.done", result.FileCount, result.PrefCount);

        if (BasisAppRelaunch.IsSupported)
        {
            ShowDialogue(
                BasisLocalization.Get("settings.developer.backup.restore.title"),
                message + "\n\n" + BasisLocalization.Get("settings.developer.backup.restart.prompt"),
                BasisLocalization.Get("settings.developer.backup.restart.now"),
                BasisLocalization.Get("settings.developer.backup.restart.later"),
                accepted =>
                {
                    if (accepted) BasisAppRelaunch.RebootAndReconnect();
                });
            return;
        }

        Notify(message + "\n\n" + BasisLocalization.Get("settings.developer.backup.restart.manual"));
    }

    private static void RevealBackupsFolder()
    {
        try
        {
            string folder = BasisUserDataBackup.BackupsFolder;
            Directory.CreateDirectory(folder);
            BasisFileBrowserUtility.Reveal(folder);
        }
        catch (System.Exception e)
        {
            BasisDebug.LogWarning("Could not open the backups folder: " + e.Message);
        }
    }

    private static string ReadField(PanelTextField field)
    {
        if (field == null || field._inputField == null) return string.Empty;
        return field._inputField.text ?? string.Empty;
    }

    private static void Notify(string message)
    {
        ShowDialogue(
            BasisLocalization.Get("settings.developer.backup.title"),
            message,
            BasisLocalization.Get("ui.ok"),
            null,
            null);
    }

    private static void ShowDialogue(
        string title, string description, string accept, string deny, System.Action<bool> callback)
    {
        if (BasisMainMenu.Instance == null)
        {
            BasisDebug.Log(description);
            return;
        }

        if (BasisMainMenu.Instance.Dialogue)
        {
            BasisMainMenu.Instance.Dialogue.ReleaseInstance();
        }

        if (string.IsNullOrEmpty(deny))
        {
            BasisMainMenu.Instance.OpenDialogue(title, description, accept, value => callback?.Invoke(value));
            return;
        }

        BasisMainMenu.Instance.OpenDialogue(title, description, accept, deny, value => callback?.Invoke(value));
    }
}
