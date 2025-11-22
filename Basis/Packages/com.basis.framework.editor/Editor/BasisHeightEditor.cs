using UnityEngine;
using UnityEditor;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.Drivers;

public class BasisHeightEditorWindow : EditorWindow
{
    private float customEyeHeight = BasisLocalHeight.FallbackEyeHeightMeters; // Default custom height input
    private float customPlayerScale = 1.0f; // Default custom scale input

    [MenuItem("Basis/Height/Height Editor Window")]
    public static void ShowWindow()
    {
        GetWindow<BasisHeightEditorWindow>("Basis Height Tools");
    }

    private void OnGUI()
    {
        GUILayout.Label("Basis Player Height Tools", EditorStyles.boldLabel);
        GUILayout.Space(10);

        if (GUILayout.Button("Capture Real User Sizes"))
        {
            CaptureRealUserSizes();
        }

        if (GUILayout.Button("Reset To Defaults"))
        {
            ResetToDefaults();
        }

        if (GUILayout.Button("Load Player Scale From Disk"))
        {
            LoadPlayerHeight();
        }

        GUILayout.Space(20);
        GUILayout.Label("Custom Height", EditorStyles.boldLabel);

        customEyeHeight = EditorGUILayout.FloatField("Eye Height Meters", customEyeHeight);
        if (GUILayout.Button("Set Eye Height"))
        {
            BasisLocalPlayer.Instance.Height.SetPlayerSize(BasisHeightMeasurement.EyeHeightMeters, customEyeHeight);
        }
        customPlayerScale = EditorGUILayout.FloatField("Player Scale Vs Avatar", customPlayerScale);
        if (GUILayout.Button("Set Player Scale"))
        {
            BasisLocalPlayer.Instance.Height.SetPlayerSize(BasisHeightMeasurement.ScaleMultiplier, customPlayerScale);
        }
    }

    private static void CaptureRealUserSizes()
    {
        BasisLocalPlayer.Instance.Height.CaptureRealUserSizes();
        BasisDebug.Log("Player height captured successfully.");
    }

    private void ResetToDefaults()
    {
        customEyeHeight = BasisLocalHeight.FallbackEyeHeightMeters;
        customPlayerScale = 1.0f;
        BasisLocalPlayer.Instance.Height.SetPlayerSize(BasisHeightMeasurement.ScaleMultiplier, 1.0f);
    }

    private static void LoadPlayerHeight()
    {
        BasisLocalHeight.LoadSavedScales();
        BasisLocalPlayer.Instance?.Height?.SetupForAvatar();
    }
}
