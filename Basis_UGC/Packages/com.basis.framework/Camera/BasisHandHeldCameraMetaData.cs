using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
[System.Serializable]
public class BasisHandHeldCameraMetaData
{

    public readonly (int width, int height)[] resolutions = new (int, int)[]
    {
        (1280, 720),  // 720p
        (1920, 1080), // 1080p
        (3840, 2160), // 4K
        (7680, 4320), // 8K
       // (16384,16384), // 16k
    };

    public readonly string[] formats = { "PNG", "EXR" };
    public readonly int[] MSAALevels = { 1, 2, 4, 8 };
    public readonly string[] apertures = { "f/1.4", "f/2.8", "f/4", "f/5.6", "f/8", "f/11", "f/16" };
    public readonly string[] shutterSpeeds = { "1/1000", "1/500", "1/250", "1/125", "1/60", "1/30", "1/15" };
    public readonly string[] isoValues = { "100", "200", "400", "800", "1600", "3200", "6400" };

    public VolumeProfile Profile;
    public Tonemapping tonemapping;
    public DepthOfField depthOfField;
    public Bloom bloom;
    public ColorAdjustments colorAdjustments;
}
