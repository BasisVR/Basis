using Basis.BasisUI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Accessibility settings module that controls bloom intensity
/// by modifying Bloom overrides on all existing Volumes in the scene.
/// Only applies when the bloom override toggle is enabled.
/// </summary>
public class SMModuleBloomOverrideURP : BasisSettingsBase
{
    private bool _overrideEnabled;
    private float _pendingIntensity = 1f;

    private static string K_USE_BLOOM_OVERRIDE => BasisSettingsDefaults.UseBloomOverride.BindingKey;
    private static string K_BLOOM_INTENSITY => BasisSettingsDefaults.BloomIntensity.BindingKey;

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (matchedSettingName == K_USE_BLOOM_OVERRIDE)
        {
            _overrideEnabled = optionValue == "true";
            ApplyOverride();
        }
        else if (matchedSettingName == K_BLOOM_INTENSITY)
        {
            if (SliderReadOption(optionValue, out float intensity))
            {
                _pendingIntensity = intensity;
                ApplyOverride();
            }
        }
    }

    public override void ChangedSettings()
    {
    }

    private void ApplyOverride()
    {
        Volume[] volumes = FindObjectsByType<Volume>(FindObjectsSortMode.None);
        foreach (Volume volume in volumes)
        {
            if (volume.profile == null)
                continue;

            if (!volume.profile.TryGet<Bloom>(out Bloom bloom))
                continue;

            if (_overrideEnabled)
            {
                bloom.intensity.overrideState = true;
                bloom.intensity.value = _pendingIntensity;
            }
            else
            {
                bloom.intensity.overrideState = false;
            }
        }
    }
}
