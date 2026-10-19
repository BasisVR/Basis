using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using UnityEngine;

[AutoStaticsCleanup]
public partial class BasisVerticalSyncModule : BasisSettingsBase
{
    public static int CappedFrameRateSelected = 120;

    private enum VSyncMode { On, Capped, Half, Off }
    private static VSyncMode _requestedMode = VSyncMode.On;

#if UNITY_SERVER
    /// <summary>
    /// Frames the headless loop runs per outgoing avatar send. The transmit accumulator in
    /// <c>BasisTransmissionResults.ScheduleTick</c> can only fire on a frame boundary, so a frame
    /// period that does not divide the server's send interval spaces sends unevenly — 25 FPS
    /// (40ms) against the stock 50ms interval sends at 40/40/40/80ms, which reads as jitter even
    /// though the average rate is a correct 20 Hz. Two frames per send divides the interval
    /// exactly and leaves a frame of slack for frame-time variance.
    /// </summary>
    public const int HeadlessFramesPerSend = 2;

    /// <summary>Bounds on the derived headless frame rate, for servers configured with an extreme send interval.</summary>
    public const int HeadlessMinFrameRate = 20;
    public const int HeadlessMaxFrameRate = 90;

    private const int HeadlessFallbackSyncIntervalMs = 50;

    private static int _appliedHeadlessFrameRate;

    /// <summary>
    /// Paces the headless loop against the server's advertised send interval. Called at startup
    /// with the default interval and again from the server metadata handler once the real one is
    /// known, so a server configured away from the stock 50ms retunes instead of beating.
    /// </summary>
    public static void ApplyHeadlessFrameRate()
    {
        int syncIntervalMs = Basis.Scripts.Networking.BasisNetworkManagement.ServerMetaDataMessage.SyncInterval;
        if (syncIntervalMs <= 0)
        {
            syncIntervalMs = HeadlessFallbackSyncIntervalMs;
        }

        int rate = Mathf.RoundToInt(1000f * HeadlessFramesPerSend / syncIntervalMs);
        rate = Mathf.Clamp(rate, HeadlessMinFrameRate, HeadlessMaxFrameRate);

        QualitySettings.vSyncCount = 0;

        if (_appliedHeadlessFrameRate == rate)
        {
            return;
        }

        _appliedHeadlessFrameRate = rate;
        Application.targetFrameRate = rate;
        BasisDebug.Log($"Headless frame rate set to {rate} FPS ({HeadlessFramesPerSend} frames per send) for a {syncIntervalMs}ms server send interval.");
    }
#endif

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
#if UNITY_SERVER
        // Server ignores client settings entirely
        return;
#endif


        // Cap value setting
        if (matchedSettingName == BasisSettingsDefaults.VSyncCapFps.BindingKey)
        {
            if (int.TryParse(
                    optionValue,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out CappedFrameRateSelected))
            {
                BasisDebug.Log(
                    $"Target Framerate set to {CappedFrameRateSelected}",
                    BasisDebug.LogTag.Local);
            }
            return;
        }
        if (matchedSettingName == BasisSettingsDefaults.VSync.BindingKey)
        {
            // Non-desktop devices force vsync off
            if (BasisDeviceManagement.StaticCurrentMode != BasisConstants.Desktop)
            {
                _requestedMode = VSyncMode.Off;
                return;
            }

            switch (optionValue)
            {
                case "on":
                    _requestedMode = VSyncMode.On;
                    break;
                case "capped":
                    _requestedMode = VSyncMode.Capped;
                    break;
                case "half":
                    _requestedMode = VSyncMode.Half;
                    break;
                case "off":
                    _requestedMode = VSyncMode.Off;
                    break;
            }
        }
    }

    public override void ChangedSettings()
    {
#if UNITY_SERVER
        ApplyHeadlessFrameRate();
        return;
#endif

        if (BasisDeviceManagement.StaticCurrentMode != BasisConstants.Desktop)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            return;
        }

        ApplyMode(_requestedMode);
    }

    private static void ApplyMode(VSyncMode mode)
    {
        QualitySettings.maxQueuedFrames = -1;

        switch (mode)
        {
            case VSyncMode.On:
                QualitySettings.vSyncCount = 1;
                Application.targetFrameRate = -1;
                break;

            case VSyncMode.Capped:
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = CappedFrameRateSelected;
                break;

            case VSyncMode.Half:
                QualitySettings.vSyncCount = 2;
                Application.targetFrameRate = -1;
                break;

            case VSyncMode.Off:
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                break;
        }
    }
}
