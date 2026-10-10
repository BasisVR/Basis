using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.Networking;
using Basis.Scripts.Platform;
using System;
using System.Text;
using UnityEngine;
using static SerializableBasis;

[AutoStaticsCleanup]
public static partial class BasisContentShareText
{
    private static readonly Color LinkColor = new Color(0.2f, 0.85f, 0.85f, 1f);
    private static readonly Color TextColor = new Color(1f, 0.85f, 0.35f, 1f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void Register()
    {
        BasisContentSharePayloadRegistry.Register(new BasisContentSharePayloadKind
        {
            Type = ContentShareType.Link,
            Name = "Link",
            NameKey = "content.share.type.link",
            Color = LinkColor,
            ShareableKind = BasisShareableKind.Link,
            Describe = DescribeLink,
        });
        BasisContentSharePayloadRegistry.Register(new BasisContentSharePayloadKind
        {
            Type = ContentShareType.Text,
            Name = "Text",
            NameKey = "content.share.type.text",
            Color = TextColor,
            ShareableKind = BasisShareableKind.Text,
            Describe = DescribeText,
        });
        BasisDesktopClipboard.OnTextPasted -= Share;
        BasisDesktopClipboard.OnTextPasted += Share;
    }

    public static void Share(string text)
    {
        string payload = Normalize(text);
        if (payload.Length == 0) return;
        if (BasisNetworkConnection.LocalPlayerPeer == null)
        {
            BasisDebug.LogWarning("Pasted text was not shared: not connected to a server.", BasisDebug.LogTag.Networking);
            return;
        }

        BasisMainMenu.Open();
        BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
        if (menu == null || menu.Dialogue != null) return;

        if (payload.Length > ContentSharePayload.MaxTextLength)
        {
            menu.OpenDialogue(BasisLocalization.Get("content.share.paste.tooLong.title"), BasisLocalization.Get("content.share.paste.tooLong", payload.Length.ToString("N0"), ContentSharePayload.MaxTextLength.ToString("N0")), BasisLocalization.Get("ui.ok"), _ => { }, category: BasisNotificationCategory.Content);
            return;
        }

        ContentShareType type = Classify(payload);
        bool link = type == ContentShareType.Link;
        menu.OpenDialogue(BasisLocalization.Get(link ? "content.share.paste.link.title" : "content.share.paste.text.title"), BasisLocalization.Get(link ? "content.share.paste.link.body" : "content.share.paste.text.body"), BasisLocalization.Get("library.share"), BasisLocalization.Get("ui.cancel"), accepted =>
        {
            if (accepted) BasisContentShareManager.ShareInlinePayload(payload, type);
        }, category: BasisNotificationCategory.Content);
        BasisMenuDialoguePanel dialogue = menu.Dialogue;
        if (dialogue == null) return;
        dialogue.CaptureOnClose = false;
        dialogue.ShowDetails(DetailRows(type, payload));
    }

    internal static void Present(BasisContentSphere sphere, string title, string sharerLine)
    {
        string payload = sphere.ContentURL ?? string.Empty;
        if (payload.Length > ContentSharePayload.MaxTextLength) payload = payload.Substring(0, ContentSharePayload.MaxTextLength);
        string sphereId = sphere.SphereNetID;
        bool link = sphere.ContentType == ContentShareType.Link && ContentSharePayload.TryParseLink(payload, out _);

        BasisMainMenu.Open();
        BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
        if (menu == null || menu.Dialogue != null) return;

        string description = link ? $"{sharerLine}\n\n{BasisLocalization.Get("content.share.link.open.question")}" : sharerLine;
        Action<bool> chosen = accepted =>
        {
            if (!accepted) BasisContentShareManager.RequestRemoveSphere(sphereId);
            else if (link) OpenLink(payload);
        };
        string accept = BasisLocalization.Get(link ? "content.share.link.open" : "ui.ok");
        if (sphere.CanLocalPlayerRemove) menu.OpenDialogue(title, description, accept, BasisLocalization.Get("library.delete"), chosen, category: BasisNotificationCategory.Content);
        else menu.OpenDialogue(title, description, accept, chosen, category: BasisNotificationCategory.Content);
        BasisMenuDialoguePanel dialogue = menu.Dialogue;
        if (dialogue == null) return;
        dialogue.CaptureOnClose = false;
        dialogue.ShowDetails(DetailRows(link ? ContentShareType.Link : ContentShareType.Text, payload));
        PanelButton copy = null;
        copy = dialogue.AddOption(BasisLocalization.Get("keyboard.key.copy"), () => BasisClipboard.Copy(payload, copy), false);
    }

    public static bool OpenLink(string payload)
    {
        if (!ContentSharePayload.TryParseLink(payload, out Uri link))
        {
            BasisDebug.LogWarning("A shared link was not opened: it is not an http or https address.", BasisDebug.LogTag.Networking);
            return false;
        }
        Application.OpenURL(link.AbsoluteUri);
        return true;
    }

    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        StringBuilder builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                c = '\n';
            }
            if (char.IsControl(c) && c != '\n' && c != '\t') continue;
            builder.Append(c);
        }
        return builder.ToString().Trim();
    }

    public static ContentShareType Classify(string payload) => ContentSharePayload.TryParseLink(payload, out _) ? ContentShareType.Link : ContentShareType.Text;

    public static string Preview(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || maxLength <= 0) return string.Empty;
        StringBuilder builder = new StringBuilder(maxLength + 3);
        bool pendingSpace = false;
        int i = 0;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            bool pair = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            if (builder.Length + (pendingSpace ? 1 : 0) + (pair ? 2 : 1) > maxLength) break;
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);
            if (pair) builder.Append(text[++i]);
        }
        if (i < text.Length) builder.Append("...");
        return builder.ToString();
    }

    public static string Plain(string text) => string.IsNullOrEmpty(text) ? text : text.Replace("\\", "\\​").Replace('<', '‹');

    private static string DescribeLink(string payload) => ContentSharePayload.TryParseLink(payload, out Uri link) ? Plain(link.Host) : null;

    private static string DescribeText(string payload) => string.IsNullOrWhiteSpace(payload) ? null : Plain(Preview(payload, BasisContentSphere.MaxTitleNameLength));

    private static BasisMenuDialoguePanel.DetailRow[] DetailRows(ContentShareType type, string payload)
    {
        if (type == ContentShareType.Link && ContentSharePayload.TryParseLink(payload, out Uri link))
        {
            string site = link.IdnHost == link.Host ? link.Host : $"{link.Host} ({link.IdnHost})";
            return new[]
            {
                new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("content.share.link.site"), site, true),
                new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("content.share.type.link"), payload, true),
            };
        }
        return new[] { new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("content.share.type.text"), payload, true) };
    }
}
