using System;

[Serializable]
public class BasisPlayerSettingsData
{
    //specify values with a value else when a outdated setting is loaded its default will be well default - ld
    public string UUID = string.Empty;
    public float VolumeLevel = 1;
    public bool AvatarVisible = true;
    public bool AvatarInteraction = true;
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
