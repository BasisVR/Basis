using System.Globalization;
using Basis.BasisUI;
using Basis.Scripts.Avatar;
using UnityEngine;
using Basis.Scripts.Settings;

/// <summary>
/// Settings bridge that forwards the three avatar-load concurrency sliders into
/// <see cref="BasisAvatarFactory"/>. Unlike the other SMModule classes, this one is not
/// a <see cref="BasisSettingsBase"/> MonoBehaviour — it doesn't need to live on the
/// BasisFramework prefab. It subscribes directly to
/// <see cref="BasisSettingsSystem.OnSettingChanged"/> via a runtime-init hook, which
/// means adding a new load-gate setting is a pure code change with no prefab edit.
/// </summary>
public static class SMModuleAvatarLoadGates
{
    private static string K_DOWNLOADS => BasisSettingsDefaults.MaxConcurrentAvatarDownloads.BindingKey;
    private static string K_DISC_LOADS => BasisSettingsDefaults.MaxConcurrentAvatarDiscLoads.BindingKey;
    private static string K_ADDRESSABLES => BasisSettingsDefaults.MaxConcurrentAvatarAddressables.BindingKey;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register()
    {
        // Seed the gates from whatever is currently in the settings store so the first
        // room load uses the saved capacities, not the hard-coded defaults inside
        // BasisAvatarFactory. If the setting was never saved, LoadFloat returns the
        // provided default which matches the binding default.
        ApplyDownloads(BasisSettingsSystem.LoadFloat(K_DOWNLOADS, BasisSettingsDefaults.MaxConcurrentAvatarDownloads.DefaultValue.GetDefault()));
        ApplyDiscLoads(BasisSettingsSystem.LoadFloat(K_DISC_LOADS, BasisSettingsDefaults.MaxConcurrentAvatarDiscLoads.DefaultValue.GetDefault()));
        ApplyAddressables(BasisSettingsSystem.LoadFloat(K_ADDRESSABLES, BasisSettingsDefaults.MaxConcurrentAvatarAddressables.DefaultValue.GetDefault()));

        BasisSettingsSystem.OnSettingChanged -= OnSettingChanged;
        BasisSettingsSystem.OnSettingChanged += OnSettingChanged;
    }

    private static void OnSettingChanged(string settingName, string value)
    {
        // BasisSettingsBase ToLowers for its subclasses; we do the same so keys match.
        string key = settingName?.ToLower();
        if (key == K_DOWNLOADS)
        {
            if (TryParse(value, out float raw)) ApplyDownloads(raw);
        }
        else if (key == K_DISC_LOADS)
        {
            if (TryParse(value, out float raw)) ApplyDiscLoads(raw);
        }
        else if (key == K_ADDRESSABLES)
        {
            if (TryParse(value, out float raw)) ApplyAddressables(raw);
        }
    }

    private static bool TryParse(string s, out float value) =>
        float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    private static void ApplyDownloads(float raw)
    {
        int cap = Mathf.Max(1, Mathf.RoundToInt(raw));
        BasisAvatarFactory.SetDownloadGateCapacity(cap);
        BasisDebug.Log($"MaxConcurrentAvatarDownloads -> {cap}");
    }

    private static void ApplyDiscLoads(float raw)
    {
        int cap = Mathf.Max(1, Mathf.RoundToInt(raw));
        BasisAvatarFactory.SetDiscGateCapacity(cap);
        BasisDebug.Log($"MaxConcurrentAvatarDiscLoads -> {cap}");
    }

    private static void ApplyAddressables(float raw)
    {
        int cap = Mathf.Max(1, Mathf.RoundToInt(raw));
        BasisAvatarFactory.SetAddressableGateCapacity(cap);
        BasisDebug.Log($"MaxConcurrentAvatarAddressables -> {cap}");
    }
}
