using System;

[Serializable]
public class BasisPlayerSettingsData
{
    public string UUID;
    public float VolumeLevel;
    public bool AvatarVisible;
    public bool AvatarInteraction;
    public int Version = 2;

    public static readonly BasisPlayerSettingsData Default = new BasisPlayerSettingsData("", 1.0f, true, true);

    public BasisPlayerSettingsData(string uuid, float volumeLevel, bool avatarVisible, bool avatarInteraction)
    {
        UUID = uuid;
        VolumeLevel = volumeLevel;
        AvatarVisible = avatarVisible;
        AvatarInteraction = avatarInteraction;
    }
}
