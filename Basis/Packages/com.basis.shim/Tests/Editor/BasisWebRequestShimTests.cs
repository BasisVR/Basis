using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisWebRequestShimTests
    {
        const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const BindingFlags Everything = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        static readonly string[] Consented = { "Get", "GetText", "Post", "PostText" };
        readonly List<GameObject> hosts = new List<GameObject>();
        bool untrustedBefore;
        bool hapticsBefore;

        [SetUp]
        public void SetUp()
        {
            untrustedBefore = BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue;
            hapticsBefore = BasisSettingsDefaults.UIHaptics.RawValue;
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = true;
            Drain();
        }

        [TearDown]
        public void TearDown()
        {
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = untrustedBefore;
            BasisSettingsDefaults.UIHaptics.RawValue = hapticsBefore;
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
            Drain();
        }

        static void Drain()
        {
            while (BasisDeviceManagement.mainThreadActions.TryDequeue(out Action action)) action();
        }

        static string Item(string method) => BasisCilboxPermissionText.MethodPrefix + typeof(BasisWebRequestShim).FullName + "." + method;

        static FieldInfo Field(string name)
        {
            FieldInfo field = typeof(CilboxBasisCommon).GetField(name, Internal);
            Assert.IsNotNull(field, $"CilboxBasisCommon.{name} moved; this test cannot drive the shim.");
            return field;
        }

        CilboxBasisCommon AddBox(Type boxType)
        {
            GameObject host = new GameObject(boxType.Name + " web request test");
            hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }

        CilboxProxy Script(Type boxType, bool consent = true, bool running = true)
        {
            CilboxBasisCommon box = AddBox(boxType);
            box.interpreterAccountingDepth = running ? 1u : 0u;
            Field("consentHash").SetValue(box, "test-hash");
            if (consent)
            {
                FieldInfo state = Field("consentState");
                state.SetValue(box, Enum.Parse(state.FieldType, "Allowed"));
                Field("consentGranted").SetValue(box, new HashSet<string>(Consented.Select(Item), StringComparer.Ordinal));
            }
            CilboxProxy proxy = new GameObject("Script").AddComponent<CilboxProxy>();
            proxy.transform.SetParent(box.transform);
            proxy.box = box;
            return proxy;
        }

        static bool Allowed(CilboxBasisCommon box, Type shim, string method)
        {
            return box.CheckMethodAllowed(out MethodInfo _, shim, method, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), method);
        }

        static BasisWebResult GetAndWait(CilboxProxy script, string url)
        {
            BasisWebResult? result = null;
            BasisWebRequestShim.GetText(script, url, (r, code, text) => result = r);
            Assert.IsNull(result, "answers always arrive on a later frame, never inside the call");
            Drain();
            Assert.IsTrue(result.HasValue, "every refused request is answered");
            return result.Value;
        }

        [Test]
        public void TheShimTypes_HaveNothingButTheirEntryPoints()
        {
            foreach (Type shim in new[] { typeof(BasisWebRequestShim), typeof(BasisHapticsShim) })
            {
                Assert.IsEmpty(shim.GetNestedTypes(Everything), shim.Name + ": a nested type on a whitelisted shim is reachable from scripts");
                foreach (MethodInfo method in shim.GetMethods(Everything)) Assert.IsTrue(method.IsPublic && method.IsStatic, shim.Name + "." + method.Name + " must be public static");
                foreach (FieldInfo field in shim.GetFields(Everything)) Assert.IsTrue(field.IsLiteral, shim.Name + "." + field.Name + " must be a constant");
            }
            CollectionAssert.AreEquivalent(Consented.Append(nameof(BasisWebRequestShim.IsValidUrl)).ToArray(), typeof(BasisWebRequestShim).GetMethods(Everything).Select(m => m.Name).Distinct().ToArray());
            CollectionAssert.AreEquivalent(Enum.GetValues(typeof(BasisWebRequests.Status)).Cast<BasisWebRequests.Status>().Select(v => (int)v), Enum.GetValues(typeof(BasisWebResult)).Cast<BasisWebResult>().Select(v => (int)v), "the script result enum mirrors the framework status");
        }

        [Test]
        public void WorldsAndProps_AllowTheEntryPoints_AvatarsNever()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis) })
            {
                CilboxBasisCommon box = AddBox(boxType);
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisWebRequestShim)), boxType.Name);
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisWebResult)), boxType.Name);
                Assert.IsTrue(Allowed(box, typeof(BasisWebRequestShim), nameof(BasisWebRequestShim.IsValidUrl)), boxType.Name);
                foreach (string method in Consented) Assert.IsTrue(Allowed(box, typeof(BasisWebRequestShim), method), boxType.Name + "." + method);
                Assert.IsFalse(Allowed(box, typeof(BasisWebRequestShim), "Begin"), boxType.Name + ": helpers must never resolve");
                Assert.IsFalse(box.CheckTypeAllowed(typeof(BasisWebRequests)), boxType.Name + ": the framework engine is floor-denied");
            }
            CilboxBasisCommon avatar = AddBox(typeof(CilboxAvatarBasis));
            Assert.IsFalse(avatar.CheckTypeAllowed(typeof(BasisWebRequestShim)), "avatars travel with their wearer, so they never get web requests");
            foreach (string method in Consented) Assert.IsFalse(Allowed(avatar, typeof(BasisWebRequestShim), method), "avatar." + method);
        }

        [Test]
        public void WithoutConsent_NotRunning_OrAsAnAvatar_RequestsAreRefused()
        {
            Assert.AreEqual(BasisWebResult.NotAllowed, GetAndWait(Script(typeof(CilboxSceneBasis), consent: false), "https://example.com/a"));
            Assert.AreEqual(BasisWebResult.NotAllowed, GetAndWait(Script(typeof(CilboxPropBasis), running: false), "https://example.com/a"));
            Assert.AreEqual(BasisWebResult.NotAllowed, GetAndWait(Script(typeof(CilboxAvatarBasis)), "https://example.com/a"));
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = false;
            Assert.AreEqual(BasisWebResult.NotAllowed, GetAndWait(Script(typeof(CilboxSceneBasis)), "https://example.com/a"), "with Allow Untrusted Permissions off nothing goes out");
        }

        [Test]
        public void ARunningScript_NotInsideContent_IsRefused()
        {
            Assert.AreEqual(BasisWebResult.NotAllowed, GetAndWait(Script(typeof(CilboxSceneBasis)), "https://example.com/a"), "a box that is not part of a world or prop has no one to ask");
        }

        [TestCase("https://example.com/a", true)]
        [TestCase("https://api.example.com/v1/scores?x=1", true)]
        [TestCase("http://example.com/a", false)]
        [TestCase("https://example.com:8443/a", false)]
        [TestCase("https://user:pass@example.com/a", false)]
        [TestCase("https://93.184.216.34/a", false)]
        [TestCase("https://[2606:2800:220:1:248:1893:25c8:1946]/a", false)]
        [TestCase("https://localhost/a", false)]
        [TestCase("https://intranet/a", false)]
        [TestCase("ftp://example.com/a", false)]
        [TestCase("file:///C:/Windows/win.ini", false)]
        [TestCase("/relative", false)]
        [TestCase("", false)]
        public void OnlyPublicHttpsHostNames_AreValid(string url, bool valid)
        {
            Assert.AreEqual(valid, BasisWebRequestShim.IsValidUrl(url), url);
        }

        [Test]
        public void OverlongUrls_AreRejected()
        {
            Assert.IsFalse(BasisWebRequestShim.IsValidUrl("https://example.com/" + new string('a', BasisWebRequests.MaxUrlLength)));
        }

        [Test]
        public void Redirects_MayOnlyStayOnTheApprovedHost()
        {
            Uri from = new Uri("https://example.com/start");
            Assert.IsTrue(BasisWebRequests.TryRedirect(from, "/next", "example.com", out Uri next));
            Assert.AreEqual("https://example.com/next", next.AbsoluteUri);
            Assert.IsTrue(BasisWebRequests.TryRedirect(from, "https://EXAMPLE.com/x", "example.com", out _));
            Assert.IsFalse(BasisWebRequests.TryRedirect(from, "https://other.com/x", "example.com", out _), "another host was never approved");
            Assert.IsFalse(BasisWebRequests.TryRedirect(from, "https://sub.example.com/x", "example.com", out _), "a subdomain is another host");
            Assert.IsFalse(BasisWebRequests.TryRedirect(from, "http://example.com/x", "example.com", out _), "no downgrade to http");
            Assert.IsFalse(BasisWebRequests.TryRedirect(from, "https://127.0.0.1/x", "example.com", out _));
            Assert.IsFalse(BasisWebRequests.TryRedirect(from, "", "example.com", out _));
        }

        [Test]
        public void OnlyKnownContentTypes_MayBeUploaded()
        {
            Assert.IsTrue(BasisWebRequests.IsAllowedContentType(BasisWebRequestShim.Json));
            Assert.IsTrue(BasisWebRequests.IsAllowedContentType(BasisWebRequestShim.Text));
            Assert.IsTrue(BasisWebRequests.IsAllowedContentType(BasisWebRequestShim.Binary));
            Assert.IsTrue(BasisWebRequests.IsAllowedContentType(BasisWebRequestShim.Form));
            Assert.IsFalse(BasisWebRequests.IsAllowedContentType("text/plain\r\nAuthorization: x"), "no header injection through the content type");
            Assert.IsFalse(BasisWebRequests.IsAllowedContentType("multipart/form-data"));
            Assert.IsFalse(BasisWebRequests.IsAllowedContentType(null));
        }

        [Test]
        public void TheConsentPrompt_CallsWebRequestsSevere()
        {
            List<BasisCilboxPermissionText.Line> lines = BasisCilboxPermissionText.Describe(new[] { Item("Get"), Item("Post") });
            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual(BasisLocalization.Get("settings.cilboxPermissions.cap.webRequests"), lines[0].Text);
            Assert.IsTrue(lines[0].Severe);
        }

        [Test]
        public void Haptics_EveryBoxAllowsPlay_AndFloorDeniesTheRest()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                CilboxBasisCommon box = AddBox(boxType);
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisHapticsShim)), boxType.Name);
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisHapticHand)), boxType.Name);
                Assert.IsTrue(Allowed(box, typeof(BasisHapticsShim), nameof(BasisHapticsShim.Play)), boxType.Name);
                Assert.IsFalse(Allowed(box, typeof(BasisHapticsShim), "Spend"), boxType.Name + ": helpers must never resolve");
            }
        }

        [Test]
        public void Haptics_RefuseBadInput_AndRespectTheSetting()
        {
            BasisSettingsDefaults.UIHaptics.RawValue = false;
            CilboxProxy world = Script(typeof(CilboxSceneBasis));
            Assert.IsFalse(BasisHapticsShim.Play(world, BasisHapticHand.Right), "UI Haptics off means nothing plays");
            BasisSettingsDefaults.UIHaptics.RawValue = true;
            Assert.IsFalse(BasisHapticsShim.Play("not a script", BasisHapticHand.Right));
            Assert.IsFalse(BasisHapticsShim.Play(Script(typeof(CilboxSceneBasis), running: false), BasisHapticHand.Right));
            Assert.IsFalse(BasisHapticsShim.Play(world, (BasisHapticHand)7, 0.5f, 0.1f));
            Assert.IsFalse(BasisHapticsShim.Play(world, BasisHapticHand.Left, float.NaN, 0.1f));
            Assert.IsFalse(BasisHapticsShim.Play(world, BasisHapticHand.Left, 0.5f, float.NaN));
            Assert.IsFalse(BasisHapticsShim.Play(world, BasisHapticHand.Left, 0f, 0.1f));
            Assert.IsFalse(BasisHapticsShim.Play(Script(typeof(CilboxAvatarBasis)), BasisHapticHand.Both), "an avatar no local player wears cannot buzz anyone");
        }

        [Test]
        public void PlayerUUID_IsReadableInEveryBox()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                CilboxBasisCommon box = AddBox(boxType);
                Assert.IsTrue(Allowed(box, typeof(IBasisPlayer), "get_UUID"), boxType.Name);
                Assert.IsTrue(Allowed(box, typeof(IBasisPlayer), "get_SafeDisplayName"), boxType.Name);
                Assert.IsTrue(Allowed(box, typeof(IBasisPlayer), "get_DisplayName"), boxType.Name);
                Assert.IsFalse(Allowed(box, typeof(IBasisPlayer), "set_UUID"), boxType.Name + ": still no setters");
            }
            Assert.IsNull(BasisPlayersShim.GetByUUID(null));
            Assert.IsNull(BasisPlayersShim.GetByUUID(string.Empty));
        }
    }
}
