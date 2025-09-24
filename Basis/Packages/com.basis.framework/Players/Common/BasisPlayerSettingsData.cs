using System;

/// <summary>
/// Serializable data container representing per-player settings in the Basis SDK.
/// </summary>
/// <remarks>
/// Instances of this class are persisted to disk as JSON and cached in memory by
/// <see cref="BasisPlayerSettingsManager"/>. Defaults are chosen to ensure
/// a consistent user experience even if older or incomplete settings files are loaded.
/// </remarks>
[Serializable]
public class BasisPlayerSettingsData
{
    /// <summary>
    /// Unique identifier for the player. Used as the key for persistence.
    /// </summary>
    public string UUID = string.Empty;

    /// <summary>
    /// Master volume level for this player, typically clamped to <c>[0,5]</c>.
    /// Defaults to <c>1.0</c>.
    /// </summary>
    public float VolumeLevel = 1;

    /// <summary>
    /// Whether the player's avatar should be visible to others.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool AvatarVisible = true;

    /// <summary>
    /// Whether the player's avatar allows interaction by others.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool AvatarInteraction = true;

    /// <summary>
    /// Version number of the settings schema. Used to upgrade old files gracefully.
    /// Defaults to <c>2</c>.
    /// </summary>
    public int Version = 2;

    /// <summary>
    /// A static default settings instance (volume 1.0, avatar visible, avatar interaction enabled).
    /// Useful as a baseline when creating new profiles or repairing corrupted files.
    /// </summary>
    public static readonly BasisPlayerSettingsData Default = new BasisPlayerSettingsData("", 1.0f, true, true);

    /// <summary>
    /// Creates a new player settings record with explicit values.
    /// </summary>
    /// <param name="uuid">Unique identifier for the player.</param>
    /// <param name="volumeLevel">Initial volume level.</param>
    /// <param name="avatarVisible">Whether the avatar should be visible.</param>
    /// <param name="avatarInteraction">Whether the avatar can be interacted with.</param>
    public BasisPlayerSettingsData(string uuid, float volumeLevel, bool avatarVisible, bool avatarInteraction)
    {
        UUID = uuid;
        VolumeLevel = volumeLevel;
        AvatarVisible = avatarVisible;
        AvatarInteraction = avatarInteraction;
    }
}
