using UnityEditor;
using UnityEngine;

namespace Basis.IK.Debugging
{
    // Basis ▸ Debug ▸ IK ▸ Calibration Math Sweep.
    public class BasisCalibrationMathSweepWindow : EditorWindow
    {
        BasisCalibrationMathSweepConfig _cfg = BasisCalibrationMathSweepConfig.Default();
        string _path;
        BasisCalibrationMathSummary _last;
        bool _hasResult;
        Vector2 _scroll;

        [MenuItem("Basis/Debug/IK/Calibration Math Sweep")]
        public static void ShowWindow()
        {
            var w = GetWindow<BasisCalibrationMathSweepWindow>("Calibration Math Sweep");
            w.minSize = new Vector2(420, 480);
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(_path))
            {
                _path = BasisCalibrationMathSweep.DefaultPath();
            }
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "Exercises the real calibration math the live FullBodyCalibration runs: tracker→bone " +
                "inverse-offset capture/apply (BasisCalibrationMath), device scaling, per-effector " +
                "rotation calibration (BasisAnimationRiggingHelper — the #531 no-orientation-leak), " +
                "pitch-calibrated eye height, and the avatar scale modifier. Asserts round-trip / no-leak " +
                "invariants over many synthetic poses. Edit mode, no avatar.", MessageType.Info);

            EditorGUILayout.Space();
            _cfg.CasesPerSection = EditorGUILayout.IntSlider("Cases per Section", _cfg.CasesPerSection, 100, 20000);

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            _path = EditorGUILayout.TextField("CSV Path", _path);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string picked = EditorUtility.SaveFilePanel("Calibration Math Sweep CSV", System.IO.Path.GetDirectoryName(_path),
                    System.IO.Path.GetFileName(_path), "csv");
                if (!string.IsNullOrEmpty(picked)) _path = picked;
            }
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button("Reset to persistentDataPath"))
            {
                _path = BasisCalibrationMathSweep.DefaultPath();
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Run Sweep", GUILayout.Height(32)))
            {
                _last = BasisCalibrationMathSweep.Run(_cfg, _path);
                _hasResult = true;
                if (_last.Ok) Debug.Log($"[CalibrationMathSweep] {_last.Cases} cases, {_last.Failures} fails -> {_last.Path}");
                else Debug.LogError($"[CalibrationMathSweep] failed: {_last.Error}");
            }

            if (_hasResult)
            {
                EditorGUILayout.Space();
                if (_last.Ok)
                {
                    var gate = BasisIKTestGates.GateCalibrationMath(_last);
                    var prev = GUI.color;
                    GUI.color = gate.pass ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.5f);
                    EditorGUILayout.LabelField(gate.pass ? "GATE: PASS" : "GATE: FAIL", EditorStyles.boldLabel);
                    GUI.color = prev;
                    EditorGUILayout.HelpBox(gate.reason, gate.pass ? MessageType.Info : MessageType.Warning);

                    EditorGUILayout.HelpBox(
                        $"{_last.Cases} cases, {_last.Failures} failures.\n" +
                        $"Inverse offset: pos err {_last.MaxOffsetPosErr:E2} m, rot err {_last.MaxOffsetRotErrDeg:F3}°, rigid-follow {_last.MaxRigidFollowErr:E2} m\n" +
                        $"Device scale round-trip: {_last.MaxScalePosErr:E2} m\n" +
                        $"Rotation calibration (no-leak): {_last.MaxRotCalErrDeg:F3}°\n" +
                        $"Scale modifier mismatches: {_last.ScaleModifierMismatches}\n" +
                        $"Feel height: viewpoint err {_last.MaxFeelHeightErr:E2} m, too-tall ratio err {_last.MaxFeelFactorErr:E2}\n" +
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
