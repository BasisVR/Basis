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

        static bool Method(CilboxBasisCommon box, Type type, string name, params Type[] parameters) =>
            box.CheckTypeAllowed(type) &&
            box.CheckMethodAllowed(out MethodInfo _, type, name, Array.ConvertAll(parameters, SerializedTypeDescriptorBuilder.FromNativeType), Array.Empty<SerializedTypeDescriptor>(), name);

        [Test]
        public void ControllerInput_WorldAny_OthersSelfOnly()
        {
            Assert.IsTrue(Method(Box(typeof(CilboxSceneBasis)), typeof(BasisControllerInputShim), nameof(BasisControllerInputShim.GetButtonDown)));
            Assert.IsTrue(Method(Box(typeof(CilboxSceneBasis)), typeof(BasisControllerInputShim), "get_" + nameof(BasisControllerInputShim.IsBlocked)));
            foreach (Type boxType in new[] { typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                Assert.IsFalse(Method(Box(boxType), typeof(BasisControllerInputShim), nameof(BasisControllerInputShim.GetButtonDown), typeof(BasisControllerHand), typeof(BasisControllerButton)), boxType.Name);
                Assert.IsTrue(Method(Box(boxType), typeof(BasisControllerInputShim), nameof(BasisControllerInputShim.GetButtonDown), typeof(object), typeof(BasisControllerHand), typeof(BasisControllerButton)), boxType.Name);
                Assert.IsTrue(Method(Box(boxType), typeof(BasisControllerInputShim), "get_" + nameof(BasisControllerInputShim.IsBlocked)), boxType.Name);
                Assert.IsFalse(Method(Box(boxType), typeof(BasisDesktopInputShim), "get_" + nameof(BasisDesktopInputShim.MouseDelta)), boxType.Name);
                Assert.IsTrue(Method(Box(boxType), typeof(BasisDesktopInputShim), nameof(BasisDesktopInputShim.GetMouseDelta), typeof(object)), boxType.Name);
                Assert.IsFalse(Method(Box(boxType), typeof(BasisDesktopInputShim), nameof(BasisDesktopInputShim.GetMouseButton), typeof(int)), boxType.Name);
                Assert.IsTrue(Method(Box(boxType), typeof(BasisDesktopInputShim), nameof(BasisDesktopInputShim.GetMouseButton), typeof(object), typeof(int)), boxType.Name);
            }
        }

        [Test]
        public void InputRequest_EveryBox()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                Assert.IsTrue(Method(Box(boxType), typeof(BasisInputShim), nameof(BasisInputShim.Request)), boxType.Name);
                Assert.IsTrue(Method(Box(boxType), typeof(BasisInputRequest), nameof(BasisInputRequest.Read)), boxType.Name);
                Assert.IsTrue(Box(boxType).CheckTypeAllowed(typeof(UnityEngine.InputSystem.Key)), boxType.Name);
                Assert.IsTrue(Box(boxType).CheckTypeAllowed(typeof(BasisInputGamepadControl)), boxType.Name);
            }
        }

        [Test]
        public void PoseOverride_EveryBox()
        {
            string[] calls = { nameof(BasisPoseOverrideShim.Claim), nameof(BasisPoseOverrideShim.Release), nameof(BasisPoseOverrideShim.ReleaseAll), nameof(BasisPoseOverrideShim.IsClaimed), nameof(BasisPoseOverrideShim.Set), nameof(BasisPoseOverrideShim.SetRotation), nameof(BasisPoseOverrideShim.SetBlend), nameof(BasisPoseOverrideShim.TryGetUnoverridden), nameof(BasisPoseOverrideShim.ClaimEffector), nameof(BasisPoseOverrideShim.ReleaseEffector), nameof(BasisPoseOverrideShim.IsEffectorClaimed), nameof(BasisPoseOverrideShim.SetEffector), nameof(BasisPoseOverrideShim.SetEffectorAnchored), nameof(BasisPoseOverrideShim.SetEffectorBlend), nameof(BasisPoseOverrideShim.TryGetTracked) };
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                foreach (string call in calls) Assert.IsTrue(Method(Box(boxType), typeof(BasisPoseOverrideShim), call), boxType.Name + "." + call);
            }
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
