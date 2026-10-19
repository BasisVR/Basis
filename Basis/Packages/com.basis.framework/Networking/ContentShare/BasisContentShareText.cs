using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.Networking;
using Basis.Scripts.Platform;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using static SerializableBasis;

[AutoStaticsCleanup]
public static partial class BasisContentShareText
{
    private static readonly Color LinkColor = new Color(0.2f, 0.85f, 0.85f, 1f);
    private static readonly Color TextColor = new Color(1f, 0.85f, 0.35f, 1f);

    private const string SevereHex = "#FF5A4A";
    private const string CautionHex = "#E6C15A";

    /// <summary>
    /// Most combining marks one character may carry. Real scripts stay well under it; stacked
    /// "zalgo" text, which smears over the lines around it, does not.
    /// </summary>
    public const int MaxCombiningMarks = 6;

    private const char ZeroWidthSpace = (char)0x200B;
    private const char ZeroWidthNonJoiner = (char)0x200C;
    private const char ZeroWidthJoiner = (char)0x200D;
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;
    private const char SingleAngleQuote = (char)0x2039;

    // Read-only lookups that never need resetting. Marked so the statics-cleanup generator skips
    // them: an initializer it cannot reproduce fails the generator for the whole assembly.
    [NoAutoStaticsCleanup] private static readonly string BackslashBreak = "\\" + ZeroWidthSpace;

    [NoAutoStaticsCleanup] private static readonly (BasisLinkSafety.Warning Flag, string Key)[] LinkWarnings =
    {
        (BasisLinkSafety.Warning.LocalNetwork, "content.share.link.warning.local"),
        (BasisLinkSafety.Warning.Program, "content.share.link.warning.program"),
        (BasisLinkSafety.Warning.LookAlikeName, "content.share.link.warning.lookalike"),
        (BasisLinkSafety.Warning.Shortened, "content.share.link.warning.shortened"),
        (BasisLinkSafety.Warning.IpAddress, "content.share.link.warning.ip"),
        (BasisLinkSafety.Warning.Unencrypted, "content.share.link.warning.unencrypted"),
    };

    // Shapes almost never meant for a whole server: private key blocks, well-known API token
    // formats, JSON web tokens, a password written into an address, and sign-in parameters.
    [NoAutoStaticsCleanup] private static readonly Regex SecretPattern = new Regex(
        @"-----BEGIN [A-Z ]*PRIVATE KEY"
        + @"|\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_\w{20,}|glpat-[\w-]{20,}|xox[abposr]-[\w-]{10,}|sk-[\w-]{20,}|[rs]k_live_\w{16,}|A[KS]IA[0-9A-Z]{16}|AIza[\w-]{35})"
        + @"|\beyJ[\w-]{8,}\.eyJ[\w-]{8,}\.[\w-]{8,}"
        + @"|\b[MNO][\w-]{23,27}\.[\w-]{6}\.[\w-]{27,40}\b"
        + @"|\b[a-z][a-z0-9+.-]*://[^\s/?#@:]+:[^\s/?#@]+@"
        + @"|[?&#;](?:access_token|id_token|refresh_token|auth_token|token|auth|password|passwd|pwd|pass|secret|client_secret|api_key|apikey|key|code|otp|sig|signature|x-amz-signature|x-amz-credential|session|sessionid|session_id)=[^&#\s]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        if (BasisContentShareFolder.TryHandlePaste(payload)) return;
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

        bool link = TryReadLink(payload, out Uri uri, out string address);
        ContentShareType type = link ? ContentShareType.Link : ContentShareType.Text;

        // Raised before anything leaves this machine: once shared, everyone on the server can read
        // it, and an accidental Ctrl+V outside a text field lands here.
        List<BasisMenuDialoguePanel.DetailRow> rows = new List<BasisMenuDialoguePanel.DetailRow>(8);
        BasisPanelSeverity severity = BasisPanelSeverity.None;
        if (LooksSensitive(payload))
        {
            rows.Add(WarningRow("content.share.paste.warning.secret", true));
            severity = BasisPanelSeverity.Hot;
        }
        if (link)
        {
            BasisLinkSafety.Warning warnings = BasisLinkSafety.Inspect(uri);
            if ((warnings & BasisLinkSafety.Warning.LocalNetwork) != 0)
            {
                // For the sharer a local address is about reach rather than danger: it will not
                // work for anyone else, so it gets its own wording and only an amber tint.
                rows.Add(WarningRow("content.share.paste.warning.local", false));
                warnings &= ~BasisLinkSafety.Warning.LocalNetwork;
                severity = Worse(severity, BasisPanelSeverity.Caution);
            }
            AddLinkRows(rows, uri, address, warnings);
            severity = Worse(severity, SeverityOf(warnings));
        }
        else
        {
            rows.Add(TextRow(payload));
        }

        menu.OpenDialogue(BasisLocalization.Get(link ? "content.share.paste.link.title" : "content.share.paste.text.title"), BasisLocalization.Get(link ? "content.share.paste.link.body" : "content.share.paste.text.body"), BasisLocalization.Get("library.share"), BasisLocalization.Get("ui.cancel"), accepted =>
        {
            if (accepted) BasisContentShareManager.ShareInlinePayload(payload, type);
        }, severity: severity, category: BasisNotificationCategory.Content);
        BasisMenuDialoguePanel dialogue = menu.Dialogue;
        if (dialogue == null) return;
        dialogue.CaptureOnClose = false;
        dialogue.ShowDetails(rows);
    }

    /// <summary>
    /// What somebody sees on pressing a shared link or text orb. A link asks "do you want to open
    /// this link?" with Yes and No, names the site and the exact address Yes opens, and lists what
    /// <see cref="BasisLinkSafety.Inspect"/> found. No only closes; removing the orb is its own
    /// button, so turning a link down never deletes it for everyone.
    /// </summary>
    internal static void Present(BasisContentSphere sphere, string title, string sharerLine)
    {
        string payload = sphere.ContentURL ?? string.Empty;
        if (payload.Length > ContentSharePayload.MaxTextLength) payload = payload.Substring(0, ContentSharePayload.MaxTextLength);
        // Cleaned again on arrival: the sharer's client is not trusted to have done it.
        payload = Normalize(payload);
        string sphereId = sphere.SphereNetID;
        bool canRemove = sphere.CanLocalPlayerRemove;
        Uri link = null;
        string address = null;
        bool isLink = sphere.ContentType == ContentShareType.Link && TryReadLink(payload, out link, out address);

        BasisMainMenu.Open();
        BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
        if (menu == null || menu.Dialogue != null) return;

        BasisMenuDialoguePanel dialogue;
        if (isLink)
        {
            BasisLinkSafety.Warning warnings = BasisLinkSafety.Inspect(link);
            string question = BasisLocalization.Get("content.share.link.open.question");
            string description = string.IsNullOrEmpty(sharerLine) ? question : $"{sharerLine}\n\n{question}";
            menu.OpenDialogue(title, description, BasisLocalization.Get("ui.yes"), BasisLocalization.Get("ui.no"), accepted =>
            {
                if (accepted) BasisLinkSafety.Open(address);
            }, severity: SeverityOf(warnings), category: BasisNotificationCategory.Content);
            dialogue = menu.Dialogue;
            if (dialogue == null) return;
            List<BasisMenuDialoguePanel.DetailRow> rows = new List<BasisMenuDialoguePanel.DetailRow>(8);
            AddLinkRows(rows, link, address, warnings);
            dialogue.ShowDetails(rows);
        }
        else
        {
            menu.OpenDialogue(title, sharerLine, BasisLocalization.Get("ui.ok"), _ => { }, category: BasisNotificationCategory.Content);
            dialogue = menu.Dialogue;
            if (dialogue == null) return;
            dialogue.ShowDetails(new[] { TextRow(payload) });
        }
        dialogue.CaptureOnClose = false;

        // Copy takes the address exactly as Yes would open it, or the cleaned text.
        string copied = isLink ? address : payload;
        PanelButton copy = null;
        copy = dialogue.AddOption(BasisLocalization.Get("keyboard.key.copy"), () => BasisClipboard.Copy(copied, copy), false);
        if (canRemove)
        {
            dialogue.EnableAlternate(BasisLocalization.Get("library.delete"), () => BasisContentShareManager.RequestRemoveSphere(sphereId));
        }
        if (isLink && dialogue.AcceptButton != null && dialogue.DeclineButton != null)
        {
            // Yes and No side by side with the extras after them, so Delete never sits beside Yes.
            dialogue.DeclineButton.rectTransform.SetSiblingIndex(dialogue.AcceptButton.rectTransform.GetSiblingIndex() + 1);
        }
    }

    /// <summary>
    /// A payload counts as a link only when the shared link rules accept it and it rebuilds into
    /// an address that is shown and opened as one string; anything else is shared and shown as text.
    /// </summary>
    public static bool TryReadLink(string payload, out Uri link, out string address)
    {
        if (!ContentSharePayload.TryParseLink(payload, out link) || !BasisLinkSafety.TryGetOpenAddress(link, out address) || address.Length > ContentSharePayload.MaxTextLength)
        {
            link = null;
            address = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Cleans pasted or received text before it is shown, copied or shared. Line breaks become \n;
    /// control characters go, tabs and line breaks excepted; so do the invisible characters that
    /// let what is shown differ from what is copied: direction overrides, zero-width spaces, word
    /// joiners, byte-order marks and tag characters. Zero-width joiners and non-joiners stay,
    /// because emoji sequences and several scripts need them. Unpaired surrogates are dropped, and
    /// combining marks past <see cref="MaxCombiningMarks"/> on one character are cut.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        StringBuilder builder = new StringBuilder(text.Length);
        int marks = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            int width = 1;
            UnicodeCategory category;
            if (char.IsSurrogate(c))
            {
                if (!char.IsSurrogatePair(text, i)) continue;
                width = 2;
                category = CharUnicodeInfo.GetUnicodeCategory(text, i);
            }
            else
            {
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                    c = '\n';
                }
                else if (c == LineSeparator || c == ParagraphSeparator)
                {
                    c = '\n';
                }
                if (char.IsControl(c) && c != '\n' && c != '\t') continue;
                // Not char.GetUnicodeCategory: on Mono it calls the invisible soft hyphen a dash.
                category = CharUnicodeInfo.GetUnicodeCategory(c);
            }

            bool drop;
            if (category == UnicodeCategory.Format)
            {
                drop = c != ZeroWidthJoiner && c != ZeroWidthNonJoiner;
            }
            else if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark)
            {
                drop = ++marks > MaxCombiningMarks;
            }
            else
            {
                marks = 0;
                drop = false;
            }
            if (drop)
            {
                i += width - 1;
                continue;
            }

            builder.Append(c);
            if (width == 2) builder.Append(text[++i]);
        }
        return builder.ToString().Trim();
    }

    public static ContentShareType Classify(string payload) => TryReadLink(payload, out _, out _) ? ContentShareType.Link : ContentShareType.Text;

    /// <summary>
    /// Whether pasted text looks like something its owner would not mean to show a whole server: a
    /// password, a private key, an API or sign-in token, or an address with credentials in it. A
    /// guess, used only to warn before sharing, never to refuse.
    /// </summary>
    public static bool LooksSensitive(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (SecretPattern.IsMatch(text)) return true;
        string single = text.Trim();
        return LooksLikePassword(single) || LooksLikeKey(single);
    }

    // One word holding every kind of character: what password managers generate and password
    // rules demand. E-mail addresses are left alone.
    private static bool LooksLikePassword(string text)
    {
        if (text.Length < 8 || text.Length > 64 || text.Contains("://")) return false;
        bool lower = false, upper = false, digit = false, symbol = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) return false;
            if (char.IsLower(c)) lower = true;
            else if (char.IsUpper(c)) upper = true;
            else if (char.IsDigit(c)) digit = true;
            else symbol = true;
        }
        int at = text.IndexOf('@');
        bool email = at > 0 && at == text.LastIndexOf('@') && text.IndexOf('.', at) > at + 1;
        return lower && upper && digit && symbol && !email;
    }

    // A long unbroken run of letters and digits in both cases: the shape of most API keys and
    // session tokens. Hashes and ids are usually a single case, so they do not trip it.
    private static bool LooksLikeKey(string text)
    {
        if (text.Length < 32 || text.Length > 512) return false;
        bool lower = false, upper = false, digit = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 'a' && c <= 'z') lower = true;
            else if (c >= 'A' && c <= 'Z') upper = true;
            else if (c >= '0' && c <= '9') digit = true;
            else if (c != '-' && c != '_' && c != '+' && c != '/' && c != '=' && c != '.') return false;
        }
        return lower && upper && digit;
    }

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

    /// <summary>
    /// Text for a label with rich text off, shown exactly as written. TextMeshPro still decodes
    /// \uXXXX and \UXXXXXXXX escapes there, so a zero-width space follows every backslash; it
    /// changes nothing visible and stops any escape from turning into a different character.
    /// </summary>
    public static string Verbatim(string text) => string.IsNullOrEmpty(text) ? text : text.Replace("\\", BackslashBreak);

    /// <summary>Remote text for a label with rich text on: no tag can open and no escape can decode.</summary>
    public static string Plain(string text) => string.IsNullOrEmpty(text) ? text : Verbatim(text).Replace('<', SingleAngleQuote);

    // The orb names the site in the ASCII spelling the browser will use, so a look-alike name
    // cannot pass for the real one at a glance.
    private static string DescribeLink(string payload)
    {
        if (!TryReadLink(Normalize(payload), out Uri link, out _)) return null;
        return Plain(link.HostNameType == UriHostNameType.IPv6 ? link.Host : link.IdnHost);
    }

    private static string DescribeText(string payload)
    {
        string text = Normalize(payload);
        return text.Length == 0 ? null : Plain(Preview(text, BasisContentSphere.MaxTitleNameLength));
    }

    private static BasisPanelSeverity SeverityOf(BasisLinkSafety.Warning warnings)
    {
        if ((warnings & BasisLinkSafety.Severe) != 0) return BasisPanelSeverity.Hot;
        return warnings != BasisLinkSafety.Warning.None ? BasisPanelSeverity.Caution : BasisPanelSeverity.None;
    }

    private static BasisPanelSeverity Worse(BasisPanelSeverity a, BasisPanelSeverity b) => a > b ? a : b;

    private static void AddLinkRows(List<BasisMenuDialoguePanel.DetailRow> rows, Uri link, string address, BasisLinkSafety.Warning warnings)
    {
        rows.Add(new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("content.share.link.site"), Verbatim(BasisLinkSafety.SiteLabel(link)), true));
        for (int i = 0; i < LinkWarnings.Length; i++)
        {
            if ((warnings & LinkWarnings[i].Flag) == 0) continue;
            rows.Add(WarningRow(LinkWarnings[i].Key, (LinkWarnings[i].Flag & BasisLinkSafety.Severe) != 0));
        }
        rows.Add(new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("content.share.type.link"), Verbatim(address), true));
    }

    private static BasisMenuDialoguePanel.DetailRow WarningRow(string key, bool severe)
    {
        string title = $"<color={(severe ? SevereHex : CautionHex)}>{BasisLocalization.Get(key + ".title")}</color>";
        return new BasisMenuDialoguePanel.DetailRow(title, BasisLocalization.Get(key), true);
    }

    private static BasisMenuDialoguePanel.DetailRow TextRow(string text) =>
        new BasisMenuDialoguePanel.DetailRow(BasisLocalization.Get("content.share.type.text"), Verbatim(text), true);
}
