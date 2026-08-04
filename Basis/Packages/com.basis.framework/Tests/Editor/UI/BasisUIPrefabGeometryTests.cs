using System;
using System.Collections.Generic;
using System.IO;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisUIPrefabGeometryTests
    {
        private const float Tolerance = 0.5f;
        private const int MaxSettlePasses = 10;
        private const string GoldenWriteEnvVar = "BASIS_UI_GOLDEN_WRITE";
        private const string GoldenDirectory = "Packages/com.basis.framework/Tests/Editor/UI/Goldens";
        private const string ElementRoot = "Packages/com.basis.sdk/Prefabs/Panel Elements/";
        private const string PanelRoot = "Packages/com.basis.sdk/Prefabs/";

        private readonly List<GameObject> _roots = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject root in _roots)
            {
                if (root)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
            _roots.Clear();
        }

        public readonly struct PrefabCase
        {
            public readonly string Key;
            public readonly string Path;
            public readonly float HostWidth;

            public PrefabCase(string key, string path, float hostWidth)
            {
                Key = key;
                Path = path;
                HostWidth = hostWidth;
            }

            public override string ToString() => Key;
        }

        public static IEnumerable<PrefabCase> Cases()
        {
            yield return new PrefabCase("panel_element_base_w1200", ElementRoot + "Panel Element Base.prefab", 1200f);
            yield return new PrefabCase("panel_element_base_entry_w1200", ElementRoot + "Panel Element Base - Entry Variant.prefab", 1200f);
            yield return new PrefabCase("pe_button_w1200", ElementRoot + "PE Button.prefab", 1200f);
            yield return new PrefabCase("pe_button_hotbar_w1200", ElementRoot + "PE Button - Hotbar Variant.prefab", 1200f);
            yield return new PrefabCase("pe_toggle_entry_w1200", ElementRoot + "PE Toggle - Entry Variant.prefab", 1200f);
            yield return new PrefabCase("pe_slider_entry_w1200", ElementRoot + "PE Slider - Entry Variant.prefab", 1200f);
            yield return new PrefabCase("pe_dropdown_entry_w1200", ElementRoot + "PE Dropdown - Entry Variant.prefab", 1200f);
            yield return new PrefabCase("pe_text_field_entry_w1200", ElementRoot + "PE Text Field - Entry Variant.prefab", 1200f);
            yield return new PrefabCase("pe_label_field_w1200", ElementRoot + "PE Label Field.prefab", 1200f);
            yield return new PrefabCase("pe_section_toggle_entry_w1200", ElementRoot + "PE Section Toggle - Entry Variant.prefab", 1200f);
            yield return new PrefabCase("scroll_view_vertical_w1200", ElementRoot + "Scroll View Vertical.prefab", 1200f);
            yield return new PrefabCase("scroll_view_horizontal_w1200", ElementRoot + "Scroll View Horizontal.prefab", 1200f);
            yield return new PrefabCase("tab_page_w1200", ElementRoot + "Tab Page.prefab", 1200f);
            yield return new PrefabCase("tab_group_vertical_w1200", ElementRoot + "Tab Group Vertical.prefab", 1200f);
            yield return new PrefabCase("panel_element_base_w350", ElementRoot + "Panel Element Base.prefab", 350f);
            yield return new PrefabCase("pe_button_w350", ElementRoot + "PE Button.prefab", 350f);
            yield return new PrefabCase("pe_dropdown_entry_w350", ElementRoot + "PE Dropdown - Entry Variant.prefab", 350f);
            yield return new PrefabCase("pe_label_field_w350", ElementRoot + "PE Label Field.prefab", 350f);
            yield return new PrefabCase("menu_panel_standalone", PanelRoot + "Menu Panel.prefab", -1f);
            yield return new PrefabCase("menu_panel_page_standalone", PanelRoot + "Menu Panel - Page.prefab", -1f);
        }

        [Test]
        [TestCaseSource(nameof(Cases))]
        public void PrefabGeometry_MatchesGolden(PrefabCase testCase)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(testCase.Path);
            Assert.That(prefab, Is.Not.Null, $"prefab not found: {testCase.Path}");

            RectTransform observedRoot = BuildAndSettle(prefab, testCase.HostWidth, out int settlePasses, out int noOpResizes);
            BasisUILayoutSnapshot snapshot = BasisUILayoutSnapshot.Capture(observedRoot);
            snapshot.SettlePasses = settlePasses;
            snapshot.NoOpResizes = noOpResizes;

            string goldenPath = Path.Combine(Path.GetFullPath(GoldenDirectory), testCase.Key + ".json");

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GoldenWriteEnvVar)))
            {
                Directory.CreateDirectory(Path.GetFullPath(GoldenDirectory));
                File.WriteAllText(goldenPath, snapshot.ToJson());
                Assert.Pass($"golden written: {goldenPath}");
            }

            Assert.That(File.Exists(goldenPath), Is.True,
                $"no golden for {testCase.Key}; run once with {GoldenWriteEnvVar}=1 to record it");

            BasisUILayoutSnapshot golden = BasisUILayoutSnapshot.FromJson(File.ReadAllText(goldenPath));
            List<string> problems = golden.Diff(snapshot, Tolerance);
            Assert.That(problems, Is.Empty,
                $"{testCase.Key} geometry drifted from golden:\n{string.Join("\n", problems)}");
            Assert.That(settlePasses, Is.LessThanOrEqualTo(golden.SettlePasses),
                $"{testCase.Key} takes more layout passes to settle than the golden ({settlePasses} > {golden.SettlePasses})");
            Assert.That(noOpResizes, Is.LessThanOrEqualTo(golden.NoOpResizes),
                $"{testCase.Key} re-writes sizes on a no-op rebuild more than the golden ({noOpResizes} > {golden.NoOpResizes})");
        }

        private RectTransform BuildAndSettle(GameObject prefab, float hostWidth, out int settlePasses, out int noOpResizes)
        {
            RectTransform rebuildTarget;
            RectTransform observedRoot;

            if (hostWidth > 0f)
            {
                GameObject canvasGO = new GameObject("CanvasRoot", typeof(Canvas));
                _roots.Add(canvasGO);
                canvasGO.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                RectTransform canvasRect = (RectTransform)canvasGO.transform;
                canvasRect.sizeDelta = new Vector2(1920, 1080);

                GameObject hostGO = new GameObject("Host", typeof(RectTransform), typeof(VerticalLayoutGroup));
                RectTransform host = (RectTransform)hostGO.transform;
                host.SetParent(canvasRect, false);
                host.sizeDelta = new Vector2(hostWidth, 2000f);

                VerticalLayoutGroup group = hostGO.GetComponent<VerticalLayoutGroup>();
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = true;
                group.childForceExpandHeight = false;
                group.spacing = 0;
                group.padding = new RectOffset(0, 0, 0, 0);
                group.childAlignment = TextAnchor.UpperLeft;

                GameObject instance = UnityEngine.Object.Instantiate(prefab, host, false);
                instance.name = prefab.name;

                rebuildTarget = host;
                observedRoot = host;
            }
            else
            {
                GameObject instance = UnityEngine.Object.Instantiate(prefab);
                instance.name = prefab.name;
                _roots.Add(instance);
                rebuildTarget = (RectTransform)instance.transform;
                observedRoot = rebuildTarget;
            }

            settlePasses = MaxSettlePasses;
            BasisUILayoutSnapshot previous = null;
            for (int pass = 1; pass <= MaxSettlePasses; pass++)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rebuildTarget);
                BasisUILayoutSnapshot current = BasisUILayoutSnapshot.Capture(observedRoot);
                if (previous != null && previous.Diff(current, 0.01f).Count == 0)
                {
                    settlePasses = pass;
                    break;
                }
                previous = current;
            }

            BasisUIResizeProbe.AttachToTree(observedRoot);
            BasisUIResizeProbe.ResetTree(observedRoot);
            LayoutRebuilder.ForceRebuildLayoutImmediate(rebuildTarget);
            noOpResizes = BasisUIResizeProbe.TotalResizes(observedRoot);

            return observedRoot;
        }
    }
}
