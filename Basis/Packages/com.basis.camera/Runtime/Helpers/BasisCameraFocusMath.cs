using UnityEngine;
using UnityEngine.Rendering.Universal;
public static class BasisCameraFocusMath
{
    private const float FocalLengthMargin = 1.15f, AutoFocusPullRate = 8f, AutoFocusSnapDistance = 8f, GaussianFalloffRatio = 2.5f;
    private const float RackEpsilon = 0.001f;
    public static float MinimumDistance(DepthOfField depthOfField) => depthOfField != null ? Mathf.Max(0.1f, depthOfField.focalLength.value * 0.001f * FocalLengthMargin) : 0.1f;
    public static float AutoFocusStep(float current, float depth, float deltaTime) => Mathf.Abs(depth - current) > AutoFocusSnapDistance ? depth : Mathf.Lerp(current, depth, 1f - Mathf.Exp(-AutoFocusPullRate * deltaTime));
    public static bool IsRackNegligible(float current, float target) => Mathf.Abs(target - current) <= RackEpsilon;
    public static bool TryGetDepth(Camera camera, Vector3 worldPoint, float minimumDistance, out float depth)
    {
        depth = 0f;
        if (camera == null) return false;

        camera.transform.GetPositionAndRotation(out Vector3 eye, out Quaternion rotation);
        depth = Vector3.Dot(worldPoint - eye, rotation * Vector3.forward);
        return depth > minimumDistance;
    }
    public static void Apply(DepthOfField depthOfField, float focus)
    {
        depthOfField.focusDistance.overrideState = true;
        depthOfField.focusDistance.value = focus;

        if (depthOfField.mode.value == DepthOfFieldMode.Gaussian)
        {
            depthOfField.gaussianStart.overrideState = true;
            depthOfField.gaussianStart.value = focus;
            depthOfField.gaussianEnd.overrideState = true;
            depthOfField.gaussianEnd.value = focus * GaussianFalloffRatio;
        }
    }
    public static float SampleRack(float from, float to, float t)
    {
        float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t)), near = 1f / Mathf.Max(from, 1e-4f);
        float far = 1f / Mathf.Max(to, 1e-4f);
        return 1f / Mathf.Lerp(near, far, eased);
    }
}
