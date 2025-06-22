using UnityEngine;
using UnityEditor;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.Drivers;

public class BasisHeightEditorWindow : EditorWindow
{
    private float customHeight = 1.7f; // Default custom height input

    [MenuItem("Basis/Height/Height Editor Window")]
    public static void ShowWindow()
    {
        GetWindow<BasisHeightEditorWindow>("Basis Height Tools");
    }

    private void OnGUI()
    {
        GUILayout.Label("Basis Player Height Tools", EditorStyles.boldLabel);
        GUILayout.Space(10);

        if (GUILayout.Button("Recalculate Player Eye Height"))
        {
            RecalculatePlayerEyeHeight();
        }

        if (GUILayout.Button("Capture Player Height"))
        {
            CapturePlayerHeight();
        }

        if (GUILayout.Button("Get Default or Load Player Height"))
        {
            GetDefaultOrLoadPlayerHeight();
        }

        if (GUILayout.Button("Save Player Height"))
        {
            SavePlayerHeight();
        }

        GUILayout.Space(20);
        GUILayout.Label("Custom Height", EditorStyles.boldLabel);

        customHeight = EditorGUILayout.FloatField("Custom Eye Height", customHeight);

        if (GUILayout.Button("Set Custom Player Height"))
        {
            BasisHeightDriver.SetCustomPlayerHeight(customHeight);
        }
    }

    private static void RecalculatePlayerEyeHeight()
    {
        BasisLocalPlayer basisPlayer = BasisLocalPlayer.Instance;

        if (basisPlayer == null)
        {
            BasisDebug.LogError("No BasisLocalPlayer instance found.");
            return;
        }

        BasisHeightDriver.SetPlayersEyeHeight(basisPlayer, BasisSelectedHeightMode.EyeHeight);
        BasisDebug.Log("Player eye height recalculated successfully.");
    }

    private static void CapturePlayerHeight()
    {
        BasisHeightDriver.CapturePlayerHeight();
        BasisDebug.Log("Player height captured successfully.");
    }

    private static void GetDefaultOrLoadPlayerHeight()
    {
        float height = BasisHeightDriver.GetDefaultOrLoadPlayerHeight();
        BasisDebug.Log($"Loaded or default player height: {height}");
    }

    private static void SavePlayerHeight()
    {
        BasisLocalPlayer basisPlayer = BasisLocalPlayer.Instance;

        if (basisPlayer == null)
        {
            BasisDebug.LogError("No BasisLocalPlayer instance found.");
            return;
        }

        BasisHeightDriver.SaveHeight(basisPlayer.CurrentHeight.SelectedPlayerHeight);
        BasisDebug.Log($"Player height saved: {basisPlayer.CurrentHeight.SelectedPlayerHeight}");
    }
}
