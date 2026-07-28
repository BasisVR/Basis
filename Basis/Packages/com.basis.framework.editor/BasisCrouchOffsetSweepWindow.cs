using UnityEditor;
using UnityEngine;

namespace Basis.IK.Debugging
{
    // Basis ▸ Debug ▸ IK ▸ Crouch Offset Sweep.
    public class BasisCrouchOffsetSweepWindow : EditorWindow
    {
        BasisCrouchOffsetSweepConfig _cfg = BasisCrouchOffsetSweepConfig.Default();
        string _path;
        BasisCrouchOffsetSweepSummary _last;
        bool _hasResult;
        Vector2 _scroll;

        [MenuItem("Basis/Debug/IK/Crouch Offset Sweep")]
        public static void ShowWindow()
        {
            var w = GetWindow<BasisCrouchOffsetSweepWindow>("Crouch Offset Sweep");
            w.minSize = new Vector2(360, 380);
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(_path)) _path = BasisCrouchOffsetSweep.DefaultPath();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "Sweeps crouch depth against the sit-back. Verifies the hips slide back by exactly the " +
                "corpus curve, land on the rest-length sphere once engaged, leak nothing sideways, grow " +
                "monotonically, and never move while standing, below the deadzone, or disabled. Same math " +
                "as the live ApplyCrouchBodyOffset.",
                MessageType.Info);

            EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);
            _cfg.DepthSteps = EditorGUILayout.IntSlider("Depth Steps", _cfg.DepthSteps, 5, 91);
            _cfg.YawSteps = EditorGUILayout.IntSlider("Yaw Steps", _cfg.YawSteps, 1, 16);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            _path = EditorGUILayout.TextField("CSV Path", _path);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string picked = EditorUtility.SaveFilePanel("Crouch Offset Sweep CSV", System.IO.Path.GetDirectoryName(_path),
                    System.IO.Path.GetFileName(_path), "csv");
                if (!string.IsNullOrEmpty(picked)) _path = picked;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            if (GUILayout.Button("Run Sweep", GUILayout.Height(32)))
            {
                _last = BasisCrouchOffsetSweep.Run(_cfg, _path);
                _hasResult = true;
                if (_last.Ok) Debug.Log($"[CrouchOffsetSweep] {_last.Cases} cases -> {_last.Path}");
                else Debug.LogError($"[CrouchOffsetSweep] failed: {_last.Error}");
            }

            if (_hasResult)
            {
                EditorGUILayout.Space();
                if (_last.Ok)
                {
                    var g = BasisIKTestGates.GateCrouchOffset(_last);
                    EditorGUILayout.HelpBox(
                        (g.pass ? "PASS  " : "FAIL  ") + g.reason + "\n" +
                        $"cases={_last.Cases} applied={_last.AppliedCases} nan={_last.NaNCount} fails={_last.Failures}\n" +
                        $"magErr={_last.MaxMagErrM:F5}m sphere={_last.MaxSphereErrM:F5}m lat={_last.MaxLateralLeakM:F5}m " +
                        $"standingMoves={_last.StandingMoves} mono={_last.MonotonicViolations}\n" + _last.Path,
                        g.pass ? MessageType.Info : MessageType.Error);
                    if (GUILayout.Button("Reveal CSV")) EditorUtility.RevealInFinder(_last.Path);
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
