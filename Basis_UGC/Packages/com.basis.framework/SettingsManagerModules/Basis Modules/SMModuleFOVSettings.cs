using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using BattlePhaze.SettingsManager;

public class SMModuleFOVSettings : SettingsManagerOption
{
    public void Awake()
    {
        BasisLocalCameraDriver.InstanceExists += InstanceExists;
        if(BasisLocalCameraDriver.Instance != null)
        {
            InstanceExists();
        }
    }
    public void OnDestroy()
    {
        BasisLocalCameraDriver.InstanceExists -= InstanceExists;
    }
    private void InstanceExists()
    {
        if (BasisDeviceManagement.IsUserInDesktop())
        {
            BasisLocalCameraDriver.Instance.Camera.fieldOfView = SelectedFOV;
        }
    }
    public float SelectedFOV = 60;
    public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
    {
        if (NameReturn(0, Option))
        {
            if (SliderReadOption(Option, Manager, out SelectedFOV))
            {
                if (BasisLocalCameraDriver.Instance != null)
                {
                    InstanceExists();
                }
            }
        }
    }
}
