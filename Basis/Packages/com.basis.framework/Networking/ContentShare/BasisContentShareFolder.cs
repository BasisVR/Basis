using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using static SerializableBasis;

[AutoStaticsCleanup]
public static partial class BasisContentShareFolder
{
    public enum BuildResult
    {
        Ok,
        Nothing,
        TooLarge,
    }

    private static readonly Color FolderColor = new Color(1f, 0.5f, 0.75f, 1f);
    public const int LabelNameLength = 16;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void Register()
    {
        BasisContentSharePayloadRegistry.Register(new BasisContentSharePayloadKind
        {
            Type = ContentShareType.Folder,
            Name = "Folder",
            NameKey = "content.share.type.folder",
            Color = FolderColor,
            ShareableKind = BasisShareableKind.Folder,
            Describe = Describe,
            Accept = Accept,
        });
    }

    public static bool TryGetShareType(BasisDataStoreItemKeys.ItemKey item, out ContentShareType type)
    {
        type = ContentShareType.Prop;
        if (item == null || string.IsNullOrEmpty(item.Pass) || !ContentShareFolder.IsShareableUrl(item.Url)) return false;
        if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable) return false;
        switch (item.Mode)
        {
            case BundledContentHolder.Mode.Avatar:
                type = ContentShareType.Avatar;
                return true;
            case BundledContentHolder.Mode.Prop:
                type = ContentShareType.Prop;
                return true;
            case BundledContentHolder.Mode.World:
                type = ContentShareType.World;
                return true;
            default:
                return false;
        }
    }

    public static BundledContentHolder.Mode ToMode(ContentShareType type) => type switch
    {
        ContentShareType.Avatar => BundledContentHolder.Mode.Avatar,
        ContentShareType.Prop => BundledContentHolder.Mode.Prop,
        _ => BundledContentHolder.Mode.World,
    };

    public static BuildResult BuildPayload(string id, string name, IReadOnlyList<BasisDataStoreItemKeys.ItemKey> items, Func<BasisDataStoreItemKeys.ItemKey, string> nameOf, out string payload, out int shared, out int skipped)
    {
        payload = null;
        skipped = 0;
        List<ContentShareFolder.Item> entries = new List<ContentShareFolder.Item>(items?.Count ?? 0);
        for (int i = 0; items != null && i < items.Count; i++)
        {
            BasisDataStoreItemKeys.ItemKey item = items[i];
            if (!TryGetShareType(item, out ContentShareType type))
            {
                skipped++;
                continue;
            }
            if (entries.Exists(entry => entry.Url == item.Url && entry.Password == item.Pass)) continue;
            entries.Add(new ContentShareFolder.Item { Type = type, Url = item.Url, Password = item.Pass, Name = nameOf?.Invoke(item) ?? string.Empty });
        }
        shared = entries.Count;
        if (shared == 0) return BuildResult.Nothing;
        if (shared > ContentShareFolder.MaxItems) return BuildResult.TooLarge;
        payload = ContentShareFolder.Write(id, name, entries);
        if (payload.Length > ContentSharePayload.MaxLength)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ContentShareFolder.Item unnamed = entries[i];
                unnamed.Name = string.Empty;
                entries[i] = unnamed;
            }
            payload = ContentShareFolder.Write(id, name, entries);
        }
        if (ContentShareFolder.TryRead(payload, out _)) return BuildResult.Ok;
        payload = null;
        return BuildResult.TooLarge;
    }

    public static List<BasisDataStoreItemKeys.ItemKey> ToItemKeys(ContentShareFolder.Contents folder)
    {
        List<BasisDataStoreItemKeys.ItemKey> keys = new List<BasisDataStoreItemKeys.ItemKey>(folder.Items.Count);
        for (int i = 0; i < folder.Items.Count; i++)
        {
            ContentShareFolder.Item item = folder.Items[i];
            keys.Add(new BasisDataStoreItemKeys.ItemKey
            {
                Mode = ToMode(item.Type),
                PlacementType = BundledContentHolder.PlacementType.SpawnAtRaycast,
                Url = item.Url,
                Pass = item.Password,
            });
        }
        return keys;
    }

    public static async Task<int> Save(ContentShareFolder.Contents folder)
    {
        List<BasisDataStoreItemKeys.ItemKey> keys = ToItemKeys(folder);
        string name = DisplayName(folder);
        int added;
        try
        {
            added = await BasisDataStoreItemKeys.AddNewKeys(keys);
            await BasisDataStoreItemFolders.AddShared(folder.Id, name, keys);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Shared folder '{name}' could not be saved: {ex}", BasisDebug.LogTag.Networking);
            return 0;
        }
        BasisDebug.Log($"Saved shared folder '{name}': {added} new of {keys.Count} items.", BasisDebug.LogTag.Networking);
        BasisNotificationCenter.LogResolved(BasisLocalization.Get("content.share.folder.saved.title"), BasisLocalization.Get("content.share.folder.saved", Quoted(folder), keys.Count), AddressableAssets.Sprites.FileTray, BasisNotificationStatus.Accepted, BasisNotificationCategory.Content);
        await LibraryProvider.RefreshIfOpen();
        return added;
    }

    public static bool TryHandlePaste(string payload)
    {
        if (!ContentShareFolder.IsFolderText(payload)) return false;
        BasisMainMenu.Open();
        BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
        if (menu == null || menu.Dialogue != null) return true;

        string title = BasisLocalization.Get("content.share.folder.paste.title");
        if (!ContentShareFolder.TryRead(payload, out ContentShareFolder.Contents folder))
        {
            menu.OpenDialogue(title, BasisLocalization.Get("content.share.folder.unreadable"), BasisLocalization.Get("ui.ok"), _ => { }, category: BasisNotificationCategory.Content);
            return true;
        }

        string canonical = ContentShareFolder.Write(folder.Id, folder.Name, folder.Items);
        string named = Quoted(folder);
        bool connected = BasisNetworkConnection.LocalPlayerPeer != null;
        if (connected)
        {
            menu.OpenDialogue(title, BasisLocalization.Get("content.share.folder.paste.body", named, folder.Items.Count), BasisLocalization.Get("library.share"), BasisLocalization.Get("ui.cancel"), accepted =>
            {
                if (accepted) BasisContentShareManager.ShareInlinePayload(canonical, ContentShareType.Folder);
            }, category: BasisNotificationCategory.Content);
        }
        else
        {
            menu.OpenDialogue(title, BasisLocalization.Get("content.share.save.folder", named, folder.Items.Count), BasisLocalization.Get("ui.save"), BasisLocalization.Get("ui.cancel"), accepted =>
            {
                if (accepted) _ = Save(folder);
            }, category: BasisNotificationCategory.Content);
        }
        BasisMenuDialoguePanel dialogue = menu.Dialogue;
        if (dialogue == null) return true;
        dialogue.CaptureOnClose = false;
        dialogue.ShowDetails(DetailRows(folder));
        if (connected) dialogue.EnableAlternate(BasisLocalization.Get("content.share.folder.saveToLibrary"), () => _ = Save(folder));
        return true;
    }

    internal static void Present(BasisContentSphere sphere, string title, string sharerLine)
    {
        string payload = sphere.ContentURL ?? string.Empty;
        string sphereId = sphere.SphereNetID;
        bool readable = ContentShareFolder.TryRead(payload, out ContentShareFolder.Contents folder);

        BasisMainMenu.Open();
        BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
        if (menu == null || menu.Dialogue != null) return;

        string question = readable ? BasisLocalization.Get("content.share.save.folder", Quoted(folder), folder.Items.Count) : BasisLocalization.Get("content.share.folder.unreadable");
        string description = $"{sharerLine}\n\n{question}";
        string accept = BasisLocalization.Get(readable ? "ui.save" : "ui.ok");
        if (sphere.CanLocalPlayerRemove)
        {
            menu.OpenDialogue(title, description, accept, BasisLocalization.Get("library.delete"), accepted =>
            {
                if (!accepted) BasisContentShareManager.RequestRemoveSphere(sphereId);
                else if (readable) _ = Save(folder);
            }, category: BasisNotificationCategory.Content);
        }
        else
        {
            menu.OpenDialogue(title, description, accept, accepted =>
            {
                if (accepted && readable) _ = Save(folder);
            }, category: BasisNotificationCategory.Content);
        }
        BasisMenuDialoguePanel dialogue = menu.Dialogue;
        if (dialogue == null) return;
        dialogue.CaptureOnClose = false;
        if (!readable) return;
        dialogue.ShowDetails(DetailRows(folder));
    }

    public static string DisplayName(ContentShareFolder.Contents folder) => string.IsNullOrEmpty(folder?.Name) ? BasisLocalization.Get("library.folder.sharedDefaultName") : folder.Name;

    public static string Label(ContentShareFolder.Contents folder) => $"{ContentShareFolder.CleanName(DisplayName(folder), LabelNameLength)} \u00B7 {folder.Items.Count}";

    private static string Quoted(ContentShareFolder.Contents folder) => $"\"{BasisContentShareText.Plain(DisplayName(folder))}\"";

    private static string Describe(string payload) => ContentShareFolder.TryRead(payload, out ContentShareFolder.Contents folder) ? Label(folder) : null;

    private static string Accept(string payload)
    {
        if (!ContentShareFolder.TryRead(payload, out ContentShareFolder.Contents folder)) return null;
        _ = Save(folder);
        return DisplayName(folder);
    }

    private static BasisMenuDialoguePanel.DetailRow[] DetailRows(ContentShareFolder.Contents folder)
    {
        BasisMenuDialoguePanel.DetailRow[] rows = new BasisMenuDialoguePanel.DetailRow[folder.Items.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            ContentShareFolder.Item item = folder.Items[i];
            rows[i] = new BasisMenuDialoguePanel.DetailRow(TypeName(item.Type), BasisContentShareText.Verbatim(ItemLabel(item)), true);
        }
        return rows;
    }

    private static string ItemLabel(ContentShareFolder.Item item)
    {
        if (!string.IsNullOrEmpty(item.Name)) return item.Name;
        return Uri.TryCreate(item.Url, UriKind.Absolute, out Uri uri) ? uri.Host : string.Empty;
    }

    private static string TypeName(ContentShareType type) => BasisLocalization.Get(type switch
    {
        ContentShareType.Avatar => "content.share.type.avatar",
        ContentShareType.Prop => "content.share.type.prop",
        _ => "content.share.type.world",
    });
}
