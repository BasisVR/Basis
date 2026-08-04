using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisSizeFitterParityTests
    {
        public readonly struct Scenario
        {
            public readonly string Name;
            public readonly Vector2 ParentSize;
            public readonly ContentSizeFitter.FitMode HorizontalFit;
            public readonly ContentSizeFitter.FitMode VerticalFit;
            public readonly bool UseParentAsPreferredSize;
            public readonly bool ParentLayoutGroup;
            public readonly int Children;

            public Scenario(string name, Vector2 parentSize, ContentSizeFitter.FitMode horizontalFit, ContentSizeFitter.FitMode verticalFit, bool useParentAsPreferredSize, bool parentLayoutGroup, int children)
            {
                Name = name;
                ParentSize = parentSize;
                HorizontalFit = horizontalFit;
                VerticalFit = verticalFit;
                UseParentAsPreferredSize = useParentAsPreferredSize;
                ParentLayoutGroup = parentLayoutGroup;
                Children = children;
            }

            public override string ToString() => Name;
        }

        public static IEnumerable<Scenario> Scenarios()
        {
            yield return new Scenario("parent_larger_vertical", new Vector2(800, 300), ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true, false, 2);
            yield return new Scenario("parent_smaller_vertical", new Vector2(100, 300), ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true, false, 2);
            yield return new Scenario("parent_larger_horizontal", new Vector2(800, 300), ContentSizeFitter.FitMode.PreferredSize, ContentSizeFitter.FitMode.Unconstrained, true, false, 2);
            yield return new Scenario("both_axes_preferred", new Vector2(800, 300), ContentSizeFitter.FitMode.PreferredSize, ContentSizeFitter.FitMode.PreferredSize, true, false, 2);
            yield return new Scenario("flag_off_plain_fitter", new Vector2(800, 300), ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, false, false, 2);
            yield return new Scenario("under_layout_group_parent", new Vector2(800, 300), ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true, true, 2);
            yield return new Scenario("no_children", new Vector2(800, 300), ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true, false, 0);
            yield return new Scenario("min_size_vertical", new Vector2(800, 300), ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.MinSize, true, false, 2);
            yield return new Scenario("many_children", new Vector2(800, 300), ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize, true, false, 12);
        }

        private readonly List<GameObject> _roots = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject root in _roots)
            {
                if (root)
                {
                    Object.DestroyImmediate(root);
                }
            }
            _roots.Clear();
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        public void FinalSize_MatchesStockFitterPlusParentWidthFloor(Scenario scenario)
        {
            RectTransform stockRoot = BuildTree(scenario, true, out RectTransform stockContent);
            RectTransform basisRoot = BuildTree(scenario, false, out RectTransform basisContent);

            Settle(stockRoot, stockContent);
            Settle(basisRoot, basisContent);

            float parentWidth = scenario.ParentSize.x;
            bool floorsWidth = scenario.UseParentAsPreferredSize && scenario.HorizontalFit == ContentSizeFitter.FitMode.PreferredSize;
            bool floorsHeight = scenario.UseParentAsPreferredSize && scenario.VerticalFit == ContentSizeFitter.FitMode.PreferredSize;

            float expectedWidth = floorsWidth ? Mathf.Max(stockContent.rect.width, parentWidth) : stockContent.rect.width;
            float expectedHeight = floorsHeight ? Mathf.Max(stockContent.rect.height, parentWidth) : stockContent.rect.height;

            Assert.That(basisContent.rect.width, Is.EqualTo(expectedWidth).Within(0.01f),
                $"{scenario.Name}: width diverged from legacy behaviour (stock fitter {stockContent.rect.width}, parent width {parentWidth})");
            Assert.That(basisContent.rect.height, Is.EqualTo(expectedHeight).Within(0.01f),
                $"{scenario.Name}: height diverged from legacy behaviour (stock fitter {stockContent.rect.height}, parent width {parentWidth})");

            int childCount = basisContent.childCount;
            Assert.That(childCount, Is.EqualTo(stockContent.childCount));
            for (int i = 0; i < childCount; i++)
            {
                RectTransform basisChild = (RectTransform)basisContent.GetChild(i);
                RectTransform stockChild = (RectTransform)stockContent.GetChild(i);
                Assert.That(basisChild.anchoredPosition.x, Is.EqualTo(stockChild.anchoredPosition.x).Within(0.01f), $"{scenario.Name}: child {i} x drifted");
                Assert.That(basisChild.anchoredPosition.y, Is.EqualTo(stockChild.anchoredPosition.y).Within(0.01f), $"{scenario.Name}: child {i} y drifted");
                Assert.That(basisChild.rect.size.x, Is.EqualTo(stockChild.rect.size.x).Within(0.01f), $"{scenario.Name}: child {i} width drifted");
                Assert.That(basisChild.rect.size.y, Is.EqualTo(stockChild.rect.size.y).Within(0.01f), $"{scenario.Name}: child {i} height drifted");
            }
        }

        [Test]
        [TestCaseSource(nameof(Scenarios))]
        public void NoOpRebuildAfterSettle_WritesNoSizes(Scenario scenario)
        {
            RectTransform basisRoot = BuildTree(scenario, false, out RectTransform basisContent);
            Settle(basisRoot, basisContent);

            BasisUIResizeProbe.AttachToTree(basisRoot);
            BasisUIResizeProbe.ResetTree(basisRoot);
            Settle(basisRoot, basisContent);

            Assert.That(BasisUIResizeProbe.TotalResizes(basisRoot), Is.EqualTo(0),
                $"{scenario.Name}: a rebuild with no content change re-wrote sizes somewhere in the tree");
        }

        private RectTransform BuildTree(Scenario scenario, bool stockFitter, out RectTransform content)
        {
            GameObject canvasGO = new GameObject("Root", typeof(Canvas));
            _roots.Add(canvasGO);
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = (RectTransform)canvasGO.transform;
            canvasRect.sizeDelta = new Vector2(1920, 1080);

            GameObject parentGO = new GameObject("Parent", typeof(RectTransform));
            RectTransform parent = (RectTransform)parentGO.transform;
            parent.SetParent(canvasRect, false);
            parent.sizeDelta = scenario.ParentSize;
            if (scenario.ParentLayoutGroup)
            {
                VerticalLayoutGroup parentGroup = parentGO.AddComponent<VerticalLayoutGroup>();
                parentGroup.childControlWidth = false;
                parentGroup.childControlHeight = false;
                parentGroup.childForceExpandWidth = false;
                parentGroup.childForceExpandHeight = false;
            }

            GameObject contentGO = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup));
            content = (RectTransform)contentGO.transform;
            content.SetParent(parent, false);

            VerticalLayoutGroup group = contentGO.GetComponent<VerticalLayoutGroup>();
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            group.spacing = 0;
            group.padding = new RectOffset(0, 0, 0, 0);
            group.childAlignment = TextAnchor.UpperLeft;

            ContentSizeFitter fitter;
            if (stockFitter)
            {
                fitter = contentGO.AddComponent<ContentSizeFitter>();
            }
            else
            {
                BasisSizeFitter basisFitter = contentGO.AddComponent<BasisSizeFitter>();
                basisFitter.UseParentAsPreferredSize = scenario.UseParentAsPreferredSize;
                fitter = basisFitter;
            }
            fitter.horizontalFit = scenario.HorizontalFit;
            fitter.verticalFit = scenario.VerticalFit;

            for (int i = 0; i < scenario.Children; i++)
            {
                GameObject child = new GameObject("Child" + i, typeof(RectTransform), typeof(LayoutElement));
                child.transform.SetParent(content, false);
                LayoutElement element = child.GetComponent<LayoutElement>();
                element.preferredHeight = 100f;
                element.preferredWidth = 120f;
                element.minHeight = 40f;
                element.minWidth = 60f;
            }

            return canvasRect;
        }

        private static void Settle(RectTransform root, RectTransform content)
        {
            RectTransform parent = (RectTransform)content.parent;
            RectTransform target = parent.TryGetComponent(out ILayoutController _) ? parent : content;
            for (int i = 0; i < 5; i++)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(target);
            }
        }
    }
}
