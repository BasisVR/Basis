using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Static metadata/configuration for the handheld camera system:
/// resolution presets, formats, capture settings, and references to
/// URP post-processing components inside a <see cref="VolumeProfile"/>.
/// </summary>
[System.Serializable]
public class BasisHandHeldCameraMetaData
{
    /// <summary>
    /// Supported output resolutions (width, height).
    /// </summary>
    public readonly (int width, int height)[] resolutions = new (int, int)[]
    {
        (1280, 720),  // 720p
        (1920, 1080), // 1080p
        (3840, 2160), // 4K
        (7680, 4320), // 8K
        // (16384,16384), // 16k (disabled by default)
    };

    /// <summary>
    /// Supported capture formats (e.g., PNG, EXR).
    /// </summary>
    public readonly string[] formats = { "PNG", "EXR" };

    /// <summary>
    /// Supported MSAA (multisample anti-aliasing) levels.
    /// </summary>
    public readonly int[] MSAALevels = { 1, 2, 4, 8 };

    /// <summary>
    /// Aperture (f-stop) presets used by the physical camera model.
    /// </summary>
    public readonly string[] apertures = { "f/1.4", "f/2.8", "f/4", "f/5.6", "f/8", "f/11", "f/16" };

    /// <summary>
    /// Shutter speed presets used by the physical camera model (as strings like "1/125").
    /// </summary>
    public readonly string[] shutterSpeeds = { "1/1000", "1/500", "1/250", "1/125", "1/60", "1/30", "1/15" };

    /// <summary>
    /// ISO presets used by the physical camera model.
    /// </summary>
    public readonly string[] isoValues = { "100", "200", "400", "800", "1600", "3200", "6400" };

    /// <summary>
    /// URP post-processing profile containing the effects referenced below.
    /// Ensure it has the components you plan to use (DoF, Bloom, etc.).
    /// </summary>
    public VolumeProfile Profile;

    /// <summary>
    /// Tonemapping component reference (optional; retrieved from <see cref="Profile"/>).
    /// </summary>
    public Tonemapping tonemapping;

    /// <summary>
    /// Depth of Field component reference (optional; retrieved from <see cref="Profile"/>).
    /// </summary>
    public DepthOfField depthOfField;

    /// <summary>
    /// Bloom component reference (optional; retrieved from <see cref="Profile"/>).
    /// </summary>
    public Bloom bloom;

    /// <summary>
    /// Color Adjustments component reference (optional; retrieved from <see cref="Profile"/>).
    /// </summary>
    public ColorAdjustments colorAdjustments;

    /// <summary>
    /// volumetrics
    /// </summary>
#if Basis_VOLUMETRIC_SUPPORTED

    public VolumetricFogVolumeComponent VolumetricFogVolume;
#endif
}
