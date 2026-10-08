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
        var window = GetWindow<BasisFiniteWatchdogWindow>("Finite Watchdog");
        window.minSize = new Vector2(420f, 220f);
    }

    void OnGUI()
    {
        BasisEditorUI.Header("Finite Watchdog",
            "Hunts the first non-finite value behind Invalid AABB / IsFinite spam.");

        EditorGUILayout.HelpBox(
            "Hunts the first NaN behind 'Invalid AABB' / 'IsFinite(distanceForSort)' spam. " +
            "While armed: every stage in the frame that writes transforms is bracketed by a " +
            "local-space scan (local player + remote players), cameras + local avatar root are " +
            "checked every frame, and every renderer's bounds on the sweep cadence. The first hit " +
            "logs one [FiniteWatchdog] error naming the object, the stage that wrote it and its " +
            "ancestor chain, then the watchdog disarms.",
            MessageType.Info);

        BasisFiniteWatchdog.Enabled = EditorGUILayout.ToggleLeft("Enabled (scans while in play mode)", BasisFiniteWatchdog.Enabled);

#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
        BasisFiniteWatchdog.FullSweepIntervalSeconds = EditorGUILayout.Slider(
            new GUIContent("Renderer sweep interval (s)", "How often the all-renderers bounds sweep runs. The per-frame camera/root checks are free."),
            BasisFiniteWatchdog.FullSweepIntervalSeconds, 0.25f, 10f);

        BasisFiniteWatchdog.ScanRemotePlayers = EditorGUILayout.ToggleLeft(
            new GUIContent("Scan remote players", "Include remote avatars, mouth markers and nameplates in the stage checkpoints. Remote and local avatars share slot-indexed job state, so a corrupt remote is usually how corruption reaches the local one."),
            BasisFiniteWatchdog.ScanRemotePlayers);

        using (new EditorGUI.DisabledScope(!BasisFiniteWatchdog.ScanRemotePlayers))
        {
            BasisFiniteWatchdog.RemotePlayersPerCheckpoint = EditorGUILayout.IntSlider(
                new GUIContent("Remote players / checkpoint", "Remote avatars visited per remote checkpoint. The cursor carries across calls, so the whole lobby is still covered — just spread over frames. 0 scans everyone every time."),
                BasisFiniteWatchdog.RemotePlayersPerCheckpoint, 0, 32);
        }

        BasisFiniteWatchdog.IgnorePreexisting = EditorGUILayout.ToggleLeft(
            new GUIContent("Ignore pre-existing damage", "Arming an already-exploded avatar otherwise re-reports the same dead bone at the first checkpoint every time, which names a victim rather than a writer. With this on, everything already bad during the prime window is recorded and skipped, so the next report is a value that was finite and just became bad."),
            BasisFiniteWatchdog.IgnorePreexisting);

        using (new EditorGUI.DisabledScope(!BasisFiniteWatchdog.IgnorePreexisting))
        {
            BasisFiniteWatchdog.PrimeFrames = EditorGUILayout.IntSlider(
                new GUIContent("Prime frames", "How long after arming to record bad values instead of reporting them. Must outlast a full remote round-robin sweep."),
                BasisFiniteWatchdog.PrimeFrames, 1, 240);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            string state = !BasisFiniteWatchdog.Enabled ? "Off"
                : BasisFiniteWatchdog.Disarmed ? "TRIPPED — report below"
                : !Application.isPlaying ? "Armed, waiting for play mode"
                : BasisFiniteWatchdog.Priming ? $"Priming — {BasisFiniteWatchdog.PrimeFramesRemaining} frames left"
                : "Armed, scanning";
            EditorGUILayout.LabelField("State", state);
            if (GUILayout.Button("Re-arm", GUILayout.Width(80f)))
            {
                BasisFiniteWatchdog.Rearm();
            }
        }

        if (BasisFiniteWatchdog.IgnoredCount > 0)
        {
            EditorGUILayout.LabelField("Skipping (already bad)", $"{BasisFiniteWatchdog.IgnoredCount} value(s) recorded during priming");
        }

        EditorGUILayout.LabelField("Coverage last frame",
            $"{BasisFiniteWatchdog.CheckpointsLastFrame} checkpoints, {BasisFiniteWatchdog.TransformsScannedLastFrame} transforms read");
        EditorGUILayout.LabelField("Last clean stage",
            string.IsNullOrEmpty(BasisFiniteWatchdog.LastCleanStage) ? "—" : BasisFiniteWatchdog.LastCleanStage);

        if (!string.IsNullOrEmpty(BasisFiniteWatchdog.LastReport))
        {
            BasisEditorUI.SectionTitle("Last report");
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.TextArea(BasisFiniteWatchdog.LastReport, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("Copy report to clipboard"))
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
