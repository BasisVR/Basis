using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests
{
    public class BasisExternalLinksTests
    {
        private const string Page = "https://example.com/page?x=1";
        private readonly List<GameObject> spawned = new List<GameObject>();
        private bool settingBefore;

        [SetUp]
        public void SetUp()
        {
            BasisUserActivation.Clear();
            settingBefore = BasisSettingsDefaults.ExternalLinkRequests.RawValue;
            BasisSettingsDefaults.ExternalLinkRequests.RawValue = true;
        }

        [TearDown]
        public void TearDown()
        {
            SetAsking(false);
            SetServerLock(false);
            BasisSettingsDefaults.ExternalLinkRequests.RawValue = settingBefore;
            BasisUserActivation.Clear();
            foreach (GameObject go in spawned)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
            spawned.Clear();
        }

        private GameObject Spawn(string name, Transform parent = null)
        {
            GameObject go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent);
            spawned.Add(go);
            return go;
        }

        private GameObject ButtonOn<T>(out T content) where T : BasisContentBase
        {
            GameObject root = Spawn(typeof(T).Name);
            content = root.AddComponent<T>();
            return Spawn("Link Button", root.transform);
        }

        private static void SetAsking(bool value)
        {
            FieldInfo field = typeof(BasisExternalLinks).GetField("asking", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "BasisExternalLinks.asking moved; this test cannot hold a prompt open.");
            field.SetValue(null, value);
        }

        private static void SetServerLock(bool value)
        {
            PropertyInfo property = typeof(BasisNetworkModeration).GetProperty(nameof(BasisNetworkModeration.GlobalExternalLinksLocked), BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(property, "BasisNetworkModeration.GlobalExternalLinksLocked moved; this test cannot lock links.");
            property.SetValue(null, value);
        }

        private static BasisExternalLinks.Result Refusal(string url, GameObject source)
        {
            Assert.IsFalse(BasisExternalLinks.Admit(url, source, out _, out _, out _, out BasisExternalLinks.Result refusal), "The request was admitted.");
            return refusal;
        }

        [TestCase("https://example.com/page?x=1")]
        [TestCase("http://example.org")]
        [TestCase("https://sub.example.co.uk:8443/path/to/page#part")]
        [TestCase("https://example.com/download/setup.exe.html")]
        public void PlainAddressesOfPublicSitesAreAccepted(string url)
        {
            Assert.IsTrue(BasisExternalLinks.TryParse(url, out Uri link, out string address), url);
            Assert.IsNotNull(link);
            Assert.IsFalse(string.IsNullOrEmpty(address));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("example.com")]
        [TestCase("javascript:alert(1)")]
        [TestCase("file:///C:/Windows/System32/calc.exe")]
        [TestCase("steam://run/440")]
        [TestCase("ms-settings:privacy")]
        [TestCase("mailto:someone@example.com")]
        [TestCase("https://user:pass@example.com/")]
        [TestCase("https://example.com@evil.example/")]
        [TestCase("https://exa mple.com/")]
        [TestCase("https://example.com/\u202Eexe.txt")]
        [TestCase("http://localhost:8080/")]
        [TestCase("http://127.0.0.1/")]
        [TestCase("http://2130706433/")]
        [TestCase("http://192.168.1.1/admin")]
        [TestCase("http://10.0.0.5/")]
        [TestCase("http://169.254.169.254/latest/meta-data/")]
        [TestCase("http://[::1]/")]
        [TestCase("http://router/")]
        [TestCase("https://nas.local/")]
        [TestCase("https://example.com/setup.exe")]
        [TestCase("https://example.com/files/Tool.MSI")]
        public void AnythingElseIsRefused(string url)
        {
            Assert.IsFalse(BasisExternalLinks.TryParse(url, out Uri link, out string address), url);
            Assert.IsNull(link);
            Assert.IsNull(address);
            Assert.IsFalse(BasisExternalLinks.TryParse(null, out _, out _));
        }

        [Test]
        public void TheAddressShownIsThePlainAsciiAddressThatOpens()
        {
            Assert.IsTrue(BasisExternalLinks.TryParse("https://bücher.example/straße?q=ü", out Uri link, out string address));
            StringAssert.StartsWith("https://xn--bcher-kva.example/", address);
            foreach (char c in address)
            {
                Assert.Less((int)c, 128, address);
            }
            Assert.AreNotEqual(BasisLinkSafety.Warning.None, BasisLinkSafety.Inspect(link) & BasisLinkSafety.Warning.LookAlikeName);
        }

        [Test]
        public void ContentThatWasNotJustUsedCannotAsk()
        {
            GameObject button = ButtonOn(out BasisProp _);
            Assert.AreEqual(BasisExternalLinks.Result.NoInteraction, Refusal(Page, button));
        }

        [Test]
        public void UsingTheContentLetsItAsk()
        {
            GameObject button = ButtonOn(out BasisProp prop);
            BasisUserActivation.Note(button);
            Assert.IsTrue(BasisExternalLinks.Admit(Page, button, out Uri link, out string address, out BasisContentBase content, out _));
            Assert.AreSame(prop, content);
            Assert.AreEqual("example.com", link.Host);
            Assert.AreEqual(Page, address);
        }

        [Test]
        public void UsingOtherContentDoesNotLetThisContentAsk()
        {
            GameObject button = ButtonOn(out BasisProp _);
            GameObject other = ButtonOn(out BasisProp _);
            BasisUserActivation.Note(other);
            Assert.AreEqual(BasisExternalLinks.Result.NoInteraction, Refusal(Page, button));
        }

        [Test]
        public void AWorldObjectOutsideTheSceneRootCountsAsTheWorld()
        {
            GameObject sign = Spawn("Sign");
            Assume.That(BasisContent.TryResolve(sign, out _), Is.False, "The open scene already holds a world.");
            BasisScene scene = Spawn("World Root").AddComponent<BasisScene>();
            BasisUserActivation.Note(sign);
            Assert.IsTrue(BasisExternalLinks.Admit(Page, sign, out _, out _, out BasisContentBase content, out _));
            Assert.AreSame(scene, content);
        }

        [Test]
        public void AvatarsAndObjectsOutsideContentCanNeverAsk()
        {
            GameObject badge = ButtonOn(out BasisAvatar _);
            BasisUserActivation.Note(badge);
            Assert.AreEqual(BasisExternalLinks.Result.Blocked, Refusal(Page, badge));
            Assert.AreEqual(BasisExternalLinks.Result.Blocked, Refusal(Page, null));

            GameObject loose = Spawn("Loose");
            Assume.That(BasisContent.TryResolve(loose, out _), Is.False, "The open scene already holds a world.");
            BasisUserActivation.Note(loose);
            Assert.AreEqual(BasisExternalLinks.Result.Blocked, Refusal(Page, loose));
        }

        [Test]
        public void ThePlayerSettingAndTheServerLockBothRefuse()
        {
            GameObject button = ButtonOn(out BasisProp _);
            BasisUserActivation.Note(button);

            BasisSettingsDefaults.ExternalLinkRequests.RawValue = false;
            Assert.AreEqual(BasisExternalLinks.Result.Blocked, Refusal(Page, button));
            BasisSettingsDefaults.ExternalLinkRequests.RawValue = true;

            SetServerLock(true);
            Assert.IsTrue(BasisNetworkModeration.ExternalLinksBlockedLocally);
            Assert.AreEqual(BasisExternalLinks.Result.Blocked, Refusal(Page, button));
            SetServerLock(false);

            Assert.IsTrue(BasisExternalLinks.Admit(Page, button, out _, out _, out _, out _));
        }

        [Test]
        public void OnlyOneQuestionIsAskedAtATime()
        {
            GameObject button = ButtonOn(out BasisProp _);
            BasisUserActivation.Note(button);
            SetAsking(true);
            Assert.AreEqual(BasisExternalLinks.Result.Busy, Refusal(Page, button));
        }

        [Test]
        public void SayingNoQuietsTheContent_AndOpeningForgivesIt()
        {
            GameObject button = ButtonOn(out BasisProp prop);
            BasisUserActivation.Note(button);

            BasisExternalLinks.StrikeOut(prop);
            Assert.IsTrue(BasisExternalLinks.IsMuted(prop));
            Assert.AreEqual(BasisExternalLinks.Result.Blocked, Refusal(Page, button));

            BasisExternalLinks.Forgive(prop);
            Assert.IsFalse(BasisExternalLinks.IsMuted(prop));

            for (int i = 0; i < BasisExternalLinks.DeclinesBeforeMute; i++) BasisExternalLinks.StrikeOut(prop);
            Assert.IsTrue(BasisExternalLinks.IsMuted(prop));
            BasisExternalLinks.Forgive(prop);
        }

        [Test]
        public void RefusalsReachTheCallerWithoutAPrompt()
        {
            GameObject button = ButtonOn(out BasisProp _);
            List<BasisExternalLinks.Result> results = new List<BasisExternalLinks.Result>();
            BasisExternalLinks.Request("javascript:alert(1)", button, results.Add);
            BasisExternalLinks.Request(Page, button, results.Add);
            BasisExternalLinks.Request(Page, null, results.Add);
            CollectionAssert.AreEqual(new[] { BasisExternalLinks.Result.InvalidUrl, BasisExternalLinks.Result.NoInteraction, BasisExternalLinks.Result.Blocked }, results);
            Assert.IsFalse(BasisExternalLinks.IsAsking);
        }

        [Test]
        public void ACallbackThatThrowsDoesNotBreakTheCaller()
        {
            LogAssert.ignoreFailingMessages = true;
            Assert.DoesNotThrow(() => BasisExternalLinks.Request("not a link", null, _ => throw new InvalidOperationException("content bug")));
        }

        [Test]
        public void ContentNamesCannotCarryMarkupOrInvisibleCharacters()
        {
            string clean = BasisExternalLinks.CleanName("<size=500>Big</size>\u202E \\u003Cb\\u003E  World\n\t");
            Assert.IsFalse(clean.Contains("<") || clean.Contains(">") || clean.Contains("\\") || clean.Contains("\u202E") || clean.Contains("\n"), clean);
            Assert.AreEqual(string.Empty, BasisExternalLinks.CleanName(null));
            Assert.LessOrEqual(BasisExternalLinks.CleanName(new string('a', 500)).Length, 48);
            string emoji = BasisExternalLinks.CleanName(new string('a', 47) + "\U0001F600");
            Assert.IsFalse(emoji.Length > 0 && char.IsHighSurrogate(emoji[emoji.Length - 1]), "A surrogate pair was split.");
        }

        [Test]
        public void TheLinkComponentIsApprovedForWorldsAndPropsButNeverAvatars()
        {
            string type = typeof(BasisExternalLinkInteractable).FullName;
            Assert.IsTrue(Selector("Scene").selectedTypes.Contains(type), "Worlds cannot use " + type);
            Assert.IsTrue(Selector("Prop").selectedTypes.Contains(type), "Props cannot use " + type);
            Assert.IsFalse(Selector("Avatar").selectedTypes.Contains(type), "Avatars must never carry " + type);
        }

        private static ContentPoliceSelector Selector(string kind)
        {
            ContentPoliceSelector selector = AssetDatabase.LoadAssetAtPath<ContentPoliceSelector>($"Packages/com.basis.sdk/Settings/{kind}ContentPoliceSelector.asset");
            Assert.IsNotNull(selector, kind + " selector asset moved.");
            return selector;
        }
    }
}
