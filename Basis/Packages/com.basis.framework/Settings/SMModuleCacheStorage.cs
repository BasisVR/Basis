using Basis.BasisUI;

public class SMModuleCacheStorage : BasisSettingsBase
{
    private static string K_CACHE_MAX_SIZE => BasisSettingsDefaults.CacheMaxSizeGB.BindingKey;

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName != K_CACHE_MAX_SIZE)
            return;

        if (SliderReadOption(optionValue, out float sizeGB))
        {
            long maxBytes = (long)(sizeGB * 1024L * 1024L * 1024L);
            BasisDebug.Log($"Cache Max Size set to {sizeGB} GB ({maxBytes} bytes)", BasisDebug.LogTag.Event);
            BasisStorageManagement.MaxCacheSizeBytes = maxBytes;
            BasisStorageManagement.EnforceCacheSizeLimit();
        }
        else
        {
            BasisStorageManagement.MaxCacheSizeBytes = BasisStorageManagement.DefaultMaxCacheSizeBytes;
        }
    }

    public override void ChangedSettings()
    {
    }
}
