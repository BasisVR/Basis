using UnityEngine;

public class BasisDisableOnAndroid : MonoBehaviour
{
    public GameObject DisableMe;
    public void OnEnable()
    {
        if (Application.platform == RuntimePlatform.Android)
        {
            GameObject.Destroy(DisableMe.gameObject);
        }
    }
}
