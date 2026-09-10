using UnityEngine;
public static class BasisCameraRecordingLimits
{
    public const int MinGifFrameRate = 5, MaxGifFrameRate = 30, MinGifWidth = 160, MaxGifWidth = 1280;
    public const float MinGifDurationSeconds = 1f, MaxGifDurationSeconds = 15f;
    public const int MinVideoFrameRate = 10, MaxVideoFrameRate = 60, MinVideoWidth = 320, MaxVideoWidth = 3840;
    public const int MinVideoQuality = 30, MaxVideoQuality = 95;
    public const float MinVideoDurationSeconds = 1f, MaxVideoDurationSeconds = 120f;
    public const int MinPhotogrammetryWidth = 640, MaxPhotogrammetryWidth = 1920;
    public const float MinPhotogrammetryDistanceMeters = 0.05f, MaxPhotogrammetryDistanceMeters = 2f;
    public const float MinPhotogrammetryAngleDegrees = 2f, MaxPhotogrammetryAngleDegrees = 60f;
    public const float MinPhotogrammetryPathSettleSeconds = 0.1f, MaxPhotogrammetryPathSettleSeconds = 10f;
    public static readonly int[] GifWidthPresets = { 320, 480, 640, 960 };
    public static readonly int[] VideoWidthPresets = { 1280, 1920, 2560, 3840 };
    public static readonly int[] PhotogrammetryWidthPresets = { 854, 1280 };
    public static void ClipSize(int requestedWidth, int minWidth, int maxWidth, int captureWidth, int captureHeight, out int width, out int height)
    {
        width = Mathf.Clamp(requestedWidth, minWidth, maxWidth);
        height = Mathf.Clamp(captureWidth > 0 && captureHeight > 0 ? Mathf.RoundToInt(width * (float)captureHeight / captureWidth) : width * 9 / 16, 16, maxWidth);
    }
}
