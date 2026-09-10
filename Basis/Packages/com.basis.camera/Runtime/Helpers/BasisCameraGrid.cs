using UnityEngine;
public enum BasisCameraGridPattern
{
    Thirds = 0,
    Quarters = 1,
    GoldenRatio = 2,
    Diagonals = 3,
    Centre = 4,
}
public static class BasisCameraGrid
{
    public const float DefaultOpacity = 0.6f, MinOpacity = 0.1f, MaxOpacity = 1f;
    public const string ShaderResource = "BasisGridOverlay";
    public const string MissingShader = "Grid overlay shader 'BasisGridOverlay' could not be loaded — the alignment grid is unavailable.";
    private const float ReferenceHeight = 720f, MinLineWidth = 1f, MaxLineWidth = 6f;
    private const int EvenShaderPattern = 0, GoldenShaderPattern = 1, DiagonalShaderPattern = 2, CentreShaderPattern = 3;
    public static readonly string[] PatternKeys = { "camera.grid.thirds", "camera.grid.quarters", "camera.grid.golden", "camera.grid.diagonal", "camera.grid.centre" };
    private static readonly int[] ShaderPatterns = { EvenShaderPattern, EvenShaderPattern, GoldenShaderPattern, DiagonalShaderPattern, CentreShaderPattern };
    private static readonly float[] DivisionCounts = { 3f, 4f, 0f, 0f, 0f };
    private static readonly int ColourProperty = Shader.PropertyToID("_GridColor"), OpacityProperty = Shader.PropertyToID("_GridOpacity");
    private static readonly int ThicknessProperty = Shader.PropertyToID("_GridThickness"), PatternProperty = Shader.PropertyToID("_GridPattern");
    private static readonly int DivisionsProperty = Shader.PropertyToID("_GridDivisions");
    public static int Pattern(int index) => Mathf.Clamp(index, 0, PatternKeys.Length - 1);
    public static float LineThickness(int feedHeight) => Mathf.Clamp(Mathf.Round(feedHeight / ReferenceHeight), MinLineWidth, MaxLineWidth);
    public static void Apply(Material material, int patternIndex, float opacity, int feedHeight)
    {
        int index = Pattern(patternIndex);
        material.SetColor(ColourProperty, Color.white);
        material.SetFloat(OpacityProperty, Mathf.Clamp(opacity, MinOpacity, MaxOpacity));
        material.SetFloat(ThicknessProperty, LineThickness(feedHeight));
        material.SetFloat(PatternProperty, ShaderPatterns[index]);
        material.SetFloat(DivisionsProperty, DivisionCounts[index]);
    }
}
