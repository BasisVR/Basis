using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Answers one question: when a setting appears to do nothing, where did it stop?
///
/// The chain from a settings slider to a pixel has several places it can break silently, and all of them
/// look identical from the outside. The feature may not be in the renderer the camera runs. The camera's
/// volume mask may not include the layer the settings module put its volume on. Another volume may sit at
/// a higher priority and supply the same overrides. Every one of those renders a perfectly good effect
/// that ignores the panel, so reading the code cannot tell them apart - only the live stack can.
/// </summary>
public static class BasisGlobalIlluminationDiagnostic
{
    [MenuItem("Basis/Rendering/Global Illumination Diagnostic")]
    public static void Run()
    {
        Debug.Log(Describe());
    }

    public static string Describe()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine("Basis Global Illumination diagnostic");
        report.AppendLine($"  play mode        : {Application.isPlaying} (the settings module only exists while playing)");

        RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
        report.AppendLine($"  pipeline         : {(pipeline != null ? pipeline.name : "<none>")}");
        DescribeFeature(pipeline, report);
        DescribeStack(report);
        DescribeCameras(report);
        DescribeVolumes(report);
        DescribeDefaultProfiles(report);
        return report.ToString();
    }

    private static void DescribeFeature(RenderPipelineAsset pipeline, StringBuilder report)
    {
        if (!(pipeline is UniversalRenderPipelineAsset asset))
        {
            report.AppendLine("  feature          : the active pipeline is not URP, so the effect cannot run at all");
            return;
        }

        bool found = false;
        foreach (ScriptableRendererData data in asset.rendererDataList)
        {
            if (data == null) { continue; }
            for (int index = 0; index < data.rendererFeatures.Count; index++)
            {
                if (!(data.rendererFeatures[index] is BasisGlobalIlluminationFeature feature)) { continue; }
                found = true;
                report.AppendLine($"  feature          : on '{data.name}', active={feature.isActive}, material={feature.Material != null}, rayTracing={feature.RayTracingAvailable}");
                // A debug view left on makes the pass replace the camera image instead of compositing into
                // it, and then every setting that only scales indirect colour reads as doing nothing while
                // the master switch still visibly works. That is this report's most common cause, so the
                // view a diagnostic was taken through is stated rather than assumed.
                report.AppendLine(feature.DebugView == BasisGlobalIlluminationDebugView.None
                    ? "  debug view       : Off (the effect is compositing into the camera image)"
                    : $"  debug view       : {feature.DebugView} - THE PASS IS REPLACING THE CAMERA IMAGE. Intensity, saturation, tint, the fallbacks and the emitters will all read as doing nothing until this is set back to Off.");
            }
        }
        if (!found)
        {
            report.AppendLine("  feature          : NOT PRESENT in any renderer this pipeline uses - the effect never runs, whatever the panel says");
        }
    }

    private static void DescribeStack(StringBuilder report)
    {
        VolumeStack stack = VolumeManager.instance != null ? VolumeManager.instance.stack : null;
        BasisGlobalIlluminationVolume resolved = stack != null ? stack.GetComponent<BasisGlobalIlluminationVolume>() : null;
        if (resolved == null)
        {
            report.AppendLine("  resolved stack   : no global illumination component in the stack - nothing will render");
            return;
        }

        // These are the values the shader will actually read this frame, after every volume has blended.
        report.AppendLine("  resolved stack   : " +
            $"enable={resolved.enable.value} mode={resolved.mode.value} intensity={resolved.intensity.value:F3} " +
            $"saturation={resolved.saturation.value:F3} emitterIntensity={resolved.emitterIntensity.value:F3} " +
            $"quality={resolved.quality.value} resolution={resolved.resolution.value} fallback={resolved.fallback.value}");
    }

    private static void DescribeCameras(StringBuilder report)
    {
        Camera[] cameras = Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int index = 0; index < cameras.Length; index++)
        {
            Camera camera = cameras[index];
            if (camera == null || camera.cameraType != CameraType.Game) { continue; }
            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) { continue; }
            report.AppendLine($"  camera '{camera.name}' : postFx={data.renderPostProcessing} volumeMask={LayerMaskText(data.volumeLayerMask)} " +
                $"trigger={(data.volumeTrigger != null ? data.volumeTrigger.name : "<self>")}");
        }
    }

    private static void DescribeVolumes(StringBuilder report)
    {
        Volume[] volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        List<string> carriers = new List<string>();
        for (int index = 0; index < volumes.Length; index++)
        {
            Volume volume = volumes[index];
            if (volume == null) { continue; }
            // profileRef is internal; asking for the instantiated profile only when there is one avoids the
            // profile getter cloning a copy just to look at it.
            VolumeProfile profile = volume.HasInstantiatedProfile() ? volume.profile : volume.sharedProfile;
            if (profile == null || !profile.TryGet(out BasisGlobalIlluminationVolume component)) { continue; }

            // Priority is what settles a disagreement, and the settings module sits at 1000. Anything above
            // that overriding the same parameters is what makes the panel look dead.
            carriers.Add($"    '{volume.name}' layer={LayerMask.LayerToName(volume.gameObject.layer)}({volume.gameObject.layer}) " +
                $"priority={volume.priority} weight={volume.weight} global={volume.isGlobal} " +
                $"enabled={volume.enabled} overrides[enable={component.enable.overrideState} intensity={component.intensity.overrideState}] " +
                $"intensity={component.intensity.value:F3}");
        }

        report.AppendLine($"  volumes carrying global illumination : {carriers.Count}");
        for (int index = 0; index < carriers.Count; index++) { report.AppendLine(carriers[index]); }
        if (carriers.Count > 1)
        {
            report.AppendLine("    (more than one: the highest priority that overrides a parameter wins it, and the settings module uses 1000)");
        }
    }

    private static void DescribeDefaultProfiles(StringBuilder report)
    {
        DescribeProfile("global default ", VolumeManager.instance != null ? VolumeManager.instance.globalDefaultProfile : null, report);
        DescribeProfile("quality default", VolumeManager.instance != null ? VolumeManager.instance.qualityDefaultProfile : null, report);
    }

    private static void DescribeProfile(string label, VolumeProfile profile, StringBuilder report)
    {
        if (profile == null)
        {
            report.AppendLine($"  {label}  : <none>");
            return;
        }
        if (!profile.TryGet(out BasisGlobalIlluminationVolume component))
        {
            report.AppendLine($"  {label}  : '{profile.name}' carries no global illumination component");
            return;
        }
        report.AppendLine($"  {label}  : '{profile.name}' enable={component.enable.value} intensity={component.intensity.value:F3} emitterIntensity={component.emitterIntensity.value:F3}");
    }

    private static string LayerMaskText(LayerMask mask)
    {
        if (mask.value == ~0) { return "Everything"; }
        if (mask.value == 0) { return "Nothing"; }
        StringBuilder text = new StringBuilder();
        for (int layer = 0; layer < 32; layer++)
        {
            if ((mask.value & (1 << layer)) == 0) { continue; }
            if (text.Length > 0) { text.Append('|'); }
            string name = LayerMask.LayerToName(layer);
            text.Append(string.IsNullOrEmpty(name) ? layer.ToString() : name);
        }
        return text.ToString();
    }
}
