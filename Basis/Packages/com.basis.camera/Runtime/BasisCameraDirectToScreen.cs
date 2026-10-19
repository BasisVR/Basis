using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using UnityEngine;
public enum BasisCameraDirectToScreenState
{
    Off = 0,
    Presenting = 1,
    WaitingForVR = 2,
    NoOutputSocket = 3,
    Unsupported = 4,
    DisplayNotConnected = 5,
}
public enum BasisCameraDirectToScreenFit
{
    Fit = 0,
    Fill = 1,
    Stretch = 2,
    MatchWindow = 3,
}
[AutoStaticsCleanup]
public static partial class BasisCameraDirectToScreen
{
    public const int MaxMatchWindowFeedDimension = 4096;
    public const int MaxDisplays = 8;
    public static readonly Vector2 DefaultAlignment = new Vector2(0.5f, 0.5f);
    [NoAutoStaticsCleanup] public static readonly string[] FitKeys = { "camera.directToScreen.fit.fit", "camera.directToScreen.fit.fill", "camera.directToScreen.fit.stretch", "camera.directToScreen.fit.matchWindow" };
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
    public static int DisplayCount => Application.isEditor ? MaxDisplays : Mathf.Clamp(Display.displays.Length, 1, MaxDisplays);
    public static BasisCameraDirectToScreenFit SanitizeFit(int fit) => fit >= 0 && fit < FitKeys.Length ? (BasisCameraDirectToScreenFit)fit : BasisCameraDirectToScreenFit.Fit;
    public static int SanitizeDisplay(int display) => Mathf.Clamp(display, 0, MaxDisplays - 1);
    public static string DisplayLabel(int display) => display == 0 ? BasisLocalization.Get("camera.directToScreen.display.main") : BasisLocalization.Get("camera.directToScreen.display.window", display + 1);
    public static bool IsDisplayConnected(int display) => display == 0 || (display > 0 && display < DisplayCount);
    public static bool ShouldPresent(bool enabled, bool inVR, bool bodyAllowsLiveFeed, bool supported) => ShouldPresent(enabled, inVR, bodyAllowsLiveFeed, supported, 0, true);
    public static bool ShouldPresent(bool enabled, bool inVR, bool bodyAllowsLiveFeed, bool supported, int display, bool displayConnected) => enabled && (inVR || display > 0) && displayConnected && bodyAllowsLiveFeed && supported;
    public static void ActivateDisplay(int display)
    {
        if (display <= 0 || Application.isEditor || display >= Display.displays.Length) return;
        Display target = Display.displays[display];
        if (!target.active) target.Activate();
    }
    public static bool TryGetDisplaySize(int display, out int width, out int height)
    {
        width = height = 0;
        if (display < 0 || display >= Display.displays.Length) return false;
        Display target = Display.displays[display];
        width = target.renderingWidth;
        height = target.renderingHeight;
        return width > 0 && height > 0;
    }
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
