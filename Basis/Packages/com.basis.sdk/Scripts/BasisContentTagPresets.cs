/// <summary>
/// Curated content-safety tag presets shared between the SDK inspectors (writers) and
/// the client filter UI (readers). Single source of truth so the same labels appear
/// for creators tagging content and for users blocking it. Creators can always type
/// custom tags too — these are just the buttons we surface as toggles.
/// </summary>
public static class BasisContentTagPresets
{
    /// <summary>
    /// Order is the display order. Adult/explicit categories first so they're hard to
    /// overlook, then accessibility warnings.
    /// </summary>
    public static readonly string[] All =
    {
        "18+",
        "Sexual Content",
        "Nudity",
        "Horror",
        "Gore",
        "Violence",
        "Drugs",
        "Seizure Warning",
        "Loud Audio",
        "Flashing Lights",
    };
}
