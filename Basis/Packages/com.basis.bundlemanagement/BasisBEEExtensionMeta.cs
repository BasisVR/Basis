using UnityEngine;
[System.Serializable]
public class BasisBEEExtensionMeta
{
    [SerializeField]
    public BasisRemoteEncyptedBundle StoredRemote = new BasisRemoteEncyptedBundle();//where we got meta file from
    [SerializeField]
    public BasisStoredEncryptedBundle StoredLocal = new BasisStoredEncryptedBundle();//where we got bundle file from
    public string UniqueVersion;
}
