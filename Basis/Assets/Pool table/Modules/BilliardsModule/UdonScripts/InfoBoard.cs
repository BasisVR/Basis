

using UnityEngine;




public class InfoBoard : MonoBehaviour
{
    [SerializeField] private GameObject[] modify;
    [SerializeField] private Texture2D english;
    [SerializeField] private Texture2D japanese;

    public void SwitchToJP()
    {
        foreach (GameObject obj in modify)
        {
            obj.GetComponent<MeshRenderer>().sharedMaterial.SetTexture("_MainTex", japanese);
        }
    }

    public void SwitchToEN()
    {
        foreach (GameObject obj in modify)
        {
            obj.GetComponent<MeshRenderer>().sharedMaterial.SetTexture("_MainTex", english);
        }
    }
}
