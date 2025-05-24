[System.Serializable]
public class BasisLoadableBundle
{
    public string UnlockPassword;
    //encrypted state
    public BasisRemoteEncyptedBundle BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle();
    public BasisStoredEncryptedBundle BasisLocalEncryptedBundle= new BasisStoredEncryptedBundle();
    public BasisBundleConnector BasisBundleConnector;

}

