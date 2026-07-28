using System;
using System.IO;
using Basis.Scripts.Networking.Voice.Testing;
using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.Networking.Voice.EditorTools
{
    /// <summary>
    /// Scores a LIVE end-to-end voice capture with the same metrics as the offline sim:
    /// play a known WAV through the real client (e.g. BasisAudioClipPlayer sends it on the
    /// voice channel), record what a remote client hears (voice-recording decoded-PCM tap),
    /// then load both files here. Reports latency, underrun notches ("bubbling"), segmental
    /// SNR, and dropped audio — so live-server results compare directly against sim results.
    /// </summary>
    public class BasisVoiceCaptureAnalyzerWindow : EditorWindow
    {
        string _referencePath = "";
        string _capturePath = "";
        string _report = "";

        [MenuItem("Basis/Debug/Voice Capture Analyzer")]
        public static void ShowWindow()
        {
            var window = GetWindow<BasisVoiceCaptureAnalyzerWindow>();
            window.titleContent = new GUIContent("Voice Capture Analyzer");
            window.minSize = new Vector2(460, 300);
            window.Show();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Compare what a remote client HEARD against what was SENT. Reference = the WAV " +
                "played into the voice channel; Capture = the remote client's voice recording of " +
                "that speaker. Both mono/stereo, 16-bit or float WAV.",
                MessageType.Info);

            DrawFileRow("Reference WAV (sent)", ref _referencePath);
            DrawFileRow("Captured WAV (heard)", ref _capturePath);

            using (new EditorGUI.DisabledScope(!File.Exists(_referencePath) || !File.Exists(_capturePath)))
            {
                if (GUILayout.Button("Analyze", GUILayout.Height(26)))
                    Analyze();
            }

            if (!string.IsNullOrEmpty(_report))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.TextArea(_report, GUILayout.ExpandHeight(true));
            }
        }

        void DrawFileRow(string label, ref string path)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                path = EditorGUILayout.TextField(label, path);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFilePanel(label, Path.GetDirectoryName(path), "wav");
                    if (!string.IsNullOrEmpty(picked)) path = picked;
                    GUI.FocusControl(null);
                }
            }
        }

        void Analyze()
        {
            try
            {
                float[] reference = BasisVoiceQualityAnalysis.ReadWavMono(_referencePath, out int refRate);
                float[] capture = BasisVoiceQualityAnalysis.ReadWavMono(_capturePath, out int capRate);

                double lagMs = BasisVoiceQualityAnalysis.EstimateLagMs(reference, refRate, capture, capRate, 3000.0);
                var notches = BasisVoiceQualityAnalysis.FindNotches(capture, capRate);
                double notchMs = 0;
                foreach (var n in notches) notchMs += n.DurationMs;

                double snr = double.NaN;
                double droppedMs = 0;
                if (refRate == capRate)
                {
                    int lag = BasisVoiceQualityAnalysis.SampleAlign(reference, capture, capRate, 3000.0);
                    snr = BasisVoiceQualityAnalysis.MedianSegmentalSnrDb(reference, capture, capRate, lag);
                    droppedMs = BasisVoiceQualityAnalysis.DroppedAudioMs(reference, capture, capRate, lag);
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Reference: {reference.Length / (double)refRate:F2}s @ {refRate} Hz");
                sb.AppendLine($"Capture:   {capture.Length / (double)capRate:F2}s @ {capRate} Hz");
                sb.AppendLine($"End-to-end latency (envelope xcorr): {(lagMs < 0 ? "n/a (no energy)" : lagMs.ToString("F0") + " ms")}");
                sb.AppendLine($"Underrun notches ('bubbles'): {notches.Count}  ({notchMs:F1} ms total)");
                if (notches.Count > 0)
                {
                    sb.AppendLine("  first 10:");
                    for (int i = 0; i < Math.Min(10, notches.Count); i++)
                        sb.AppendLine($"    @{notches[i].StartMs / 1000.0:F2}s  {notches[i].DurationMs:F1} ms");
                }
                sb.AppendLine(refRate == capRate
                    ? $"Median segmental SNR vs reference: {snr:F1} dB (codec-only is typically 8-20 dB; compare against a clean local capture)"
                    : "Segmental SNR skipped: sample rates differ (resample one side to match).");
                if (refRate == capRate)
                    sb.AppendLine($"Dropped audio (reference loud, capture silent): {droppedMs:F0} ms");
                _report = sb.ToString();
            }
            catch (Exception ex)
            {
                _report = $"Analysis failed: {ex.Message}";
            }
        }
    }
}
