using Basis.Scripts.Device_Management;
using UnityEngine;
public class BasisVerticalSyncModule : BasisSettingsBase
{
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        Application.targetFrameRate = -1;
        QualitySettings.maxQueuedFrames = -1;
#if UNITY_SERVER
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 25;
#endif
        if (BasisDeviceManagement.StaticCurrentMode == BasisConstants.Desktop)
        {
            BasisDebug.Log($"Vertical Sync Changed to {optionValue}", BasisDebug.LogTag.Local);
            switch (optionValue.ToLower())
            {
                case "on":
                    QualitySettings.vSyncCount = 1;
                    Application.targetFrameRate = -1;
                    break;
                case "capped":
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = (int)Screen.currentResolution.refreshRateRatio.value;
                    break;
                case "half":
                    QualitySettings.vSyncCount = 2;
                    Application.targetFrameRate = -1;
                    break;
                case "off":
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = -1;
                    break;
            }
        }
        else
        {
            QualitySettings.vSyncCount = 0;
        }
    }
}
