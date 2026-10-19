using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisBottleneckHintPagesTests
    {
        private readonly List<GameObject> _roots = new List<GameObject>();
        private RectTransform _host;

        [SetUp]
        public void SetUp()
        {
            BasisAddressablePrefabCache.ResetForTests(path => AssetDatabase.LoadAssetAtPath<GameObject>(path));
            SettingsProviderBottleneckHints.Release(null);

            GameObject canvasObject = new GameObject("CanvasRoot", typeof(Canvas));
            _roots.Add(canvasObject);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.sizeDelta = new Vector2(1920, 1080);

            _host = (RectTransform)new GameObject("Host", typeof(RectTransform)).transform;
            _host.SetParent(canvasRect, false);
            _host.sizeDelta = new Vector2(1200, 1000);
        }

        [TearDown]
        public void TearDown()
        {
            SettingsProviderBottleneckHints.Release(null);
            BasisAddressablePrefabCache.ResetForTests(null);
            for (int Index = 0; Index < _roots.Count; Index++)
            {
                if (_roots[Index]) Object.DestroyImmediate(_roots[Index]);
            }
            _roots.Clear();
        }

        [Test]
        public void ASecondPageIsHintedWithoutDroppingTheFirst()
        {
            PanelElementDescriptor graphics = Page(BasisSettingsDefaults.UseMaxVisibleAvatars);
            PanelElementDescriptor chat = Page(BasisSettingsDefaults.NPEnabled);

            SettingsProviderBottleneckHints.Bind(graphics);
            Assert.That(HintCount(), Is.EqualTo(1));

            SettingsProviderBottleneckHints.Bind(chat);
            Assert.That(HintCount(), Is.EqualTo(2), "binding the Chat & UI page must keep the Graphics hints");

            SettingsProviderBottleneckHints.Bind(chat);
            Assert.That(HintCount(), Is.EqualTo(2), "binding a page again replaces its own hints");

            SettingsProviderBottleneckHints.Release(graphics);
            Assert.That(HintCount(), Is.EqualTo(1));

            SettingsProviderBottleneckHints.Release(chat);
            Assert.That(HintCount(), Is.EqualTo(0));
        }

        [Test]
        public void ReleasingAPageThatWasNeverBoundChangesNothing()
        {
            PanelElementDescriptor graphics = Page(BasisSettingsDefaults.UseMaxVisibleAvatars);
            PanelElementDescriptor other = Page(BasisSettingsDefaults.NPEnabled);

            SettingsProviderBottleneckHints.Bind(graphics);
            SettingsProviderBottleneckHints.Release(other);

            Assert.That(HintCount(), Is.EqualTo(1));
        }

        private PanelElementDescriptor Page(BasisSettingsBinding<bool> binding)
        {
            PanelTabPage page = PanelTabPage.CreateVertical(_host);
            Assert.That(page, Is.Not.Null, "the tab page prefab did not load");
            PanelToggle toggle = PanelToggle.CreateNewEntry(page.Descriptor.ContentParent);
            toggle.Descriptor.SetTitle(binding.BindingKey);
            toggle.AssignBinding(binding);
            return page.Descriptor;
        }

        private static int HintCount()
        {
            FieldInfo lit = typeof(SettingsProviderBottleneckHints).GetField("Lit", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(lit, Is.Not.Null, "SettingsProviderBottleneckHints.Lit was renamed; this test needs updating.");
            return ((ICollection)lit.GetValue(null)).Count;
        }
    }
}
