using UnityEditor;
using UnityEngine;

namespace Basis.IK.Debugging
{
    // Basis ▸ Debug ▸ IK ▸ Leg Inversion Sweep. Flags backward-bending (inhuman) knees from an inverted hint/pole.
    public class BasisLegInversionSweepWindow : EditorWindow
    {
        BasisLegInversionConfig _cfg = BasisLegInversionConfig.Default();
        string _path;
        BasisLegInversionSummary _last;
        bool _hasResult;
        Vector2 _scroll;

        [MenuItem("Basis/Debug/IK/Leg Inversion Sweep")]
        public static void ShowWindow()
        {
            var w = GetWindow<BasisLegInversionSweepWindow>("Leg Inversion Sweep");
            w.minSize = new Vector2(400, 540);
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(_path)) _path = BasisLegInversionSweep.DefaultPath();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "Detects INHUMAN knee poses (knee bending backward) caused by the hint/pole role inverting -- " +
                "e.g. a leg tracker that drives the knee hint behind the leg. The 'hint' pass sweeps the hint over " +
                "a sphere at fixed foot targets; the 'target' pass sweeps the foot with a good hint. Same math the live rig runs.",
                MessageType.Info);

            EditorGUILayout.LabelField("Leg Geometry", EditorStyles.boldLabel);
            _cfg.Base.UpperLength = EditorGUILayout.FloatField("Upper Length (m)", _cfg.Base.UpperLength);
            _cfg.Base.LowerLength = EditorGUILayout.FloatField("Lower Length (m)", _cfg.Base.LowerLength);
            _cfg.Base.IsLeft = EditorGUILayout.Toggle("Left Leg (mirror X)", _cfg.Base.IsLeft);
            _cfg.Base.RestKneeDir = EditorGUILayout.Vector3Field("Rest Knee Dir", _cfg.Base.RestKneeDir);
            _cfg.Base.RestShinDir = EditorGUILayout.Vector3Field("Rest Shin Dir", _cfg.Base.RestShinDir);
            _cfg.Base.BendNormal = EditorGUILayout.Vector3Field("Bend Normal (hips right)", _cfg.Base.BendNormal);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Hint Stress", EditorStyles.boldLabel);
            _cfg.Base.HintDir = EditorGUILayout.Vector3Field("Nominal Hint Dir", _cfg.Base.HintDir);
            _cfg.Base.HintDistanceFrac = EditorGUILayout.Slider("Hint Distance Frac", _cfg.Base.HintDistanceFrac, 0f, 1f);
            _cfg.HintAzSteps = EditorGUILayout.IntSlider("Azimuth Steps", _cfg.HintAzSteps, 4, 144);
            _cfg.HintElSteps = EditorGUILayout.IntSlider("Elevation Steps", _cfg.HintElSteps, 1, 57);
            _cfg.SafeConeDeg = EditorGUILayout.Slider("Safe Hint Cone (deg)", _cfg.SafeConeDeg, 10f, 120f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Target Grid (good hint)", EditorStyles.boldLabel);
            _cfg.Base.MinFrac = EditorGUILayout.Vector3Field("Min Frac", _cfg.Base.MinFrac);
            _cfg.Base.MaxFrac = EditorGUILayout.Vector3Field("Max Frac", _cfg.Base.MaxFrac);
            _cfg.Base.Steps = EditorGUILayout.Vector3IntField("Steps (X,Y,Z)", _cfg.Base.Steps);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            _path = EditorGUILayout.TextField("CSV Path", _path);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string picked = EditorUtility.SaveFilePanel("Leg Inversion Sweep CSV", System.IO.Path.GetDirectoryName(_path),
                    System.IO.Path.GetFileName(_path), "csv");
                if (!string.IsNullOrEmpty(picked)) _path = picked;
            }
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button("Reset to persistentDataPath")) _path = BasisLegInversionSweep.DefaultPath();

            EditorGUILayout.Space();
            if (GUILayout.Button("Run Sweep", GUILayout.Height(32)))
            {
                _last = BasisLegInversionSweep.Run(_cfg, _path);
                _hasResult = true;
                if (_last.Ok) Debug.Log($"[LegInversionSweep] {_last.Rows} rows -> {_last.Path}");
                else Debug.LogError($"[LegInversionSweep] failed: {_last.Error}");
            }

            if (_hasResult)
            {
                EditorGUILayout.Space();
                if (_last.Ok)
                {
                    var g = BasisIKTestGates.GateLegInversion(_last);
                    var prev = GUI.color;
                    GUI.color = g.pass ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.5f);
                    EditorGUILayout.LabelField(g.pass ? "PASS" : "FAIL", EditorStyles.boldLabel);
                    GUI.color = prev;

                    string onset = float.IsNaN(_last.OnsetDeviationDeg) ? "none" : $"{_last.OnsetDeviationDeg:F0}°";
                    EditorGUILayout.HelpBox(
                        $"{g.reason}\n\n" +
                        $"Hint stress (well-conditioned): {_last.HintInverted} inverted; inside {_cfg.SafeConeDeg:F0}° cone: {_last.SafeConeInversions}/{_last.SafeConeSamples}; onset {onset}.\n" +
                        $"Pole singularities (excluded from gate): {_last.SingularInversions}/{_last.SingularSamples} still invert after the solver blend.\n" +
                        $"Good-hint targets: {_last.TargetInversions}/{_last.TargetReachable} inverted.\n" +
                        $"Flexion limit: min knee interior {_last.MinKneeFlexDeg:F0}° over {_last.FlexClampSamples} over-fold pulls (clamp = {Basis.IK.BasisLegSolveCore.MinKneeInteriorDeg:F0}°).\n" +
                        $"Worst knee swivel from forward: {_last.WorstSwivelDeg:F0}° (180° = straight back).\n" +
                        _last.Path, MessageType.None);
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Reveal CSV")) EditorUtility.RevealInFinder(_last.Path);
                    if (GUILayout.Button("Copy Path")) EditorGUIUtility.systemCopyBuffer = _last.Path;
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    EditorGUILayout.HelpBox("Sweep failed: " + _last.Error, MessageType.Error);
                }
            }

            EditorGUILayout.EndScrollView();
        }
    }
}
