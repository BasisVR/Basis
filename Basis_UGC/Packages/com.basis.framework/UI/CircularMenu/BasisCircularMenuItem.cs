using TMPro;
using UnityEngine;
using UnityEngine.UI;

[System.Serializable]
public class BasisCircularMenuItem : MonoBehaviour
{
    public Image Icon;
    public TextMeshProUGUI Text;
    public RectTransform Point;
    public RectTransform Parent;
    public BasisUGCMenuDescription Description = new BasisUGCMenuDescription();
}
