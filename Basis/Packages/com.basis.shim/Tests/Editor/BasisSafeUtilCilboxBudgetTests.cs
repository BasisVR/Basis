using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Basis;
using Basis.BasisUI;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisSafeUtilCilboxBudgetTests
    {
        GameObject root;
        CilboxSceneBasis box;
        CilboxProxy proxy;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("SafeUtil Cilbox budget test");
            box = root.AddComponent<CilboxSceneBasis>();
            proxy = root.AddComponent<CilboxProxy>();
            proxy.box = box;
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null)
                UnityEngine.Object.DestroyImmediate(root);
        }

        [Test]
        public void GetExecutionBudgetUs_ReturnsOwningBoxBudget()
        {
            box.timeoutLengthUs = 321_000;

            Assert.AreEqual(321_000, SafeUtil.GetExecutionBudgetUs(proxy));
        }

        [Test]
        public void GetRemainingExecutionBudgetUs_UsesOwningBoxDeadline()
        {
            const long budgetUs = 1_000_000;
            box.timeoutLengthUs = budgetUs;
            box.interpreterTicksInUs = Stopwatch.Frequency / 1_000_000;
            Assert.Greater(box.interpreterTicksInUs, 0);

            box.interpreterAccountingDropDead = Stopwatch.GetTimestamp() + budgetUs * box.interpreterTicksInUs;

            long remaining = SafeUtil.GetRemainingExecutionBudgetUs(proxy);

            Assert.Greater(remaining, 0);
            Assert.LessOrEqual(remaining, budgetUs);
        }

        [Test]
        public void GetRemainingExecutionBudgetUs_ClampsExpiredDeadlineToZero()
        {
            box.interpreterTicksInUs = Stopwatch.Frequency / 1_000_000;
            box.interpreterAccountingDropDead = Stopwatch.GetTimestamp() - box.interpreterTicksInUs;

            Assert.AreEqual(0, SafeUtil.GetRemainingExecutionBudgetUs(proxy));
        }

        [Test]
        public void GetLastFrameExecutionUs_ReturnsOwningBoxAccounting()
        {
            box.usSpentLastFrame = 12_345;

            Assert.AreEqual(12_345, SafeUtil.GetLastFrameExecutionUs(proxy));
        }

        [Test]
        public void GetCurrentLanguage_ReturnsSelectedBasisUILanguage()
        {
            Assert.AreEqual(BasisLocalization.CurrentLanguage, SafeUtil.GetCurrentLanguage());
        }

        [Test]
        public void SafeUtilWhitelist_PreservesExistingSurfaceAndAddsLanguageGetter()
        {
            FieldInfo field = typeof(CilboxBasisCommon).GetField(
                "commonMethodWhitelist",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field);

            var whitelist = (Dictionary<Type, HashSet<string>>)field.GetValue(null);
            Assert.IsTrue(whitelist.TryGetValue(typeof(SafeUtil), out HashSet<string> allowed));

            CollectionAssert.AreEquivalent(new[]
            {
                ".ctor",
                nameof(SafeUtil.GetCurrentLanguage),
                nameof(SafeUtil.AddEventTrigger),
                nameof(SafeUtil.MakeNetworkable),
                nameof(SafeUtil.MakeInteractable),
                nameof(SafeUtil.GetExecutionBudgetUs),
                nameof(SafeUtil.GetRemainingExecutionBudgetUs),
                nameof(SafeUtil.GetLastFrameExecutionUs),
                nameof(SafeUtil.GetContentGroupId),
                nameof(SafeUtil.GetContentNetworkId),
                nameof(SafeUtil.GetContentName),
                nameof(SafeUtil.GetContentAuthor),
                nameof(SafeUtil.GetContentTags),
            }, allowed);
        }

        [Test]
        public void ContentGetters_ReadOwningContentDescription()
        {
            BasisProp prop = root.AddComponent<BasisProp>();
            prop.BasisBundleDescription = new BasisBundleDescription("Test Prop", "desc")
            {
                ContentGroupId = "group-123",
                Tags = new[] { "Horror" },
                Authorship = new BasisBundleAuthorship { Author = "Someone", Contact = "https://example.invalid" },
            };
            box.interpreterAccountingDepth = 1;

            Assert.AreEqual("group-123", SafeUtil.GetContentGroupId(proxy));
            Assert.AreEqual("Test Prop", SafeUtil.GetContentName(proxy));
            Assert.AreEqual("Someone", SafeUtil.GetContentAuthor(proxy));
            Assert.AreEqual(string.Empty, SafeUtil.GetContentNetworkId(proxy));

            string[] tags = SafeUtil.GetContentTags(proxy);
            CollectionAssert.AreEqual(new[] { "Horror" }, tags);
            tags[0] = "Changed";
            Assert.AreEqual("Horror", prop.BasisBundleDescription.Tags[0]);
        }

        [Test]
        public void ContentGetters_ReturnEmptyWithoutContent()
        {
            // The proxy itself is not used here: TryResolve falls back to any BasisScene in the
            // editor's open scene, so only inputs that cannot resolve are deterministic.
            object notAComponent = new object();
            Assert.AreEqual(string.Empty, SafeUtil.GetContentGroupId(null));
            Assert.AreEqual(string.Empty, SafeUtil.GetContentNetworkId(notAComponent));
            Assert.AreEqual(string.Empty, SafeUtil.GetContentName(null));
            Assert.AreEqual(string.Empty, SafeUtil.GetContentAuthor(notAComponent));
            CollectionAssert.IsEmpty(SafeUtil.GetContentTags(null));
        }

        [Test]
        public void ContentGetters_AnswerOnlyAboutTheCallersOwnContent()
        {
            BasisProp mine = root.AddComponent<BasisProp>();
            mine.BasisBundleDescription = new BasisBundleDescription("Mine", "desc") { ContentGroupId = "mine-123", Tags = new[] { "Puzzle" } };
            GameObject other = new GameObject("Someone else's prop");
            try
            {
                other.AddComponent<BasisProp>().BasisBundleDescription = new BasisBundleDescription("Theirs", "desc") { ContentGroupId = "theirs-456", Tags = new[] { "Horror" } };
                BoxCollider wall = other.AddComponent<BoxCollider>();
                Transform child = new GameObject("Child").transform;
                child.SetParent(root.transform);

                Assert.AreEqual(string.Empty, SafeUtil.GetContentGroupId(proxy), "a box that is not running is not the caller");
                box.interpreterAccountingDepth = 1;
                Assert.AreEqual("mine-123", SafeUtil.GetContentGroupId(proxy));
                Assert.AreEqual("mine-123", SafeUtil.GetContentGroupId(child), "any component of the caller's own content works");
                Assert.AreEqual(string.Empty, SafeUtil.GetContentGroupId(wall), "a raycast hit on other content must not hand over its id");
                Assert.AreEqual(string.Empty, SafeUtil.GetContentName(wall));
                Assert.AreEqual(string.Empty, SafeUtil.GetContentAuthor(wall));
                Assert.AreEqual(string.Empty, SafeUtil.GetContentNetworkId(wall));
                CollectionAssert.IsEmpty(SafeUtil.GetContentTags(wall));

                CilboxSceneBasis second = other.AddComponent<CilboxSceneBasis>();
                second.interpreterAccountingDepth = 1;
                Assert.AreEqual(string.Empty, SafeUtil.GetContentGroupId(proxy), "while two boxes run, nobody can tell which one is asking");
                second.interpreterAccountingDepth = 0;
                Assert.AreEqual("mine-123", SafeUtil.GetContentGroupId(proxy));
            }
            finally
            {
                box.interpreterAccountingDepth = 0;
                UnityEngine.Object.DestroyImmediate(other);
            }
        }
    }
}
