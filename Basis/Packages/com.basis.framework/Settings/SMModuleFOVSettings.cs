using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;

public class SMModuleFOVSettings : BasisSettingsBase
{
    public float SelectedFOV = 60;

    // --- Canonical setting key (from defaults) ---
    private static string K_FIELD_OF_VIEW => BasisSettingsDefaults.FieldOfView.BindingKey; // "field of view"

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        // Only react to the canonical FOV key
        if (matchedSettingName != K_FIELD_OF_VIEW)
            return;

        if (SliderReadOption(optionValue, out SelectedFOV))
        {
            if (BasisDeviceManagement.IsUserInDesktop())
            {
                BasisLocalCameraDriver.Instance.Camera.fieldOfView = SelectedFOV;
            }
        }
    }

    public override void ChangedSettings()
    {
    }
}
