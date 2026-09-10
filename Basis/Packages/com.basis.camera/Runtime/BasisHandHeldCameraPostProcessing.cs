using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
public partial class BasisHandHeldCamera
{
    public const TonemappingMode PreviewTonemapping = TonemappingMode.Neutral;
    public TonemappingMode CaptureTonemapping { get; private set; } = TonemappingMode.ACES;
    public bool OverrideVolumetricFog
    {
        get
        {
#if Basis_VOLUMETRIC_SUPPORTED
            return MetaData.VolumetricFogVolume != null && MetaData.VolumetricFogVolume.active;
#else
            return false;
#endif
        }
    }
    public void InitializePostProcessingVolume()
    {
        if (captureCamera == null) return;
        if (CameraData == null) CameraData = captureCamera.GetUniversalAdditionalCameraData();

        CameraData.renderPostProcessing = true;
#if BASIS_HAS_GI && !UNITY_ANDROID
        SMModuleGlobalIlluminationURP.RegisterCamera(captureCamera);
#endif

        Volume volume = FindPostProcessingVolume();
        if (volume == null) return;

        if (MetaData.Profile == null) MetaData.Profile = volume.sharedProfile;
        else if (volume.sharedProfile != MetaData.Profile) volume.sharedProfile = MetaData.Profile;

        CameraData.volumeLayerMask = 1 << volume.gameObject.layer;
        CameraData.volumeTrigger = volume.transform;
    }
    public void InitializeVolumetrics()
    {
#if Basis_VOLUMETRIC_SUPPORTED
        if (MetaData.VolumetricFogVolume == null) MetaData.Profile.TryGet(out MetaData.VolumetricFogVolume);

        if (captureCamera != null && VolumetricFogSource != null)
        {
            VolumetricFogSource.Initialize(captureCamera);

            int defaultLayer = LayerMask.NameToLayer("Default");
            VolumetricFogSource.WorldVolumeLayerMask = defaultLayer >= 0 ? 1 << defaultLayer : 1;
            UpdateVolumetricFogSource();
        }
#endif
    }
    public void SetOverrideVolumetricFog(bool enabled)
    {
#if Basis_VOLUMETRIC_SUPPORTED
        if (MetaData.VolumetricFogVolume != null) MetaData.VolumetricFogVolume.active = enabled;
        UpdateVolumetricFogSource();
#endif
    }
    public void ToggleToneMapping(TonemappingMode mappingMode)
    {
        if (MetaData.tonemapping == null) return;
        MetaData.tonemapping.mode.value = mappingMode;
    }
    public void SetCaptureTonemapping(int mode) => CaptureTonemapping = System.Enum.IsDefined(typeof(TonemappingMode), mode) ? (TonemappingMode)mode : TonemappingMode.ACES;
    private Volume FindPostProcessingVolume()
    {
        Volume[] volumes = GetComponentsInChildren<Volume>(true);
        for (int Index = 0; Index < volumes.Length; Index++)
        {
            if (MetaData.Profile != null && volumes[Index].sharedProfile == MetaData.Profile) return volumes[Index];
        }
        return volumes.Length > 0 ? volumes[0] : null;
    }
    private void InitializeTonemapping()
    {
        if (MetaData.Profile.TryGet(out MetaData.tonemapping)) ToggleToneMapping(PreviewTonemapping);
    }
    private void InitializeDepthOfField()
    {
        if (!MetaData.Profile.TryGet(out MetaData.depthOfField)) BasisDebug.LogError("DoF profile not found!");
        else BasisDebug.Log($"DoF is loaded. FocusDistance: {MetaData.depthOfField.focusDistance.value}");
    }
    private void UpdateVolumetricFogSource()
    {
#if Basis_VOLUMETRIC_SUPPORTED
        if (VolumetricFogSource == null) return;

        bool useCameraOverride = OverrideVolumetricFog, worldIsInShot = backgroundMode == BasisCameraBackgroundMode.World || backgroundKeepsWorld;

        VolumetricFogSource.SuppressFog = !useCameraOverride && !worldIsInShot;
        VolumetricFogSource.UseWorldFog = !useCameraOverride && worldIsInShot;
#endif
    }
}
