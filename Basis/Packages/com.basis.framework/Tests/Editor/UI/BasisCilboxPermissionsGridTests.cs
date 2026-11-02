using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisCilboxPermissionsGridTests
    {
        private const float Tolerance = 0.5f;
        private const int SettlePasses = 8;
        private const string HashA = "grid-test-a", HashB = "grid-test-b", HashC = "grid-test-c";

        private readonly List<GameObject> _roots = new List<GameObject>();
        private readonly Vector3[] _corners = new Vector3[4];
        private RectTransform _content;
        private PanelSectionToggle _section;

        [SetUp]
        public void SetUp()
        {
            BasisAddressablePrefabCache.ResetForTests(path => AssetDatabase.LoadAssetAtPath<GameObject>(path));

            GameObject canvasObject = new GameObject("CanvasRoot", typeof(Canvas));
            _roots.Add(canvasObject);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = (RectTransform)canvasObject.transform;
            canvasRect.sizeDelta = new Vector2(1920, 1080);

            RectTransform host = (RectTransform)new GameObject("Host", typeof(RectTransform)).transform;
            host.SetParent(canvasRect, false);
            host.sizeDelta = new Vector2(1500, 1000);

            PanelTabPage page = PanelTabPage.CreateVertical(host);
            Assert.That(page, Is.Not.Null, "the tab page prefab did not load");
            _content = page.Descriptor.ContentParent;

            BasisCilboxPermissions.Allow(HashA, "Short", "Avatar", new[] { "Type:System.IO.File" }, false);
            BasisCilboxPermissions.Allow(HashB, new string('W', 300), "World", new[]
            {
                "Type:System.IO.File", "Type:UnityEngine.PlayerPrefs", "Type:System.Net.Sockets.Socket", "Type:System.Diagnostics.Process",
                "Type:UnityEngine.Application", "Type:UnityEngine.Networking.UnityWebRequest", "Type:UnityEngine.Microphone", "Type:UnityEngine.WebCamTexture",
            }, false);
            BasisCilboxPermissions.Allow(HashC, "Third", "Prop", new[] { "Type:System.IO.File" }, false);

            _section = null;
            _section = PanelSectionToggleHelpers.CreateLazyBoxedSection(_content, "Script Permissions",
                () => SettingsProviderCilboxPermissions.BuildSection(_content, page.Descriptor),
                false, _ => SettingsProviderCilboxPermissions.SettleSection(_section, _content));
            PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(_content, "Below", () => PanelToggle.CreateNewEntry(_content));

            RectTransform scroll = (RectTransform)_content.GetComponentInParent<ScrollRect>().transform;
            for (int pass = 0; pass < SettlePasses; pass++)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(scroll);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            }
        }

        [TearDown]
        public void TearDown()
        {
            BasisCilboxPermissions.Remove(HashA);
            BasisCilboxPermissions.Remove(HashB);
            BasisCilboxPermissions.Remove(HashC);
            BasisAddressablePrefabCache.ResetForTests(null);
            for (int Index = 0; Index < _roots.Count; Index++)
            {
                if (_roots[Index]) Object.DestroyImmediate(_roots[Index]);
            }
            _roots.Clear();
        }

        [Test]
        public void ApprovalsLayOutTwoPerRowInEqualColumns()
        {
            _section.SetExpanded(true);
            AssertSettled("after expanding");

            List<RectTransform> rows = Rows();
            int approvals = BasisCilboxPermissions.GetAll().Count;
            Assert.That(rows.Count, Is.EqualTo((approvals + 1) / 2));

            RectTransform firstCard = (RectTransform)rows[0].GetChild(0);
            RectTransform secondCard = (RectTransform)rows[0].GetChild(1);
            Assert.That(firstCard.rect.width, Is.GreaterThan(300f), "a card this narrow wraps its text a character at a time");
            Assert.That(secondCard.rect.width, Is.EqualTo(firstCard.rect.width).Within(Tolerance));
            Assert.That(secondCard.position.y, Is.EqualTo(firstCard.position.y).Within(Tolerance), "row mates share a row");
            Assert.That(secondCard.position.x, Is.GreaterThan(firstCard.position.x), "row mates sit side by side");
            Assert.That(secondCard.rect.height, Is.EqualTo(firstCard.rect.height).Within(Tolerance), "row mates share a height");

            RectTransform lastRow = rows[rows.Count - 1];
            Assert.That(((RectTransform)lastRow.GetChild(0)).rect.width, Is.EqualTo(firstCard.rect.width).Within(Tolerance), "a lone last card stays in its column");
        }

        [Test]
        public void LongContentStaysInsideItsCard()
        {
            _section.SetExpanded(true);
            AssertSettled("after expanding");

            foreach (RectTransform row in Rows())
            {
                for (int Index = 0; Index < row.childCount; Index++)
                {
                    PanelElementDescriptor card = row.GetChild(Index).GetComponent<PanelElementDescriptor>();
                    if (card == null) continue;

                    Assert.That(card.TitleLabel.overflowMode, Is.EqualTo(TextOverflowModes.Ellipsis));
                    Assert.That(card.DescriptionLabel.rectTransform.rect.width, Is.GreaterThan(200f));

                    RectTransform actions = (RectTransform)card.ContentParent.Find("ScriptPermissionCardActions");
                    Assert.That(actions, Is.Not.Null);
                    Assert.That(Extent(actions).y, Is.GreaterThanOrEqualTo(Extent(card.rectTransform).y - Tolerance), "the Remove button fell out of the bottom of the card");
                    Assert.That(Extent(actions).x, Is.LessThanOrEqualTo(Extent(card.DescriptionLabel.rectTransform).y + Tolerance), "the Remove button sits on top of the text");
                }
            }
        }

        [Test]
        public void RemovingAnApprovalReflowsTheGridStraightAway()
        {
            LogAssert.ignoreFailingMessages = true;
            _section.SetExpanded(true);
            AssertSettled("after expanding");

            BasisCilboxPermissions.Remove(HashC);
            RebuildList();
            AssertSettled("after removing an approval");
            Assert.That(Rows().Count, Is.EqualTo((BasisCilboxPermissions.GetAll().Count + 1) / 2));

            BasisCilboxPermissions.Remove(HashB);
            RebuildList();
            AssertSettled("after removing a second approval");
            Assert.That(Rows().Count, Is.EqualTo((BasisCilboxPermissions.GetAll().Count + 1) / 2));

            BasisCilboxPermissions.Allow(HashC, "Third", "Prop", new[] { "Type:System.IO.File" }, false);
            RebuildList();
            AssertSettled("after adding an approval");
            Assert.That(Rows().Count, Is.EqualTo((BasisCilboxPermissions.GetAll().Count + 1) / 2));
        }

        [Test]
        public void ClosingAndReopeningLeavesNothingOverlapping()
        {
            LogAssert.ignoreFailingMessages = true;
            _section.SetExpanded(true);
            AssertSettled("after expanding");
            _section.SetExpanded(false);
            AssertSettled("after closing");
            _section.SetExpanded(true);
            AssertSettled("after reopening");
        }

        private void RebuildList()
        {
            foreach (MonoBehaviour behaviour in _content.GetComponentsInChildren<MonoBehaviour>(false))
            {
                if (behaviour.GetType().Name != "CilboxPermissionsList") continue;
                behaviour.GetType().GetMethod("Rebuild").Invoke(behaviour, null);
                return;
            }
            Assert.Fail("the permissions list is not on the page");
        }

        private List<RectTransform> Rows()
        {
            List<RectTransform> rows = new List<RectTransform>();
            foreach (RectTransform rect in _content.GetComponentsInChildren<RectTransform>(false))
            {
                if (rect.name == "ScriptPermissionRow") rows.Add(rect);
            }
            return rows;
        }

        private void AssertSettled(string when)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);

            List<RectTransform> tracked = new List<RectTransform>();
            for (int Index = 0; Index < _content.childCount; Index++)
            {
                RectTransform child = (RectTransform)_content.GetChild(Index);
                if (child.gameObject.activeInHierarchy) tracked.Add(child);
            }
            tracked.AddRange(Rows());

            Vector2[] shown = new Vector2[tracked.Count];
            for (int Index = 0; Index < tracked.Count; Index++) shown[Index] = Extent(tracked[Index]);

            for (int pass = 0; pass < SettlePasses; pass++) LayoutRebuilder.ForceRebuildLayoutImmediate(_content);

            for (int Index = 0; Index < tracked.Count; Index++)
            {
                Vector2 settled = Extent(tracked[Index]);
                Assert.That(shown[Index].x, Is.EqualTo(settled.x).Within(Tolerance), $"{when}: '{tracked[Index].name}' was drawn {shown[Index].x - settled.x:0.#} px off its settled top");
                Assert.That(shown[Index].y, Is.EqualTo(settled.y).Within(Tolerance), $"{when}: '{tracked[Index].name}' was drawn {shown[Index].y - settled.y:0.#} px off its settled bottom");
            }

            float spacing = _content.GetComponent<VerticalLayoutGroup>().spacing;
            RectTransform previous = null;
            for (int Index = 0; Index < tracked.Count; Index++)
            {
                if (tracked[Index].parent != _content) continue;
                if (previous != null)
                {
                    float gap = Extent(previous).y - Extent(tracked[Index]).x;
                    Assert.That(gap, Is.EqualTo(spacing).Within(Tolerance), $"{when}: '{tracked[Index].name}' starts {gap:0.#} px below '{previous.name}' instead of {spacing}");
                }
                previous = tracked[Index];
            }
        }

        private Vector2 Extent(RectTransform rect)
        {
            rect.GetWorldCorners(_corners);
            return new Vector2(_content.InverseTransformPoint(_corners[1]).y, _content.InverseTransformPoint(_corners[0]).y);
        }
    }
}
