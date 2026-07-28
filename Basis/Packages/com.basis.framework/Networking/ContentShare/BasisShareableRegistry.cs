using System;
using System.Collections.Generic;

public enum BasisShareableKind
{
    Server,
    World,
    Prop,
    Avatar,
    Image,
    Other
}

/// <summary>How the Library should present a <see cref="BasisShareableAction"/>.
/// Semantic (not a concrete UI style) so this framework type stays decoupled from
/// BasisUI prefabs/sprites — the Library maps these to buttons.</summary>
public enum BasisShareableActionStyle
{
    /// <summary>A normal/affirmative action (e.g. "Share"), rendered as an accept button.</summary>
    Positive,
    /// <summary>A removal action, rendered as the trash button. When no explicit
    /// confirm text is supplied the Library shows its standard "remove {name}?" prompt.</summary>
    Destructive,
}

/// <summary>One button shown on a Library shareable entry. The registering package owns
/// the semantics (label + callback); the Library just presents it.</summary>
public sealed class BasisShareableAction
{
    /// <summary>Button text. Empty = icon-only (only meaningful for
    /// <see cref="BasisShareableActionStyle.Destructive"/>, which uses the trash icon).</summary>
    public string Label;
    public BasisShareableActionStyle Style;
    public Action Invoke;
    /// <summary>Non-null = the Library shows a yes/no dialog with this title/body before
    /// invoking <see cref="Invoke"/> (for consent-style actions).</summary>
    public string ConfirmTitle;
    public string ConfirmBody;
}

public sealed class BasisShareableEntry
{
    public string Id;
    public BasisShareableKind Kind;
    public string Title;
    public string SharerName;

    /// <summary>Buttons rendered on the entry (in order), e.g. a "Share"/"Unshare" toggle
    /// followed by a remove button. Removal is just a <see cref="BasisShareableActionStyle.Destructive"/>
    /// action here — the Library has no dedicated remove path.</summary>
    public List<BasisShareableAction> Actions;
}

/// <summary>
/// Framework-side list of shareables currently active in the instance, surfaced in the Library
/// "Instantiated" tab. Content shares register their spheres here, and optional add-on packages
/// (e.g. the image pickup) register their own entries, so the Library UI can monitor and remove them
/// without the framework referencing those packages.
/// </summary>
public static class BasisShareableRegistry
{
    private static readonly Dictionary<string, BasisShareableEntry> Entries = new Dictionary<string, BasisShareableEntry>();

    public static Action OnChanged;

    public static void Register(BasisShareableEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Id)) return;
        Entries[entry.Id] = entry;
        OnChanged?.Invoke();
    }

    public static void Unregister(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (Entries.Remove(id)) OnChanged?.Invoke();
    }

    public static void SetDetail(string id, string detail)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (Entries.TryGetValue(id, out BasisShareableEntry entry))
        {
            entry.Title = detail;
            OnChanged?.Invoke();
        }
    }

    /// <summary>Replace an entry's action buttons — e.g. flipping a "Share" toggle to
    /// "Unshare" after it's invoked. Pass an empty list (or null) to leave no buttons.</summary>
    public static void SetActions(string id, List<BasisShareableAction> actions)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (Entries.TryGetValue(id, out BasisShareableEntry entry))
        {
            entry.Actions = actions;
            OnChanged?.Invoke();
        }
    }

    /// <summary>Update who shared an entry (rendered as "shared by …") after
    /// registration — e.g. once a received share's sharer is identified.</summary>
    public static void SetSharerName(string id, string sharerName)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (Entries.TryGetValue(id, out BasisShareableEntry entry))
        {
            entry.SharerName = sharerName;
            OnChanged?.Invoke();
        }
    }

    public static IReadOnlyCollection<BasisShareableEntry> GetAll() => Entries.Values;
}
