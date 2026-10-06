using System;
using System.Reflection;
using Basis;
using Basis.Scripts.Networking.Behaviour;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisCilboxNetworkOverrideTests
    {
        private sealed class GenericNetworkScript : BasisNetworkBehaviour { }
        private sealed class AvatarNetworkScript : BasisNetworkAvatarBehaviour { }

        private GameObject root;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Cilbox network override tests");
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SceneAndPropBoxes_OverrideGenericNetworkBehaviour()
        {
            AssertGenericOverride(root.AddComponent<CilboxSceneBasis>());
            UnityEngine.Object.DestroyImmediate(root.GetComponent<CilboxSceneBasis>());

            AssertGenericOverride(root.AddComponent<CilboxPropBasis>());
        }

        [Test]
        public void AvatarBox_OverridesAvatarNetworkBehaviourWithAvatarVariant()
        {
            var box = root.AddComponent<CilboxAvatarBasis>();

            Assert.IsTrue(
                box.GetTypeOverride(typeof(BasisNetworkAvatarBehaviour).FullName, out Type replacement));
            Assert.AreEqual(typeof(BasisNetworkAvatarCilboxBehaviour), replacement);
            Assert.IsTrue(typeof(BasisNetworkAvatarBehaviour).IsAssignableFrom(replacement));
        }

        [Test]
        public void BuildHook_AttachesMatchingBridgePerProxy_WithoutDuplicates()
        {
            CilboxProxy genericProxy = root.AddComponent<CilboxProxy>();
            genericProxy.className = typeof(GenericNetworkScript).FullName;

            GameObject avatarObject = new GameObject("Avatar proxy");
            avatarObject.transform.SetParent(root.transform, false);
            CilboxProxy avatarProxy = avatarObject.AddComponent<CilboxProxy>();
            avatarProxy.className = typeof(AvatarNetworkScript).FullName;

            InvokeAttachNetworkBridges(root);
            InvokeAttachNetworkBridges(root);

            BasisNetworkCilboxBehaviour[] genericBridges =
                root.GetComponents<BasisNetworkCilboxBehaviour>();
            BasisNetworkAvatarCilboxBehaviour[] avatarBridges =
                avatarObject.GetComponents<BasisNetworkAvatarCilboxBehaviour>();

            Assert.AreEqual(1, genericBridges.Length);
            Assert.AreSame(genericProxy, genericBridges[0].Target);
            Assert.AreEqual(1, avatarBridges.Length);
            Assert.AreSame(avatarProxy, avatarBridges[0].Target);

            Assert.IsInstanceOf<BasisNetworkBehaviour>(genericBridges[0]);
            Assert.IsInstanceOf<BasisNetworkAvatarBehaviour>(avatarBridges[0]);
        }

        [Test]
        public void NormalRedirect_ResolvesCilboxProxyToItsBoundNetworkHost()
        {
            CilboxProxy proxy = root.AddComponent<CilboxProxy>();
            BasisNetworkCilboxBehaviour bridge = root.AddComponent<BasisNetworkCilboxBehaviour>();
            bridge.Bind(proxy);

            Assert.AreEqual(bridge.NetworkID, BasisCilboxNetworkRedirect.GetNetworkID(proxy));
            Assert.AreEqual(bridge.IsLocalOwner(), BasisCilboxNetworkRedirect.IsLocalOwner(proxy));
        }

        private static void AssertGenericOverride(Cilbox.Cilbox box)
        {
            Assert.IsTrue(
                box.GetTypeOverride(typeof(BasisNetworkBehaviour).FullName, out Type replacement));
            Assert.AreEqual(typeof(BasisNetworkCilboxBehaviour), replacement);
            Assert.IsTrue(typeof(BasisNetworkBehaviour).IsAssignableFrom(replacement));
        }

        private static void InvokeAttachNetworkBridges(GameObject contentRoot)
        {
            MethodInfo method = typeof(BasisCilboxBuildHook).GetMethod(
                "AttachNetworkBridges",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method);
            method.Invoke(null, new object[] { contentRoot });
        }
    }
}
