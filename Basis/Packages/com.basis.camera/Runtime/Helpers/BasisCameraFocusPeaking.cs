using UnityEngine;
public static class BasisCameraFocusPeaking
{
    public const float DefaultSensitivity = 0.5f;
    public const string ShaderResource = "BasisFocusPeaking";
    public const string MissingShader = "Focus peaking shader 'BasisFocusPeaking' could not be loaded — the overlay is unavailable.";
    private const float LeastSensitiveThreshold = 0.30f, MostSensitiveThreshold = 0.02f, GreyPictureStrength = 0.85f;
    public static readonly Color[] Colours = { new Color(1f, 0.15f, 0.15f), new Color(0.25f, 1f, 0.35f), new Color(0.3f, 0.6f, 1f), new Color(1f, 0.9f, 0.2f), new Color(1f, 1f, 1f) };
    public static readonly string[] ColourKeys = { "camera.focusPeaking.red", "camera.focusPeaking.green", "camera.focusPeaking.blue", "camera.focusPeaking.yellow", "camera.focusPeaking.white" };
    private static readonly int ColourProperty = Shader.PropertyToID("_PeakColor"), ThresholdProperty = Shader.PropertyToID("_PeakThreshold");
    private static readonly int DesaturateProperty = Shader.PropertyToID("_PeakDesaturate");
    public static float Threshold(float sensitivity) => Mathf.Lerp(LeastSensitiveThreshold, MostSensitiveThreshold, Mathf.Clamp01(sensitivity));
    public static int ColourIndex(int index) => Mathf.Clamp(index, 0, Colours.Length - 1);
    public static Color Colour(int index) => Colours[ColourIndex(index)];
    public static void Apply(Material material, int colourIndex, float sensitivity, bool greyPicture)
    {
        material.SetColor(ColourProperty, Colour(colourIndex));
        material.SetFloat(ThresholdProperty, Threshold(sensitivity));
        material.SetFloat(DesaturateProperty, greyPicture ? GreyPictureStrength : 0f);
    }
}
