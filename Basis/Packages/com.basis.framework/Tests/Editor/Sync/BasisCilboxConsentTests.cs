using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisCilboxConsentTests
    {
        const string WebRequest = "UnityEngine.Networking.UnityWebRequest";
        const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        T AddBox<T>() where T : CilboxBasisCommon
        {
            GameObject host = new GameObject(typeof(T).Name + " consent test");
            hosts.Add(host);
            T box = host.AddComponent<T>();
            box.classes = new Dictionary<string, int>();
            return box;
        }

        static FieldInfo Field(string name)
        {
            FieldInfo field = typeof(CilboxBasisCommon).GetField(name, Internal);
            Assert.IsNotNull(field, $"CilboxBasisCommon.{name} moved; this test cannot drive consent.");
            return field;
        }

        static void Grant(CilboxBasisCommon box, params string[] items)
        {
            FieldInfo state = Field("consentState");
            state.SetValue(box, Enum.Parse(state.FieldType, "Allowed"));
            Field("consentGranted").SetValue(box, new HashSet<string>(items, StringComparer.Ordinal));
        }

        static object BeginScan(CilboxBasisCommon box)
        {
            FieldInfo scanField = Field("consentScan");
            object scan = Activator.CreateInstance(scanField.FieldType, true);
            scanField.SetValue(box, scan);
            return scan;
        }

        static SortedSet<string> Set(object scan, string name)
        {
            return (SortedSet<string>)scan.GetType().GetField(name, Internal).GetValue(scan);
        }

        static bool Allowed(CilboxBasisCommon box, Type declaringType, string name, string signature = null)
        {
            return box.CheckMethodAllowed(out _, declaringType, name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), signature ?? name);
        }

        [Test]
        public void WithoutConsent_TheDefaultWhitelistIsUnchanged()
        {
            CilboxPropBasis prop = AddBox<CilboxPropBasis>();
            Assert.IsFalse(prop.CheckTypeAllowed(WebRequest));
            Assert.IsTrue(prop.CheckTypeAllowed("UnityEngine.Transform"));
            Assert.IsFalse(Allowed(prop, typeof(GameObject), nameof(GameObject.AddComponent)));
            Assert.IsFalse(prop.CheckFieldAllowed("BasisMediaPlayer", "allowLocalAddresses"));
        }

        [Test]
        public void TheDryRun_RecordsEverythingBeyondTheWhitelist_AndKeepsGoing()
        {
            CilboxSceneBasis scene = AddBox<CilboxSceneBasis>();
            object scan = BeginScan(scene);
            try
            {
                Assert.IsTrue(scene.CheckTypeAllowed(WebRequest));
                Assert.IsTrue(Allowed(scene, typeof(GameObject), nameof(GameObject.AddComponent)));
                Assert.IsTrue(scene.CheckTypeAllowed("System.IntPtr"));
                Assert.IsTrue(scene.CheckTypeAllowed(typeof(byte).MakePointerType()));
                Assert.IsTrue(Allowed(scene, typeof(GameObject), nameof(GameObject.SetActive), "Void SetActive(Byte*)"));
                Assert.IsTrue(scene.CheckTypeAllowed("UnityEngine.Transform"));
            }
            finally
            {
                Field("consentScan").SetValue(scene, null);
            }

            CollectionAssert.AreEquivalent(new[] { "Type:" + WebRequest, "Method:UnityEngine.GameObject.AddComponent" }, Set(scan, "Items"));
            CollectionAssert.AreEquivalent(new[] { "Type:System.IntPtr", "Type:System.Byte*", "Method:UnityEngine.GameObject.SetActive" }, Set(scan, "Blocked"));
        }

        [Test]
        public void Consent_OpensExactlyWhatWasApproved()
        {
            CilboxPropBasis prop = AddBox<CilboxPropBasis>();
            Grant(prop, "Type:" + WebRequest, "Method:UnityEngine.GameObject.AddComponent");

            Assert.IsTrue(prop.CheckTypeAllowed(WebRequest));
            Assert.IsTrue(Allowed(prop, typeof(GameObject), nameof(GameObject.AddComponent)));
            Assert.IsFalse(prop.CheckTypeAllowed("System.IO.File"));
            Assert.IsFalse(Allowed(prop, typeof(GameObject), nameof(GameObject.SendMessage)));
            Assert.IsFalse(prop.CheckFieldAllowed(WebRequest, "timeout"));

            CilboxPropBasis other = AddBox<CilboxPropBasis>();
            Assert.IsFalse(other.CheckTypeAllowed(WebRequest));
        }

        [Test]
        public void TheFloor_NeverOpens_EvenWhenSomethingClaimsItWasApproved()
        {
            CilboxSceneBasis scene = AddBox<CilboxSceneBasis>();
            Grant(scene,
                "Type:System.IntPtr",
                "Type:System.Runtime.InteropServices.Marshal",
                "Type:System.Runtime.CompilerServices.Unsafe",
                "Type:System.Byte*",
                "Method:UnityEngine.GameObject.SetActive",
                "Method:Unity.Collections.NativeArray.get_Item");

            Assert.IsFalse(scene.CheckTypeAllowed("System.IntPtr"));
            Assert.IsFalse(scene.CheckTypeAllowed("System.Runtime.InteropServices.Marshal"));
            Assert.IsFalse(scene.CheckTypeAllowed("System.Runtime.CompilerServices.Unsafe"));
            Assert.IsFalse(scene.CheckTypeAllowed(typeof(byte).MakePointerType()));
            Assert.IsFalse(Allowed(scene, typeof(GameObject), nameof(GameObject.SetActive), "Void SetActive(Byte*)"));
            Assert.IsFalse(Allowed(scene, typeof(Unity.Collections.NativeArray<byte>), "get_Item"));
            Assert.IsTrue(Allowed(scene, typeof(Unity.Collections.NativeArray<byte>), "get_Length"));
        }

        [Test]
        public void TheDescription_GroupsByWhatItDoes_DangerousFirst()
        {
            List<BasisCilboxPermissionText.Line> lines = BasisCilboxPermissionText.Describe(new[]
            {
                "Method:UnityEngine.GameObject.AddComponent",
                "Type:System.IO.File",
                "Method:System.IO.File.ReadAllText",
                "Type:" + WebRequest,
                "Type:Some.Unknown.Thing",
                "Method:UnityEngine.WWWForm..ctor",
            });

            Assert.AreEqual(4, lines.Count);
            Assert.IsTrue(lines[0].Severe);
            Assert.AreEqual("File, File.ReadAllText", lines[0].Detail);
            Assert.IsTrue(lines[1].Severe);
            Assert.AreEqual("UnityWebRequest", lines[1].Detail);
            Assert.IsFalse(lines[2].Severe);
            Assert.AreEqual("GameObject.AddComponent", lines[2].Detail);
            Assert.IsFalse(lines[3].Severe);
            Assert.AreEqual("Thing, WWWForm..ctor", lines[3].Detail);
            Assert.IsTrue(BasisCilboxPermissionText.AnySevere(new[] { "Method:UnityEngine.Application.OpenURL" }));
            Assert.IsFalse(BasisCilboxPermissionText.AnySevere(new[] { "Method:UnityEngine.GameObject.AddComponent" }));
        }

        [Test]
        public void SessionApprovals_CoverOnlyTheirItems_AndRemovingOneRevokesIt()
        {
            string hash = "test-" + Guid.NewGuid().ToString("N");
            string revoked = null;
            Action<string> onRevoked = h => revoked = h;
            BasisCilboxPermissions.OnRevoked += onRevoked;
            try
            {
                BasisCilboxPermissions.Allow(hash, "Test Prop", "Prop", new[] { "Type:" + WebRequest }, false);
                Assert.IsTrue(BasisCilboxPermissions.Covers(hash, new[] { "Type:" + WebRequest }));
                Assert.IsFalse(BasisCilboxPermissions.Covers(hash, new[] { "Type:" + WebRequest, "Type:System.IO.File" }));
                Assert.IsFalse(BasisCilboxPermissions.Find(hash).Remembered);
                Assert.IsTrue(BasisCilboxPermissions.GetAll().Exists(a => a.ContentHash == hash));

                BasisCilboxPermissions.Remove(hash);
                Assert.AreEqual(hash, revoked);
                Assert.IsNull(BasisCilboxPermissions.Find(hash));
            }
            finally
            {
                BasisCilboxPermissions.OnRevoked -= onRevoked;
                BasisCilboxPermissions.Remove(hash);
            }
        }

        [Test]
        public void ADecline_LastsForTheSession_UntilTheContentIsAllowed()
        {
            string hash = "test-" + Guid.NewGuid().ToString("N");
            try
            {
                Assert.IsFalse(BasisCilboxPermissions.IsDeclined(hash));
                BasisCilboxPermissions.Decline(hash);
                Assert.IsTrue(BasisCilboxPermissions.IsDeclined(hash));
                BasisCilboxPermissions.Allow(hash, "Test Prop", "Prop", new[] { "Type:" + WebRequest }, false);
                Assert.IsFalse(BasisCilboxPermissions.IsDeclined(hash));
            }
            finally
            {
                BasisCilboxPermissions.Remove(hash);
            }
        }
    }
}
