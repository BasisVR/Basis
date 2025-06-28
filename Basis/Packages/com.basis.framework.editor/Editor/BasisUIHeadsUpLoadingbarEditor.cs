using Basis.Scripts.UI.UI_Panels;
using UnityEditor;
using UnityEngine;

public class BasisUIHeadsUpLoadingbarEditor : EditorWindow
{
    private BasisUIHeadsUpLoadingbarEditor targetScript;
    public string UniqueId = "UniqueIDOutput";
    // Create a menu item to open the window
    [MenuItem("Basis/Tests/Loading Bar Tests")]
    public static void ShowWindow()
    {
        GetWindow<BasisUIHeadsUpLoadingbarEditor>("Custom Editor Window");
    }
    public bool IsRunning = false;
    private void OnGUI()
    {
        GUILayout.Label("Execute Task from Editor", EditorStyles.boldLabel);

        // Display a button
        if (GUILayout.Button("Start Testing"))
        {
            CallFunction();
            IsRunning = true;
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
