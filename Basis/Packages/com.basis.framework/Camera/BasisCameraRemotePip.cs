using UnityEngine;

/// <summary>
/// Metadata component attached to the remote PIP camera prefab.
/// Provides configuration such as the default rotation offset for the lens model.
/// </summary>
public class BasisCameraRemotePip : MonoBehaviour
{
    /// <summary>
    /// The player ID this remote PIP belongs to. Set at spawn time.
    /// </summary>
    [HideInInspector]
    public ushort PlayerID;
}
