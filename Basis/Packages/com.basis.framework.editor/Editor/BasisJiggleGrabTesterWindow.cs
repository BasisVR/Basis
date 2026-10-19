using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk.Interactions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Controls and live readout for <see cref="BasisJiggleGrabSceneDrag"/> — click-drag jiggle chains
/// in the Scene view while in play mode, so the pull can be tuned without a headset, a second
/// client, or even an avatar (any active JiggleRig in the scene is grabbable).
///
/// Every JiggleRig inspector gets a button that opens this window.
/// </summary>
public sealed class BasisJiggleGrabTesterWindow : EditorWindow
{
    [MenuItem("Basis/Debug/Jiggle Grab Tester", false, 607)]
    public static void ShowWindow()
    {
        BasisJiggleGrabTesterWindow window = GetWindow<BasisJiggleGrabTesterWindow>();
        window.titleContent = new GUIContent(BasisEditorLocalization.Get("editor.jiggleGrab.windowTitle"));
        window.minSize = new Vector2(360, 320);
        window.Show();
    }

    private void OnEnable()
    {
        EditorApplication.update += Tick;
    }

    private void OnDisable()
    {
        EditorApplication.update -= Tick;
    }

    private void Tick()
    {
        if (Application.isPlaying)
        {
            Repaint();
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.jiggleGrab.sceneViewDrag"), EditorStyles.boldLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.jiggleGrab.enterPlayMode"), MessageType.Info);
        }

        BasisJiggleGrabSceneDrag.Enabled =
            EditorGUILayout.ToggleLeft(BasisEditorLocalization.Get("editor.jiggleGrab.enableSceneDrag"), BasisJiggleGrabSceneDrag.Enabled);

        EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.jiggleGrab.sceneDragHelp"), MessageType.None);

        BasisJiggleGrabSceneDrag.PickRadius = EditorGUILayout.Slider(BasisEditorLocalization.Get("editor.jiggleGrab.pickRadius"),
            BasisJiggleGrabSceneDrag.PickRadius, BasisJiggleGrabSceneDrag.MinRadius, BasisJiggleGrabSceneDrag.MaxRadius);
        BasisJiggleGrabSceneDrag.RayLength = EditorGUILayout.Slider(BasisEditorLocalization.Get("editor.jiggleGrab.rayLength"),
            BasisJiggleGrabSceneDrag.RayLength, 1f, 100f);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.jiggleGrab.runtime"), EditorStyles.boldLabel);

        bool master = EditorGUILayout.ToggleLeft(BasisEditorLocalization.Get("editor.jiggleGrab.grabbingEnabled"), BasisJiggleGrabPermissions.MasterEnabled);
        if (master != BasisJiggleGrabPermissions.MasterEnabled)
        {
            BasisJiggleGrabPermissions.MasterEnabled = master;
        }

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.IntField(BasisEditorLocalization.Get("editor.jiggleGrab.announcedGrabs"), BasisJiggleGrabDriver.AnnouncedGrabCount);
            EditorGUILayout.IntField(BasisEditorLocalization.Get("editor.jiggleGrab.appliedGrabs"), BasisJiggleGrabDriver.AppliedGrabCount);
            EditorGUILayout.IntField(BasisEditorLocalization.Get("editor.jiggleGrab.constraintsPushed"), BasisJiggleGrabDriver.PushedConstraintCount);
            EditorGUILayout.Toggle(BasisEditorLocalization.Get("editor.jiggleGrab.draggingNow"), BasisJiggleGrabDriver.HasEditorGrab);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.jiggleGrab.lastGrabPress"), EditorStyles.boldLabel);
        string attempt = BasisJiggleGrabDriver.LastAttemptResult;
        float age = Application.isPlaying ? Time.unscaledTime - BasisJiggleGrabDriver.LastAttemptTime : 0f;
        EditorGUILayout.HelpBox(
            BasisJiggleGrabDriver.LastAttemptTime > 0f ? BasisEditorLocalization.Get("editor.jiggleGrab.attemptAge", attempt, age) : attempt,
            attempt.StartsWith("grabbed") ? MessageType.Info : MessageType.Warning);
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.jiggleGrab.lastGrabNote"), EditorStyles.miniLabel);

        EditorGUILayout.Space();
        if (GUILayout.Button(BasisEditorLocalization.Get("editor.jiggleGrab.releaseDrag")))
        {
            BasisJiggleGrabDriver.EndEditorGrab();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.jiggleGrab.inGameControls"), EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.jiggleGrab.controlsHelp"), MessageType.None);
    }
}
