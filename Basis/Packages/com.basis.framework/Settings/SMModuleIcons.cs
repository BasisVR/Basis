using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;

public class SMModuleIcons : BasisSettingsBase
{
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        if (BasisLocalCameraDriver.Instance == null)
        {
            return;
        }
        if (BasisLocalCameraDriver.Instance.microphoneIconDriver == null)
        {
            return;
        }
        switch (optionValue)
        {
            case "ActivityDetection":
                BasisLocalCameraDriver.Instance.microphoneIconDriver.OnDisplayModeChanged(BasisLocalMicrophoneIconDriver.MicrophoneDisplayMode.ActivityDetection);
                break;
            case "AlwaysVisible":
                BasisLocalCameraDriver.Instance.microphoneIconDriver.OnDisplayModeChanged(BasisLocalMicrophoneIconDriver.MicrophoneDisplayMode.AlwaysVisible);
                break;
            case "Hidden":
                BasisLocalCameraDriver.Instance.microphoneIconDriver.OnDisplayModeChanged(BasisLocalMicrophoneIconDriver.MicrophoneDisplayMode.Off);
                break;
        }
    }
}
