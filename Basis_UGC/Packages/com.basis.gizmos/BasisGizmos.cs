using UnityEngine;

public class BasisGizmos : MonoBehaviour
{
    public MeshFilter MeshFilter;
    public MeshRenderer MeshRenderer;
    /// <summary>
    /// Configures a sphere gizmo's material and mesh.
    /// </summary>
    public void ConfigureMeshGizmo(Material material, Mesh Mesh, Color color)
    {
        if (MeshFilter != null && MeshRenderer != null)
        {
            MeshFilter.mesh = Mesh;
            Material Mat = GameObject.Instantiate(material);
            Mat.color = color;
            Mat.SetInt("_CullMode", (int)UnityEngine.Rendering.CullMode.Off);
            MeshRenderer.sharedMaterial = Mat;
        }
    }

    /// <summary>
    /// Updates the position of the gizmo.
    /// </summary>
    public void UpdatePosition(Vector3 position)
    {
        this.transform.position = position;
    }

    /// <summary>
    /// Updates the size of the gizmo.
    /// </summary>
    public void UpdateSize(Vector3 scale)
    {
        this.transform.localScale = scale;
    }
}
