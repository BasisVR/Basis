using System.Collections.Generic;
using System.IO;
using Basis.BasisUI;
using Basis.Scripts.Networking;
using UnityEngine;

/// <summary>
/// General-tab "Backup &amp; Restore" section. Creating an archive is offered everywhere; restoring
/// is Windows/Linux only (<see cref="BasisUserDataBackup.RestoreSupported"/>) and lists the archives
/// found in the backups folder, plus a field for a path copied in from elsewhere. Backup and Restore
/// are each a lazy sub-section nested inside the outer Backup &amp; Restore toggle and start closed:
/// nothing is built for a half the user leaves shut, and the archive list is scanned on a worker
/// thread so opening Restore never stalls the menu on zip reads.
/// </summary>
public static class SettingsProviderBackup
{
    private static bool _busy;

    /// <summary>
    /// The live "Create Backup Now" button, null while its section is closed. Held here rather than
    /// captured because a backup keeps running after the section that started it is torn down, and
    /// whichever button exists when it finishes is the one that has to stop saying "Working…".
    /// </summary>
    private static PanelButton _createButton;

    /// <summary>
    /// Bumped whenever the archive list is rebuilt or torn down. A scan that comes back after the
    /// user collapsed Restore, closed the menu, or pressed Refresh again finds a newer number and
    /// drops its result instead of filling rows that are gone or already being refilled.
    /// </summary>
    private static int _listGeneration;

    public static void BuildSection(RectTransform container, PanelElementDescriptor tabDescriptor)
    {
        // Opening one of the nested Backup/Restore sections changes a box several levels below
        // tabDescriptor's own root. A single top-down ForceRebuild there measures each nested box
        // before it has resized itself, so walk outward from the box that actually changed
        // instead — see PanelElementDescriptor.RebuildLayoutChain.
        void RebuildFrom(RectTransform changed) =>
            PanelElementDescriptor.RebuildLayoutChain(changed, container);

        // A lazy section destroys its rows when it closes, so after that the header's own parent
        // is the innermost thing whose height changed.
        void RebuildSection(bool open, PanelElementDescriptor innermost, PanelSectionToggle header) =>
            RebuildFrom(open && innermost != null ? innermost.rectTransform : header.transform.parent as RectTransform);

        // Restore's list only exists while that half is open. Create calls this after writing an
        // archive so an open list shows the new file; a closed one re-reads the folder when it
        // next opens anyway.
        System.Action refreshList = null;

        PanelElementDescriptor createInfo = null;
        PanelSectionToggle createToggle = null;
        createToggle = PanelSectionToggleHelpers.CreateLazyBoxedSection(container,
            BasisLocalization.Get("settings.developer.backup.create.title"), () =>
        {
            createInfo = CreateInfoRow(container, BasisLocalization.Get("settings.developer.backup.create.description"));

            PanelToggle includeIdentity = PanelToggle.CreateNewEntry(container);
            includeIdentity.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.includeIdentity"));
            includeIdentity.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.includeIdentity.tooltip"));
            includeIdentity.SetValueWithoutNotify(true);

            PanelToggle includeCache = PanelToggle.CreateNewEntry(container);
            includeCache.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.includeCache"));
            includeCache.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.includeCache.tooltip"));
            includeCache.SetValueWithoutNotify(false);

            PanelButton createButton = PanelButton.CreateNew(container);
            createButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.create.tooltip"));
            createButton.OnClicked += () =>
                CreateBackup(includeCache.Value, includeIdentity.Value, () => refreshList?.Invoke());
            _createButton = createButton;
            ShowCreateBusy(_busy);

            PanelButton revealButton = PanelButton.CreateNew(container);
            revealButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.openFolder"));
            revealButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.backup.openFolder.tooltip"));
            revealButton.OnClicked += RevealBackupsFolder;
        }, false, open => RebuildSection(open, createInfo, createToggle));

        if (!BasisUserDataBackup.RestoreSupported)
        {
            PanelElementDescriptor unsupported = null;
            PanelSectionToggle unsupportedToggle = null;
            unsupportedToggle = PanelSectionToggleHelpers.CreateLazyBoxedSection(container,
                BasisLocalization.Get("settings.developer.backup.restore.title"),
                () => unsupported = CreateInfoRow(container, BasisLocalization.Get("settings.developer.backup.restore.unsupported")),
                false, open => RebuildSection(open, unsupported, unsupportedToggle));
            return;
        }

        PanelElementDescriptor restoreInfo = null;
        PanelSectionToggle restoreToggle = null;
        restoreToggle = PanelSectionToggleHelpers.CreateLazyBoxedSection(container,
            BasisLocalization.Get("settings.developer.backup.restore.title"), () =>
        {
            restoreInfo = CreateInfoRow(container, BasisLocalization.Get("settings.developer.backup.restore.description"));

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

            refreshList = () => PopulateArchiveList(listGroup, restoreIdentity, RebuildFrom);
            refreshList();

            PanelButton refreshButton = PanelButton.CreateNew(container);
            refreshButton.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.refresh"));
            refreshButton.OnClicked += refreshList;
        }, false, open =>
        {
            if (!open)
            {
                // The list went with the rows; a scan still running for it must not come back.
                refreshList = null;
                _listGeneration++;
            }
            RebuildSection(open, restoreInfo, restoreToggle);
        });
    }

    /// <summary>
    /// Fills the Available Backups box. The folder scan and every manifest read happen on a worker
    /// thread behind a placeholder row; only the newest request is allowed to write the rows back.
    /// </summary>
    private static async void PopulateArchiveList(
        PanelElementDescriptor listGroup, PanelToggle restoreIdentity, System.Action<RectTransform> rebuildFrom)
    {
        RectTransform parent = listGroup != null ? listGroup.ContentParent : null;
        if (parent == null) return;

        int generation = ++_listGeneration;

        ClearRows(parent);
        AddNoteRow(parent, BasisLocalization.Get("settings.developer.backup.scanning"));
        rebuildFrom(parent);

        List<BasisUserDataBackup.ArchiveInfo> archives = await BasisUserDataBackup.ListArchivesAsync();

        if (generation != _listGeneration || listGroup == null || parent == null) return;

        ClearRows(parent);

        if (archives.Count == 0)
        {
            AddNoteRow(parent, BasisLocalization.Get("settings.developer.backup.none"));
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

        rebuildFrom(parent);
    }

    private static void ClearRows(RectTransform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            child.SetParent(null, false);
            Object.Destroy(child.gameObject);
        }
    }

    /// <summary>A read-only "Available Backups: …" line for the list's empty and scanning states.</summary>
    private static void AddNoteRow(RectTransform parent, string text)
    {
        PanelPasswordField note = PanelPasswordField.CreateNew(parent);
        note.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.backup.available"));
        note.SetPassword(text);
        note.SetValue(true);
        note.DisableIcons();
    }

    private static PanelElementDescriptor CreateInfoRow(RectTransform parent, string text)
    {
        PanelElementDescriptor info =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
        info.SetBackgroundVisible(false);
        info.SetTitle(string.Empty);
        info.SetDescription(text);
        return info;
    }

    private static string DescribeArchive(BasisUserDataBackup.ArchiveInfo archive)
    {
        BasisUserDataBackup.Manifest manifest = archive.Manifest;
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

    private static void ShowCreateBusy(bool busy)
    {
        if (_createButton == null) return;

        _createButton.Descriptor.SetTitle(BasisLocalization.Get(
            busy ? "settings.developer.backup.working" : "settings.developer.backup.create"));
    }

    private static async void CreateBackup(bool includeCache, bool includeIdentity, System.Action refresh)
    {
        if (_busy) return;
        _busy = true;
        ShowCreateBusy(true);

        BasisUserDataBackup.BackupResult result;
        try
        {
            result = await BasisUserDataBackup.CreateAsync(includeCache, includeIdentity);
        }
        finally
        {
            _busy = false;
            ShowCreateBusy(false);
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
