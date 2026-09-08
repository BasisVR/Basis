using Basis.Scripts.Common;
using UnityEngine;

public class BasisDisableOnAndroid : MonoBehaviour
{
    public GameObject DisableMe;
    public void OnEnable()
    {
        if (DisableMe != null && BasisGpuDetection.IsMobileGpu)
        {
            GameObject.Destroy(DisableMe);
        }
    }
}
