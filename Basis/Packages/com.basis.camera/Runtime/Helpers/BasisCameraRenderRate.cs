using Basis.BasisUI;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
public static class BasisCameraRenderRate
{
    public const float MinHz = 1f, MaxHz = 120f;
    private static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
    private static int pinHolders;
    private static bool savedLimit;
    private static float savedHz, appliedHz;
    public static bool IsPinnedByRecording => pinHolders > 0;
    public static float Floor(float hz, bool consuming, float consumerHz) => consuming && consumerHz > 0f ? Mathf.Max(hz, consumerHz) : hz;
    public static void Pin()
    {
        if (pinHolders++ != 0) return;

        savedLimit = BasisSettingsDefaults.LimitHandHeldCameraRate.RawValue;
        savedHz = BasisSettingsDefaults.HandHeldCameraRenderHz.RawValue;
        appliedHz = Mathf.Clamp(DisplayRefreshHz(), MinHz, MaxHz);
        BasisSettingsDefaults.HandHeldCameraRenderHz.SetValueWithoutNotify(appliedHz);
        BasisSettingsDefaults.LimitHandHeldCameraRate.SetValueWithoutNotify(true);
    }
    public static void Unpin()
    {
        if (--pinHolders > 0) return;

        BasisSettingsDefaults.LimitHandHeldCameraRate.SetValueWithoutNotify(savedLimit);
        if (Mathf.Approximately(BasisSettingsDefaults.HandHeldCameraRenderHz.RawValue, appliedHz)) BasisSettingsDefaults.HandHeldCameraRenderHz.SetValueWithoutNotify(savedHz);
    }
    private static float DisplayRefreshHz()
    {
        if (XRSettings.isDeviceActive)
        {
            SubsystemManager.GetSubsystems(displays);
            for (int Index = 0; Index < displays.Count; Index++)
            {
                XRDisplaySubsystem display = displays[Index];
                if (display == null || !display.running) continue;
                if (display.TryGetDisplayRefreshRate(out float headsetHz) && headsetHz > 0f) return headsetHz;
            }
        }

        double screenHz = Screen.currentResolution.refreshRateRatio.value;
        return screenHz > 0.0 ? (float)screenHz : 60f;
    }
}
