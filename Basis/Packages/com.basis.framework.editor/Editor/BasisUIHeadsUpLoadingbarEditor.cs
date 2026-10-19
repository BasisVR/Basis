using Basis.Editor.Localization;
using Basis.Scripts.UI.UI_Panels;
using UnityEditor;
using UnityEngine;

public class BasisUIHeadsUpLoadingbarEditor : EditorWindow
{
    private BasisUIHeadsUpLoadingbarEditor targetScript;
    public string UniqueId = "UniqueIDOutput";
    // Create a menu item to open the window
    [MenuItem("Basis/Debug/Loading Bar Test", false, 644)]
    public static void ShowWindow()
    {
        GetWindow<BasisUIHeadsUpLoadingbarEditor>(BasisEditorLocalization.Get("editor.loadingBarTest.windowTitle"));
    }
    public bool IsRunning = false;
    private void OnGUI()
    {
        GUILayout.Label(BasisEditorLocalization.Get("editor.loadingBarTest.header"), EditorStyles.boldLabel);

        // Display a button
        if (GUILayout.Button(BasisEditorLocalization.Get("editor.loadingBarTest.start")))
        {
            CallFunction();
            IsRunning = true;
        }
        if (IsRunning && GUILayout.Button(BasisEditorLocalization.Get("editor.loadingBarTest.stop")))
        {
            IsRunning = false;
            BasisUILoadingBar.ProgressReport(UniqueId, 100, "Test Message");
        }
        if (IsRunning)
        {
            float randomValue = UnityEngine.Random.Range(0f, 99f);
            BasisUILoadingBar.ProgressReport(UniqueId, randomValue, "Test Message " + randomValue);
        }
    }

    private void CallFunction()
    {
        BasisUILoadingBar.ProgressReport(UniqueId, 33, "Test Message");
    }
}
