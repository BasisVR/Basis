#if UNITY_EDITOR
using UnityEditor;
#endif
using Basis.Scripts.UI.UI_Panels;
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
        BasisUIManagement.CloseAllMenus();
        BasisUIAcceptDenyPanel.OpenAcceptDenyPanel("do you want to quit?", (bool accepted) =>
        {
            if (accepted)
            {

#if UNITY_EDITOR
                EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
            }
        });
    }
}
