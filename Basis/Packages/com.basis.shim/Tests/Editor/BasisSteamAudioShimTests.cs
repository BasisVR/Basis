using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Shims
{
    public sealed class BasisSteamAudioShimTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();
        [TearDown]
        public void TearDown()
        {
            foreach (GameObject host in hosts) if (host != null) UnityEngine.Object.DestroyImmediate(host);
            hosts.Clear();
        }
        GameObject Host(string name)
        {
            GameObject host = new GameObject(name);
            hosts.Add(host);
            return host;
        }
        CilboxBasisCommon Box(Type boxType)
        {
            CilboxBasisCommon box = (CilboxBasisCommon)Host(boxType.Name).AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }
        static bool Method(CilboxBasisCommon box, string name) => box.CheckTypeAllowed(typeof(BasisSteamAudioShim)) && box.CheckMethodAllowed(out MethodInfo _, typeof(BasisSteamAudioShim), name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), name + "()");
        [TestCase(typeof(CilboxSceneBasis))]
        [TestCase(typeof(CilboxPropBasis))]
        [TestCase(typeof(CilboxAvatarBasis))]
        public void EveryBoxReachesTheShim(Type boxType)
        {
            CilboxBasisCommon box = Box(boxType);
            Assert.IsTrue(Method(box, nameof(BasisSteamAudioShim.Add)));
            Assert.IsTrue(Method(box, nameof(BasisSteamAudioShim.SetOcclusion)));
            Assert.IsTrue(Method(box, nameof(BasisSteamAudioShim.SetReflections)));
            Assert.IsTrue(Method(box, nameof(BasisSteamAudioShim.GetOcclusion)));
            Assert.IsTrue(Method(box, nameof(BasisSteamAudioShim.AddGeometry)));
        }
        [Test]
        public void RemotePlayerAudioIsWorldOnly()
        {
            bool Reach(Type boxType)
            {
                CilboxBasisCommon box = Box(boxType);
                return box.CheckTypeAllowed(typeof(BasisRemotePlayerAudioShim)) && box.CheckMethodAllowed(out MethodInfo _, typeof(BasisRemotePlayerAudioShim), nameof(BasisRemotePlayerAudioShim.SetOcclusion), Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), "SetOcclusion()");
            }
            Assert.IsTrue(Reach(typeof(CilboxSceneBasis)));
            Assert.IsFalse(Reach(typeof(CilboxPropBasis)));
            Assert.IsFalse(Reach(typeof(CilboxAvatarBasis)));
        }
        [Test]
        public void RemotePlayerAudioRefusesCallersThatAreNotRunningScripts()
        {
            Assert.IsFalse(BasisRemotePlayerAudioShim.SetOcclusion(null, null, true));
            Assert.IsFalse(BasisRemotePlayerAudioShim.Clear(new object(), null));
            Assert.AreEqual(0, BasisRemotePlayerAudioShim.ClearAll(null));
            Assert.AreEqual(0, BasisRemotePlayerAudioShim.RemainingReflectionPlayers(null));
            Assert.AreEqual(1f, BasisRemotePlayerAudioShim.GetOcclusion(null, null));
        }
        [Test]
        public void CallerThatIsNotARunningScriptGetsNothing()
        {
            AudioSource source = Host("source").AddComponent<AudioSource>();
            MeshFilter filter = Host("geometry").AddComponent<MeshFilter>();
            object[] callers = { null, new object(), source };
            foreach (object caller in callers)
            {
                Assert.IsFalse(BasisSteamAudioShim.Add(caller, source));
                Assert.IsFalse(BasisSteamAudioShim.Has(caller, source));
                Assert.IsFalse(BasisSteamAudioShim.SetOcclusion(caller, source, true));
                Assert.IsFalse(BasisSteamAudioShim.SetTransmission(caller, source, true, true, 8));
                Assert.IsFalse(BasisSteamAudioShim.SetReflections(caller, source, true, 1f, false));
                Assert.IsFalse(BasisSteamAudioShim.AddGeometry(caller, filter));
                Assert.IsFalse(BasisSteamAudioShim.RemoveGeometry(caller, filter));
                Assert.AreEqual(1f, BasisSteamAudioShim.GetOcclusion(caller, source));
                Assert.AreEqual(1f, BasisSteamAudioShim.GetTransmissionMid(caller, source));
                Assert.AreEqual(0, BasisSteamAudioShim.RemainingAddedSources(caller));
                Assert.AreEqual(0, BasisSteamAudioShim.RemainingReflectionSources(caller));
                Assert.AreEqual(0, BasisSteamAudioShim.RemainingGeometryTriangles(caller));
            }
        }
        [Test]
        public void NullTargetsAreRefused()
        {
            Assert.IsFalse(BasisSteamAudioShim.Add(null, null));
            Assert.IsFalse(BasisSteamAudioShim.SetDirectMix(null, null, 1f));
            Assert.IsFalse(BasisSteamAudioShim.AddGeometry(null, null));
            Assert.AreEqual(1f, BasisSteamAudioShim.GetOcclusion(null, null));
        }
    }
}
