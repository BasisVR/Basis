using UnityEditor;
using UnityEngine;

namespace Basis.IK.Debugging
{
    // Basis ▸ Debug ▸ IK ▸ Spine Sweep.
    public class BasisSpineSweepWindow : EditorWindow
    {
        BasisSpineSweepConfig _cfg = BasisSpineSweepConfig.Default();
        string _path;
        BasisSpineSummary _last;
        bool _hasResult;

        [MenuItem("Basis/Debug/IK/Spine Sweep")]
        public static void ShowWindow()
        {
            var w = GetWindow<BasisSpineSweepWindow>("Spine Sweep");
            w.minSize = new Vector2(400, 340);
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(_path)) _path = BasisSpineSweep.DefaultPath();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Exercises the virtual-spine solve helpers (BasisVirtualSpineCore chain placement, " +
                "hips position, yaw extraction). Asserts chest/spine sit on the neck→hips segment at the " +
                "right fractions (no inversion), hips drop the spine length below the neck, and yaw " +
                "extraction strips pitch/roll. Edit mode, pure math.", MessageType.Info);

            _cfg.Cases = EditorGUILayout.IntSlider("Cases", _cfg.Cases, 100, 50000);

            EditorGUILayout.Space();
            if (GUILayout.Button("Run Sweep", GUILayout.Height(30)))
            {
                _last = BasisSpineSweep.Run(_cfg, _path);
                _hasResult = true;
                if (_last.Ok) Debug.Log($"[SpineSweep] {_last.Cases} cases -> {_last.Path}");
                else Debug.LogError($"[SpineSweep] failed: {_last.Error}");
            }

            if (_hasResult)
            {
                EditorGUILayout.Space();
                if (_last.Ok)
                {
                    var gate = BasisIKTestGates.GateSpine(_last);
                    var prev = GUI.color;
                    GUI.color = gate.pass ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.5f);
                    EditorGUILayout.LabelField(gate.pass ? "GATE: PASS" : "GATE: FAIL", EditorStyles.boldLabel);
                    GUI.color = prev;
                    EditorGUILayout.HelpBox(gate.reason, gate.pass ? MessageType.Info : MessageType.Warning);

                    EditorGUILayout.HelpBox(
                        $"{_last.Cases} cases.\n" +
                        $"Chain fraction err: {_last.MaxChainFracErr:E2}; monotonic fails: {_last.ChainMonotonicFails}\n" +
                        $"Hips: Y err {_last.MaxHipsYErr:E2} m, XZ err {_last.MaxHipsXZErr:E2} m, freeze err {_last.MaxHipsFreezeErr:E2} m\n" +
                        $"Yaw: flatness {_last.MaxYawFlatErr:E2}, idempotence {_last.MaxYawIdempotentErr:F3}°, deg recovery {_last.MaxYawDegErr:F3}°\n" +
                        _last.Path, MessageType.None);
                    if (GUILayout.Button("Reveal CSV")) EditorUtility.RevealInFinder(_last.Path);
                }
                else
                {
                    EditorGUILayout.HelpBox("Sweep failed: " + _last.Error, MessageType.Error);
                }
            }
        }
    }
}
