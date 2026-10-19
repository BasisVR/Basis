using Basis.Editor.Localization;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Simulated trackers, in one window.
///
/// Spawning a test rig used to mean walking a menu of fourteen entries — nine "Create …" presets
/// plus show/hide/destroy — with no indication of which needed play mode or what each preset
/// actually spawns. They are buttons here, grouped by what you are doing: pick a rig, tweak
/// visibility, tear it down.
/// </summary>
public sealed class BasisTrackersWindow : EditorWindow
{
    private string _physicalName = "oculus_quest_pro_controller_left";
    private string _uniqueName = "oculus_quest_pro_controller_left";
    private bool _hasRole;
    private BasisBoneTrackedRole _role;
    private Vector2 _scroll;

    [MenuItem("Basis/Trackers/Simulated Trackers", false, 160)]
    public static void ShowWindow()
    {
        BasisTrackersWindow w = GetWindow<BasisTrackersWindow>();
        w.titleContent = new GUIContent(BasisEditorLocalization.Get("editor.trackersWindow.windowTitle"));
        w.minSize = new Vector2(420, 460);
        w.Show();
    }

    private void OnGUI()
    {
        _scroll = BasisEditorUI.BeginPage(_scroll);
        BasisEditorUI.Header(BasisEditorLocalization.Get("editor.trackersWindow.header"),
            BasisEditorLocalization.Get("editor.trackersWindow.subtitle"));

        if (!Application.isPlaying)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.trackersWindow.enterPlayMode"), MessageType.Warning);
        }

        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            using (BasisEditorUI.Card(BasisEditorLocalization.Get("editor.trackersWindow.rigPresets")))
            {
                BasisEditorUI.Note(BasisEditorLocalization.Get("editor.trackersWindow.rigPresetsNote"));
                GUILayout.Space(4f);

                Preset(BasisEditorLocalization.Get("editor.trackersWindow.presetFullSet"), BasisMenuItemsEditor.CreateFullSet);
                Preset(BasisEditorLocalization.Get("editor.trackersWindow.presetThreePoint"), BasisMenuItemsEditor.CreatePuck3Tracker);
                Preset(BasisEditorLocalization.Get("editor.trackersWindow.presetMax"), BasisMenuItemsEditor.CreateFullMaxTracker);
                Preset(BasisEditorLocalization.Get("editor.trackersWindow.presetMaxNormal"), BasisMenuItemsEditor.CreateFullMaxTrackerUnModifedPos);
            }

            using (BasisEditorUI.Card(BasisEditorLocalization.Get("editor.trackersWindow.singleDevices")))
            {
                Preset(BasisEditorLocalization.Get("editor.trackersWindow.presetBothHands"), BasisMenuItemsEditor.CreateLRTracker);
                Preset(BasisEditorLocalization.Get("editor.trackersWindow.presetLeftController"), BasisMenuItemsEditor.CreateViveLeftTracker);
                Preset(BasisEditorLocalization.Get("editor.trackersWindow.presetRightController"), BasisMenuItemsEditor.CreateViveRightTracker);
                Preset(BasisEditorLocalization.Get("editor.trackersWindow.presetVivePuck"), BasisMenuItemsEditor.CreatePuckTracker);
                Preset(BasisEditorLocalization.Get("editor.trackersWindow.presetUnknown"), BasisMenuItemsEditor.CreateUnknowonTracker);
            }

            using (BasisEditorUI.Card(BasisEditorLocalization.Get("editor.trackersWindow.customDevice")))
            {
                BasisEditorUI.Note(BasisEditorLocalization.Get("editor.trackersWindow.customDeviceNote"));
                _physicalName = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.trackersWindow.physicalName"), _physicalName);
                _uniqueName = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.trackersWindow.uniqueId"), _uniqueName);
                _hasRole = EditorGUILayout.Toggle(BasisEditorLocalization.Get("editor.trackersWindow.hasDefaultRole"), _hasRole);
                using (new EditorGUI.DisabledScope(!_hasRole))
                {
                    _role = (BasisBoneTrackedRole)EditorGUILayout.EnumPopup(BasisEditorLocalization.Get("editor.trackersWindow.role"), _role);
                }

                GUILayout.Space(4f);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_physicalName) || string.IsNullOrWhiteSpace(_uniqueName)))
                {
                    if (BasisEditorUI.PrimaryButton(BasisEditorLocalization.Get("editor.trackersWindow.createDevice")))
                    {
                        BasisSimulateXR sim = BasisMenuItemsEditor.FindSimulate();
                        if (sim == null)
                        {
                            Debug.LogWarning("[Trackers] No SimulateXR management is active — nothing to spawn into.");
                        }
                        else
                        {
                            if (_hasRole) sim.CreatePhysicalTrackedDevice(_uniqueName, _physicalName, _role, true);
                            else sim.CreatePhysicalTrackedDevice(_uniqueName, _physicalName);
                            BasisDeviceManagement.VisibleTrackers(true);
                        }
                    }
                }
            }

            using (BasisEditorUI.Card(BasisEditorLocalization.Get("editor.trackersWindow.visibility")))
            {
                EditorGUILayout.BeginHorizontal();
                if (BasisEditorUI.SecondaryButton(BasisEditorLocalization.Get("editor.trackersWindow.showTrackers"))) BasisMenuItemsEditor.ShowTrackersEditor();
                if (BasisEditorUI.SecondaryButton(BasisEditorLocalization.Get("editor.trackersWindow.hideTrackers"))) BasisMenuItemsEditor.HideTrackersEditor();
                EditorGUILayout.EndHorizontal();
            }

            using (BasisEditorUI.Card(BasisEditorLocalization.Get("editor.trackersWindow.teardown")))
            {
                BasisEditorUI.Note(BasisEditorLocalization.Get("editor.trackersWindow.teardownNote"));
                EditorGUILayout.BeginHorizontal();
                if (BasisEditorUI.SecondaryButton(BasisEditorLocalization.Get("editor.trackersWindow.destroyAll"))) BasisMenuItemsEditor.DestroyXRInput();
                if (BasisEditorUI.SecondaryButton(BasisEditorLocalization.Get("editor.trackersWindow.destroyAndRestore"))) BasisMenuItemsEditor.DestroyAndRebuildXRInput();
                EditorGUILayout.EndHorizontal();
            }
        }

        BasisEditorUI.EndPage();
    }

    private static void Preset(string label, System.Action action)
    {
        if (BasisEditorUI.SecondaryButton(label, 24f)) action();
    }
}
