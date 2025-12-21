using UnityEngine;

public class BasisDisableOnAndroid : MonoBehaviour
{
    public GameObject DisableMe;
    public void OnEnable()
    {
        if (Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer)
        {
            GameObject.Destroy(DisableMe.gameObject);
        }
    }
}
