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
    /// Updates the rotation of the gizmo. Used so calibration spheres anchored
    /// to bone transforms tumble with the bone instead of staying axis-aligned.
    /// </summary>
    public void UpdateRotation(Quaternion rotation)
    {
        this.transform.rotation = rotation;
    }

    /// <summary>
    /// Updates position and rotation in a single Transform mutation. Faster
    /// than two separate property writes when both change every frame.
    /// </summary>
    public void UpdatePositionAndRotation(Vector3 position, Quaternion rotation)
    {
        this.transform.SetPositionAndRotation(position, rotation);
    }

    /// <summary>
    /// Updates the size of the gizmo.
    /// </summary>
    public void UpdateSize(Vector3 scale)
    {
        this.transform.localScale = scale;
    }
}
