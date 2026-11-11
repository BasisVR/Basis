using System;
using System.IO;
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
        private sealed class DirectNetworkFieldScript : BasisNetworkBehaviour
        {
            public bool ReadsInheritedState()
            {
                return HasNetworkID && IsOwnedLocallyOnServer && CurrentOwnerId != 0;
            }
        }

        private sealed class DirectNetworkScriptUsingNativeShim : BasisNetworkBehaviour
        {
            public BasisNetworkShim Shim;

            public bool ReadsShimState()
            {
                return Shim != null &&
                       Shim.HasNetworkID &&
                       Shim.IsOwnedLocallyOnServer &&
                       Shim.CurrentOwnerId != 0;
            }
        }

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
        public void AvatarBox_UsesAvatarHostButKeepsGenericMetadataOverrideCompatible()
        {
            var box = root.AddComponent<CilboxAvatarBasis>();

            Assert.IsTrue(
                box.GetTypeOverride(typeof(BasisNetworkAvatarBehaviour).FullName, out Type avatarReplacement));
            Assert.AreEqual(typeof(BasisNetworkAvatarCilboxBehaviour), avatarReplacement);
            Assert.IsTrue(typeof(BasisNetworkAvatarBehaviour).IsAssignableFrom(avatarReplacement));

            Assert.IsTrue(
                box.GetTypeOverride(typeof(BasisNetworkBehaviour).FullName, out Type genericReplacement));
            Assert.AreEqual(typeof(BasisNetworkCilboxBehaviour), genericReplacement);
            Assert.IsTrue(typeof(BasisNetworkBehaviour).IsAssignableFrom(genericReplacement));
        }

        [Test]
        public void AvatarContentPolice_AllowsOnlyAvatarCilboxNetworkHost()
        {
            const string path = "Packages/com.basis.sdk/Settings/AvatarContentPoliceSelector.asset";
            string asset = File.ReadAllText(path);

            StringAssert.Contains("Basis.Shims.BasisNetworkAvatarCilboxBehaviour", asset);
            StringAssert.DoesNotContain("Basis.Shims.BasisNetworkCilboxBehaviour", asset);
        }

        [Test]
        public void AvatarBox_RedirectsGenericNetworkSendReceiveSurfaceToAvatarHost()
        {
            var box = root.AddComponent<CilboxAvatarBasis>();

            AssertAvatarGenericRedirect(
                box,
                "SendCustomNetworkEvent",
                new[]
                {
                    typeof(byte[]),
                    typeof(Basis.Network.Core.DeliveryMethod),
                    typeof(ushort[]),
                },
                nameof(BasisCilboxNetworkRedirect.AvatarGenericSendCustomNetworkEvent));

            AssertAvatarGenericRedirect(
                box,
                "OnNetworkMessage",
                new[]
                {
                    typeof(ushort),
                    typeof(byte[]),
                    typeof(Basis.Network.Core.DeliveryMethod),
                },
                nameof(BasisCilboxNetworkRedirect.AvatarGenericMessageCallbackNoop));

            AssertAvatarGenericRedirect(
                box,
                "get_NetworkID",
                Type.EmptyTypes,
                nameof(BasisCilboxNetworkRedirect.AvatarGenericGetNetworkID));
        }

        [Test]
        public void LegacyNetworkShimFields_RemainAllowedWhileNewUnsafeFieldsStayBlocked()
        {
            var sceneBox = root.AddComponent<CilboxSceneBasis>();
            Assert.IsTrue(
                sceneBox.GetTypeOverride(typeof(BasisNetworkBehaviour).FullName, out Type sceneReplacement));

            Assert.IsTrue(sceneBox.CheckFieldAllowed(sceneReplacement.FullName, nameof(BasisNetworkBehaviour.HasNetworkID)));
            Assert.IsTrue(sceneBox.CheckFieldAllowed(sceneReplacement.FullName, nameof(BasisNetworkBehaviour.IsOwnedLocallyOnServer)));
            Assert.IsTrue(sceneBox.CheckFieldAllowed(sceneReplacement.FullName, nameof(BasisNetworkBehaviour.CurrentOwnerId)));
            Assert.IsFalse(sceneBox.CheckFieldAllowed(sceneReplacement.FullName, nameof(BasisNetworkBehaviour.IsOwnedLocallyOnClient)));
            Assert.IsFalse(sceneBox.CheckFieldAllowed(sceneReplacement.FullName, nameof(BasisNetworkBehaviour.currentOwnedPlayer)));

            UnityEngine.Object.DestroyImmediate(sceneBox);
            var avatarBox = root.AddComponent<CilboxAvatarBasis>();
            Assert.IsTrue(
                avatarBox.GetTypeOverride(typeof(BasisNetworkBehaviour).FullName, out Type avatarGenericReplacement));
            Assert.AreEqual(typeof(BasisNetworkCilboxBehaviour), avatarGenericReplacement);
            Assert.IsTrue(avatarBox.CheckFieldAllowed(avatarGenericReplacement.FullName, nameof(BasisNetworkBehaviour.HasNetworkID)));
            Assert.IsTrue(avatarBox.CheckFieldAllowed(avatarGenericReplacement.FullName, nameof(BasisNetworkBehaviour.IsOwnedLocallyOnServer)));
            Assert.IsTrue(avatarBox.CheckFieldAllowed(avatarGenericReplacement.FullName, nameof(BasisNetworkBehaviour.CurrentOwnerId)));

            BasisNetworkShim shim = root.AddComponent<BasisNetworkShim>();
            shim.HasNetworkID = true;
            shim.IsOwnedLocallyOnServer = true;
            shim.CurrentOwnerId = 42;

            FieldInfo field = avatarGenericReplacement.GetField(
                nameof(BasisNetworkBehaviour.HasNetworkID),
                BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field);
            Assert.AreEqual(true, field.GetValue(shim));

            Assert.IsTrue(BasisCilboxNetworkRedirect.IsLocalOwner(shim));
            Assert.IsTrue(BasisCilboxNetworkRedirect.AvatarGenericIsLocalOwner(shim));
            Assert.AreEqual(shim.NetworkID, BasisCilboxNetworkRedirect.GetNetworkID(shim));
            Assert.AreEqual(shim.NetworkID, BasisCilboxNetworkRedirect.AvatarGenericGetNetworkID(shim));

            Assert.IsTrue(
                avatarBox.GetTypeOverride(typeof(BasisNetworkAvatarBehaviour).FullName, out Type avatarReplacement));
            Assert.IsFalse(avatarBox.CheckFieldAllowed(avatarReplacement.FullName, nameof(BasisNetworkAvatarBehaviour.NetworkedPlayer)));
            Assert.IsFalse(avatarBox.CheckFieldAllowed(
                typeof(Basis.Scripts.Behaviour.BasisAvatarMonoBehaviour).FullName,
                nameof(Basis.Scripts.Behaviour.BasisAvatarMonoBehaviour.MessageIndex)));
        }

        [Test]
        public void BuildHook_RejectsDirectInheritedNetworkStateFieldAccess()
        {
            CilboxProxy proxy = root.AddComponent<CilboxProxy>();
            proxy.className = typeof(DirectNetworkFieldScript).FullName;

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
                () => InvokeAttachNetworkBridges(root));
            Assert.IsInstanceOf<InvalidOperationException>(exception.InnerException);
            StringAssert.Contains(nameof(BasisNetworkBehaviour.HasNetworkID), exception.InnerException.Message);
            StringAssert.Contains(nameof(BasisNetworkBehaviour.IsOwnedLocallyOnServer), exception.InnerException.Message);
            StringAssert.Contains(nameof(BasisNetworkBehaviour.CurrentOwnerId), exception.InnerException.Message);
        }

        [Test]
        public void BuildHook_AllowsLegacyFieldsWhenReceiverIsNativeNetworkShim()
        {
            CilboxProxy proxy = root.AddComponent<CilboxProxy>();
            proxy.className = typeof(DirectNetworkScriptUsingNativeShim).FullName;

            Assert.DoesNotThrow(() => InvokeAttachNetworkBridges(root));

            BasisNetworkCilboxBehaviour[] bridges =
                root.GetComponents<BasisNetworkCilboxBehaviour>();
            Assert.AreEqual(1, bridges.Length);
            Assert.AreSame(proxy, bridges[0].Target);
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
        public void BuildHook_GenericNetworkScriptInAvatarBox_UsesAvatarBridgeOnly()
        {
            var avatarBox = root.AddComponent<CilboxAvatarBasis>();
            CilboxProxy proxy = root.AddComponent<CilboxProxy>();
            proxy.box = avatarBox;
            proxy.className = typeof(GenericNetworkScript).FullName;

            BasisNetworkCilboxBehaviour staleGeneric =
                root.AddComponent<BasisNetworkCilboxBehaviour>();
            staleGeneric.Bind(proxy);

            InvokeAttachNetworkBridges(root);
            InvokeAttachNetworkBridges(root);

            Assert.AreEqual(0, root.GetComponents<BasisNetworkCilboxBehaviour>().Length);

            BasisNetworkAvatarCilboxBehaviour[] avatarBridges =
                root.GetComponents<BasisNetworkAvatarCilboxBehaviour>();
            Assert.AreEqual(1, avatarBridges.Length);
            Assert.AreSame(proxy, avatarBridges[0].Target);
            Assert.IsTrue(avatarBridges[0].UsesGenericNetworkBehaviourApi);
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

        private static void AssertAvatarGenericRedirect(
            CilboxAvatarBasis box,
            string methodName,
            Type[] parameters,
            string expectedRedirect)
        {
            var descriptors = new SerializedTypeDescriptor[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                descriptors[i] = SerializedTypeDescriptorBuilder.FromNativeType(parameters[i]);
            }

            Assert.IsTrue(
                box.CheckMethodAllowed(
                    out MethodInfo redirect,
                    typeof(BasisNetworkCilboxBehaviour),
                    methodName,
                    descriptors,
                    Array.Empty<SerializedTypeDescriptor>(),
                    string.Empty));
            Assert.IsNotNull(redirect);
            Assert.AreEqual(expectedRedirect, redirect.Name);
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
