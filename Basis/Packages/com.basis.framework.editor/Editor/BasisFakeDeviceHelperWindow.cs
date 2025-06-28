using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEditor;
using UnityEngine;

public class BasisFakeDeviceHelperWindow : EditorWindow
{
    public string TrackerPhysicalName = "oculus_quest_pro_controller_left"; // Variable to store the input text
    public string TrackerUniquePhysicalName = "oculus_quest_pro_controller_left"; // Variable to store the input text
    public bool Overridebool = false;
    public BasisBoneTrackedRole BasisBoneTrackedRole;
    // Add menu item to open the editor window
    [MenuItem("Basis/Trackers/Basis Fake Device Helper")]
    public static void ShowWindow()
    {
        // Show the editor window
        GetWindow<BasisFakeDeviceHelperWindow>("Fake Device Helper");
    }

    public void OnGUI()
    {
        // Input field
        TrackerPhysicalName = EditorGUILayout.TextField("Tracker Physical Name:", TrackerPhysicalName);
        // Input field
        TrackerUniquePhysicalName = EditorGUILayout.TextField("Tracker Unique Physical Name:", TrackerUniquePhysicalName);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Has Default Tracked Role");
        Overridebool = EditorGUILayout.Toggle(Overridebool);
        EditorGUILayout.EndHorizontal();
        BasisBoneTrackedRole = (BasisBoneTrackedRole)EditorGUILayout.EnumPopup(BasisBoneTrackedRole);
        // Button
        if (GUILayout.Button("Submit"))
        {
            BasisSimulateXR Sim = BasisMenuItemsEditor.FindSimulate();
            Sim.CreatePhysicalTrackedDevice(TrackerUniquePhysicalName, TrackerPhysicalName, BasisBoneTrackedRole, Overridebool);
        }
    }
}
