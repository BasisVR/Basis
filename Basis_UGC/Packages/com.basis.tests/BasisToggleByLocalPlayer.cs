using UnityEngine;

public class BasisToggleByLocalPlayer : MonoBehaviour
{
    public string LocalPlayerAvatar = "Player";
    public int LocalPlayerLayer;
    public GameObject Toggle;
    public void Start()
    {
        LocalPlayerLayer = LayerMask.NameToLayer(LocalPlayerAvatar);
        if (Toggle != null)
        {
            Toggle.SetActive(false);
        }
    }
    public void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.layer == LocalPlayerLayer)
        {
            if(Toggle != null)
            {
                Toggle.SetActive(true);
            }
        }
    }
    public void OnCollisionExit(Collision collision)
    {
        if (collision.gameObject.layer == LocalPlayerLayer)
        {
            if (Toggle != null)
            {
                Toggle.SetActive(false);
            }
        }
    }
}