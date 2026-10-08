using System.Collections.Generic;
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
        var window = GetWindow<BasisTransformAccessWindow>("Transform Access");
        window.minSize = new Vector2(560f, 300f);
    }

    void OnInspectorUpdate() => Repaint();

    void OnGUI()
    {
        BasisEditorUI.Header("Transform Access",
            "Every main-thread Transform get/set on the local player's per-frame path, by call site.");

        EditorGUILayout.HelpBox(
            "Counts operations routed through BasisTransformAccess / BasisLocalPose. A main-thread " +
            "Transform read blocks until every in-flight transform job lands — ScheduleReadOnly does " +
            "not exempt it — so each of these is a potential sync point. Recording costs a dictionary " +
            "lookup per operation, far more than the reads it measures: arm it to hunt, then disarm.",
            MessageType.Info);

        BasisTransformAudit.Enabled = EditorGUILayout.ToggleLeft(
            new GUIContent("Record (play mode)", "Count every funnelled operation by call site."),
            BasisTransformAudit.Enabled);

#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
        BasisLocalPose.ValidateHits = EditorGUILayout.ToggleLeft(
            new GUIContent("Validate cache hits",
                "Re-read the real Transform on every BasisLocalPose cache hit and compare. A mismatch means " +
                "something moved a cached transform without calling BasisLocalPose.InvalidateAll() — it is " +
                "logged as an error naming the reading call site. Run this after touching any writer."),
            BasisLocalPose.ValidateHits);

        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField($"Ops last frame: {BasisTransformAudit.CallsLastFrame}", GUILayout.Width(150f));
            EditorGUILayout.LabelField($"Peak: {BasisTransformAudit.PeakCallsPerFrame}", GUILayout.Width(90f));
            EditorGUILayout.LabelField($"Sites: {BasisTransformAudit.SiteCount}", GUILayout.Width(80f));
            EditorGUILayout.LabelField($"Frames: {BasisTransformAudit.FramesObserved}");
        }

        int hits = BasisLocalPose.Hits;
        int misses = BasisLocalPose.Misses;
        int total = hits + misses;
        float ratio = total > 0 ? hits * 100f / total : 0f;
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField($"Cache: {hits} hit / {misses} miss ({ratio:F1}%)", GUILayout.Width(240f));
            EditorGUILayout.LabelField($"Slots bound: {BasisLocalPose.BoundCount}/{(int)BasisPoseSlot.Count}");
        }

        if (BasisLocalPose.StaleHits > 0)
        {
            EditorGUILayout.HelpBox(
                $"{BasisLocalPose.StaleHits} STALE cache hit(s). Last: {BasisLocalPose.LastStaleSite}\n" +
                "A cached transform was moved without BasisLocalPose.InvalidateAll(). Find that writer and add one.",
                MessageType.Error);
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            _sortByPeak = EditorGUILayout.ToggleLeft(
                new GUIContent("Sort by peak frame", "Sort by the worst single frame rather than the last one — catches sites that spike instead of sitting high."),
                _sortByPeak, GUILayout.Width(160f));
            if (GUILayout.Button("Reset counts", GUILayout.Width(110f))) BasisTransformAudit.Reset();
        }

        var sites = new List<BasisTransformAudit.Site>(BasisTransformAudit.Sites);
        sites.Sort((a, b) => _sortByPeak ? b.PeakFrame.CompareTo(a.PeakFrame) : b.LastFrame.CompareTo(a.LastFrame));

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            EditorGUILayout.LabelField("Call site", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField("Last", EditorStyles.miniBoldLabel, GUILayout.Width(50f));
            EditorGUILayout.LabelField("Peak", EditorStyles.miniBoldLabel, GUILayout.Width(50f));
            EditorGUILayout.LabelField("Total", EditorStyles.miniBoldLabel, GUILayout.Width(70f));
            EditorGUILayout.LabelField("Ops", EditorStyles.miniBoldLabel, GUILayout.Width(220f));
        }

        using (var scope = new EditorGUILayout.ScrollViewScope(_scroll))
        {
            _scroll = scope.scrollPosition;
            if (sites.Count == 0)
            {
                EditorGUILayout.LabelField(BasisTransformAudit.Enabled
                    ? "Nothing recorded yet — enter play mode."
                    : "Not recording.", EditorStyles.miniLabel);
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
            EditorGUILayout.LabelField("By operation (last frame)", EditorStyles.boldLabel);
            IReadOnlyList<int> ops = BasisTransformAudit.OpsLastFrame;
            for (int i = 0; i < ops.Count; i++)
            {
                if (ops[i] == 0) continue;
                EditorGUILayout.LabelField($"{(BasisTransformOp)i}: {ops[i]}", EditorStyles.miniLabel);
            }
        }
#else
        EditorGUILayout.HelpBox("Recording is compiled out of this build.", MessageType.Warning);
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
