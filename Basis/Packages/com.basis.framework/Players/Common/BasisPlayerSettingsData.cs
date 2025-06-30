[System.Serializable]
public class BasisPlayerSettingsData
{
    public string UUID = string.Empty;
    public float VolumeLevel = 1.0f;
    public bool AvatarVisible = true;

    public BasisPlayerSettingsData(string uuid, float volume, bool avatarVisible)
    {
        UUID = uuid;
        VolumeLevel = volume;
        AvatarVisible = avatarVisible;
    }
    public static readonly BasisPlayerSettingsData Default = new BasisPlayerSettingsData(null, 1.0f,true);

}
