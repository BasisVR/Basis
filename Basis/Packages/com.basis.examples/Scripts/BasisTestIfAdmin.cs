using System;
using UnityEngine;

public class BasisTestIfAdmin : MonoBehaviour
{
    public void Awake()
    {
        BasisNetworkEvents.IsLocalAdmin += IsLocalAdmin;
    }

    private void IsLocalAdmin(bool State)
    {
        BasisDebug.Log($"Is Admin On Server {State}");
    }

    public void OnEnable()
    {
        BasisNetworkEvents.RequestIsAdminCheck();
    }
}
