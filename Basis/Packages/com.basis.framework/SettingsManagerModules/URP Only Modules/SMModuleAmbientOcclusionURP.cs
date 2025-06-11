#define SETTINGS_MANAGER_UNIVERSAL
using BattlePhaze.SettingsManager;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[Serializable]
public class SMModuleAmbientOcclusionURP : SettingsManagerOption
{
    public ScreenSpaceAmbientOcclusion AmbientOcclusion;

    public override void ReceiveOption(SettingsMenuInput option, SettingsManager manager)
    {
        /*
                    if (NameReturn(0, option))
                    {
                        BasisDebug.Log($"[SMModuleAmbientOcclusionURP] Received quality option: {option.SelectedValue}");
                        SetQuality(option.SelectedValue);
                    }
            */
    }
    private void SetQuality(string quality)
    {
        if (AmbientOcclusion == null)
        {
            BasisDebug.LogError("[SMModuleAmbientOcclusionURP] AmbientOcclusion is null. Cannot set quality.");
            return;
        }

        AmbientOcclusion.SetActive(quality != "very low");

        if (quality == "very low")
        {
            BasisDebug.Log("[SMModuleAmbientOcclusionURP] Quality set to 'very low'; Ambient Occlusion disabled.");
            return;
        }

        BasisDebug.Log($"[SMModuleAmbientOcclusionURP] Configuring SSAO for quality level: {quality}");
        switch (quality)
        {
            case "low":
                SetSettings("Low", "Low", "Low");
                break;
            case "medium":
                SetSettings("Medium", "Medium", "Medium");
                break;
            case "high":
                SetSettings("Medium", "High", "High");
                break;
            case "ultra":
                SetSettings("High", "High", "High");
                break;
            default:
                Debug.LogWarning($"[SMModuleAmbientOcclusionURP] Unknown quality level: {quality}. No changes made.");
                break;
        }
    }

    private object settingsInstance;

    private void SetSettings(string SamplesQuality = "High", string NormalSamples = "Medium", string BlurQuality = "High")
    {
        if (AmbientOcclusion == null)
        {
            BasisDebug.LogError("SSAO Feature reference is missing.");
            return;
        }

        FieldInfo settingsField = typeof(ScreenSpaceAmbientOcclusion).GetField("m_Settings", BindingFlags.NonPublic | BindingFlags.Instance);
        if (settingsField != null)
        {
            settingsInstance = settingsField.GetValue(AmbientOcclusion);
            if (settingsInstance == null)
            {
                BasisDebug.LogError("Failed to retrieve m_Settings.");
                return;
            }

            // Set the values dynamically using reflection
            SetSettingValue("NormalSamples", NormalSamples);
            SetSettingValue("Samples", SamplesQuality);
            SetSettingValue("BlurQuality", BlurQuality);
        }
        else
        {
            BasisDebug.LogError("m_Settings field not found.");
        }
    }

    private void SetSettingValue(string fieldName, string value)
    {
        // Access the field within m_Settings
        var field = settingsInstance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            // Convert the string value to the appropriate enum type
            object enumValue = ConvertToEnum(field.FieldType, value);
            if (enumValue != null)
            {
                field.SetValue(settingsInstance, enumValue);
                BasisDebug.Log($"[SMModuleAmbientOcclusionURP] Set {fieldName} to {enumValue}");
            }
            else
            {
                Debug.LogWarning($"[SMModuleAmbientOcclusionURP] Unable to convert '{value}' to {field.FieldType}.");
            }
        }
        else
        {
            Debug.LogWarning($"[SMModuleAmbientOcclusionURP] Field '{fieldName}' not found.");
        }
    }

    private object ConvertToEnum(Type enumType, string value)
    {
        // Check if the type is an enum
        if (enumType.IsEnum)
        {
            try
            {
                return Enum.Parse(enumType, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SMModuleAmbientOcclusionURP] Error parsing enum: {e.Message}");
            }
        }
        return null;
    }
}
