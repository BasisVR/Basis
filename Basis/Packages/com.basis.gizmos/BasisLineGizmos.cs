using UnityEngine;

public class BasisLineGizmos : MonoBehaviour
{
    public LineRenderer LineRenderer;

    /// <summary>
    /// Updates the position of the gizmo.
    /// </summary>
    public void UpdatePosition(Vector3 position)
    {
        this.transform.position = position;
    }
}
