using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using Cilbox;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisCilboxProtectedObjectsTests
    {
        static readonly Type Guard = typeof(CilboxBasisCommon).Assembly.GetType("Cilbox.CilboxProtectedObjects", true);
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        GameObject Host(string name)
        {
            GameObject host = new GameObject(name);
            hosts.Add(host);
            return host;
        }

        static void Invoke(string name, Type[] signature, params object[] arguments)
        {
            MethodInfo method = Guard.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, signature, null);
            Assert.IsNotNull(method, $"CilboxProtectedObjects.{name} moved");
            try
            {
                method.Invoke(null, arguments);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
        }

        static void SetActive(GameObject target, bool value) => Invoke("SetActive", new[] { typeof(GameObject), typeof(bool) }, target, value);
        static void DestroyImmediate(UnityEngine.Object target) => Invoke("DestroyImmediate", new[] { typeof(UnityEngine.Object) }, target);

        static MethodInfo Resolved(CilboxBasisCommon box, Type declaringType, string name, params Type[] parameters)
        {
            SerializedTypeDescriptor[] descriptors = Array.ConvertAll(parameters, SerializedTypeDescriptorBuilder.FromNativeType);
            Assert.IsTrue(box.CheckMethodAllowed(out MethodInfo method, declaringType, name, descriptors, Array.Empty<SerializedTypeDescriptor>(), name), name);
            return method;
        }

        [Test]
        public void SetActiveAndDestroy_ResolveToTheGuard_InEveryBox()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                CilboxBasisCommon box = (CilboxBasisCommon)Host(boxType.Name).AddComponent(boxType);
                box.classes = new Dictionary<string, int>();
                box.usage = new CilboxUsage(box);
                Assert.AreEqual(Guard, Resolved(box, typeof(GameObject), nameof(GameObject.SetActive), typeof(bool))?.DeclaringType, boxType.Name);
                Assert.AreEqual(Guard, Resolved(box, typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), typeof(UnityEngine.Object))?.DeclaringType, boxType.Name);
                Assert.AreEqual(Guard, Resolved(box, typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), typeof(UnityEngine.Object), typeof(float))?.DeclaringType, boxType.Name);
                Assert.AreEqual(Guard, Resolved(box, typeof(UnityEngine.Object), nameof(UnityEngine.Object.DestroyImmediate), typeof(UnityEngine.Object))?.DeclaringType, boxType.Name);
            }
        }

        [Test]
        public void ContentObjects_CanBeSwitchedAndDestroyed()
        {
            GameObject prop = Host("Prop");
            prop.AddComponent<BasisProp>();
            GameObject part = new GameObject("Part");
            part.transform.SetParent(prop.transform, false);
            SetActive(part, false);
            Assert.IsFalse(part.activeSelf);
            SetActive(part, true);
            Assert.IsTrue(part.activeSelf);
            BoxCollider collider = part.AddComponent<BoxCollider>();
            DestroyImmediate(collider);
            Assert.IsTrue(collider == null);
            DestroyImmediate(part);
            Assert.IsTrue(part == null);
        }

        [Test]
        public void AvatarTransforms_StayAccessible_EvenUnderAPlayerRig()
        {
            if (UnityEngine.Object.FindAnyObjectByType<BasisScene>(FindObjectsInactive.Include) != null) Assert.Ignore("this test needs a scene without a world in it");
            GameObject rig = Host("Player rig");
            GameObject avatar = new GameObject("Avatar");
            avatar.transform.SetParent(rig.transform, false);
            avatar.AddComponent<BasisAvatar>();
            GameObject hips = new GameObject("Hips");
            hips.transform.SetParent(avatar.transform, false);
            GameObject hand = new GameObject("Hand");
            hand.transform.SetParent(hips.transform, false);

            SetActive(hand, false);
            Assert.IsFalse(hand.activeSelf, "a bone of the local avatar can be switched off");
            SetActive(hand, true);
            Assert.IsTrue(hand.activeSelf);
            SetActive(avatar, false);
            Assert.IsFalse(avatar.activeSelf, "the avatar root itself can be switched off");
            SetActive(avatar, true);
            BoxCollider collider = hips.AddComponent<BoxCollider>();
            DestroyImmediate(collider);
            Assert.IsTrue(collider == null, "avatar components can be destroyed");

            SetActive(rig, false);
            Assert.IsTrue(rig.activeSelf, "the rig holding the avatar is the player's, not the avatar's");

            GameObject remote = Host("Remote avatar");
            remote.AddComponent<BasisAvatar>();
            GameObject head = new GameObject("Head");
            head.transform.SetParent(remote.transform, false);
            SetActive(head, false);
            Assert.IsFalse(head.activeSelf, "a remote avatar outside any world scene is still the avatar's");
            DestroyImmediate(head);
            Assert.IsTrue(head == null);
        }

        [Test]
        public void ObjectsOutsideAnyContentOrWorld_AreRefused()
        {
            LogAssert.ignoreFailingMessages = true;
            GameObject framework = Host("Framework driver");
            if (UnityEngine.Object.FindAnyObjectByType<BasisScene>(FindObjectsInactive.Include) != null) Assert.Ignore("this test needs a scene without a world in it");
            SetActive(framework, false);
            Assert.IsTrue(framework.activeSelf, "an object with no content in a scene that is not a world belongs to Basis");
            BoxCollider collider = framework.AddComponent<BoxCollider>();
            DestroyImmediate(collider);
            Assert.IsFalse(collider == null, "its components cannot be destroyed either");
            DestroyImmediate(framework);
            Assert.IsFalse(framework == null);
            ScriptableObject asset = ScriptableObject.CreateInstance<ScriptableObject>();
            DestroyImmediate(asset);
            Assert.IsTrue(asset == null, "assets are not scene objects and keep the old behaviour");
            Assert.Throws<NullReferenceException>(() => SetActive(null, true));
        }
    }
}
