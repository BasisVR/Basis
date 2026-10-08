using UnityEngine;

[System.Serializable]
public class BasisLoadableBundle
{
    public string UnlockPassword;
    //encrypted state
    public BasisRemoteEncyptedBundle BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle();
    public BasisStoredEncryptedBundle BasisLocalEncryptedBundle= new BasisStoredEncryptedBundle();
    [HideInInspector]
    public BasisBundleConnector BasisBundleConnector;
    [HideInInspector]
    /// <summary>
    /// only used to submit data.
    /// </summary>
    [System.NonSerialized]
    public BasisLoadableGameobject LoadableGameobject = null;
    /// <summary>
    /// Registry key of the wrapper this bundle's load reservation was taken on. Stamped by
    /// BasisLoadHandler when the reservation is taken and consumed when it is released.
    /// <para>The release must not re-derive the key from this record: the key includes the
    /// content version tag, and other systems write that tag here after the load has already
    /// reserved (LibraryProvider stamps the cached tag onto the bundle a player holds). A
    /// drifted key either finds nothing — the reservation leaks and the bundle never unloads —
    /// or silently decrements a DIFFERENT wrapper that aliases the same AssetBundle, which can
    /// then hit zero and Unload(true) the meshes and humanoid rig its own live avatars are
    /// still driven with.</para>
    /// </summary>
    [System.NonSerialized]
    public string ReservedWrapperKey;
}

