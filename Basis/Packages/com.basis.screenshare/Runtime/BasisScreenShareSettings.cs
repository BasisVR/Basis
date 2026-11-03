using Unity.Scripting.LifecycleManagement;
using System;
using Basis;
using Basis.Scripts.Settings;
using UnityEngine;

[NoAutoStaticsCleanup]
public static class BasisScreenShareSettings
{
    public const string ResolutionNative = "native", Resolution1440 = "1440", Resolution1080 = "1080", Resolution720 = "720", Resolution480 = "480";
    public const string QualityAuto = "auto", QualityLow = "low", QualityHigh = "high", QualityMax = "max";
    public static readonly string[] Resolutions = { ResolutionNative, Resolution1440, Resolution1080, Resolution720, Resolution480 };
    public static readonly string[] FrameRates = { "60", "30", "15", "5" };
    public static readonly string[] Qualities = { QualityAuto, QualityLow, QualityHigh, QualityMax };
    public const string AudioAuto = "auto", AudioApplication = "application", AudioSystem = "system";
    public static readonly string[] AudioSources = { AudioAuto, AudioApplication, AudioSystem };
    public const float AutoBitsPerPixel = 0.08f;
    public const int MinBitrateKbps = 500, AudioBitrateKbps = 128;

    static BasisScreenShareSettings()
    {
        BasisSettingsBindingPostLoad.Register(typeof(BasisScreenShareSettings));
    }

    public static BasisSettingsBinding<string> Resolution = new("screenshareresolution", new BasisPlatformDefault<string>(Resolution1080));
    public static BasisSettingsBinding<string> FrameRate = new("screensharefps", new BasisPlatformDefault<string>("30"));
    public static BasisSettingsBinding<string> Quality = new("screensharequality", new BasisPlatformDefault<string>(QualityAuto));
    public static BasisSettingsBinding<bool> ShareAudio = new("screenshareaudio", new BasisPlatformDefault<bool>(true));
    public static BasisSettingsBinding<string> AudioSource = new("screenshareaudiosource", new BasisPlatformDefault<string>(AudioAuto));
    public static BasisSettingsBinding<bool> ShowCursor = new("screensharecursor", new BasisPlatformDefault<bool>(true));
    public static BasisSettingsBinding<bool> Receive = new("screensharereceive", new BasisPlatformDefault<bool>(true));
    public static BasisSettingsBinding<float> ViewDistance = new("screenshareviewdistance", new BasisPlatformDefault<float>(40f));
    public static BasisSettingsBinding<float> Volume = new("screensharevolume", new BasisPlatformDefault<float>(1f));

    public static void EnsureLoaded()
    {
        _ = Resolution.RawValue;
    }

    public static Vector2Int MaxSize(string resolution, Vector2Int source)
    {
        int height = resolution switch
        {
            Resolution1440 => 1440,
            Resolution1080 => 1080,
            Resolution720 => 720,
            Resolution480 => 480,
            _ => source.y > 0 ? source.y : 2160,
        };
        int width = resolution == ResolutionNative ? (source.x > 0 ? source.x : 3840) : (height * 16 + 8) / 9;
        if (source.x > 0 && source.y > 0 && (float)source.x / source.y > 16f / 9f) width = Mathf.RoundToInt(height * (float)source.x / source.y);
        return new Vector2Int(Math.Min(width, 4096), Math.Min(height, 4096));
    }

    public static int Fps(string value) => int.TryParse(value, out int fps) ? Mathf.Clamp(fps, 1, 120) : 30;

    public static int Bitrate(string quality, Vector2Int size, int fps, int serverMaxKbps)
    {
        int cap = serverMaxKbps > 0 ? serverMaxKbps : 12000;
        float auto = size.x * (float)size.y * Mathf.Max(fps, 5) * AutoBitsPerPixel / 1000f;
        float kbps = quality switch
        {
            QualityLow => auto * 0.5f,
            QualityHigh => auto * 1.75f,
            QualityMax => cap,
            _ => auto,
        };
        return Mathf.Clamp(Mathf.RoundToInt(kbps), MinBitrateKbps, cap);
    }
}
