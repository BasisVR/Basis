using System;
using System.Reflection;
using Basis;
using Basis.Scripts.Networking.Behaviour;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

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
        public void NetworkStateFields_RemainBlockedBecauseFieldReceiversCannotBeRemapped()
        {
            var sceneBox = root.AddComponent<CilboxSceneBasis>();
            Assert.IsTrue(
                sceneBox.GetTypeOverride(typeof(BasisNetworkBehaviour).FullName, out Type normalReplacement));
            Assert.IsFalse(sceneBox.CheckFieldAllowed(normalReplacement.FullName, nameof(BasisNetworkBehaviour.HasNetworkID)));
            Assert.IsFalse(sceneBox.CheckFieldAllowed(normalReplacement.FullName, nameof(BasisNetworkBehaviour.IsOwnedLocallyOnClient)));
            Assert.IsFalse(sceneBox.CheckFieldAllowed(normalReplacement.FullName, nameof(BasisNetworkBehaviour.currentOwnedPlayer)));

            UnityEngine.Object.DestroyImmediate(sceneBox);
            var avatarBox = root.AddComponent<CilboxAvatarBasis>();
            Assert.IsTrue(
                avatarBox.GetTypeOverride(typeof(BasisNetworkAvatarBehaviour).FullName, out Type avatarReplacement));
            Assert.IsFalse(avatarBox.CheckFieldAllowed(avatarReplacement.FullName, nameof(BasisNetworkAvatarBehaviour.NetworkedPlayer)));
            Assert.IsFalse(avatarBox.CheckFieldAllowed(
                typeof(Basis.Scripts.Behaviour.BasisAvatarMonoBehaviour).FullName,
                nameof(Basis.Scripts.Behaviour.BasisAvatarMonoBehaviour.MessageIndex)));
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

        [Test]
        public void UnboundNormalRedirect_DegradesToSafeDefaults()
        {
            CilboxProxy proxy = root.AddComponent<CilboxProxy>();

            LogAssert.Expect(
                LogType.Error,
                "[BasisCilboxNetwork] No BasisNetworkCilboxBehaviour is bound to this interpreted networking instance; the operation was ignored.");

            Assert.AreEqual(0, BasisCilboxNetworkRedirect.GetNetworkID(proxy));
            Assert.IsFalse(BasisCilboxNetworkRedirect.IsLocalOwner(proxy));
            Assert.DoesNotThrow(() => BasisCilboxNetworkRedirect.SendCustomNetworkEvent(
                proxy, null, Basis.Network.Core.DeliveryMethod.Unreliable, null));
            Assert.IsFalse(BasisCilboxNetworkRedirect.TakeOwnershipAsync(proxy, 1).Result.Success);
            Assert.IsFalse(BasisCilboxNetworkRedirect.RequestWhoIsOwnershipAsync(proxy, 1).Result.Success);
        }

        [Test]
        public void UnboundAvatarRedirect_DropsSendWithoutThrowing()
        {
            CilboxProxy proxy = root.AddComponent<CilboxProxy>();

            LogAssert.Expect(
                LogType.Error,
                "[BasisCilboxNetwork] No BasisNetworkAvatarCilboxBehaviour is bound to this interpreted networking instance; the operation was ignored.");

            Assert.DoesNotThrow(() => BasisCilboxNetworkRedirect.NetworkMessageSend(
                proxy, null, Basis.Network.Core.DeliveryMethod.Unreliable, null));
            Assert.DoesNotThrow(() => BasisCilboxNetworkRedirect.NetworkMessageSendDirect(
                proxy, null, Basis.Network.Core.DeliveryMethod.Unreliable, null, true));
        }

        [Test]
        public void Dispatch_DropsDisabledProxyBeforeMethodLookup()
        {
            CilboxProxy proxy = root.AddComponent<CilboxProxy>();
            proxy.disabled = true;
            proxy.enabled = false;
            proxy.cls = new CilboxClass
            {
                methodNameToIndex = null,
                methods = null,
            };

            Assert.DoesNotThrow(() => InvokeDispatch(proxy, "OnNetworkMessage", null));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Dispatch_LogsArityMismatchAndDoesNotInterpret()
        {
            CilboxProxy proxy = root.AddComponent<CilboxProxy>();
            proxy.className = "TestNetworkScript";
            proxy.cls = new CilboxClass
            {
                methodNameToIndex = new System.Collections.Generic.Dictionary<string, uint>
                {
                    ["OnNetworkMessage"] = 0,
                },
                methods = new[]
                {
                    new CilboxMethod
                    {
                        methodName = "OnNetworkMessage",
                        signatureParameters = new[] { "sender" },
                        isStatic = false,
                    },
                },
            };

            LogAssert.Expect(
                LogType.Error,
                "[BasisCilboxNetwork] TestNetworkScript.OnNetworkMessage expected 1 arguments, got 0.");

            InvokeDispatch(proxy, "OnNetworkMessage", null);
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

        private static void InvokeDispatch(CilboxProxy proxy, string methodName, object[] arguments)
        {
            Type dispatchType = typeof(BasisNetworkCilboxBehaviour).Assembly.GetType(
                "Basis.Shims.BasisCilboxNetworkDispatch");
            Assert.IsNotNull(dispatchType);

            MethodInfo method = dispatchType.GetMethod(
                "Invoke",
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, new object[] { proxy, methodName, arguments });
        }
    }
}
