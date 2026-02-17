using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using UnityEngine;

public class BasisVerticalSyncModule : BasisSettingsBase
{
    public static int CappedFrameRateSelected = 120;

    private enum VSyncMode { On, Capped, Half, Off }
    private static VSyncMode _requestedMode = VSyncMode.On;

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
#if UNITY_SERVER
        // Server ignores client settings entirely
        return;
#endif


        // Cap value setting
        if (matchedSettingName == BasisSettingsDefaults.VSyncCapFps.BindingKey)
        {
            if (int.TryParse(
                    optionValue,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out CappedFrameRateSelected))
            {
                BasisDebug.Log(
                    $"Target Framerate set to {CappedFrameRateSelected}",
                    BasisDebug.LogTag.Local);
            }
            return;
        }
        if (matchedSettingName == BasisSettingsDefaults.VSync.BindingKey)
        {
            // Non-desktop devices force vsync off
            if (BasisDeviceManagement.StaticCurrentMode != BasisConstants.Desktop)
            {
                _requestedMode = VSyncMode.Off;
                return;
            }

            switch (optionValue)
            {
                case "on":
                    _requestedMode = VSyncMode.On;
                    break;
                case "capped":
                    _requestedMode = VSyncMode.Capped;
                    break;
                case "half":
                    _requestedMode = VSyncMode.Half;
                    break;
                case "off":
                    _requestedMode = VSyncMode.Off;
                    break;
            }
        }
    }

    public override void ChangedSettings()
    {
#if UNITY_SERVER
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 25;
        return;
#endif

        if (BasisDeviceManagement.StaticCurrentMode != BasisConstants.Desktop)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            return;
        }

        ApplyMode(_requestedMode);
    }

    private static void ApplyMode(VSyncMode mode)
    {
        QualitySettings.maxQueuedFrames = -1;

        switch (mode)
        {
            case VSyncMode.On:
                QualitySettings.vSyncCount = 1;
                Application.targetFrameRate = -1;
                break;

            case VSyncMode.Capped:
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = CappedFrameRateSelected;
                break;

            case VSyncMode.Half:
                QualitySettings.vSyncCount = 2;
                Application.targetFrameRate = -1;
                break;

            case VSyncMode.Off:
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                break;
        }
    }
}
