using System;
using System.IO;
using Basis.Editor.Localization;
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
    public sealed class BasisVoiceCaptureTab : BasisEditorTabPage
    {
        public override string Title => BasisEditorLocalization.Get("editor.voice.capture.title");
        public override string Subtitle =>
            BasisEditorLocalization.Get("editor.voice.capture.subtitle");

        string _referencePath = "";
        string _capturePath = "";
        string _report = "";

        public override void Draw()
        {
            EditorGUILayout.HelpBox(
                BasisEditorLocalization.Get("editor.voice.capture.help"),
                MessageType.Info);

            DrawFileRow(BasisEditorLocalization.Get("editor.voice.capture.referenceWav"), ref _referencePath);
            DrawFileRow(BasisEditorLocalization.Get("editor.voice.capture.capturedWav"), ref _capturePath);

            using (new EditorGUI.DisabledScope(!File.Exists(_referencePath) || !File.Exists(_capturePath)))
            {
                if (BasisEditorUI.PrimaryButton(BasisEditorLocalization.Get("editor.voice.capture.analyze"), 26f))
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
                sb.AppendLine(BasisEditorLocalization.Get("editor.voice.capture.reportReference", reference.Length / (double)refRate, refRate));
                sb.AppendLine(BasisEditorLocalization.Get("editor.voice.capture.reportCapture", capture.Length / (double)capRate, capRate));
                sb.AppendLine(BasisEditorLocalization.Get("editor.voice.capture.reportLatency", lagMs < 0 ? BasisEditorLocalization.Get("editor.voice.capture.noEnergy") : lagMs.ToString("F0") + " ms"));
                sb.AppendLine(BasisEditorLocalization.Get("editor.voice.capture.reportNotches", notches.Count, notchMs));
                if (notches.Count > 0)
                {
                    sb.AppendLine("  " + BasisEditorLocalization.Get("editor.voice.capture.reportFirst"));
                    for (int i = 0; i < Math.Min(10, notches.Count); i++)
                        sb.AppendLine($"    @{notches[i].StartMs / 1000.0:F2}s  {notches[i].DurationMs:F1} ms");
                }
                sb.AppendLine(refRate == capRate
                    ? BasisEditorLocalization.Get("editor.voice.capture.reportSnr", snr)
                    : BasisEditorLocalization.Get("editor.voice.capture.reportSnrSkipped"));
                if (refRate == capRate)
                    sb.AppendLine(BasisEditorLocalization.Get("editor.voice.capture.reportDropped", droppedMs));
                _report = sb.ToString();
            }
            catch (Exception ex)
            {
                _report = BasisEditorLocalization.Get("editor.voice.capture.reportFailed", ex.Message);
            }
        }
    }
}
