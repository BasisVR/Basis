using System;
using System.Collections.Generic;
using System.IO;
using Basis.Scripts.Networking.Voice.Testing;
using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.Networking.Voice.EditorTools
{
    /// <summary>
    /// Editor front-end for <see cref="BasisVoiceSim"/>. Runs the real voice pipeline
    /// (Opus encode -> wire -> simulated server relay -> impaired network -> real jitter
    /// buffer/decoder/audio-callback) against a matrix of network conditions, scores the
    /// rendered audio, and can export any single scenario's reference/output/baseline as
    /// WAVs for listening. Pure offline simulation — no play mode, no live server.
    /// </summary>
    public class BasisVoiceTestRunnerWindow : EditorWindow
    {
        static readonly Color Green = new Color(0.45f, 0.9f, 0.45f);
        static readonly Color Red = new Color(0.95f, 0.5f, 0.5f);
        static readonly Color Grey = new Color(0.65f, 0.65f, 0.65f);

        enum Scope { Quick, Full }

        Scope _scope = Scope.Quick;
        int _seeds = 1;

        List<BasisVoiceSimResult> _results;
        string _summary = "";
        Vector2 _scroll;
        Vector2 _resultScroll;

        // Single-run section
        int _singleProfileIndex;
        BasisVoiceSignal _singleSignal = BasisVoiceSignal.SpeechLike;
        float _singleDuration = 6f;
        int _singleBitrate = LocalOpusSettings.DefaultBitrate;
        int _singleFloor = 5;
        bool _single44100;
        bool _single40ms;
        float _singleHangMs;
        BasisVoiceSimResult _singleResult;

        static readonly Func<BasisVoiceNetProfile>[] ProfileFactories =
        {
            BasisVoiceSimMatrix.Perfect,
            BasisVoiceSimMatrix.Lan,
            BasisVoiceSimMatrix.Jitter30,
            BasisVoiceSimMatrix.Loss5,
            BasisVoiceSimMatrix.Loss15,
            BasisVoiceSimMatrix.JitterLoss,
            BasisVoiceSimMatrix.Burst160,
            BasisVoiceSimMatrix.Burst800,
            BasisVoiceSimMatrix.Stall600,
            BasisVoiceSimMatrix.Chaos,
        };
        static readonly string[] ProfileNames =
        {
            "perfect", "lan", "jitter30", "loss5", "loss15",
            "jitterloss", "burst160ms", "burst800ms", "stall600ms", "chaos",
        };

        [MenuItem("Basis/Debug/Voice Pipeline Test")]
        public static void ShowWindow()
        {
            var window = GetWindow<BasisVoiceTestRunnerWindow>();
            window.titleContent = new GUIContent("Voice Pipeline Test");
            window.minSize = new Vector2(480, 560);
            window.Show();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            Header("Voice Pipeline - End-to-End Offline Test");
            EditorGUILayout.HelpBox(
                "Drives the REAL voice path: Opus encode (same CTLs as the live encoder) -> wire " +
                "serialization -> server relay wrap -> seeded network impairments -> the real " +
                "BasisAudioReceiver (jitter buffer, FEC/PLC, adaptive depth, fades, resampler). " +
                "Output audio is scored for underrun notches ('bubbling'), SNR vs a codec-only " +
                "baseline, dropped audio, and latency.",
                MessageType.Info);

            DrawMatrix();
            DrawSingleRun();

            EditorGUILayout.EndScrollView();
        }

        void DrawMatrix()
        {
            Header("Matrix");
            _scope = (Scope)EditorGUILayout.EnumPopup("Preset", _scope);
            _seeds = EditorGUILayout.IntSlider(new GUIContent("Seeds (repeats)"), _seeds, 1, 5);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Run Matrix", GUILayout.Height(26)))
                    RunMatrix();
                using (new EditorGUI.DisabledScope(_results == null || _results.Count == 0))
                {
                    if (GUILayout.Button("Export CSV...", GUILayout.Height(26)))
                        ExportCsv();
                }
            }

            if (!string.IsNullOrEmpty(_summary))
            {
                GUIStyle bold = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
                EditorGUILayout.LabelField(_summary, bold);
            }

            if (_results != null && _results.Count > 0)
            {
                Header("Results");
                _resultScroll = EditorGUILayout.BeginScrollView(_resultScroll, GUILayout.MinHeight(180), GUILayout.MaxHeight(320));
                foreach (var r in _results)
                {
                    Color c = r.Passed ? (string.IsNullOrEmpty(r.Error) ? Green : Red) : Red;
                    if (r.Passed && r.ProfileName != "perfect" && r.ProfileName != "lan" && r.ProfileName != "baseline")
                        c = Grey; // impaired profiles are observational
                    GUIStyle style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = c }, wordWrap = false };
                    EditorGUILayout.LabelField(r.Summary, style);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        void RunMatrix()
        {
            var scenarios = BasisVoiceSimMatrix.Enumerate(_scope == Scope.Full, _seeds);
            var results = new List<BasisVoiceSimResult>(scenarios.Count);
            try
            {
                for (int i = 0; i < scenarios.Count; i++)
                {
                    var s = scenarios[i];
                    if (EditorUtility.DisplayCancelableProgressBar("Voice Pipeline Test", $"{i + 1}/{scenarios.Count}  {s.Name}", (float)i / scenarios.Count))
                        break;
                    results.Add(BasisVoiceSim.Run(s));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            _results = results;
            int pass = 0, fail = 0, errors = 0;
            foreach (var r in results)
            {
                if (r.Passed) pass++; else fail++;
                if (!string.IsNullOrEmpty(r.Error)) errors++;
            }
            _summary = $"{results.Count} scenarios: {pass} pass, {fail} fail, {errors} exceptions.";
            Repaint();
        }

        void ExportCsv()
        {
            string path = EditorUtility.SaveFilePanel("Export Voice Test CSV", "", "voice_pipeline_results.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, BasisVoiceSimMatrix.ToCsv(_results));
            EditorUtility.RevealInFinder(path);
        }

        void DrawSingleRun()
        {
            Header("Single Scenario (listenable WAV export)");
            _singleSignal = (BasisVoiceSignal)EditorGUILayout.EnumPopup("Signal", _singleSignal);
            _singleProfileIndex = EditorGUILayout.Popup("Network profile", _singleProfileIndex, ProfileNames);
            _singleDuration = EditorGUILayout.Slider("Duration (s)", _singleDuration, 2f, 30f);
            _singleBitrate = EditorGUILayout.IntSlider("Bitrate (bps)", _singleBitrate, 6000, 128000);
            _singleFloor = EditorGUILayout.IntSlider("Jitter buffer floor", _singleFloor, 1, 10);
            _single44100 = EditorGUILayout.Toggle("44.1 kHz output (resampler)", _single44100);
            _single40ms = EditorGUILayout.Toggle("40 ms frames", _single40ms);
            _singleHangMs = EditorGUILayout.Slider("Receiver hang (ms)", _singleHangMs, 0f, 2000f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Run", GUILayout.Height(24)))
                    RunSingle();
                using (new EditorGUI.DisabledScope(_singleResult == null || _singleResult.OutputMono == null))
                {
                    if (GUILayout.Button("Export WAVs...", GUILayout.Height(24)))
                        ExportSingleWavs();
                }
            }

            if (_singleResult != null)
            {
                GUIStyle style = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = _singleResult.Passed ? Green : Red },
                    wordWrap = true,
                };
                EditorGUILayout.LabelField(_singleResult.Summary, style);
            }
        }

        void RunSingle()
        {
            var s = new BasisVoiceScenario
            {
                Name = $"single/{_singleSignal}/{ProfileNames[_singleProfileIndex]}",
                Signal = _singleSignal,
                Profile = ProfileFactories[_singleProfileIndex](),
                DurationSeconds = _singleDuration,
                Bitrate = _singleBitrate,
                JitterBufferFloor = _singleFloor,
                OutputSampleRate = _single44100 ? 44100 : 48000,
                FrameDurationSeconds = _single40ms ? 0.04f : 0.02f,
                ReceiverHangAtSeconds = _singleHangMs > 0f ? Mathf.Min(2.5f, (float)_singleDuration * 0.4f) : 0f,
                ReceiverHangDurationMs = _singleHangMs,
                KeepAudio = true,
            };
            try
            {
                EditorUtility.DisplayProgressBar("Voice Pipeline Test", s.Name, 0.5f);
                _singleResult = BasisVoiceSim.Run(s);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Repaint();
        }

        void ExportSingleWavs()
        {
            string dir = EditorUtility.SaveFolderPanel("Export WAVs", "", "voice_test");
            if (string.IsNullOrEmpty(dir)) return;
            var r = _singleResult;
            string baseName = r.ScenarioName.Replace('/', '_');
            int outRate = _single44100 ? 44100 : 48000;
            BasisVoiceQualityAnalysis.WriteWav(Path.Combine(dir, baseName + "_reference.wav"), r.ReferenceMono, LocalOpusSettings.MicrophoneSampleRate);
            BasisVoiceQualityAnalysis.WriteWav(Path.Combine(dir, baseName + "_output.wav"), r.OutputMono, outRate);
            if (r.BaselineMono != null && !ReferenceEquals(r.BaselineMono, r.OutputMono))
                BasisVoiceQualityAnalysis.WriteWav(Path.Combine(dir, baseName + "_baseline.wav"), r.BaselineMono, outRate);
            EditorUtility.RevealInFinder(dir);
        }

        static void Header(string text)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(text, EditorStyles.boldLabel);
        }
    }
}
