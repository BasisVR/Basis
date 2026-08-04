using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisUIHotbarOverflowTests
    {
        private const string ScrollViewPath = "Packages/com.basis.sdk/Prefabs/Panel Elements/Scroll View Horizontal.prefab";
        private const string HotbarButtonPath = "Packages/com.basis.sdk/Prefabs/Panel Elements/PE Button - Hotbar Variant.prefab";
        private const float BarWidth = 1500f;
        private const float BarHeight = 200f;
        private const float Spacing = 15f;

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

        private (RectTransform viewport, RectTransform content, ScrollRect scrollRect) BuildBar(int buttonCount)
        {
            GameObject canvasGO = new GameObject("CanvasRoot", typeof(Canvas));
            _roots.Add(canvasGO);
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = (RectTransform)canvasGO.transform;
            canvasRect.sizeDelta = new Vector2(1920, 1080);

            GameObject barGO = new GameObject("Bar", typeof(RectTransform));
            RectTransform bar = (RectTransform)barGO.transform;
            bar.SetParent(canvasRect, false);
            bar.sizeDelta = new Vector2(BarWidth, BarHeight);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScrollViewPath);
            Assert.That(prefab, Is.Not.Null, $"prefab not found: {ScrollViewPath}");
            GameObject scroll = Object.Instantiate(prefab, bar, false);
            ScrollRect scrollRect = scroll.GetComponent<ScrollRect>();
            RectTransform viewport = (RectTransform)scroll.transform.Find("Viewport");
            RectTransform content = (RectTransform)scroll.transform.Find("Viewport/Content");
            Assert.That(scrollRect, Is.Not.Null);
            Assert.That(viewport, Is.Not.Null);
            Assert.That(content, Is.Not.Null);

            GameObject buttonPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HotbarButtonPath);
            Assert.That(buttonPrefab, Is.Not.Null, $"prefab not found: {HotbarButtonPath}");
            for (int i = 0; i < buttonCount; i++)
            {
                Object.Instantiate(buttonPrefab, content, false);
            }

            for (int i = 0; i < 5; i++)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            }

            return (viewport, content, scrollRect);
        }

        private static float MeasuredRowWidth(RectTransform content)
        {
            float sum = 0f;
            for (int i = 0; i < content.childCount; i++)
            {
                sum += LayoutUtility.GetPreferredSize((RectTransform)content.GetChild(i), 0);
            }
            if (content.childCount > 1)
            {
                sum += Spacing * (content.childCount - 1);
            }
            return sum;
        }

        [Test]
        public void FewButtons_RowIsFlooredToTheBarWidth()
        {
            (RectTransform viewport, RectTransform content, _) = BuildBar(6);
            Assert.That(viewport.rect.width, Is.EqualTo(BarWidth).Within(0.5f));
            Assert.That(content.rect.width, Is.EqualTo(viewport.rect.width).Within(0.5f));
        }

        [Test]
        public void FewButtons_ButtonsStretchToShareTheBarEvenly()
        {
            (_, RectTransform content, _) = BuildBar(6);
            float expected = (BarWidth - Spacing * 5f) / 6f;
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform button = (RectTransform)content.GetChild(i);
                Assert.That(button.rect.width, Is.EqualTo(expected).Within(1f), $"button {i} width");
            }
        }

        [Test]
        public void ManyButtons_RowGrowsPastTheBar_InsteadOfSquishingTheButtons()
        {
            (RectTransform viewport, RectTransform content, _) = BuildBar(20);
            float measured = MeasuredRowWidth(content);
            Assert.That(measured, Is.GreaterThan(viewport.rect.width),
                "20 hotbar buttons no longer outgrow the bar; the min width that makes overflow possible is gone");
            Assert.That(content.rect.width, Is.EqualTo(measured).Within(1f));

            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform button = (RectTransform)content.GetChild(i);
                float preferred = LayoutUtility.GetPreferredSize(button, 0);
                Assert.That(button.rect.width, Is.EqualTo(preferred).Within(0.5f), $"button {i} was squeezed below its preferred width");
                Assert.That(button.rect.width, Is.GreaterThanOrEqualTo(99.5f), $"button {i} collapsed below the hotbar minimum width");
            }
        }

        [Test]
        public void ManyButtons_LayOutSequentially_WithoutOverlap()
        {
            (_, RectTransform content, _) = BuildBar(20);
            Vector3[] corners = new Vector3[4];
            float previousRightEdge = float.NegativeInfinity;
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform button = (RectTransform)content.GetChild(i);
                button.GetWorldCorners(corners);
                Assert.That(corners[0].x, Is.GreaterThanOrEqualTo(previousRightEdge - 0.01f), $"button {i} overlaps the one before it");
                previousRightEdge = corners[3].x;
            }
        }

        [Test]
        public void ManyButtons_RowStartsAtTheBarsLeftEdge()
        {
            (RectTransform viewport, RectTransform content, _) = BuildBar(20);
            Vector3[] viewportCorners = new Vector3[4];
            Vector3[] contentCorners = new Vector3[4];
            viewport.GetWorldCorners(viewportCorners);
            content.GetWorldCorners(contentCorners);
            Assert.That(contentCorners[0].x, Is.EqualTo(viewportCorners[0].x).Within(0.01f),
                "an overflowing hotbar row should start at the left edge and scroll rightward, not spill past both edges");
        }

        [Test]
        public void TheHorizontalScrollView_ScrollsHorizontally()
        {
            (_, _, ScrollRect scrollRect) = BuildBar(20);
            Assert.That(scrollRect.horizontal, Is.True, "Scroll View Horizontal must scroll horizontally");
            Assert.That(scrollRect.vertical, Is.False, "Scroll View Horizontal must not scroll vertically");
        }

        [Test]
        public void TheViewport_MasksOverflowingButtons()
        {
            (RectTransform viewport, _, _) = BuildBar(20);
            Assert.That(viewport.TryGetComponent(out RectMask2D _), Is.True,
                "without a viewport mask, overflowing buttons render past the bar's edges");
        }
    }
}
