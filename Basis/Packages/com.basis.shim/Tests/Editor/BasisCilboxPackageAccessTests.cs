using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk.Cilbox;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [BasisCilboxAllow(BasisCilboxBoxes.World | BasisCilboxBoxes.Prop)]
    public static class BasisCilboxPackageAccessProbe
    {
        public static int Read() => 1;
        [BasisCilboxBlock(BasisCilboxBoxes.Prop)] public static void Write() { }
        [BasisCilboxBlock(BasisCilboxBoxes.All)] public static int Secret => 2;
        [BasisCilboxAllow(BasisCilboxBoxes.World)] public static int Shared;
        public static int Hidden;
        public struct Unmarked { public int Value; }
        [BasisCilboxAllow(BasisCilboxBoxes.All, AllFields = true)] public struct Data { public int Value; }
    }

    [BasisCilboxAllow(BasisCilboxBoxes.Avatar, Nested = true)]
    public sealed class BasisCilboxPackageAccessNested
    {
        public enum Mode { A, B }
    }

    public sealed class BasisCilboxPackageAccessTests
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
            GameObject host = new GameObject(boxType.Name + " package access test");
            hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }

        static bool Method(CilboxBasisCommon box, Type type, string name) =>
            box.CheckTypeAllowed(type) &&
            box.CheckMethodAllowed(out MethodInfo _, type, name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), name + "()");

        [Test]
        public void Type_FollowsBoxes()
        {
            Assert.IsTrue(Box(typeof(CilboxSceneBasis)).CheckTypeAllowed(typeof(BasisCilboxPackageAccessProbe)));
            Assert.IsTrue(Box(typeof(CilboxPropBasis)).CheckTypeAllowed(typeof(BasisCilboxPackageAccessProbe)));
            Assert.IsFalse(Box(typeof(CilboxAvatarBasis)).CheckTypeAllowed(typeof(BasisCilboxPackageAccessProbe)));
        }

        [Test]
        public void Block_HoldsBackMethodsAndProperties()
        {
            Assert.IsTrue(Method(Box(typeof(CilboxSceneBasis)), typeof(BasisCilboxPackageAccessProbe), nameof(BasisCilboxPackageAccessProbe.Read)));
            Assert.IsTrue(Method(Box(typeof(CilboxSceneBasis)), typeof(BasisCilboxPackageAccessProbe), nameof(BasisCilboxPackageAccessProbe.Write)));
            Assert.IsFalse(Method(Box(typeof(CilboxPropBasis)), typeof(BasisCilboxPackageAccessProbe), nameof(BasisCilboxPackageAccessProbe.Write)));
            Assert.IsFalse(Method(Box(typeof(CilboxSceneBasis)), typeof(BasisCilboxPackageAccessProbe), "get_" + nameof(BasisCilboxPackageAccessProbe.Secret)));
        }

        [Test]
        public void Fields_DefaultDeny()
        {
            Assert.IsTrue(Box(typeof(CilboxSceneBasis)).CheckFieldAllowed(typeof(BasisCilboxPackageAccessProbe), nameof(BasisCilboxPackageAccessProbe.Shared)));
            Assert.IsFalse(Box(typeof(CilboxPropBasis)).CheckFieldAllowed(typeof(BasisCilboxPackageAccessProbe), nameof(BasisCilboxPackageAccessProbe.Shared)));
            Assert.IsFalse(Box(typeof(CilboxSceneBasis)).CheckFieldAllowed(typeof(BasisCilboxPackageAccessProbe), nameof(BasisCilboxPackageAccessProbe.Hidden)));
            Assert.IsTrue(Box(typeof(CilboxAvatarBasis)).CheckFieldAllowed(typeof(BasisCilboxPackageAccessProbe.Data), nameof(BasisCilboxPackageAccessProbe.Data.Value)));
        }

        [Test]
        public void Nested_OnlyWhenAsked()
        {
            Assert.IsFalse(Box(typeof(CilboxSceneBasis)).CheckTypeAllowed(typeof(BasisCilboxPackageAccessProbe.Unmarked)));
            Assert.IsTrue(Box(typeof(CilboxAvatarBasis)).CheckTypeAllowed(typeof(BasisCilboxPackageAccessNested.Mode)));
            Assert.IsFalse(Box(typeof(CilboxSceneBasis)).CheckTypeAllowed(typeof(BasisCilboxPackageAccessNested.Mode)));
        }
    }
}
