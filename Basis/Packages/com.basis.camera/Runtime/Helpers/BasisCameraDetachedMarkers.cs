using UnityEngine;
public enum BasisCameraDetachedMarker
{
    Off = 0,
    Puck = 1,
    Gizmo = 2,
}
public static class BasisCameraDetachedMarkers
{
    public const string PuckPrefabAddress = "Packages/com.basis.sdk/Prefabs/UI/Camera Prefab/BasisCameraRemotePip.prefab";
    public const float MinScale = 0.25f, MaxScale = 4f;
    private const float LensOffset = 0.25f, FallbackGrabSize = 0.2f, MinGrabSize = 0.08f;
    public static float ParkDistance(float markerScale) => LensOffset * Mathf.Max(1f, markerScale);
    public static float GripSize(float knobSize) => Mathf.Max(MinGrabSize, knobSize * 1.2f);
    public static Vector3 GrabBoxSize(Vector3 measuredSize, float prefabScaleX, float markerScale)
    {
        float floor = MinGrabSize / Mathf.Max(1e-4f, Mathf.Abs(prefabScaleX) * markerScale);
        return measuredSize == Vector3.zero ? Vector3.one * Mathf.Max(FallbackGrabSize, floor) : Vector3.Max(measuredSize * 1.2f, Vector3.one * floor);
    }
    public static bool TryGetLocalRendererBounds(GameObject root, out Vector3 center, out Vector3 size)
    {
        center = Vector3.zero;
        size = Vector3.zero;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;

        Bounds world = renderers[0].bounds;
        for (int Index = 1; Index < renderers.Length; Index++) world.Encapsulate(renderers[Index].bounds);

        center = root.transform.InverseTransformPoint(world.center);
        Vector3 scale = root.transform.lossyScale;
        size = new Vector3(world.size.x / Mathf.Max(1e-4f, Mathf.Abs(scale.x)), world.size.y / Mathf.Max(1e-4f, Mathf.Abs(scale.y)), world.size.z / Mathf.Max(1e-4f, Mathf.Abs(scale.z)));
        return true;
    }
}
