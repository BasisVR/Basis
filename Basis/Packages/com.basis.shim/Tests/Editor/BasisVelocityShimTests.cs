using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using Basis.Scripts.BasisCharacterController;
using Basis.Scripts.BasisSdk.Players;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisVelocityShimTests
    {
        static readonly Type[] Boxes = { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) };
        static readonly string Read = "get_" + nameof(BasisVelocityShim.Velocity);
        static readonly string[] Writes = { nameof(BasisVelocityShim.SetVelocity), nameof(BasisVelocityShim.AddVelocity) };
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
            GameObject host = new GameObject(boxType.Name + " velocity shim test");
            hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }

        static bool Method(CilboxBasisCommon box, string name) =>
            box.CheckTypeAllowed(typeof(BasisVelocityShim)) && box.CheckMethodAllowed(out MethodInfo _, typeof(BasisVelocityShim), name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), name + "()");

        [Test]
        public void Worlds_SetAddAndRead()
        {
            CilboxBasisCommon scene = Box(typeof(CilboxSceneBasis));
            Assert.IsTrue(Method(scene, Read));
            foreach (string write in Writes) Assert.IsTrue(Method(scene, write), write);
        }

        [Test]
        public void Props_OnlyRead()
        {
            CilboxBasisCommon prop = Box(typeof(CilboxPropBasis));
            Assert.IsTrue(Method(prop, Read));
            foreach (string write in Writes) Assert.IsFalse(Method(prop, write), write + " would let a prop someone else spawned launch every player who can see it.");
        }

        [Test]
        public void Avatars_CannotReachIt()
        {
            CilboxBasisCommon avatar = Box(typeof(CilboxAvatarBasis));
            Assert.IsFalse(avatar.CheckTypeAllowed(typeof(BasisVelocityShim)));
            Assert.IsFalse(Method(avatar, Read));
        }

        [Test]
        public void TheDriverBehindIt_IsNotReachable()
        {
            foreach (Type boxType in Boxes) Assert.IsFalse(Box(boxType).CheckTypeAllowed(typeof(BasisLocalCharacterDriver)), boxType.Name);
        }

        [Test]
        public void TheShim_HasNoNonPublicMembers()
        {
            Assert.IsEmpty(typeof(BasisVelocityShim).GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly),
                "Cilbox resolves non-public members of a whitelisted type, so helpers belong on BasisLocalCharacterDriver.");
        }

        [Test]
        public void AskingToSetIt_ReadsAsMovingYou()
        {
            List<BasisCilboxPermissionText.Line> lines = BasisCilboxPermissionText.Describe(new[] { BasisCilboxPermissionText.MethodPrefix + typeof(BasisVelocityShim).FullName + "." + nameof(BasisVelocityShim.SetVelocity) });
            Assert.AreEqual(1, lines.Count);
            Assert.IsTrue(lines[0].Severe);
            Assert.AreEqual(BasisLocalization.Get("settings.cilboxPermissions.cap.yourPlayer"), lines[0].Text);
        }

        [Test]
        public void WithoutALocalPlayer_NothingHappens()
        {
            Assume.That(BasisLocalPlayer.Instance == null);
            Assert.AreEqual(Vector3.zero, BasisVelocityShim.Velocity);
            Assert.DoesNotThrow(() => BasisVelocityShim.SetVelocity(Vector3.up));
            Assert.DoesNotThrow(() => BasisVelocityShim.AddVelocity(Vector3.up));
        }
    }
}
