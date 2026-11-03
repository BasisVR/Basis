using Unity.Scripting.LifecycleManagement;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisScreenShareBack
{
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static Material backMaterial;
    static readonly Color BackColor = new Color(0.09f, 0.085f, 0.12f, 1f);

    static Material BackMaterial()
    {
        if (backMaterial != null) return backMaterial;
        Shader shader = BundledContentHolder.Instance != null ? BundledContentHolder.Instance.UnlitUrpShader : null;
        if (shader == null) return null;
        backMaterial = new Material(shader) { name = "BasisScreenShareBack" };
        if (backMaterial.HasProperty(BaseColorId)) backMaterial.SetColor(BaseColorId, BackColor);
        else backMaterial.color = BackColor;
        return backMaterial;
    }

    public static Renderer BuildBack(Transform root, Bounds screen)
    {
        Material material = BackMaterial();
        if (material == null) return null;
        GameObject back = new GameObject("Back");
        back.layer = root.gameObject.layer;
        back.transform.SetParent(root, false);
        back.transform.localPosition = new Vector3(screen.center.x, screen.center.y, screen.center.z + 0.02f);
        Vector3 e = screen.extents;
        Mesh mesh = new Mesh { name = "ScreenShareBack" };
        mesh.vertices = new[] { new Vector3(-e.x, -e.y, 0f), new Vector3(e.x, -e.y, 0f), new Vector3(e.x, e.y, 0f), new Vector3(-e.x, e.y, 0f) };
        mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
        mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        back.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = back.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return renderer;
    }
}
