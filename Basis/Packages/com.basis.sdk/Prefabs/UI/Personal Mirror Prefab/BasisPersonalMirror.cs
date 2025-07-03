using UnityEngine;

public class BasisPersonalMirror : MonoBehaviour
{
    public static BasisPersonalMirror Instance;
    public void OnEnable()
    {
        Instance = this;

    }
}
