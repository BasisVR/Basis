using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Transmitters;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisPoseOverrideShimTests
    {
        const ushort LocalId = 7, RemoteId = 8;
        const int RightHandSlot = (int)HumanBodyBones.RightHand;
        static readonly Type Overrides = typeof(BasisPoseOverrideShim).Assembly.GetType("Cilbox.CilboxPoseOverrides", true);
        static readonly PropertyInfo LocalPlayerInstance = typeof(BasisLocalPlayer).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        readonly List<GameObject> hosts = new List<GameObject>();
        BasisNetworkTransmitter transmitterBefore;
        BasisLocalPlayer localBefore;
        BasisLocalRigDriver rig;

        static int ClaimCount => (int)Overrides.GetProperty("ClaimCount", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        static void Tick() => Overrides.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);

        [SetUp]
        public void SetUp()
        {
            transmitterBefore = BasisNetworkManagement.Transmitter;
            localBefore = BasisLocalPlayer.Instance;
            BasisNetworkManagement.Transmitter = new BasisNetworkTransmitter(LocalId);
            GameObject playerHost = new GameObject("pose test local player");
            playerHost.SetActive(false);
            hosts.Add(playerHost);
            BasisLocalPlayer player = playerHost.AddComponent<BasisLocalPlayer>();
            rig = new BasisLocalRigDriver();
            BasisEerieMovementSetup.SetDefaultValues(ref rig.IKJob);
            player.LocalRigDriver = rig;
            LocalPlayerInstance.SetValue(null, player);
            Assume.That(ClaimCount, Is.EqualTo(0), "a claim leaked from another test");
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = hosts.Count - 1; i >= 1; i--) if (hosts[i] != null) UnityEngine.Object.DestroyImmediate(hosts[i]);
            Tick();
            if (hosts.Count > 0 && hosts[0] != null) UnityEngine.Object.DestroyImmediate(hosts[0]);
            hosts.Clear();
            LocalPlayerInstance.SetValue(null, localBefore);
            BasisNetworkManagement.Transmitter = transmitterBefore;
        }

        GameObject Content<T>() where T : BasisContentBase
        {
            GameObject root = new GameObject(typeof(T).Name + " pose test");
            hosts.Add(root);
            root.AddComponent<T>();
            return root;
        }

        GameObject Prop(bool spawnedHere)
        {
            GameObject root = Content<BasisProp>();
            root.GetComponent<BasisProp>().AssignContentIdentifier(new BasisNetworkContentBase.BasisContentInformation { LoadedNetID = "pose-test", SpawnedByLocalPlayer = spawnedHere });
            return root;
        }

        GameObject Avatar(ushort linkedPlayer)
        {
            GameObject root = Content<BasisAvatar>();
            root.GetComponent<BasisAvatar>().LinkedPlayerID = linkedPlayer;
            return root;
        }

        CilboxProxy Script(Type boxType, GameObject content, CilboxBasisCommon shared = null)
        {
            CilboxBasisCommon box = shared;
            if (box == null)
            {
                GameObject host = new GameObject(boxType.Name + " pose test");
                if (content != null) host.transform.SetParent(content.transform);
                else hosts.Add(host);
                box = (CilboxBasisCommon)host.AddComponent(boxType);
                box.classes = new Dictionary<string, int>();
                box.interpreterAccountingDepth = 1;
            }
            CilboxProxy proxy = new GameObject("Script").AddComponent<CilboxProxy>();
            proxy.transform.SetParent(box.transform);
            proxy.box = box;
            return proxy;
        }

        bool Weight(int slot) => rig.IKJob.slotWeights[slot];
        float Blend(int slot) => rig.IKJob.slotBlends[slot];

        [Test]
        public void Refused_WithoutInterpreterCaller()
        {
            Assert.IsFalse(BasisPoseOverrideShim.Claim(null, HumanBodyBones.RightHand));
            Assert.IsFalse(BasisPoseOverrideShim.Claim(new object(), HumanBodyBones.RightHand));
            CilboxProxy idle = Script(typeof(CilboxSceneBasis), null);
            idle.box.interpreterAccountingDepth = 0;
            Assert.IsFalse(BasisPoseOverrideShim.Claim(idle, HumanBodyBones.RightHand), "a call from outside the interpreter");
            Assert.AreEqual(0, ClaimCount);
        }

        [Test]
        public void OnlyContentThatRunsForThisPlayer()
        {
            Assert.IsFalse(BasisPoseOverrideShim.Claim(Script(typeof(CilboxAvatarBasis), Avatar(RemoteId)), HumanBodyBones.RightHand), "another player's avatar runs on this client too");
            Assert.IsFalse(BasisPoseOverrideShim.Claim(Script(typeof(CilboxPropBasis), Prop(false)), HumanBodyBones.RightHand), "another player's prop");
            Assert.IsFalse(BasisPoseOverrideShim.TryGetUnoverridden(Script(typeof(CilboxPropBasis), Prop(false)), HumanBodyBones.RightHand, out _, out _));
            Assert.IsTrue(BasisPoseOverrideShim.Claim(Script(typeof(CilboxSceneBasis), null), HumanBodyBones.Head));
            Assert.IsTrue(BasisPoseOverrideShim.Claim(Script(typeof(CilboxAvatarBasis), Avatar(LocalId)), HumanBodyBones.LeftHand));
            Assert.IsTrue(BasisPoseOverrideShim.Claim(Script(typeof(CilboxPropBasis), Prop(true)), HumanBodyBones.RightHand));
            Assert.AreEqual(3, ClaimCount);
        }

        [Test]
        public void Usage_StartsWithTheFirstPose_AndReleaseRestoresIt()
        {
            CilboxProxy script = Script(typeof(CilboxSceneBasis), null);
            Assert.IsTrue(BasisPoseOverrideShim.Claim(script, HumanBodyBones.RightHand));
            Assert.IsFalse(Weight(RightHandSlot), "a claim with no pose yet must not snap the hand to identity");
            Assert.IsTrue(BasisPoseOverrideShim.SetRotation(script, HumanBodyBones.RightHand, Quaternion.Euler(0f, 90f, 0f)));
            Assert.IsTrue(Weight(RightHandSlot));
            Assert.IsTrue(BasisPoseOverrideShim.SetBlend(script, HumanBodyBones.RightHand, 0.25f));
            Assert.AreEqual(0.25f, Blend(RightHandSlot));
            Assert.IsTrue(BasisPoseOverrideShim.SetBlend(script, HumanBodyBones.RightHand, 7f));
            Assert.AreEqual(1f, Blend(RightHandSlot));
            BasisPoseOverrideShim.SetBlend(script, HumanBodyBones.RightHand, 0.5f);
            BasisPoseOverrideShim.Release(script, HumanBodyBones.RightHand);
            Assert.IsFalse(Weight(RightHandSlot));
            Assert.AreEqual(1f, Blend(RightHandSlot), "the next user of the slot gets a full blend");
            Assert.IsFalse(BasisPoseOverrideShim.IsClaimed(script, HumanBodyBones.RightHand));
            Assert.AreEqual(0, ClaimCount);
        }

        [Test]
        public void AHeldBone_BelongsToItsContent()
        {
            CilboxProxy first = Script(typeof(CilboxSceneBasis), null), sibling = Script(typeof(CilboxSceneBasis), null, first.box as CilboxBasisCommon);
            CilboxProxy other = Script(typeof(CilboxPropBasis), Prop(true));
            Assert.IsTrue(BasisPoseOverrideShim.Claim(first, HumanBodyBones.RightHand));
            Assert.IsFalse(BasisPoseOverrideShim.Claim(other, HumanBodyBones.RightHand), "other content must not take a held bone");
            Assert.IsFalse(BasisPoseOverrideShim.Set(other, HumanBodyBones.RightHand, Vector3.zero, Quaternion.identity));
            BasisPoseOverrideShim.Release(other, HumanBodyBones.RightHand);
            Assert.IsTrue(BasisPoseOverrideShim.IsClaimed(first, HumanBodyBones.RightHand), "other content must not release it either");
            Assert.IsTrue(BasisPoseOverrideShim.Claim(sibling, HumanBodyBones.RightHand), "another script of the same content shares it");
            Assert.IsTrue(BasisPoseOverrideShim.Set(sibling, HumanBodyBones.RightHand, Vector3.zero, Quaternion.identity));
            BasisPoseOverrideShim.ReleaseAll(sibling);
            Assert.AreEqual(0, ClaimCount);
        }

        [Test]
        public void BadPoses_AreRefused()
        {
            CilboxProxy script = Script(typeof(CilboxSceneBasis), null);
            Assert.IsFalse(BasisPoseOverrideShim.Set(script, HumanBodyBones.RightHand, Vector3.zero, Quaternion.identity), "not claimed");
            Assert.IsFalse(BasisPoseOverrideShim.Claim(script, HumanBodyBones.LastBone));
            Assert.IsFalse(BasisPoseOverrideShim.Claim(script, HumanBodyBones.LeftThumbProximal), "fingers have no override slot");
            Assert.IsTrue(BasisPoseOverrideShim.Claim(script, HumanBodyBones.RightHand));
            Assert.IsFalse(BasisPoseOverrideShim.SetRotation(script, HumanBodyBones.RightHand, new Quaternion(float.NaN, 0f, 0f, 1f)));
            Assert.IsFalse(BasisPoseOverrideShim.SetRotation(script, HumanBodyBones.RightHand, new Quaternion(0f, 0f, 0f, 0f)));
            Assert.IsFalse(BasisPoseOverrideShim.Set(script, HumanBodyBones.RightHand, new Vector3(float.PositiveInfinity, 0f, 0f), Quaternion.identity));
            Assert.IsFalse(BasisPoseOverrideShim.SetBlend(script, HumanBodyBones.RightHand, float.NaN));
            Assert.IsFalse(Weight(RightHandSlot));
        }

        [Test]
        public void ADestroyedScript_LetsGoOnTheNextFrame()
        {
            CilboxProxy script = Script(typeof(CilboxSceneBasis), null);
            Assert.IsTrue(BasisPoseOverrideShim.Claim(script, HumanBodyBones.RightHand));
            Assert.IsTrue(BasisPoseOverrideShim.SetRotation(script, HumanBodyBones.RightHand, Quaternion.identity));
            UnityEngine.Object.DestroyImmediate(script.gameObject);
            Tick();
            Assert.AreEqual(0, ClaimCount);
            Assert.IsFalse(Weight(RightHandSlot), "a script that never released must not leave the arm stuck");
        }

        void Solve(bool trackedHand)
        {
            ref Basis.IK.BasisEeriePlan plan = ref rig.IKJob.plan;
            plan.rightArm.has = true;
            plan.rightArm.weight = trackedHand ? 1f : 0f;
            plan.rightArm.solve = trackedHand;
            rig.IKJob.targetPositionRightHand = new Vector3(0f, 1f, 0f);
            rig.IKJob.targetRotationRightHand = Quaternion.identity;
            typeof(BasisLocalRigDriver).GetMethod("ApplyEffectorOverrides", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(rig, null);
        }

        [Test]
        public void HandEffector_PullsTheArmToTheTarget_EvenWithNoController()
        {
            CilboxProxy script = Script(typeof(CilboxSceneBasis), null);
            Assert.IsTrue(BasisPoseOverrideShim.ClaimEffector(script, HumanBodyBones.RightHand));
            Solve(false);
            Assert.IsFalse(rig.IKJob.plan.rightArm.solve, "claimed with no target yet must leave the arm alone");
            Quaternion turned = Quaternion.Euler(0f, 0f, 90f);
            Assert.IsTrue(BasisPoseOverrideShim.SetEffector(script, HumanBodyBones.RightHand, new Vector3(0.5f, 1.2f, 0.3f), turned));
            Solve(false);
            Assert.IsTrue(rig.IKJob.plan.rightArm.solve, "a desktop arm has no controller, the effector alone drives it");
            Assert.AreEqual(1f, rig.IKJob.plan.rightArm.weight);
            Assert.AreEqual(new Vector3(0.5f, 1.2f, 0.3f), rig.IKJob.targetPositionRightHand);
            Assert.Less(Quaternion.Angle(turned, rig.IKJob.targetRotationRightHand), 0.01f);
            Assert.IsFalse(BasisPoseOverrideShim.TryGetTracked(script, HumanBodyBones.RightHand, out _, out _), "no controller to report");
            BasisPoseOverrideShim.SetEffectorBlend(script, HumanBodyBones.RightHand, 0.25f);
            Solve(false);
            Assert.AreEqual(0.25f, rig.IKJob.plan.rightArm.weight, 1e-5f, "with no controller the blend fades from the animated arm");
        }

        [Test]
        public void HandEffector_BlendsFromTheController_AndReportsIt()
        {
            CilboxProxy script = Script(typeof(CilboxSceneBasis), null);
            Assert.IsTrue(BasisPoseOverrideShim.ClaimEffector(script, HumanBodyBones.RightHand));
            Assert.IsTrue(BasisPoseOverrideShim.SetEffector(script, HumanBodyBones.RightHand, new Vector3(0f, 2f, 0f), Quaternion.identity));
            BasisPoseOverrideShim.SetEffectorBlend(script, HumanBodyBones.RightHand, 0.5f);
            Solve(true);
            Assert.AreEqual(new Vector3(0f, 1.5f, 0f), rig.IKJob.targetPositionRightHand);
            Assert.AreEqual(1f, rig.IKJob.plan.rightArm.weight);
            Assert.IsTrue(BasisPoseOverrideShim.TryGetTracked(script, HumanBodyBones.RightHand, out Vector3 controller, out _));
            Assert.AreEqual(new Vector3(0f, 1f, 0f), controller, "where the controller is, before the effector moved the target");
            BasisPoseOverrideShim.ReleaseEffector(script, HumanBodyBones.RightHand);
            Solve(true);
            Assert.AreEqual(new Vector3(0f, 1f, 0f), rig.IKJob.targetPositionRightHand, "released, the controller drives the hand again");
        }

        [Test]
        public void EffectorAndBoneClaims_AreSeparate()
        {
            CilboxProxy mine = Script(typeof(CilboxSceneBasis), null), other = Script(typeof(CilboxPropBasis), Prop(true));
            Assert.IsTrue(BasisPoseOverrideShim.ClaimEffector(mine, HumanBodyBones.RightHand));
            Assert.IsFalse(BasisPoseOverrideShim.ClaimEffector(other, HumanBodyBones.RightHand));
            Assert.IsTrue(BasisPoseOverrideShim.Claim(other, HumanBodyBones.RightHand), "the bone rotation after the solve is its own claim");
            Assert.IsFalse(BasisPoseOverrideShim.ClaimEffector(mine, HumanBodyBones.RightLowerArm), "only hands and feet are effectors");
            Assert.IsFalse(BasisPoseOverrideShim.SetEffector(mine, HumanBodyBones.RightHand, new Vector3(float.NaN, 0f, 0f), Quaternion.identity));
        }

        [Test]
        public void ADisabledScript_LetsGo()
        {
            CilboxProxy script = Script(typeof(CilboxSceneBasis), null);
            Assert.IsTrue(BasisPoseOverrideShim.Claim(script, HumanBodyBones.RightHand));
            script.gameObject.SetActive(false);
            Tick();
            Assert.AreEqual(0, ClaimCount);
        }
    }
}
