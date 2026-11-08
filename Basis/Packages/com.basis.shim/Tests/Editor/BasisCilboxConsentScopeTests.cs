using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisCilboxConsentScopeTests : BasisCilboxGateRig
    {
        const string Scripts = "same compiled scripts";
        static string KeyOf(CilboxBasisCommon box, IList<CilboxProxy> proxies) => (string)ShimMethod("Cilbox.CilboxBasisConsent", "KeyOf").Invoke(null, new object[] { box, proxies });
        CilboxBasisCommon Content()
        {
            CilboxBasisCommon box = Box(typeof(CilboxPropBasis));
            box.assemblyData = Scripts;
            return box;
        }
        static CilboxProxy Proxy(CilboxBasisCommon box, string data, params UnityEngine.Object[] references)
        {
            GameObject host = new GameObject("proxy");
            host.transform.SetParent(box.transform);
            CilboxProxy proxy = host.AddComponent<CilboxProxy>();
            proxy.box = box;
            proxy.className = "Spinner";
            proxy.serializedObjectData = data;
            proxy.fieldsObjects = new List<UnityEngine.Object>(references);
            return proxy;
        }
        [Test]
        public void IdenticalScripts_WithDifferentFieldValues_GetDifferentKeys()
        {
            CilboxBasisCommon approved = Content(), copy = Content();
            string approvedKey = KeyOf(approved, new[] { Proxy(approved, "speed=1") });
            string copyKey = KeyOf(copy, new[] { Proxy(copy, "speed=900") });
            Assert.AreNotEqual(approvedKey, copyKey);
        }
        [Test]
        public void IdenticalScripts_WithDifferentObjectReferences_GetDifferentKeys()
        {
            CilboxBasisCommon approved = Content(), copy = Content();
            GameObject mild = Own(new GameObject("mild")), other = Own(new GameObject("other"));
            Assert.AreNotEqual(KeyOf(approved, new[] { Proxy(approved, "target=0", mild) }), KeyOf(copy, new[] { Proxy(copy, "target=0", other) }));
        }
        [Test]
        public void IdenticalContent_StillSharesOneKey_InAnyOrder()
        {
            CilboxBasisCommon first = Content(), second = Content();
            string a = KeyOf(first, new[] { Proxy(first, "speed=1"), Proxy(first, "speed=2") });
            string b = KeyOf(second, new[] { Proxy(second, "speed=2"), Proxy(second, "speed=1") });
            Assert.AreEqual(a, b, "the same avatar worn twice keeps one approval");
        }
        [Test]
        public void ProxiesOfAnotherBox_DoNotChangeTheKey()
        {
            CilboxBasisCommon mine = Content(), theirs = Content();
            CilboxProxy own = Proxy(mine, "speed=1");
            Assert.AreEqual(KeyOf(mine, new[] { own }), KeyOf(mine, new[] { own, Proxy(theirs, "speed=900") }));
        }
        [Test]
        public void TheCodeOnlyKey_IsUnchanged_SoSaveStoresKeepTheirFolder()
        {
            CilboxBasisCommon first = Content(), second = Content();
            string legacy;
            using (SHA256 sha = SHA256.Create()) legacy = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(typeof(CilboxPropBasis).FullName + "\n" + Scripts))).Replace("-", string.Empty).ToLowerInvariant();
            Assert.AreEqual(legacy, KeyOf(first, null), "save and server stores without a Content ID are named from this key");
            Assert.AreEqual(KeyOf(first, null), KeyOf(second, null));
            Assert.AreNotEqual(KeyOf(first, null), KeyOf(first, new[] { Proxy(first, "speed=1") }));
            FieldInfo code = Field("codeHash");
            Assert.AreEqual(typeof(string), code.FieldType);
        }
    }
}
