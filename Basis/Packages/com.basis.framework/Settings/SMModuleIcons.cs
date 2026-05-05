using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using UnityEngine;

// Only the mic-specific stanzas inside ValidSettingsChange are gated by
// BASIS_DISABLE_MICROPHONE. The class itself, plus the AvatarPreview and
// DesktopReticle handlers, must stay compiled in mic-disabled builds —
// they have nothing to do with the microphone.
public class SMModuleIcons : BasisSettingsBase
{
    // --- Canonical setting keys (from defaults) ---
    private static string K_AVATAR_PREVIEW   => BasisSettingsDefaults.AvatarPreview.BindingKey;
    private static string K_DESKTOP_RETICLE  => BasisSettingsDefaults.DesktopReticle.BindingKey;
#if !BASIS_DISABLE_MICROPHONE
    private static string K_MICROPHONE_ICON          => BasisSettingsDefaults.MicrophoneIcon.BindingKey;
    private static string K_MICROPHONE_ICON_OFFSET_X => BasisSettingsDefaults.MicrophoneIconOffsetX.BindingKey;
    private static string K_MICROPHONE_ICON_OFFSET_Y => BasisSettingsDefaults.MicrophoneIconOffsetY.BindingKey;
#endif

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (BasisLocalCameraDriver.Instance == null)
            return;

        if (matchedSettingName == K_AVATAR_PREVIEW)
        {
            bool enabled = optionValue == "true";
            BasisLocalCameraDriver.Instance.avatarPreviewDriver.SetEnabled(enabled);
            return;
        }

        if (matchedSettingName == K_DESKTOP_RETICLE)
        {
            bool enabled = optionValue == "true";
            // Reticle only exists while in desktop mode
            BasisDesktopEye.Instance?.Reticle?.SetEnabled(enabled);
            return;
        }

#if !BASIS_DISABLE_MICROPHONE
        if (BasisLocalCameraDriver.Instance.microphoneIconDriver == null)
            return;

        if (matchedSettingName == K_MICROPHONE_ICON)
        {
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
        else if (matchedSettingName == K_MICROPHONE_ICON_OFFSET_X)
        {
            if (float.TryParse(optionValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x))
            {
                var offset = BasisLocalCameraDriver.Instance.microphoneIconDriver.IconPositionOffset;
                offset.x = x;
                BasisLocalCameraDriver.Instance.microphoneIconDriver.IconPositionOffset = offset;
            }
        }
        else if (matchedSettingName == K_MICROPHONE_ICON_OFFSET_Y)
        {
            if (float.TryParse(optionValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
            {
                var offset = BasisLocalCameraDriver.Instance.microphoneIconDriver.IconPositionOffset;
                offset.y = y;
                BasisLocalCameraDriver.Instance.microphoneIconDriver.IconPositionOffset = offset;
            }
        }
#endif
    }

    public override void ChangedSettings()
    {
    }
}
