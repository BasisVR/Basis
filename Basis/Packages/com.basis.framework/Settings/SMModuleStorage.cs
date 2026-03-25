using Basis.BasisUI;
using Basis.Scripts.Avatar;

public class SMModuleStorage : BasisSettingsBase
{
    // --- Canonical setting key (from defaults) ---
    private static string K_AVATAR_DOWNLOAD_SIZE => BasisSettingsDefaults.AvatarDownloadSize.BindingKey; // "avatar download size"

    // Avatar Download Size
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        // Only react to the avatar download size setting
        if (matchedSettingName != K_AVATAR_DOWNLOAD_SIZE)
            return;

        if (SliderReadOption(optionValue, out float inMB))
        {
            long maxDownloadInBytes = MiBToBytes((int)inMB);
            BasisDebug.Log($"Avatar Download Size Was in MB {inMB} in Bytes was {maxDownloadInBytes}", BasisDebug.LogTag.Networking);

            BasisAvatarFactory.MaxDownloadSizeInMBRemote = maxDownloadInBytes;
        }
        else
        {
            // Fallback: 4 GiB
            BasisAvatarFactory.MaxDownloadSizeInMBRemote = 4L * 1024 * 1024 * 1024;
        }
    }

    private long MiBToBytes(int mb)
    {
        return (long)mb * 1024 * 1024;
    }

    public override void ChangedSettings()
    {
    }
}
