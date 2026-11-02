using System;
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
                if (_roots[Index]) UnityEngine.Object.DestroyImmediate(_roots[Index]);
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

        [Test]
        public void ApprovalKeepsWhoWhatWhenWhere()
        {
            const string hash = "grid-test-record";
            try
            {
                BasisCilboxPermissions.Allow(new BasisCilboxPermissions.Approval
                {
                    ContentHash = hash,
                    ContentName = "Recorded",
                    ContentKind = "Prop",
                    ContentId = "net-id-1",
                    Items = new List<string> { "Type:System.IO.File" },
                    Blocked = new List<string> { "Type:System.Runtime.InteropServices.Marshal" },
                    OwnerName = "Spawner",
                    OwnerUUID = "owner-uuid-1",
                    WorldName = "Test World",
                    Server = "host.example:4296",
                    RequestedUtc = "2026-01-02 03:04:05",
                }, new[] { new BasisCilboxPermissionText.Use("Type:System.IO.File", "Door.Start", "File.ReadAllText()") }, false);

                BasisCilboxPermissions.Approval stored = BasisCilboxPermissions.Find(hash);
                Assert.That(stored, Is.Not.Null);
                Assert.That(stored.RequestedUtc, Is.EqualTo("2026-01-02 03:04:05"));
                Assert.That(stored.ApprovedUtc, Is.Not.Empty);
                Assert.That(stored.Calls.Count, Is.EqualTo(1));

                BasisCilboxPermissions.Approval copy = JsonUtility.FromJson<BasisCilboxPermissions.Approval>(JsonUtility.ToJson(stored));
                Assert.That(copy.OwnerUUID, Is.EqualTo("owner-uuid-1"));
                Assert.That(copy.Server, Is.EqualTo("host.example:4296"));
                Assert.That(copy.Uses()[0].Code, Is.EqualTo("File.ReadAllText()"));

                Assert.That(copy.ContentId, Is.EqualTo("net-id-1"));
                Assert.That(copy.WorldName, Is.EqualTo("Test World"));
                Assert.That(copy.Blocked, Is.EquivalentTo(new[] { "Type:System.Runtime.InteropServices.Marshal" }));

                List<BasisMenuDialoguePanel.DetailRow> rows = BasisCilboxConsentPrompt.Record(stored);
                Assert.That(rows.Count, Is.EqualTo(4), "who, what, when, where");
                Assert.That(rows[0].Value, Is.EqualTo("Spawner (owner-uuid-1)"));
            }
            finally
            {
                BasisCilboxPermissions.Remove(hash);
            }
        }

        [Serializable]
        private class LanguageFile
        {
            public List<LanguageEntry> entries = new List<LanguageEntry>();
        }

        [Serializable]
        private class LanguageEntry
        {
            public string key;
            public string value;
        }

        [Test]
        public void EnglishHasEveryRecordString()
        {
            TextAsset english = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.basis.framework/BasisUI/Localization/Languages/en.json");
            Assert.That(english, Is.Not.Null);
            HashSet<string> keys = new HashSet<string>();
            foreach (LanguageEntry entry in JsonUtility.FromJson<LanguageFile>(english.text).entries) keys.Add(entry.key);
            foreach (string key in new[] { "from", "fromYou", "inServer", "inOffline", "review", "review.tooltip", "record.who", "record.what", "record.when", "record.where", "record.yours", "record.name", "record.id", "record.key", "record.asked", "record.allowed", "record.by", "record.kept", "record.world", "record.server", "record.offline", "record.unknown" })
            {
                Assert.That(keys, Does.Contain("settings.cilboxPermissions." + key));
            }
        }

        [Test]
        public void OlderApprovalsSayWhatWasNotRecorded()
        {
            string record = Flatten(BasisCilboxConsentPrompt.Record(new BasisCilboxPermissions.Approval
            {
                ContentHash = "old",
                ContentName = "Old",
                ContentKind = "Avatar",
                ApprovedUtc = "2026-01-02 03:04",
                Remembered = true,
            }));
            Assert.That(record, Does.Contain(BasisLocalization.Get("settings.cilboxPermissions.record.unknown")));
            Assert.That(record, Does.Not.Contain(BasisLocalization.Get("settings.cilboxPermissions.record.offline")));
        }

        [Test]
        public void EveryCardHasReviewBeforeRemove()
        {
            _section.SetExpanded(true);
            AssertSettled("after expanding");
            int cards = 0;
            foreach (RectTransform row in Rows())
            {
                for (int Index = 0; Index < row.childCount; Index++)
                {
                    PanelElementDescriptor card = row.GetChild(Index).GetComponent<PanelElementDescriptor>();
                    if (card == null) continue;
                    PanelButton[] buttons = card.ContentParent.Find("ScriptPermissionCardActions").GetComponentsInChildren<PanelButton>(true);
                    Assert.That(buttons.Length, Is.EqualTo(2));
                    Assert.That(buttons[0].Descriptor.TitleLabel.text, Is.EqualTo(BasisLocalization.Get("settings.cilboxPermissions.review")));
                    cards++;
                }
            }
            Assert.That(cards, Is.EqualTo(BasisCilboxPermissions.GetAll().Count));
        }

        private static string Flatten(List<BasisMenuDialoguePanel.DetailRow> rows)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            foreach (BasisMenuDialoguePanel.DetailRow row in rows) text.Append(row.Title).Append('\n').Append(row.Value).Append('\n');
            return text.ToString();
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
