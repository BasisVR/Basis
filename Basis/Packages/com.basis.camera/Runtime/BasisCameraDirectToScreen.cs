using Basis.Scripts.Device_Management;
using UnityEngine;
public enum BasisCameraDirectToScreenState
{
    Off = 0,
    Presenting = 1,
    WaitingForVR = 2,
    NoOutputSocket = 3,
    Unsupported = 4,
}
public enum BasisCameraDirectToScreenFit
{
    Fit = 0,
    Fill = 1,
    Stretch = 2,
    MatchWindow = 3,
}
public static class BasisCameraDirectToScreen
{
    public const int MaxMatchWindowFeedDimension = 4096;
    public static readonly Vector2 DefaultAlignment = new Vector2(0.5f, 0.5f);
    public static readonly string[] FitKeys = { "camera.directToScreen.fit.fit", "camera.directToScreen.fit.fill", "camera.directToScreen.fit.stretch", "camera.directToScreen.fit.matchWindow" };
#if UNITY_INCLUDE_TESTS
    public static bool? VRModeOverrideForTest;
#endif
    public static bool IsSupported
    {
        get
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.LinuxPlayer:
                    return true;
                default:
                    return false;
            }
        }
    }
    public static bool IsInVR
    {
        get
        {
#if UNITY_INCLUDE_TESTS
            if (VRModeOverrideForTest.HasValue) return VRModeOverrideForTest.Value;
#endif
            return BasisDeviceManagement.IsCurrentModeVR();
        }
    }
    public static BasisCameraDirectToScreenFit SanitizeFit(int fit) => fit >= 0 && fit < FitKeys.Length ? (BasisCameraDirectToScreenFit)fit : BasisCameraDirectToScreenFit.Fit;
    public static bool ShouldPresent(bool enabled, bool inVR, bool bodyAllowsLiveFeed, bool supported) => enabled && inVR && bodyAllowsLiveFeed && supported;
    public static void MatchWindowFeedSize(int previewWidth, int previewHeight, int windowWidth, int windowHeight, out int width, out int height)
    {
        width = previewWidth;
        height = previewHeight;
        if (previewWidth <= 0 || previewHeight <= 0 || windowWidth <= 0 || windowHeight <= 0) return;

        float aspect = (float)windowWidth / windowHeight, w = Mathf.Sqrt((float)previewWidth * previewHeight * aspect);
        float h = w / aspect, over = Mathf.Max(w, h) / MaxMatchWindowFeedDimension;
        if (over > 1f)
        {
            w /= over;
            h /= over;
        }
        width = Mathf.Max(16, Mathf.RoundToInt(w) & ~1);
        height = Mathf.Max(16, Mathf.RoundToInt(h) & ~1);
    }
}
