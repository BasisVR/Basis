using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.Networking;
using System;
using System.Collections.Generic;
using UnityEngine;
using static SerializableBasis;

[AutoStaticsCleanup]
public static partial class BasisContentShareSetting
{
    private static readonly Color SettingColor = new Color(0.55f, 0.75f, 1f, 1f);
    private const string PathSeparator = " › ";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void Register()
    {
        BasisContentSharePayloadRegistry.Register(new BasisContentSharePayloadKind
        {
            Type = ContentShareType.Setting,
            Name = "Setting",
            NameKey = "content.share.type.setting",
            Color = SettingColor,
            ShareableKind = BasisShareableKind.Setting,
            Describe = Describe,
        });
    }

    public static void AddShareOption(PanelComponent control, BasisMenuDialoguePanel dialogue)
    {
        if (dialogue == null || BasisNetworkConnection.LocalPlayerPeer == null) return;
        if (!TryCapture(control, out ContentShareSetting.Contents location, out ContentShareSetting.Contents value)) return;
        dialogue.AddOption(BasisLocalization.Get("library.share"), () =>
        {
            Dismiss(dialogue);
            Offer(location, value);
        }, false);
    }

    public static bool CanOffer(PanelComponent control) =>
        BasisNetworkConnection.LocalPlayerPeer != null && control != null && control.CanShareSetting && BasisMenuLocation.IsOnOpenPanel(control);

    public static void OfferShare(PanelComponent control)
    {
        if (BasisNetworkConnection.LocalPlayerPeer == null) return;
        if (!TryCapture(control, out ContentShareSetting.Contents location, out ContentShareSetting.Contents value)) return;
        Offer(location, value);
    }

    public static bool TryCapture(PanelComponent control, out ContentShareSetting.Contents location, out ContentShareSetting.Contents value)
    {
        location = null;
        value = null;
        if (control == null || !control.CanShareSetting || !BasisMenuLocation.TryLocate(control, out string menu, out string tab, out string path)) return false;
        string key = BasisMenuLocation.ControlKey(control);
        if (!ContentShareSetting.IsValidMenu(menu) || !ContentShareSetting.IsValidKey(tab) || !ContentShareSetting.IsValidKey(key)) return false;

        string title = BasisMenuLocation.TitleOf(control);
        if (string.IsNullOrEmpty(title)) title = key;
        location = new ContentShareSetting.Contents { Menu = menu, Tab = tab, Key = key, Title = title, Path = path };
        if (control.TryGetShareValue(out string type, out string text, out string display))
        {
            value = new ContentShareSetting.Contents { Menu = menu, Tab = tab, Key = key, Title = title, Path = path, ValueType = type, Value = text, ValueText = display };
        }
        return true;
    }

    public static Predicate<PanelComponent> Matcher(ContentShareSetting.Contents setting)
    {
        Predicate<PanelComponent> identity = setting != null ? BasisMenuLocation.Matcher(setting.Key) : null;
        if (identity == null) return null;
        return control => control != null && control.CanShareSetting && identity(control);
    }

    public static string DisplayTitle(ContentShareSetting.Contents setting) => string.IsNullOrEmpty(setting.Title) ? setting.Key : setting.Title;

    public static string Label(ContentShareSetting.Contents setting)
    {
        string title = DisplayTitle(setting);
        return setting.HasValue && !string.IsNullOrEmpty(setting.ValueText) ? $"{title}: {setting.ValueText}" : title;
    }

    public static void Show(ContentShareSetting.Contents setting)
    {
        if (setting == null) return;
        BasisMenuLocation.Reveal(setting.Menu, setting.Tab, Matcher(setting), (control, path) =>
        {
            if (setting.HasValue) OfferValue(control, path, setting);
        }, () => ShowMissing(setting));
    }

    internal static void Present(BasisContentSphere sphere, string title, string sharerLine)
    {
        string sphereId = sphere.SphereNetID;
        bool canRemove = sphere.CanLocalPlayerRemove;
        bool readable = ContentShareSetting.TryRead(sphere.ContentURL, out ContentShareSetting.Contents setting);

        BasisMainMenu.Open();
        BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
        if (menu == null || menu.Dialogue != null) return;

        string question = BasisLocalization.Get(!readable ? "content.share.setting.unreadable" : setting.HasValue ? "content.share.setting.value.question" : "content.share.setting.location.question");
        string description = string.IsNullOrEmpty(sharerLine) ? question : $"{sharerLine}\n\n{question}";
        if (readable)
        {
            menu.OpenDialogue(title, description, BasisLocalization.Get("content.share.setting.show"), BasisLocalization.Get("ui.cancel"), accepted =>
            {
                if (accepted) Show(setting);
            }, category: BasisNotificationCategory.Content);
        }
        else
        {
            menu.OpenDialogue(title, description, BasisLocalization.Get("ui.ok"), _ => { }, category: BasisNotificationCategory.Content);
        }
        BasisMenuDialoguePanel dialogue = menu.Dialogue;
        if (dialogue == null) return;
        dialogue.CaptureOnClose = false;
        if (readable) dialogue.ShowDetails(DetailRows(setting));
        if (canRemove) dialogue.EnableAlternate(BasisLocalization.Get("library.delete"), () => BasisContentShareManager.RequestRemoveSphere(sphereId));
        if (readable && canRemove && dialogue.AcceptButton != null && dialogue.DeclineButton != null)
        {
            dialogue.DeclineButton.rectTransform.SetSiblingIndex(dialogue.AcceptButton.rectTransform.GetSiblingIndex() + 1);
        }
    }

    private static void Offer(ContentShareSetting.Contents location, ContentShareSetting.Contents value)
    {
        string body = BasisLocalization.Get("content.share.setting.offer.body", Quoted(DisplayTitle(location)));
        BasisMenuDialoguePanel dialogue = OpenPrompt(BasisLocalization.Get("content.share.setting.offer.title"), body, BasisLocalization.Get("content.share.setting.location"), BasisLocalization.Get("ui.cancel"), accepted =>
        {
            if (accepted) Share(location);
        });
        if (dialogue == null) return;

        List<BasisMenuDialoguePanel.DetailRow> rows = new List<BasisMenuDialoguePanel.DetailRow>(2) { Row("content.share.setting.where", Where(location)) };
        if (value != null)
        {
            rows.Add(Row("content.share.setting.yourValue", value.ValueText));
            dialogue.EnableAlternate(BasisLocalization.Get("content.share.setting.value"), () => Share(value));
        }
        dialogue.ShowDetails(rows);
    }

    private static void Share(ContentShareSetting.Contents setting)
    {
        if (BasisNetworkConnection.LocalPlayerPeer == null)
        {
            BasisDebug.LogWarning("Setting was not shared: not connected to a server.", BasisDebug.LogTag.Networking);
            return;
        }
        BasisContentShareManager.ShareInlinePayload(ContentShareSetting.Write(setting), ContentShareType.Setting);
    }

    private static void OfferValue(PanelComponent control, string path, ContentShareSetting.Contents setting)
    {
        string title = BasisMenuLocation.TitleOf(control);
        if (string.IsNullOrEmpty(title)) title = DisplayTitle(setting);
        if (!control.TryReadShareValue(setting.ValueType, setting.Value, out string shared, out string current, out bool unchanged))
        {
            OpenPrompt(title, BasisLocalization.Get("content.share.setting.value.unusable"), BasisLocalization.Get("ui.ok"), null, _ => { });
            return;
        }
        if (unchanged)
        {
            OpenPrompt(title, BasisLocalization.Get("content.share.setting.value.same"), BasisLocalization.Get("ui.ok"), null, _ => { })?.ShowDetails(new[] { Row("content.share.setting.yourValue", current) });
            return;
        }
        if (!control.IsInteractable)
        {
            OpenPrompt(title, BasisLocalization.Get("content.share.setting.value.locked"), BasisLocalization.Get("ui.ok"), null, _ => { });
            return;
        }

        string key = setting.Key;
        string valueType = setting.ValueType;
        string value = setting.Value;
        BasisMenuDialoguePanel dialogue = OpenPrompt(title, BasisLocalization.Get("content.share.setting.apply.body"), BasisLocalization.Get("content.share.setting.apply"), BasisLocalization.Get("ui.cancel"), accepted =>
        {
            if (!accepted) return;
            if (control == null || !control.TryApplyShareValue(valueType, value))
            {
                BasisDebug.LogWarning($"Shared value for setting '{key}' could not be applied.", BasisDebug.LogTag.Networking);
            }
        });
        dialogue?.ShowDetails(new[]
        {
            Row("content.share.setting.where", path),
            Row("content.share.setting.yourValue", current),
            Row("content.share.setting.sharedValue", shared),
        });
    }

    private static void ShowMissing(ContentShareSetting.Contents setting)
    {
        OpenPrompt(BasisLocalization.Get("content.share.setting.missing.title"), BasisLocalization.Get("content.share.setting.missing", Quoted(DisplayTitle(setting))), BasisLocalization.Get("ui.ok"), null, _ => { });
    }

    private static BasisMenuDialoguePanel OpenPrompt(string title, string body, string accept, string decline, Action<bool> callback)
    {
        BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
        if (menu == null || (menu.Dialogue != null && !menu.Dialogue.IsReleased)) return null;
        BasisMenuDialoguePanel dialogue = BasisMenuDialoguePanel.CreateNew(title, body, accept, decline, callback, false, BasisPanelSeverity.None, BasisNotificationCategory.Content);
        if (dialogue == null) return null;
        menu.Dialogue = dialogue;
        dialogue.CaptureOnClose = false;
        return dialogue;
    }

    private static void Dismiss(BasisMenuDialoguePanel dialogue)
    {
        if (dialogue == null || dialogue.IsReleased) return;
        dialogue.CaptureOnClose = false;
        dialogue.ReleaseInstance();
    }

    private static string Describe(string payload) => ContentShareSetting.TryRead(payload, out ContentShareSetting.Contents setting) ? Label(setting) : null;

    private static string Where(ContentShareSetting.Contents setting) => string.IsNullOrEmpty(setting.Path) ? DisplayTitle(setting) : setting.Path + PathSeparator + DisplayTitle(setting);

    private static string Quoted(string text) => $"\"{BasisContentShareText.Plain(text)}\"";

    private static BasisMenuDialoguePanel.DetailRow Row(string key, string value) =>
        new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get(key), BasisContentShareText.Verbatim(value ?? string.Empty), true);

    private static List<BasisMenuDialoguePanel.DetailRow> DetailRows(ContentShareSetting.Contents setting)
    {
        List<BasisMenuDialoguePanel.DetailRow> rows = new List<BasisMenuDialoguePanel.DetailRow>(3) { Row("content.share.type.setting", DisplayTitle(setting)) };
        if (!string.IsNullOrEmpty(setting.Path)) rows.Add(Row("content.share.setting.where", setting.Path));
        if (setting.HasValue) rows.Add(Row("content.share.setting.theirValue", string.IsNullOrEmpty(setting.ValueText) ? setting.Value : setting.ValueText));
        return rows;
    }
}
