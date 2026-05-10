#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Basis.Scripts.BasisSdk.Players;

public class BasisFootDriverWindow : EditorWindow
{
    private Vector2 _scroll;
    private bool _repaintWhilePlaying = true;
    private float _repaintInterval = 0.06f;
    private double _nextRepaint;
    private static bool _sceneGizmos = true;

    [MenuItem("Basis/Debug/Foot Driver")]
    public static void Open()
    {
        var w = GetWindow<BasisFootDriverWindow>("Foot Driver");
        w.minSize = new Vector2(400, 460);
        w.Show();
    }

    private void OnEnable()
    {
        EditorApplication.update += Tick;
        SceneView.duringSceneGui += OnScene;
    }

    private void OnDisable()
    {
        EditorApplication.update -= Tick;
        SceneView.duringSceneGui -= OnScene;
    }

    private void Tick()
    {
        if (!_repaintWhilePlaying || !EditorApplication.isPlaying) return;
        if (EditorApplication.timeSinceStartup >= _nextRepaint)
        {
            _nextRepaint = EditorApplication.timeSinceStartup + _repaintInterval;
            Repaint();
        }
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            _repaintWhilePlaying = GUILayout.Toggle(_repaintWhilePlaying, "Auto", EditorStyles.toolbarButton, GUILayout.Width(50));
            _repaintInterval = EditorGUILayout.Slider(_repaintInterval, 0.016f, 0.5f);
            if (GUILayout.Button("Now", EditorStyles.toolbarButton, GUILayout.Width(40))) Repaint();
        }

        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode.", MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        var fd = BasisLocalPlayer.Instance?.BasisLocalFootDriver;
        if (fd == null || !fd.IsInitialized)
        {
            EditorGUILayout.HelpBox("Foot driver not ready.", MessageType.Warning);
            EditorGUILayout.EndScrollView();
            return;
        }

        // Calibration
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Measured (from T-pose)", EditorStyles.boldLabel);
            EditorGUILayout.FloatField("Stance Width", fd.CalibratedStanceWidth);
            EditorGUILayout.FloatField("Hip to Foot", fd.CalibratedHipToFoot);
            EditorGUILayout.FloatField("Left Leg", fd.CalibratedLeftLeg);
            EditorGUILayout.FloatField("Right Leg", fd.CalibratedRightLeg);
            EditorGUILayout.FloatField("Foot Length", fd.CalibratedFootLength);
            EditorGUILayout.FloatField("Ankle Height", fd.CalibratedAnkleHeight);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Derived Step Params", EditorStyles.miniLabel);
            EditorGUILayout.FloatField("Step Height", fd.DerivedStepHeight);
            EditorGUILayout.FloatField("Step Trigger", fd.DerivedStepTrigger);
            EditorGUILayout.FloatField("Fast Speed Ref", fd.DerivedFastSpeed);
        }

        EditorGUILayout.Space(4);

        // Locomotion
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Locomotion", EditorStyles.boldLabel);
            EditorGUILayout.FloatField("Speed (m/s)", fd.Speed);
            EditorGUILayout.Vector3Field("Velocity", fd.SmoothedVelocity);
        }

        EditorGUILayout.Space(4);
        DrawFootGUI("Left", fd.LeftIsPlanted, fd.LeftStepProgress, fd.LeftFootPosition,
            fd.LeftIdealPos, fd.LeftStepTarget, new Color(0.2f, 0.9f, 0.4f));

        EditorGUILayout.Space(4);
        DrawFootGUI("Right", fd.RightIsPlanted, fd.RightStepProgress, fd.RightFootPosition,
            fd.RightIdealPos, fd.RightStepTarget, new Color(0.2f, 0.5f, 1f));

        EditorGUILayout.Space(6);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            _sceneGizmos = EditorGUILayout.Toggle("Scene Gizmos", _sceneGizmos);
            if (GUILayout.Button("Focus Feet"))
            {
                Vector3 c = (fd.LeftFootPosition + fd.RightFootPosition) * 0.5f + Vector3.up * 0.5f;
                SceneView.lastActiveSceneView?.LookAt(c, Quaternion.Euler(45, 0, 0), 2f);
            }
            if (GUILayout.Button("Select Player"))
            {
                var go = BasisLocalPlayer.Instance?.gameObject;
                if (go) { Selection.activeGameObject = go; EditorGUIUtility.PingObject(go); }
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawFootGUI(string name, bool planted, float progress, Vector3 pos,
        Vector3 ideal, Vector3 stepTarget, Color tint)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(name, EditorStyles.boldLabel, GUILayout.Width(60));

                var bg = GUI.backgroundColor;
                GUI.backgroundColor = planted ? new Color(0.3f, 1f, 0.3f) : new Color(1f, 0.8f, 0.2f);
                GUILayout.Button(planted ? "PLANTED" : "STEPPING", GUILayout.Width(80));
                GUI.backgroundColor = bg;

                if (!planted)
                {
                    var r = EditorGUILayout.GetControlRect(GUILayout.Width(100), GUILayout.Height(16));
                    EditorGUI.ProgressBar(r, progress, $"{progress:P0}");
                }
            }

            EditorGUILayout.Vector3Field("Position", pos);
            EditorGUILayout.Vector3Field("Ideal", ideal);
            if (!planted) EditorGUILayout.Vector3Field("Step Target", stepTarget);

            float drift = new Vector2(pos.x - ideal.x, pos.z - ideal.z).magnitude;
            var rect = EditorGUILayout.GetControlRect(GUILayout.Height(14));
            float bar = Mathf.Clamp01(drift / 0.2f);
            EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width * bar, rect.height),
                Color.Lerp(new Color(0.2f, 0.8f, 0.2f), new Color(0.9f, 0.2f, 0.1f), bar));
            GUI.Label(rect, $"  drift: {drift * 100f:F1}cm", EditorStyles.miniLabel);
        }
    }

    // ─────────── Scene overlay ───────────

    private void OnScene(SceneView sv)
    {
        if (!_sceneGizmos || !EditorApplication.isPlaying) return;
        var fd = BasisLocalPlayer.Instance?.BasisLocalFootDriver;
        if (fd == null || !fd.IsInitialized) return;

        Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;

        DrawSceneFoot(fd.LeftFootPosition, fd.LeftFootRotation, fd.LeftIdealPos,
            fd.LeftIsPlanted, fd.LeftStepTarget, "L", new Color(0.2f, 0.9f, 0.4f));
        DrawSceneFoot(fd.RightFootPosition, fd.RightFootRotation, fd.RightIdealPos,
            fd.RightIsPlanted, fd.RightStepTarget, "R", new Color(0.2f, 0.5f, 1f));

        if (fd.SmoothedVelocity.sqrMagnitude > 0.01f)
        {
            Handles.color = new Color(1f, 0.2f, 1f, 0.9f);
            Handles.DrawLine(fd.HipsPosition, fd.HipsPosition + fd.SmoothedVelocity * 0.5f, 2f);
        }

        Handles.color = Color.white;
        Handles.Label(fd.HipsPosition + Vector3.up * 0.1f, $"{fd.Speed:F2} m/s");
    }

    private void DrawSceneFoot(Vector3 pos, Quaternion rot, Vector3 ideal,
        bool planted, Vector3 stepTarget, string side, Color col)
    {
        Handles.color = col;
        Handles.DrawWireDisc(pos, rot * Vector3.up, 0.04f);
        Handles.DrawLine(pos, pos + rot * Vector3.forward * 0.08f, 2f);

        // Ideal
        Handles.color = col * 0.5f;
        Handles.SphereHandleCap(0, ideal, Quaternion.identity, 0.012f, EventType.Repaint);
        Handles.DrawDottedLine(pos, ideal, 3f);

        // Step target
        if (!planted)
        {
            Handles.color = new Color(1f, 0.6f, 0.1f, 0.8f);
            Handles.SphereHandleCap(0, stepTarget, Quaternion.identity, 0.012f, EventType.Repaint);
            Handles.DrawDottedLine(pos, stepTarget, 2f);
        }

        float drift = new Vector2(pos.x - ideal.x, pos.z - ideal.z).magnitude;
        Handles.Label(pos + Vector3.up * 0.04f,
            $"{side} {(planted ? "plant" : "step")} d:{drift * 100f:F0}cm",
            new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = col } });
    }
}
#endif
