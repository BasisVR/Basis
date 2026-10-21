using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisExternalLinkShimTests
    {
        const string Page = "https://example.com/";
        const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        readonly List<GameObject> hosts = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            BasisUserActivation.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
            BasisUserActivation.Clear();
        }

        GameObject Host(string name, Transform parent = null)
        {
            GameObject host = new GameObject(name);
            if (parent != null) host.transform.SetParent(parent);
            hosts.Add(host);
            return host;
        }

        T AddBox<T>(Transform parent = null) where T : CilboxBasisCommon
        {
            T box = Host(typeof(T).Name + " link test", parent).AddComponent<T>();
            box.classes = new Dictionary<string, int>();
            return box;
        }

        static bool Allowed(CilboxBasisCommon box, Type declaringType, string name, out MethodInfo mi)
        {
            return box.CheckMethodAllowed(out mi, declaringType, name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), "Void " + name + "(System.String)");
        }

        static MethodInfo RedirectTarget()
        {
            MethodInfo target = typeof(BasisExternalLinkShim).GetMethod("OpenURL", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            Assert.IsNotNull(target, "BasisExternalLinkShim.OpenURL(string) moved; the Application.OpenURL redirect has nothing to call.");
            return target;
        }

        CilboxProxy ScriptOn<TBox>(GameObject content, uint depth) where TBox : CilboxBasisCommon
        {
            TBox box = AddBox<TBox>(content.transform);
            box.interpreterAccountingDepth = depth;
            CilboxProxy proxy = Host("Script", content.transform).AddComponent<CilboxProxy>();
            proxy.box = box;
            return proxy;
        }

        static BasisExternalLinkResult OpenFrom(object self, string url)
        {
            List<BasisExternalLinkResult> results = new List<BasisExternalLinkResult>();
            BasisExternalLinkShim.Open(self, url, results.Add);
            Assert.AreEqual(1, results.Count, "Open must answer exactly once.");
            return results[0];
        }

        [Test]
        public void ApplicationOpenURL_IsRoutedThroughTheLinkPrompt_InEveryBox()
        {
            MethodInfo target = RedirectTarget();
            foreach (CilboxBasisCommon box in new CilboxBasisCommon[] { AddBox<CilboxSceneBasis>(), AddBox<CilboxPropBasis>(), AddBox<CilboxAvatarBasis>() })
            {
                Assert.IsTrue(Allowed(box, typeof(Application), nameof(Application.OpenURL), out MethodInfo mi), box.GetType().Name);
                Assert.AreEqual(target, mi, box.GetType().Name + " would call the real Application.OpenURL.");
            }
        }

        [Test]
        public void ApplicationOpenURL_IsNoLongerSomethingToGrant()
        {
            CilboxSceneBasis scene = AddBox<CilboxSceneBasis>();
            FieldInfo scanField = typeof(CilboxBasisCommon).GetField("consentScan", Internal);
            Assert.IsNotNull(scanField, "CilboxBasisCommon.consentScan moved; this test cannot run the dry run.");
            object scan = Activator.CreateInstance(scanField.FieldType, true);
            scanField.SetValue(scene, scan);
            try
            {
                Assert.IsTrue(Allowed(scene, typeof(Application), nameof(Application.OpenURL), out _));
            }
            finally
            {
                scanField.SetValue(scene, null);
            }
            foreach (string set in new[] { "Items", "Blocked" })
            {
                SortedSet<string> items = (SortedSet<string>)scan.GetType().GetField(set, Internal).GetValue(scan);
                Assert.IsEmpty(items, set);
            }
        }

        [Test]
        public void TheLinkShim_IsOpenToWorldsAndProps_ButNotAvatars()
        {
            foreach (CilboxBasisCommon box in new CilboxBasisCommon[] { AddBox<CilboxSceneBasis>(), AddBox<CilboxPropBasis>() })
            {
                string kind = box.GetType().Name;
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisExternalLinkShim).FullName), kind);
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisExternalLinkResult).FullName), kind);
                Assert.IsTrue(Allowed(box, typeof(BasisExternalLinkShim), nameof(BasisExternalLinkShim.Open), out _), kind);
                Assert.IsTrue(Allowed(box, typeof(BasisExternalLinkShim), nameof(BasisExternalLinkShim.IsValidUrl), out _), kind);
                Assert.IsFalse(Allowed(box, typeof(BasisExternalLinkShim), "OpenURL", out _), kind + " can call the redirect target directly.");
            }
            CilboxAvatarBasis avatar = AddBox<CilboxAvatarBasis>();
            Assert.IsFalse(avatar.CheckTypeAllowed(typeof(BasisExternalLinkShim).FullName));
        }

        [Test]
        public void TheLinkInternals_CanNeverBeGrantedToAScript()
        {
            string[] internals = { typeof(BasisLinkSafety).FullName, typeof(BasisContentShareText).FullName, typeof(BasisExternalLinks).FullName, typeof(BasisUserActivation).FullName };
            CilboxSceneBasis scene = AddBox<CilboxSceneBasis>();
            FieldInfo state = typeof(CilboxBasisCommon).GetField("consentState", Internal);
            Assert.IsNotNull(state, "CilboxBasisCommon.consentState moved; this test cannot grant consent.");
            state.SetValue(scene, Enum.Parse(state.FieldType, "Allowed"));
            HashSet<string> granted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string type in internals) granted.Add("Type:" + type);
            typeof(CilboxBasisCommon).GetField("consentGranted", Internal).SetValue(scene, granted);
            bool untrustedBefore = BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue;
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = true;
            try
            {
                foreach (string type in internals)
                {
                    Assert.IsFalse(scene.CheckTypeAllowed(type), type + " was reachable after a grant.");
                }
            }
            finally
            {
                BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = untrustedBefore;
            }

            CilboxPropBasis prop = AddBox<CilboxPropBasis>();
            FieldInfo scanField = typeof(CilboxBasisCommon).GetField("consentScan", Internal);
            object scan = Activator.CreateInstance(scanField.FieldType, true);
            scanField.SetValue(prop, scan);
            try
            {
                foreach (string type in internals) prop.CheckTypeAllowed(type);
            }
            finally
            {
                scanField.SetValue(prop, null);
            }
            SortedSet<string> blocked = (SortedSet<string>)scan.GetType().GetField("Blocked", Internal).GetValue(scan);
            SortedSet<string> items = (SortedSet<string>)scan.GetType().GetField("Items", Internal).GetValue(scan);
            foreach (string type in internals)
            {
                Assert.IsTrue(blocked.Contains("Type:" + type), type + " is not on the floor.");
                Assert.IsFalse(items.Contains("Type:" + type), type + " would be offered in the consent prompt.");
            }
        }

        [Test]
        public void AScriptCanOnlyAskAsItself_WhileItIsRunning()
        {
            GameObject prop = Host("Prop");
            prop.AddComponent<BasisProp>();

            CilboxProxy idle = ScriptOn<CilboxPropBasis>(prop, 0);
            Assert.AreEqual(BasisExternalLinkResult.Blocked, OpenFrom(idle, Page));

            CilboxProxy running = ScriptOn<CilboxPropBasis>(prop, 1);
            Assert.AreEqual(BasisExternalLinkResult.NoInteraction, OpenFrom(running, Page));
            Assert.AreEqual(BasisExternalLinkResult.InvalidUrl, OpenFrom(running, "javascript:alert(1)"));

            Assert.AreEqual(BasisExternalLinkResult.Blocked, OpenFrom(new object(), Page));
            Assert.AreEqual(BasisExternalLinkResult.Blocked, OpenFrom(prop.GetComponent<BasisProp>(), Page));

            GameObject avatar = Host("Avatar");
            avatar.AddComponent<Basis.Scripts.BasisSdk.BasisAvatar>();
            CilboxProxy worn = ScriptOn<CilboxAvatarBasis>(avatar, 1);
            BasisUserActivation.Note(avatar);
            Assert.AreEqual(BasisExternalLinkResult.Blocked, OpenFrom(worn, Page));
            Assert.IsFalse(BasisExternalLinks.IsAsking);
        }

        [Test]
        public void TheRedirectedOpenURL_NeverOpensWithoutAQuestion()
        {
            Assert.DoesNotThrow(() => RedirectTarget().Invoke(null, new object[] { Page }));
            Assert.DoesNotThrow(() => RedirectTarget().Invoke(null, new object[] { "file:///C:/Windows/System32/calc.exe" }));
            Assert.IsFalse(BasisExternalLinks.IsAsking);
        }

        [Test]
        public void IsValidUrl_FollowsTheSameRulesAsTheFramework()
        {
            Assert.IsTrue(BasisExternalLinkShim.IsValidUrl(Page));
            Assert.IsFalse(BasisExternalLinkShim.IsValidUrl("http://localhost:3000/"));
            Assert.IsFalse(BasisExternalLinkShim.IsValidUrl("file:///etc/passwd"));
            Assert.IsFalse(BasisExternalLinkShim.IsValidUrl(null));
        }

        [Test]
        public void TheShimSurvivesStripping()
        {
            string xml = File.ReadAllText("Packages/com.basis.shim/Shims/link.xml");
            StringAssert.Contains("\"" + typeof(BasisExternalLinkShim).FullName + "\"", xml);
            StringAssert.Contains("\"" + typeof(BasisExternalLinkResult).FullName + "\"", xml);
        }
    }
}
