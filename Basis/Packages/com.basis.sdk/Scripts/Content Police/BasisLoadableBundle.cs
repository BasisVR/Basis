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
    public BasisLoadableGameobject LoadableGameobject = null;
}

