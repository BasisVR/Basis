using System.Globalization;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Basis.Scripts.Drivers;
using Basis.Scripts.Settings;
[NoAutoStaticsCleanup]
public abstract class BasisSettingsBase : MonoBehaviour
{
    [System.ThreadStatic] private static string lastSettingName, lastSettingNameLower, lastOptionValue, lastOptionValueLower;
    [System.ThreadStatic] private static CultureInfo lastSettingCulture;
    public virtual void Awake()
    {
        BasisSettingsSystem.OnSettingChanged += TOLowerValidSettingsChange;
        BasisSettingsSystem.OnSettingsFinishedChanges += ChangedSettings;
    }

    public void OnDestroy()
    {
        BasisSettingsSystem.OnSettingChanged -= TOLowerValidSettingsChange;
        BasisSettingsSystem.OnSettingsFinishedChanges -= ChangedSettings;
    }
    public bool SliderReadOption(string String, out float Value)
    {
        return float.TryParse(String, NumberStyles.Any, CultureInfo.InvariantCulture, out Value);
    }
    public static bool StaticSliderReadOption(string String, out float Value)
    {
        return float.TryParse(String, NumberStyles.Any, CultureInfo.InvariantCulture, out Value);
    }
    public static int PlayerVolumeLayerMask
    {
        get
        {
            if (!BasisLocalCameraDriver.HasInstance || BasisLocalCameraDriver.Instance == null) return 1;
            Camera camera = BasisLocalCameraDriver.Instance.Camera;
            if (camera == null || !camera.TryGetComponent(out UniversalAdditionalCameraData data)) return 1;
            return data.volumeLayerMask.value;
        }
    }
    public static bool CanOverrideVolume(Volume volume, int playerVolumeLayerMask)
    {
        return volume != null && (playerVolumeLayerMask & (1 << volume.gameObject.layer)) != 0;
    }
    public void TOLowerValidSettingsChange(string matchedSettingName, string optionValue)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (matchedSettingName == null || optionValue == null || !ReferenceEquals(culture, lastSettingCulture))
        {
            lastSettingCulture = culture;
            lastSettingName = null;
            lastOptionValue = null;
            ValidSettingsChange(matchedSettingName.ToLower(), optionValue.ToLower());
            return;
        }
        if (!ReferenceEquals(matchedSettingName, lastSettingName))
        {
            lastSettingNameLower = matchedSettingName.ToLower();
            lastSettingName = matchedSettingName;
        }
        if (!ReferenceEquals(optionValue, lastOptionValue))
        {
            lastOptionValueLower = optionValue.ToLower();
            lastOptionValue = optionValue;
        }
        ValidSettingsChange(lastSettingNameLower, lastOptionValueLower);
    }
    /// <summary>
    /// Called when a valid setting change occurs.
    /// Provides which setting was matched and the new option value.
    /// </summary>
    public abstract void ValidSettingsChange(string matchedSettingName, string optionValue);
    public abstract void ChangedSettings();
}
