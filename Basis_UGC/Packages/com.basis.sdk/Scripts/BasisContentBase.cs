using System;
using UnityEngine;

public abstract class BasisContentBase : MonoBehaviour
{
    [SerializeField]
    public BasisBundleDescription BasisBundleDescription;
    private string networkID;
    public Action<string> OnNetworkIDSet;
    public string NetworkID
    {
        get
        {
            return networkID;
        }

        set
        {
            networkID = value;
            OnNetworkIDSet?.Invoke(networkID);
        }
    }
}
