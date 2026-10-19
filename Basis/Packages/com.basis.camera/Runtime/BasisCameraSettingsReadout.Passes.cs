using Basis.BasisUI;
using CameraSettings = BasisHandHeldCameraUI.CameraSettings;
public static partial class BasisCameraSettingsReadout
{
    private static void AppendRenderPasses(CameraSettings settings)
    {
        Section("camera.passes");
        Row("camera.passes.output", BasisLocalization.Get(BasisCameraPasses.OutputKeys[(int)BasisCameraPasses.SanitizeOutput(settings.passOutput)]));
        Row("camera.passes.depthRange", Number(BasisCameraPasses.SanitizeDepthRange(settings.passDepthRange)) + " m");
        Row("camera.passes.separate", settings.separatePasses ? BasisCameraPasses.Describe(BasisCameraPasses.SetOf(settings.separateDepth, settings.separateNormals, settings.separateMask, settings.separateMotion)) : OnOff(false));
    }
}
