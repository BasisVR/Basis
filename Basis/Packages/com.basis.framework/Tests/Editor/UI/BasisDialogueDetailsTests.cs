using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisDialogueDetailsTests
    {
        private const string Line = "Leaderboard.Upload: UnityWebRequest.Post(), UnityWebRequest.SendWebRequest(), DownloadHandler.text";
        private readonly List<GameObject> _roots = new List<GameObject>();
        private readonly Vector3[] _corners = new Vector3[4];
        private BasisMenuDialoguePanel _panel;

        [SetUp]
        public void SetUp()
        {
            BasisAddressablePrefabCache.ResetForTests(path => AssetDatabase.LoadAssetAtPath<GameObject>(path));
            GameObject canvasObject = new GameObject("CanvasRoot", typeof(Canvas));
            _roots.Add(canvasObject);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.sizeDelta = new Vector2(1920, 1080);
            _panel = AddressableUIInstanceBase.CreateNew<BasisMenuDialoguePanel>(BasisMenuDialoguePanel.DialogueStyles.Default, canvasRect);
            Assert.That(_panel, Is.Not.Null, "the dialogue prefab did not load");
            typeof(BasisMenuPanel).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_panel, null);
            _panel.CaptureOnClose = false;
            _panel.LoadData(BasisMenuDialoguePanel.DialoguePanelData);
            _panel.FillDialogue("Allow Script Permissions?", "Test Prop (prop) wants to do things scripts are normally blocked from doing:", "Allow", "Don't Allow");
        }

        [TearDown]
        public void TearDown()
        {
            BasisAddressablePrefabCache.ResetForTests(null);
            for (int Index = 0; Index < _roots.Count; Index++)
            {
                if (_roots[Index]) Object.DestroyImmediate(_roots[Index]);
            }
            _roots.Clear();
        }

        private ScrollRect[] Lists() => _panel.Descriptor.DescriptionLabel.rectTransform.parent.GetComponentsInChildren<ScrollRect>(false);

        [Test]
        public void ShowingDetailsAgain_ReplacesTheList()
        {
            _panel.ShowDetails(new[] { new BasisMenuDialoguePanel.DetailRow("One", "first", true), new BasisMenuDialoguePanel.DetailRow("Two", "second", true) });
            _panel.ShowDetails(new[] { new BasisMenuDialoguePanel.DetailRow("Three", "third", true) });

            ScrollRect[] lists = Lists();
            Assert.AreEqual(1, lists.Length);
            Assert.AreEqual(1, lists[0].content.childCount);
            Assert.AreEqual("Three", lists[0].content.GetChild(0).GetComponent<PanelElementDescriptor>().Title);
        }

        [Test]
        public void WrappedRows_GrowWithTheirText_StackWithoutOverlap_AndGrowTheDialogue()
        {
            _panel.ShowDetails(new[] { new BasisMenuDialoguePanel.DetailRow("Short", "first", true) });
            float before = _panel.Data.PanelSize.y;
            string body = string.Join("\n", Line, Line, Line, Line);
            _panel.ShowDetails(new[]
            {
                new BasisMenuDialoguePanel.DetailRow("Files", body, true, true),
                new BasisMenuDialoguePanel.DetailRow("Internet", body, true, true),
            });

            RectTransform content = Lists()[0].content;
            RectTransform first = (RectTransform)content.GetChild(0);
            RectTransform second = (RectTransform)content.GetChild(1);
            Assert.Greater(first.rect.height, 144f, "a wrapped row should be taller than a one-line Entry row");
            first.GetWorldCorners(_corners);
            float firstBottom = _corners[0].y;
            second.GetWorldCorners(_corners);
            Assert.GreaterOrEqual(firstBottom + 0.5f, _corners[1].y, "the second row starts inside the first");
            Assert.Greater(_panel.Data.PanelSize.y, before);
        }
    }
}
