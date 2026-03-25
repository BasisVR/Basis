using System.Globalization;
using UnityEngine;
public abstract class BasisSettingsBase : MonoBehaviour
{
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
    public void TOLowerValidSettingsChange(string matchedSettingName, string optionValue)
    {
        ValidSettingsChange(matchedSettingName.ToLower(), optionValue.ToLower());
    }
    /// <summary>
    /// Called when a valid setting change occurs.
    /// Provides which setting was matched and the new option value.
    /// </summary>
    public abstract void ValidSettingsChange(string matchedSettingName, string optionValue);
    public abstract void ChangedSettings();
}
