using Basis.Scripts.Drivers;
using UnityEngine;

public class SMModuleMotionVectorsURP : BasisSettingsBase
{
    public override void Awake()
    {
        base.Awake();
        BasisLocalCameraDriver.InstanceExists += ApplyMotionVectors;
        ApplyMotionVectors();
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue) { }
    public override void ChangedSettings() { }

    public static void ApplyMotionVectors()
    {
        BasisLocalCameraDriver driver = BasisLocalCameraDriver.Instance;
        if (driver == null || driver.Camera == null) return;

        DepthTextureMode mode = driver.Camera.depthTextureMode | DepthTextureMode.MotionVectors;
        if (driver.Camera.depthTextureMode != mode)
        {
            driver.Camera.depthTextureMode = mode;
        }
    }
}
