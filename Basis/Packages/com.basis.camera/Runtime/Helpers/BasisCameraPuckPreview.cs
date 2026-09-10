using UnityEngine;
using UnityEngine.Rendering;
public static class BasisCameraPuckPreview
{
    public static bool ShouldShow(Vector3 cameraPosition, Quaternion cameraRotation, Vector3 viewerHead, bool showing, float showAngle, float hideAngle)
    {
        Vector3 toViewer = viewerHead - cameraPosition;
        if (toViewer.sqrMagnitude < 1e-8f) return true;

        float threshold = showing ? Mathf.Max(showAngle, hideAngle) : showAngle;
        return Vector3.Angle(cameraRotation * Vector3.forward, toViewer) <= threshold;
    }
    public static float ParkDistance(float markerScale, float clearance) => BasisCameraDetachedMarkers.ParkDistance(markerScale) + clearance * Mathf.Max(1f, markerScale);
    public static float Growth(float distance, float referenceDistance, float maxGrowth) => referenceDistance <= 1e-4f ? 1f : Mathf.Clamp(distance / referenceDistance, 1f, Mathf.Max(1f, maxGrowth));
    public static GameObject CreateQuad(Material template, out Material material)
    {
        if (template != null)
        {
            material = Object.Instantiate(template);
        }
        else
        {
            Shader unlit = Shader.Find("Unlit/Texture");
            material = unlit != null ? new Material(unlit) : null;
            if (material == null) return null;
        }

        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);

        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "CameraPuckLookAtPreview";

        if (quad.TryGetComponent(out MeshCollider meshCollider)) Object.DestroyImmediate(meshCollider);

        int overlayUi = BasisCameraCaptureLayers.Marker;
        if (overlayUi >= 0) quad.layer = overlayUi;

        if (quad.TryGetComponent(out MeshRenderer meshRenderer))
        {
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }
        return quad;
    }
}
