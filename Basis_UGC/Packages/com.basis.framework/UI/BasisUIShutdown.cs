#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.UI;

public class BasisUIShutdown : MonoBehaviour
{
    public Button Button;
    public void Awake()
    {
        Button.onClick.RemoveAllListeners();
        Button.onClick.AddListener(Shutdown);
    }
    protected private void Shutdown()
    {
#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}