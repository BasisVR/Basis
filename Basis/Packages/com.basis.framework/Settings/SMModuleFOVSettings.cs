using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
public class SMModuleFOVSettings : BasisSettingsBase
{
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        SliderReadOption(optionValue, out SelectedFOV);
        if (BasisDeviceManagement.IsUserInDesktop())
        {
            BasisLocalCameraDriver.Instance.Camera.fieldOfView = SelectedFOV;
        }
    }

    public float SelectedFOV = 60;
}
