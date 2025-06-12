using UnityEngine;
using UnityEngine.UI;

public class BasisTestToggleGameobjectUI : MonoBehaviour
{
    public Button Button;
    public GameObject Toggle;
    public void Start()
    {
        Button.onClick.AddListener(ToggleGameobject);
    }
    public void ToggleGameobject()
    {
        Toggle.SetActive(!Toggle.activeSelf);
    }
}
