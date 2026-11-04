using System;
using UnityEngine;
using UnityEngine.Rendering;
public class BasisCameraCountdownRing : MonoBehaviour
{
    public const int Segments = 128;
    public const float CaptureLead = 0.5f, ThicknessFraction = 0.12f, Margin = 0.015f, FrontOffset = 0.005f;
    public static readonly Color StartColor = Color.white, EndColor = new Color(1f, 0.2f, 0.16f, 1f);
    private static readonly int baseColorId = Shader.PropertyToID("_BaseColor"), baseMapId = Shader.PropertyToID("_BaseMap");
    private GameObject ringObject;
    private Mesh mesh;
    private Material startMaterial, endMaterial;
    private int[] indices;
    private float captureTime, startTime;
    private int shownSegments = -1;
    public static float CaptureDelay(int seconds) => seconds + CaptureLead;
    public static BasisCameraCountdownRing Begin(GameObject camera, float secondsUntilCapture, float totalSeconds)
    {
        if (camera == null || secondsUntilCapture <= 0f || totalSeconds <= 0f) return null;
        if (!camera.TryGetComponent(out BasisCameraCountdownRing ring)) ring = camera.AddComponent<BasisCameraCountdownRing>();
        if (!ring.EnsureBuilt()) { Destroy(ring); return null; }
        ring.captureTime = Time.time + secondsUntilCapture;
        ring.startTime = ring.captureTime - totalSeconds;
        ring.shownSegments = -1;
        ring.ringObject.SetActive(true);
        ring.enabled = true;
        ring.Tick();
        return ring;
    }
    public static void Cancel(GameObject camera)
    {
        if (camera != null && camera.TryGetComponent(out BasisCameraCountdownRing ring)) ring.Stop();
    }
    public void Stop()
    {
        if (ringObject != null) ringObject.SetActive(false);
        enabled = false;
    }
    private void Update() => Tick();
    private void Tick()
    {
        float now = Time.time;
        if (now >= captureTime) { Stop(); return; }
        Show(Mathf.Clamp(Mathf.FloorToInt((now - startTime) / (captureTime - startTime) * Segments), 0, Segments));
    }
    private void Show(int segments)
    {
        if (segments == shownSegments) return;
        shownSegments = segments;
        SetRange(0, 0, segments);
        SetRange(1, segments, Segments - segments);
    }
    private void SetRange(int submesh, int first, int count)
    {
        if (count <= 0) mesh.SetTriangles(Array.Empty<int>(), submesh, false);
        else mesh.SetTriangles(indices, first * 12, count * 12, submesh, false);
    }
    private bool EnsureBuilt()
    {
        if (ringObject != null) return true;
        Material source = null;
        bool any = false;
        Vector3 min = default, max = default;
        Matrix4x4 toLocal = transform.worldToLocalMatrix;
        MeshFilter[] filters = GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter filter = filters[i];
            if (filter.sharedMesh == null || !filter.TryGetComponent(out MeshRenderer renderer)) continue;
            if (source == null) source = renderer.sharedMaterial;
            Bounds b = filter.sharedMesh.bounds;
            Matrix4x4 m = toLocal * filter.transform.localToWorldMatrix;
            for (int c = 0; c < 8; c++)
            {
                Vector3 p = m.MultiplyPoint3x4(new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z));
                if (!any) { min = max = p; any = true; }
                else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            }
        }
        if (source == null || !any) return false;
        float halfX = Mathf.Max(Mathf.Abs(min.x), Mathf.Abs(max.x)), halfY = Mathf.Max(Mathf.Abs(min.y), Mathf.Abs(max.y));
        float outer = Mathf.Sqrt(halfX * halfX + halfY * halfY) + Margin;
        float inner = outer * (1f - ThicknessFraction);
        float z = max.z + FrontOffset;
        Vector3[] vertices = new Vector3[(Segments + 1) * 2];
        for (int i = 0; i <= Segments; i++)
        {
            float angle = i * Mathf.PI * 2f / Segments;
            float x = -Mathf.Sin(angle), y = Mathf.Cos(angle);
            vertices[i * 2] = new Vector3(x * inner, y * inner, z);
            vertices[i * 2 + 1] = new Vector3(x * outer, y * outer, z);
        }
        indices = new int[Segments * 12];
        for (int i = 0; i < Segments; i++)
        {
            int a = i * 2, b = a + 1, c = a + 2, d = a + 3, o = i * 12;
            indices[o] = a; indices[o + 1] = b; indices[o + 2] = c;
            indices[o + 3] = c; indices[o + 4] = b; indices[o + 5] = d;
            indices[o + 6] = a; indices[o + 7] = c; indices[o + 8] = b;
            indices[o + 9] = c; indices[o + 10] = d; indices[o + 11] = b;
        }
        mesh = new Mesh { name = "Camera Countdown Ring", subMeshCount = 2 };
        mesh.vertices = vertices;
        mesh.SetTriangles(indices, 0, 0, 0, false);
        mesh.SetTriangles(indices, 1, false);
        mesh.RecalculateBounds();
        startMaterial = CreateMaterial(source, StartColor);
        endMaterial = CreateMaterial(source, EndColor);
        ringObject = new GameObject("CountdownRing") { layer = gameObject.layer };
        ringObject.transform.SetParent(transform, false);
        ringObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer ringRenderer = ringObject.AddComponent<MeshRenderer>();
        ringRenderer.sharedMaterials = new[] { endMaterial, startMaterial };
        ringRenderer.shadowCastingMode = ShadowCastingMode.Off;
        ringRenderer.receiveShadows = false;
        ringRenderer.lightProbeUsage = LightProbeUsage.Off;
        ringRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return true;
    }
    private static Material CreateMaterial(Material source, Color color)
    {
        Material material = new Material(source) { name = "Camera Countdown Ring" };
        material.SetTexture(baseMapId, null);
        material.SetColor(baseColorId, color);
        return material;
    }
    private void OnDestroy()
    {
        if (ringObject != null) Destroy(ringObject);
        if (mesh != null) Destroy(mesh);
        if (startMaterial != null) Destroy(startMaterial);
        if (endMaterial != null) Destroy(endMaterial);
    }
}
