using System;
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
    /// Editor front-end for <see cref="BasisSyncSimMatrix"/>. Runs the value-sync data path through
    /// every combination of field types / features / motion / network impairment, shows a pass-fail
    /// summary, and exports the full per-scenario result table as CSV. Pure offline simulation —
    /// no play mode, no live server required.
    /// </summary>
    public sealed class BasisSyncMatrixTab : BasisEditorTabPage
    {
        public override string Title => BasisEditorLocalization.Get("editor.syncTesting.matrix.title");
        public override string Subtitle =>
            BasisEditorLocalization.Get("editor.syncTesting.matrix.subtitle");

        static readonly Color Green = new Color(0.45f, 0.9f, 0.45f);
        static readonly Color Red = new Color(0.95f, 0.5f, 0.5f);
        static readonly Color Grey = new Color(0.65f, 0.65f, 0.65f);
        static readonly Color Yellow = new Color(0.95f, 0.85f, 0.35f);

        enum Scope { Quick, Full }

        Scope _scope = Scope.Quick;
        int _seeds = 1;
        bool _interpOff;
        bool _extrap = true;
        bool _teleThresh = true;
        bool _composites = true;
        bool _checksumOff;
        bool _multiObject = true;
        bool _batched = true;
        bool _realJob = true;
        bool _lateJoin = true;

        List<BasisSyncSimResult> _results;
        string _summary = "";
        int _scenarioCount = -1;
        bool _cancel;
        Vector2 _failScroll;

        public override void Draw()
        {
            Header(BasisEditorLocalization.Get("editor.syncTesting.matrix.header"));
            EditorGUILayout.HelpBox(
                BasisEditorLocalization.Get("editor.syncTesting.matrix.help"),
                MessageType.Info);

            DrawSettings();
            DrawRun();
            DrawResults();

            EditorGUILayout.EndScrollView();
        }

        void DrawSettings()
        {
            Header(BasisEditorLocalization.Get("editor.syncTesting.matrix.scope"));
            EditorGUI.BeginChangeCheck();

            _scope = (Scope)EditorGUILayout.Popup(BasisEditorLocalization.Get("editor.syncTesting.matrix.preset"), (int)_scope,
                new[] { BasisEditorLocalization.Get("editor.syncTesting.matrix.presetQuick"), BasisEditorLocalization.Get("editor.syncTesting.matrix.presetFull") });
            _seeds = EditorGUILayout.IntSlider(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.matrix.seeds"), BasisEditorLocalization.Get("editor.syncTesting.matrix.seeds.tooltip")), _seeds, 1, 5);

            using (new EditorGUI.IndentLevelScope())
            {
                _composites = EditorGUILayout.Toggle(BasisEditorLocalization.Get("editor.syncTesting.matrix.composites"), _composites);
                _interpOff = EditorGUILayout.Toggle(BasisEditorLocalization.Get("editor.syncTesting.matrix.interpOff"), _interpOff);
                _extrap = EditorGUILayout.Toggle(BasisEditorLocalization.Get("editor.syncTesting.matrix.extrap"), _extrap);
                _teleThresh = EditorGUILayout.Toggle(BasisEditorLocalization.Get("editor.syncTesting.matrix.teleThresh"), _teleThresh);
                _checksumOff = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.matrix.checksumOff"), BasisEditorLocalization.Get("editor.syncTesting.matrix.checksumOff.tooltip")), _checksumOff);
            }

            Header(BasisEditorLocalization.Get("editor.syncTesting.matrix.multiObjectHeader"));
            _multiObject = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.matrix.multiObject"), BasisEditorLocalization.Get("editor.syncTesting.matrix.multiObject.tooltip")), _multiObject);
            using (new EditorGUI.IndentLevelScope())
            using (new EditorGUI.DisabledScope(!_multiObject))
            {
                _batched = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.matrix.batched"), BasisEditorLocalization.Get("editor.syncTesting.matrix.batched.tooltip")), _batched);
                _realJob = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.matrix.realJob"), BasisEditorLocalization.Get("editor.syncTesting.matrix.realJob.tooltip")), _realJob);
                _lateJoin = EditorGUILayout.Toggle(new GUIContent(BasisEditorLocalization.Get("editor.syncTesting.matrix.lateJoin"), BasisEditorLocalization.Get("editor.syncTesting.matrix.lateJoin.tooltip")), _lateJoin);
            }

            if (EditorGUI.EndChangeCheck() || _scenarioCount < 0)
                _scenarioCount = BasisSyncSimMatrix.CountScenarios(MatrixOptionsBuilder());

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.syncTesting.matrix.scenarioCount"), _scenarioCount.ToString("N0"));
        }

        void DrawRun()
        {
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(EditorApplication.isCompiling))
            {
                if (BasisEditorUI.PrimaryButton(BasisEditorLocalization.Get("editor.syncTesting.matrix.run"), 30f))
                    RunMatrix();
            }

            using (new EditorGUI.DisabledScope(_results == null || _results.Count == 0))
            {
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.syncTesting.exportCsv")))
                    ExportCsv();
            }
        }

        void DrawResults()
        {
            if (_results == null) return;

            Header(BasisEditorLocalization.Get("editor.syncTesting.matrix.results"));
            int pass = 0, warn = 0, fail = 0;
            for (int i = 0; i < _results.Count; i++)
            {
                BasisSyncSimResult r = _results[i];
                if (!r.Pass) fail++;
                else if (r.Warn) warn++;
                else pass++;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            Colored(BasisEditorLocalization.Get("editor.syncTesting.matrix.pass", pass), fail == 0 && warn == 0 ? Green : Grey);
            Colored(BasisEditorLocalization.Get("editor.syncTesting.matrix.warn", warn), warn > 0 ? Yellow : Grey);
            Colored(BasisEditorLocalization.Get("editor.syncTesting.matrix.fail", fail), fail > 0 ? Red : Grey);
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.syncTesting.matrix.ofTotal", _results.Count), GUILayout.Width(70));
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_summary))
                EditorGUILayout.TextArea(_summary, GUILayout.MinHeight(120));

            if (fail > 0)
            {
                Header(BasisEditorLocalization.Get("editor.syncTesting.matrix.failures"));
                _failScroll = EditorGUILayout.BeginScrollView(_failScroll, GUILayout.MaxHeight(220));
                int shown = 0;
                for (int i = 0; i < _results.Count && shown < 200; i++)
                {
                    BasisSyncSimResult r = _results[i];
                    if (r.Pass) continue;
                    shown++;
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    Colored(r.ScenarioName, Red);
                    EditorGUILayout.LabelField(r.FailReason, EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.EndVertical();
                }
            }
        }

        BasisSyncSimMatrix.MatrixOptions MatrixOptionsBuilder()
        {
            BasisSyncSimMatrix.MatrixOptions o = _scope == Scope.Quick
                ? BasisSyncSimMatrix.MatrixOptions.Quick()
                : BasisSyncSimMatrix.MatrixOptions.Full();
            o.Seeds = _seeds;
            o.CompositeConfigs = _composites;
            o.InterpolateOffVariants = _interpOff;
            o.ExtrapolateVariant = _extrap;
            o.TeleportThresholdVariant = _teleThresh;
            o.ChecksumOffVariant = _checksumOff;
            o.MultiObjectScenes = _multiObject;
            o.BatchedTransport = _batched;
            o.RealInterpJob = _realJob;
            o.LateJoinVariant = _lateJoin;
            return o;
        }

        void RunMatrix()
        {
            _cancel = false;
            BasisSyncSimMatrix.MatrixOptions o = MatrixOptionsBuilder();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _results = BasisSyncSimMatrix.RunAll(o,
                    (done, total, r) =>
                    {
                        if (done % 16 == 0 || done == total)
                        {
                            if (EditorUtility.DisplayCancelableProgressBar(BasisEditorLocalization.Get("editor.syncTesting.matrix.progressTitle"),
                                $"{done}/{total}  -  {(r != null ? r.ScenarioName : "")}", total > 0 ? (float)done / total : 0f))
                                _cancel = true;
                        }
                    },
                    () => _cancel);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            watch.Stop();
            BuildSummary(watch.ElapsedMilliseconds);
        }

        void BuildSummary(long elapsedMs)
        {
            if (_results == null) return;

            var byProfile = new SortedDictionary<string, int[]>();   // name -> [pass, fail]
            var byConfig = new SortedDictionary<string, int[]>();
            var byTransport = new SortedDictionary<string, int[]>();
            int pass = 0, warn = 0, fail = 0, soft = 0;
            int demuxEntries = 0, demuxErrors = 0;

            for (int i = 0; i < _results.Count; i++)
            {
                BasisSyncSimResult r = _results[i];
                if (!r.Pass) fail++;
                else if (r.Warn) warn++;
                else pass++;
                if (r.Pass && !r.Warn && !string.IsNullOrEmpty(r.FailReason)) soft++;
                Bump(byProfile, r.ProfileName, r.Pass);
                Bump(byConfig, r.FieldConfigName, r.Pass);
                Bump(byTransport, r.Transport, r.Pass);
                demuxEntries += r.DemuxEntries;
                demuxErrors += r.DemuxErrors;
            }

            var sb = new StringBuilder();
            sb.AppendLine(BasisEditorLocalization.Get("editor.syncTesting.matrix.summaryTotals", _results.Count, elapsedMs, pass, warn, fail, soft));
            sb.AppendLine();
            sb.AppendLine(BasisEditorLocalization.Get("editor.syncTesting.matrix.summaryByProfile"));
            foreach (var kv in byProfile)
                sb.AppendLine("  " + BasisEditorLocalization.Get("editor.syncTesting.matrix.summaryRow", kv.Key, kv.Value[0], kv.Value[1]));
            sb.AppendLine();
            sb.AppendLine(BasisEditorLocalization.Get("editor.syncTesting.matrix.summaryByTransport"));
            foreach (var kv in byTransport)
                sb.AppendLine("  " + BasisEditorLocalization.Get("editor.syncTesting.matrix.summaryRow", kv.Key, kv.Value[0], kv.Value[1]));
            sb.AppendLine("  " + BasisEditorLocalization.Get("editor.syncTesting.matrix.summaryDemux", demuxEntries, demuxErrors));
            sb.AppendLine();
            sb.AppendLine(BasisEditorLocalization.Get("editor.syncTesting.matrix.summaryConfigFailures"));
            bool anyCfgFail = false;
            foreach (var kv in byConfig)
            {
                if (kv.Value[1] == 0) continue;
                anyCfgFail = true;
                sb.AppendLine("  " + BasisEditorLocalization.Get("editor.syncTesting.matrix.summaryConfigRow", kv.Key, kv.Value[0], kv.Value[1]));
            }
            if (!anyCfgFail) sb.AppendLine("  " + BasisEditorLocalization.Get("editor.syncTesting.matrix.summaryNone"));

            _summary = sb.ToString();
            Debug.Log("[BasisSyncSim] " + _summary);
        }

        static void Bump(SortedDictionary<string, int[]> map, string key, bool pass)
        {
            if (!map.TryGetValue(key, out int[] pf)) { pf = new int[2]; map[key] = pf; }
            if (pass) pf[0]++; else pf[1]++;
        }

        void ExportCsv()
        {
            string path = EditorUtility.SaveFilePanel(BasisEditorLocalization.Get("editor.syncTesting.matrix.exportTitle"), "", "BasisSyncTestResults.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, BasisSyncSimMatrix.ToCsv(_results), new UTF8Encoding(false));
            Debug.Log($"[BasisSyncSim] wrote {_results.Count} rows to {path}");
            EditorUtility.RevealInFinder(path);
        }

        static void Header(string title)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        static void Colored(string text, Color color)
        {
            Color prev = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(text, EditorStyles.boldLabel, GUILayout.Width(90));
            GUI.color = prev;
        }
    }
}
