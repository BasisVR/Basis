using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;

public class SMModuleIcons : BasisSettingsBase
{
    // --- Canonical setting key (from defaults) ---
    private static string K_MICROPHONE_ICON => BasisSettingsDefaults.MicrophoneIcon.BindingKey; // "microphone icon"

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        // Only react to the microphone icon setting
        if (matchedSettingName != K_MICROPHONE_ICON)
            return;

        if (BasisLocalCameraDriver.Instance == null)
            return;

        if (BasisLocalCameraDriver.Instance.microphoneIconDriver == null)
            return;

        switch (optionValue)
        {
            case "activitydetection":
                BasisLocalCameraDriver.Instance.microphoneIconDriver
                    .OnDisplayModeChanged(
                        BasisLocalMicrophoneIconDriver.MicrophoneDisplayMode.ActivityDetection);
                break;

            case "alwaysvisible":
                BasisLocalCameraDriver.Instance.microphoneIconDriver
                    .OnDisplayModeChanged(
                        BasisLocalMicrophoneIconDriver.MicrophoneDisplayMode.AlwaysVisible);
                break;

            case "hidden":
                BasisLocalCameraDriver.Instance.microphoneIconDriver
                    .OnDisplayModeChanged(
                        BasisLocalMicrophoneIconDriver.MicrophoneDisplayMode.Off);
                break;
        }
    }

    public override void ChangedSettings()
    {
    }
}
