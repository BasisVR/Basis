using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisSizeFitterCharacterizationTests
    {
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

        private RectTransform CreateCanvas()
        {
            GameObject go = new GameObject("CanvasRoot", typeof(Canvas));
            _roots.Add(go);
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            RectTransform rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(1920, 1080);
            return rect;
        }

        private static RectTransform CreateRect(string name, RectTransform parent, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = size;
            return rect;
        }

        private static BasisSizeFitter AddFitterContent(RectTransform parent, int childCount, float childPreferredHeight, out RectTransform content)
        {
            GameObject go = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(BasisSizeFitter));
            content = (RectTransform)go.transform;
            content.SetParent(parent, false);

            VerticalLayoutGroup group = go.GetComponent<VerticalLayoutGroup>();
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            group.spacing = 0;
            group.padding = new RectOffset(0, 0, 0, 0);

            BasisSizeFitter fitter = go.GetComponent<BasisSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            for (int i = 0; i < childCount; i++)
            {
                GameObject child = new GameObject("Child" + i, typeof(RectTransform), typeof(LayoutElement));
                child.transform.SetParent(content, false);
                LayoutElement element = child.GetComponent<LayoutElement>();
                element.preferredHeight = childPreferredHeight;
                element.preferredWidth = 120f;
            }

            return fitter;
        }

        private static void Rebuild(RectTransform rect, int passes = 3)
        {
            for (int i = 0; i < passes; i++)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            }
        }

        [Test]
        public void ResizeProbe_CountsSizeChanges()
        {
            RectTransform canvas = CreateCanvas();
            RectTransform rect = CreateRect("Probe", canvas, new Vector2(100, 100));
            BasisUIResizeProbe probe = rect.gameObject.AddComponent<BasisUIResizeProbe>();
            probe.Resizes = 0;
            rect.sizeDelta = new Vector2(200, 100);
            Assert.That(probe.Resizes, Is.GreaterThan(0), "OnRectTransformDimensionsChange did not fire; size-change assertions in this fixture would pass vacuously");
        }

        [Test]
        public void VerticalPreferredFit_FloorsHeightAtParentWidth_NotParentHeight()
        {
            RectTransform canvas = CreateCanvas();
            RectTransform parent = CreateRect("Parent", canvas, new Vector2(800, 300));
            AddFitterContent(parent, 2, 100f, out RectTransform content);
            Rebuild(content);
            Assert.That(content.rect.height, Is.EqualTo(800f).Within(0.5f));
        }

        [Test]
        public void HorizontalPreferredFit_FloorsWidthAtParentWidth()
        {
            RectTransform canvas = CreateCanvas();
            RectTransform parent = CreateRect("Parent", canvas, new Vector2(800, 300));
            BasisSizeFitter fitter = AddFitterContent(parent, 2, 100f, out RectTransform content);
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            Rebuild(content);
            Assert.That(content.rect.width, Is.EqualTo(800f).Within(0.5f));
        }

        [Test]
        public void UnderLayoutGroupParent_TheParentWidthFloorStillApplies()
        {
            RectTransform canvas = CreateCanvas();
            RectTransform parent = CreateRect("Parent", canvas, new Vector2(800, 300));
            VerticalLayoutGroup parentGroup = parent.gameObject.AddComponent<VerticalLayoutGroup>();
            parentGroup.childControlWidth = false;
            parentGroup.childControlHeight = false;
            parentGroup.childForceExpandWidth = false;
            parentGroup.childForceExpandHeight = false;
            AddFitterContent(parent, 2, 100f, out RectTransform content);
            Rebuild(parent);
            Assert.That(content.rect.height, Is.EqualTo(800f).Within(0.5f));
        }

        [Test]
        public void ParentSmallerThanContent_ContentKeepsItsPreferredHeight()
        {
            RectTransform canvas = CreateCanvas();
            RectTransform parent = CreateRect("Parent", canvas, new Vector2(100, 300));
            AddFitterContent(parent, 2, 100f, out RectTransform content);
            Rebuild(content);
            Assert.That(content.rect.height, Is.EqualTo(200f).Within(0.5f));
        }

        [Test]
        public void UseParentAsPreferredSizeOff_BehavesAsPlainPreferredFit()
        {
            RectTransform canvas = CreateCanvas();
            RectTransform parent = CreateRect("Parent", canvas, new Vector2(800, 300));
            BasisSizeFitter fitter = AddFitterContent(parent, 2, 100f, out RectTransform content);
            fitter.UseParentAsPreferredSize = false;
            Rebuild(content);
            Assert.That(content.rect.height, Is.EqualTo(200f).Within(0.5f));
        }

        [Test]
        public void NoOpRebuildAfterSettle_DoesNotResizeTheContent()
        {
            RectTransform canvas = CreateCanvas();
            RectTransform parent = CreateRect("Parent", canvas, new Vector2(800, 300));
            AddFitterContent(parent, 2, 100f, out RectTransform content);
            Rebuild(content);

            BasisUIResizeProbe probe = content.gameObject.AddComponent<BasisUIResizeProbe>();
            probe.Resizes = 0;
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);

            Assert.That(probe.Resizes, Is.EqualTo(0), "a rebuild with no content change re-wrote the fitter size; every such write re-dirties the canvas and can loop through DelayedSetDirty");
        }

        [Test]
        public void SettledHeight_IsStableAcrossRepeatedRebuilds()
        {
            RectTransform canvas = CreateCanvas();
            RectTransform parent = CreateRect("Parent", canvas, new Vector2(800, 300));
            AddFitterContent(parent, 2, 100f, out RectTransform content);
            Rebuild(content);
            float settled = content.rect.height;
            Rebuild(content, 5);
            Assert.That(content.rect.height, Is.EqualTo(settled).Within(0.01f));
        }
    }
}
