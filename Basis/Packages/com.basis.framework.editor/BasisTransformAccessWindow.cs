using System.Collections.Generic;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shows what <see cref="BasisTransformAccess"/> funnelled, per call site. This is the map of the
/// local player's main-thread Transform work — every row is a place the main thread reached into
/// native transform state, and therefore a place that can stall on in-flight transform jobs.
///
/// A site missing from this list either did not run or was never migrated to the funnel; the list is
/// coverage, not proof of completeness.
/// </summary>
public class BasisTransformAccessWindow : EditorWindow
{
    Vector2 _scroll;
    bool _sortByPeak;

    [MenuItem("Basis/Debug/Transform Access", false, 624)]
    static void Open()
    {
        var window = GetWindow<BasisTransformAccessWindow>(BasisEditorLocalization.Get("editor.transformAccess.title"));
        window.minSize = new Vector2(560f, 300f);
    }

    void OnInspectorUpdate() => Repaint();

    void OnGUI()
    {
        BasisEditorUI.Header(BasisEditorLocalization.Get("editor.transformAccess.title"),
            BasisEditorLocalization.Get("editor.transformAccess.subtitle"));

        EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.transformAccess.help"), MessageType.Info);

        BasisTransformAudit.Enabled = EditorGUILayout.ToggleLeft(
            new GUIContent(BasisEditorLocalization.Get("editor.transformAccess.record"), BasisEditorLocalization.Get("editor.transformAccess.record.tooltip")),
            BasisTransformAudit.Enabled);

#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
        BasisLocalPose.ValidateHits = EditorGUILayout.ToggleLeft(
            new GUIContent(BasisEditorLocalization.Get("editor.transformAccess.validateHits"), BasisEditorLocalization.Get("editor.transformAccess.validateHits.tooltip")),
            BasisLocalPose.ValidateHits);

        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.opsLastFrame", BasisTransformAudit.CallsLastFrame), GUILayout.Width(150f));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.peak", BasisTransformAudit.PeakCallsPerFrame), GUILayout.Width(90f));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.sites", BasisTransformAudit.SiteCount), GUILayout.Width(80f));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.frames", BasisTransformAudit.FramesObserved));
        }

        int hits = BasisLocalPose.Hits;
        int misses = BasisLocalPose.Misses;
        int total = hits + misses;
        float ratio = total > 0 ? hits * 100f / total : 0f;
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.cache", hits, misses, ratio), GUILayout.Width(240f));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.slotsBound", BasisLocalPose.BoundCount, (int)BasisPoseSlot.Count));
        }

        if (BasisLocalPose.StaleHits > 0)
        {
            EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.transformAccess.staleHits", BasisLocalPose.StaleHits, BasisLocalPose.LastStaleSite), MessageType.Error);
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            _sortByPeak = EditorGUILayout.ToggleLeft(
                new GUIContent(BasisEditorLocalization.Get("editor.transformAccess.sortByPeak"), BasisEditorLocalization.Get("editor.transformAccess.sortByPeak.tooltip")),
                _sortByPeak, GUILayout.Width(160f));
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.transformAccess.resetCounts"), GUILayout.Width(110f))) BasisTransformAudit.Reset();
        }

        var sites = new List<BasisTransformAudit.Site>(BasisTransformAudit.Sites);
        sites.Sort((a, b) => _sortByPeak ? b.PeakFrame.CompareTo(a.PeakFrame) : b.LastFrame.CompareTo(a.LastFrame));

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.colCallSite"), EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.colLast"), EditorStyles.miniBoldLabel, GUILayout.Width(50f));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.colPeak"), EditorStyles.miniBoldLabel, GUILayout.Width(50f));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.colTotal"), EditorStyles.miniBoldLabel, GUILayout.Width(70f));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.colOps"), EditorStyles.miniBoldLabel, GUILayout.Width(220f));
        }

        using (var scope = new EditorGUILayout.ScrollViewScope(_scroll))
        {
            _scroll = scope.scrollPosition;
            if (sites.Count == 0)
            {
                EditorGUILayout.LabelField(BasisTransformAudit.Enabled
                    ? BasisEditorLocalization.Get("editor.transformAccess.nothingRecorded")
                    : BasisEditorLocalization.Get("editor.transformAccess.notRecording"), EditorStyles.miniLabel);
            }

            for (int i = 0; i < sites.Count; i++)
            {
                BasisTransformAudit.Site s = sites[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{s.ShortFile}:{s.Line}", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(s.LastFrame.ToString(), EditorStyles.miniLabel, GUILayout.Width(50f));
                    EditorGUILayout.LabelField(s.PeakFrame.ToString(), EditorStyles.miniLabel, GUILayout.Width(50f));
                    EditorGUILayout.LabelField(s.Total.ToString(), EditorStyles.miniLabel, GUILayout.Width(70f));
                    EditorGUILayout.LabelField(DescribeOps(s), EditorStyles.miniLabel, GUILayout.Width(220f));
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.transformAccess.byOperation"), EditorStyles.boldLabel);
            IReadOnlyList<int> ops = BasisTransformAudit.OpsLastFrame;
            for (int i = 0; i < ops.Count; i++)
            {
                if (ops[i] == 0) continue;
                EditorGUILayout.LabelField($"{(BasisTransformOp)i}: {ops[i]}", EditorStyles.miniLabel);
            }
        }
#else
        EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.transformAccess.compiledOut"), MessageType.Warning);
#endif
    }

#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
    /// <summary>Compact "GetPosition x3, GetPose x1" summary of what a site actually does.</summary>
    static string DescribeOps(BasisTransformAudit.Site s)
    {
        string result = string.Empty;
        for (int i = 0; i < s.Ops.Length; i++)
        {
            if (s.Ops[i] == 0) continue;
            if (result.Length > 0) result += ", ";
            result += $"{(BasisTransformOp)i} x{s.Ops[i]}";
        }
        return result;
    }
#endif
}
