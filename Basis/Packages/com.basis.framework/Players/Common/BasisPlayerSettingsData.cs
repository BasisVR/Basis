using Unity.Scripting.LifecycleManagement;
using System;

/// <summary>
/// Serializable data container representing per-player settings in the Basis SDK.
/// </summary>
/// <remarks>
/// Instances are persisted to disk as JSON and cached in memory by
/// <see cref="BasisPlayerSettingsManager"/>. Declared as a struct to minimize
/// heap allocation and GC pressure when a session holds many remote players.
/// </remarks>
[Serializable]
[NoAutoStaticsCleanup]
public struct BasisPlayerSettingsData
{
    /// <summary>
    /// Unique identifier for the player. Used as the key for persistence.
    /// </summary>
    public string UUID;

    /// <summary>
    /// Master volume level for this player, typically clamped to <c>[0,5]</c>.
    /// </summary>
    public float VolumeLevel;

    /// <summary>
    /// Whether the player's avatar should be visible to others.
    /// </summary>
    public bool AvatarVisible;

    /// <summary>
    /// Whether the player's avatar allows interaction by others.
    /// </summary>
    public bool AvatarInteraction;

    /// <summary>
    /// Whether chat messages from this player are visible above their nameplate.
    /// Set to <c>false</c> to hide chat from this player.
    /// </summary>
    public bool ChatVisible;

    /// <summary>
    /// Whether this player is blocked. A blocked player has their audio muted,
    /// their avatar hidden, and their nameplate hidden on the local client.
    /// </summary>
    public bool IsBlocked;

    /// <summary>
    /// When true, this player's avatar is always rendered on the local client,
    /// bypassing distance-range, max-visible-avatar cap, and view-cone culling.
    /// Blocking and the avatar performance filter still apply.
    /// </summary>
    public bool AlwaysShowAvatar;

    /// <summary>
    /// When true, this player's incoming voice is normalised to a common loudness on the
    /// local client, independent of whatever gain their own microphone chain applied.
    /// <see cref="VolumeLevel"/> is applied on top, so the slider still works as a trim.
    /// Off by default — opt in per player from the individual player panel.
    /// </summary>
    public bool NormalizeLoudness;

    /// <summary>
    /// Whether jiggle grab-and-pull is allowed between the local player and this player,
    /// in both directions: when false they cannot grab our jiggle and we will not grab
    /// theirs. Blocking always denies regardless of this value.
    /// </summary>
    public bool JiggleGrabAllowed;

    /// <summary>
    /// Version number of the settings schema. Used to upgrade old files gracefully.
    /// A value of <c>0</c> after deserialization signals a missing/corrupt record.
    /// </summary>
    public int Version;

    /// <summary>Schema version written by this build.</summary>
    public const int CurrentVersion = 8;

    /// <summary>
    /// Default settings (volume 1.0, avatar visible, avatar interaction enabled, chat visible, not blocked).
    /// Useful as a baseline when creating new profiles or repairing corrupted files.
    /// </summary>
    public static readonly BasisPlayerSettingsData Default = new BasisPlayerSettingsData("", 1.0f, true, true, true, false);

    /// <summary>
    /// Brings a record loaded from an older schema up to <see cref="CurrentVersion"/>.
    /// Fields added after a record was written deserialize to <c>default</c>, which is the
    /// wrong answer for anything that should be opt-out rather than opt-in.
    /// </summary>
    public void UpgradeSchema()
    {
        if (Version < 7)
        {
            JiggleGrabAllowed = true;
        }
        // v6 and v7 forced normalisation on for every player; v8 turns it back off across the
        // board. Anyone still on those versions inherited the old default rather than choosing
        // it, so there is no opt-in here worth preserving — they can re-enable it per player.
        if (Version < 8)
        {
            NormalizeLoudness = false;
        }

        Version = CurrentVersion;
    }

    /// <summary>
    /// Creates a new player settings record with explicit values.
    /// </summary>
    /// <param name="uuid">Unique identifier for the player.</param>
    /// <param name="volumeLevel">Initial volume level.</param>
    /// <param name="avatarVisible">Whether the avatar should be visible.</param>
    /// <param name="avatarInteraction">Whether the avatar can be interacted with.</param>
    /// <param name="chatVisible">Whether chat messages from this player are visible.</param>
    /// <param name="isBlocked">Whether this player is blocked (audio/avatar/nameplate hidden).</param>
    /// <param name="alwaysShowAvatar">Whether this player's avatar bypasses spatial visibility culling.</param>
    public BasisPlayerSettingsData(string uuid, float volumeLevel, bool avatarVisible, bool avatarInteraction, bool chatVisible = true, bool isBlocked = false, bool alwaysShowAvatar = false)
    {
        UUID = uuid;
        VolumeLevel = volumeLevel;
        AvatarVisible = avatarVisible;
        AvatarInteraction = avatarInteraction;
        ChatVisible = chatVisible;
        IsBlocked = isBlocked;
        AlwaysShowAvatar = alwaysShowAvatar;
        NormalizeLoudness = false;
        JiggleGrabAllowed = true;
        Version = CurrentVersion;
    }

    /// <summary>
    /// True if this record holds meaningful data (UUID populated). A <c>default</c>
    /// struct returned from <see cref="BasisPlayerSettingsManager.RequestPlayerSettings"/>
    /// signals an invalid/missing input and will fail this check.
    /// </summary>
    public bool IsValid => !string.IsNullOrEmpty(UUID);
}
