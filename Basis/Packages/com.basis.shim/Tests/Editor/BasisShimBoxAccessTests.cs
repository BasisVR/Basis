using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Which box reaches the controller input, audio routing, language event and shared random shims.
    /// </summary>
    public sealed class BasisShimBoxAccessTests
    {
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

        CilboxBasisCommon Box(Type boxType)
        {
            GameObject host = new GameObject(boxType.Name + " shim access test");
            hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }

        static bool Method(CilboxBasisCommon box, Type type, string name) =>
            box.CheckTypeAllowed(type) &&
            box.CheckMethodAllowed(out MethodInfo _, type, name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), name + "()");

        [Test]
        public void ControllerInput_WorldOnly()
        {
            Assert.IsTrue(Method(Box(typeof(CilboxSceneBasis)), typeof(BasisControllerInputShim), nameof(BasisControllerInputShim.GetButtonDown)));
            Assert.IsTrue(Method(Box(typeof(CilboxSceneBasis)), typeof(BasisControllerInputShim), "get_" + nameof(BasisControllerInputShim.IsBlocked)));
            Assert.IsFalse(Method(Box(typeof(CilboxPropBasis)), typeof(BasisControllerInputShim), nameof(BasisControllerInputShim.GetButtonDown)));
            Assert.IsFalse(Method(Box(typeof(CilboxAvatarBasis)), typeof(BasisControllerInputShim), nameof(BasisControllerInputShim.GetButtonDown)));
        }


        [Test]
        public void LanguageEvent_EveryBox_RebindOnly()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                Assert.IsTrue(Method(Box(boxType), typeof(BasisLanguageEventShim), nameof(BasisLanguageEventShim.Rebind)), boxType.Name);
                Assert.IsFalse(Method(Box(boxType), typeof(BasisLanguageEventShim), "SendMessage"), boxType.Name);
            }
        }

        [Test]
        public void SharedRandom_EveryBox()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                Assert.IsTrue(Method(Box(boxType), typeof(BasisSharedRandomShim), nameof(BasisSharedRandomShim.Value)), boxType.Name);
                Assert.IsTrue(Method(Box(boxType), typeof(BasisSharedRandomShim), nameof(BasisSharedRandomShim.NowRangeInt)), boxType.Name);
            }
        }
    }
}
