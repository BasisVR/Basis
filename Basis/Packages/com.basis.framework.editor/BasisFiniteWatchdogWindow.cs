using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Arms <see cref="BasisFiniteWatchdog"/> and shows its report. The watchdog names the FIRST
/// object whose transform/bounds went non-finite — the injection site behind an
/// "Invalid AABB" / "IsFinite(distanceForSort)" console storm — then disarms itself.
/// </summary>
public class BasisFiniteWatchdogWindow : EditorWindow
{
    Vector2 _scroll;

    [MenuItem("Basis/Debug/Finite Watchdog", false, 623)]
    static void Open()
    {
        var window = GetWindow<BasisFiniteWatchdogWindow>(BasisEditorLocalization.Get("editor.finiteWatchdog.title"));
        window.minSize = new Vector2(420f, 220f);
    }

    void OnGUI()
    {
        BasisEditorUI.Header(BasisEditorLocalization.Get("editor.finiteWatchdog.title"),
            BasisEditorLocalization.Get("editor.finiteWatchdog.subtitle"));

        EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.finiteWatchdog.help"), MessageType.Info);

        BasisFiniteWatchdog.Enabled = EditorGUILayout.ToggleLeft(BasisEditorLocalization.Get("editor.finiteWatchdog.enabled"), BasisFiniteWatchdog.Enabled);

#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
        BasisFiniteWatchdog.FullSweepIntervalSeconds = EditorGUILayout.Slider(
            new GUIContent(BasisEditorLocalization.Get("editor.finiteWatchdog.sweepInterval"), BasisEditorLocalization.Get("editor.finiteWatchdog.sweepInterval.tooltip")),
            BasisFiniteWatchdog.FullSweepIntervalSeconds, 0.25f, 10f);

        BasisFiniteWatchdog.ScanRemotePlayers = EditorGUILayout.ToggleLeft(
            new GUIContent(BasisEditorLocalization.Get("editor.finiteWatchdog.scanRemote"), BasisEditorLocalization.Get("editor.finiteWatchdog.scanRemote.tooltip")),
            BasisFiniteWatchdog.ScanRemotePlayers);

        using (new EditorGUI.DisabledScope(!BasisFiniteWatchdog.ScanRemotePlayers))
        {
            BasisFiniteWatchdog.RemotePlayersPerCheckpoint = EditorGUILayout.IntSlider(
                new GUIContent(BasisEditorLocalization.Get("editor.finiteWatchdog.remotePerCheckpoint"), BasisEditorLocalization.Get("editor.finiteWatchdog.remotePerCheckpoint.tooltip")),
                BasisFiniteWatchdog.RemotePlayersPerCheckpoint, 0, 32);
        }

        BasisFiniteWatchdog.IgnorePreexisting = EditorGUILayout.ToggleLeft(
            new GUIContent(BasisEditorLocalization.Get("editor.finiteWatchdog.ignorePreexisting"), BasisEditorLocalization.Get("editor.finiteWatchdog.ignorePreexisting.tooltip")),
            BasisFiniteWatchdog.IgnorePreexisting);

        using (new EditorGUI.DisabledScope(!BasisFiniteWatchdog.IgnorePreexisting))
        {
            BasisFiniteWatchdog.PrimeFrames = EditorGUILayout.IntSlider(
                new GUIContent(BasisEditorLocalization.Get("editor.finiteWatchdog.primeFrames"), BasisEditorLocalization.Get("editor.finiteWatchdog.primeFrames.tooltip")),
                BasisFiniteWatchdog.PrimeFrames, 1, 240);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            string state = !BasisFiniteWatchdog.Enabled ? BasisEditorLocalization.Get("editor.common.off")
                : BasisFiniteWatchdog.Disarmed ? BasisEditorLocalization.Get("editor.finiteWatchdog.stateTripped")
                : !Application.isPlaying ? BasisEditorLocalization.Get("editor.finiteWatchdog.stateWaiting")
                : BasisFiniteWatchdog.Priming ? BasisEditorLocalization.Get("editor.finiteWatchdog.statePriming", BasisFiniteWatchdog.PrimeFramesRemaining)
                : BasisEditorLocalization.Get("editor.finiteWatchdog.stateScanning");
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.finiteWatchdog.state"), state);
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.finiteWatchdog.rearm"), GUILayout.Width(80f)))
            {
                BasisFiniteWatchdog.Rearm();
            }
        }

        if (BasisFiniteWatchdog.IgnoredCount > 0)
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.finiteWatchdog.skipping"), BasisEditorLocalization.Get("editor.finiteWatchdog.skippingValue", BasisFiniteWatchdog.IgnoredCount));
        }

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.finiteWatchdog.coverage"),
            BasisEditorLocalization.Get("editor.finiteWatchdog.coverageValue", BasisFiniteWatchdog.CheckpointsLastFrame, BasisFiniteWatchdog.TransformsScannedLastFrame));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.finiteWatchdog.lastCleanStage"),
            string.IsNullOrEmpty(BasisFiniteWatchdog.LastCleanStage) ? "—" : BasisFiniteWatchdog.LastCleanStage);

        if (!string.IsNullOrEmpty(BasisFiniteWatchdog.LastReport))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.finiteWatchdog.lastReport"));
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.TextArea(BasisFiniteWatchdog.LastReport, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.finiteWatchdog.copyReport")))
            {
                EditorGUIUtility.systemCopyBuffer = BasisFiniteWatchdog.LastReport;
            }
        }
#endif
    }

    void OnInspectorUpdate()
    {
        Repaint();
    }
}
