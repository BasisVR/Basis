using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.Editor.Localization;
using Basis.Scripts.Networking.Sync.Testing;
using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.Networking.Sync.EditorTools
{
    /// <summary>
    /// Editor front-end for <see cref="BasisSyncLatencyMatrix"/>. Measures the owner-set -> remote-render
    /// latency of the real <see cref="BasisSyncReceiver"/> across send-rate / viewer-distance / jitter
    /// sweeps and exports the per-scenario table as CSV. Separate from the convergence matrix window
    /// because this is a timing/feel axis, not a pass/fail correctness axis. Pure offline simulation.
    /// </summary>
    public sealed class BasisSyncLatencyTab : BasisEditorTabPage
    {
        public override string Title => BasisEditorLocalization.Get("editor.syncTesting.latency.title");
        public override string Subtitle =>
            BasisEditorLocalization.Get("editor.syncTesting.latency.subtitle");

        static readonly Color Hot = new Color(0.95f, 0.5f, 0.5f);
        static readonly Color Warm = new Color(0.95f, 0.85f, 0.35f);
        static readonly Color Cool = new Color(0.45f, 0.9f, 0.45f);

        BasisSyncLatencyMatrix.Options _options = new BasisSyncLatencyMatrix.Options();
        List<BasisSyncLatencyResult> _results;

        public override void Draw()
        {
            EditorGUILayout.Space(6);
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.syncTesting.latency.header"));
            EditorGUILayout.HelpBox(
                BasisEditorLocalization.Get("editor.syncTesting.latency.help"),
                MessageType.Info);

            EditorGUILayout.Space(4);
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.syncTesting.latency.sweeps"));
            _options.SendRate = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.latency.sendRate")), _options.SendRate);
            _options.Distance = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.latency.distance"), BasisEditorLocalization.Get("editor.syncTesting.latency.distance.tooltip")), _options.Distance);
            _options.NoReductionControl = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.latency.noReduction"), BasisEditorLocalization.Get("editor.syncTesting.latency.noReduction.tooltip")), _options.NoReductionControl);
            _options.Jitter = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.latency.jitter")), _options.Jitter);
            _options.Extrapolate = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.latency.extrapolate")), _options.Extrapolate);
            _options.Fixes = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.latency.fixes"), BasisEditorLocalization.Get("editor.syncTesting.latency.fixes.tooltip")), _options.Fixes);
            _options.RenderHz = EditorGUILayout.Slider(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.latency.renderHz")), (float)_options.RenderHz, 30f, 144f);

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(EditorApplication.isCompiling))
                if (BasisEditorUI.PrimaryButton(BasisEditorLocalization.Get("editor.syncTesting.latency.run"), 30f))
                    _results = BasisSyncLatencyMatrix.RunAll(_options);

            using (new EditorGUI.DisabledScope(_results == null || _results.Count == 0))
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.syncTesting.exportCsv")))
                    ExportCsv();

            DrawResults();

        }

        void DrawResults()
        {
            if (_results == null || _results.Count == 0) return;

            EditorGUILayout.Space(6);
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.syncTesting.latency.results"));

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            Cell("scenario", 130, true);
            Cell("effSend", 64, true);
            Cell("mean", 60, true);
            Cell("p50", 54, true);
            Cell("p95", 54, true);
            Cell("max", 60, true);
            Cell("latσ", 54, true);
            Cell("bufMs", 60, true);
            Cell("depth", 56, true);
            Cell("sends", 54, true);
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < _results.Count; i++)
            {
                BasisSyncLatencyResult r = _results[i];
                EditorGUILayout.BeginHorizontal();
                Cell(r.Name, 130);
                Cell(r.EffSendIntervalMs.ToString("0"), 64);
                ColoredCell(r.MeanLatencyMs.ToString("0"), 60, LatencyColor(r.MeanLatencyMs));
                Cell(r.P50Ms.ToString("0"), 54);
                Cell(r.P95Ms.ToString("0"), 54);
                Cell(r.MaxLatencyMs.ToString("0"), 60);
                Cell(r.LatencyStdMs.ToString("0.0"), 54);
                Cell(r.MeanBufferMs.ToString("0"), 60);
                Cell(r.MeanDynamicDepth.ToString("0.00"), 56);
                Cell(r.Sends.ToString(), 54);
                EditorGUILayout.EndHorizontal();
            }
        }

        static Color LatencyColor(float ms) => ms >= 300f ? Hot : (ms >= 180f ? Warm : Cool);

        static void Cell(string text, float width, bool header = false)
        {
            EditorGUILayout.LabelField(text, header ? EditorStyles.miniBoldLabel : EditorStyles.miniLabel, GUILayout.Width(width));
        }

        static void ColoredCell(string text, float width, Color color)
        {
            Color prev = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel, GUILayout.Width(width));
            GUI.color = prev;
        }

        void ExportCsv()
        {
            string path = EditorUtility.SaveFilePanel(BasisEditorLocalization.Get("editor.syncTesting.latency.exportTitle"), "", "BasisSyncLatency.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, BasisSyncLatencyMatrix.ToCsv(_results), new UTF8Encoding(false));
            Debug.Log($"[BasisSyncLatency] wrote {_results.Count} rows to {path}");
            EditorUtility.RevealInFinder(path);
        }
    }
}
