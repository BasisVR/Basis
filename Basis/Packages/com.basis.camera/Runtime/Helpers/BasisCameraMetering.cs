using Unity.Collections;
using UnityEngine;
public enum BasisCameraMeteringMode
{
    Average = 0,
    CentreWeighted = 1,
    Spot = 2,
}
public static class BasisCameraMetering
{
    public const float DefaultTarget = 0.45f, DefaultSpeed = 2f, DefaultRange = 3f, MinTarget = 0.05f, MaxTarget = 0.95f;
    public const float MinSpeed = 0.1f, MaxSpeed = 8f, MinRange = 0.5f, MaxRange = 6f, Rate = 12f;
    public const int Size = 64;
    private const float MinMeasurable = 1f / 255f, EdgeWeight = 0.15f, SpotRadius = 0.2f;
    public static int SanitizeMode(int mode) => System.Enum.IsDefined(typeof(BasisCameraMeteringMode), mode) ? mode : (int)BasisCameraMeteringMode.CentreWeighted;
    public static float Weight(BasisCameraMeteringMode mode, float u, float v)
    {
        switch (mode)
        {
            case BasisCameraMeteringMode.Spot:
                return Radius(u, v) <= SpotRadius ? 1f : 0f;
            case BasisCameraMeteringMode.CentreWeighted:
                return Mathf.Lerp(1f, EdgeWeight, Mathf.Clamp01(Radius(u, v)));
            default:
                return 1f;
        }
    }
    public static float Measure(NativeArray<Color32> pixels, int width, int height, BasisCameraMeteringMode mode)
    {
        if (!pixels.IsCreated || width <= 0 || height <= 0 || pixels.Length < width * height) return -1f;

        double total = 0d, weightSum = 0d;

        for (int y = 0; y < height; y++)
        {
            float v = (y + 0.5f) / height;
            for (int x = 0; x < width; x++)
            {
                float weight = Weight(mode, (x + 0.5f) / width, v);
                if (weight <= 0f) continue;

                Color32 pixel = pixels[y * width + x];
                float luma = (0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b) / 255f;

                total += luma * weight;
                weightSum += weight;
            }
        }

        return weightSum > 0d ? (float)(total / weightSum) : -1f;
    }
    public static float GoalStops(float stopsWhenMetered, float measured, float target, float range)
    {
        float error = Mathf.Log(Mathf.Clamp(target, MinTarget, MaxTarget) / Mathf.Max(measured, MinMeasurable), 2f);
        return Mathf.Clamp(stopsWhenMetered + error, -Mathf.Abs(range), Mathf.Abs(range));
    }
    public static float Approach(float current, float goal, float speed, float deltaTime) => Mathf.Lerp(current, goal, 1f - Mathf.Exp(-Mathf.Clamp(speed, MinSpeed, MaxSpeed) * deltaTime));
    private static float Radius(float u, float v) => Mathf.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f)) * 2f;
}
