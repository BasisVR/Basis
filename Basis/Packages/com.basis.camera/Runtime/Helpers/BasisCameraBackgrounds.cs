using Basis.Scripts.Drivers;
using UnityEngine;
public enum BasisCameraBackgroundMode
{
    World = 0,
    GreenScreen = 1,
    BlueScreen = 2,
    Black = 3,
    White = 4,
    Magenta = 5,
    Custom = 6,
    Transparent = 7,
}
public static class BasisCameraBackgrounds
{
    public static readonly Color ChromaGreen = new Color(0f, 0.706f, 0.239f, 1f), ChromaBlue = new Color(0f, 0.278f, 0.678f, 1f);
    public static Color ColorFor(BasisCameraBackgroundMode mode, Color custom)
    {
        switch (mode)
        {
            case BasisCameraBackgroundMode.GreenScreen: return ChromaGreen;
            case BasisCameraBackgroundMode.BlueScreen: return ChromaBlue;
            case BasisCameraBackgroundMode.Black: return Color.black;
            case BasisCameraBackgroundMode.White: return Color.white;
            case BasisCameraBackgroundMode.Magenta: return Color.magenta;
            case BasisCameraBackgroundMode.Custom: return custom;
            case BasisCameraBackgroundMode.Transparent: return Color.clear;
            default: return custom;
        }
    }
    public static void SyncFromMainCamera(Camera capture)
    {
        if (BasisLocalCameraDriver.Instance == null) return;
        Camera main = BasisLocalCameraDriver.Instance.Camera;
        if (main == null) return;

        capture.clearFlags = main.clearFlags;
        capture.backgroundColor = main.backgroundColor;

        bool hasMainSky = main.TryGetComponent(out Skybox mainSky) && mainSky.material != null;
        bool hasCaptureSky = capture.TryGetComponent(out Skybox captureSky);
        if (hasMainSky)
        {
            if (!hasCaptureSky) captureSky = capture.gameObject.AddComponent<Skybox>();
            captureSky.material = mainSky.material;
        }
        else if (hasCaptureSky)
        {
            captureSky.material = null;
        }
    }
}
