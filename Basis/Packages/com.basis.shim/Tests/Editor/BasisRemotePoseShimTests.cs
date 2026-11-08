using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisRemotePoseShimTests
    {
        static readonly Type[] Boxes = { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) };
        static readonly Type Pass = typeof(BasisRemotePoseShim).Assembly.GetType("Cilbox.CilboxRemotePosePass", true);
        static readonly MethodInfo TwoBoneMethod = Pass.GetMethod("TwoBone", BindingFlags.NonPublic | BindingFlags.Static);
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
            CilboxBasisCommon box = (CilboxBasisCommon)Host(boxType.Name + " remote pose test").AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }
        void Chain(out Transform upper, out Transform lower, out Transform tip)
        {
            upper = Host("upper").transform;
            lower = new GameObject("lower").transform;
            tip = new GameObject("tip").transform;
            lower.SetParent(upper, false);
            tip.SetParent(lower, false);
            lower.localPosition = new Vector3(0f, -0.3f, 0f);
            tip.localPosition = new Vector3(0f, -0.3f, 0f);
        }
        static bool TwoBone(Transform upper, Transform lower, Transform tip, Vector3 target, Quaternion rotation, Vector3 hint) => (bool)TwoBoneMethod.Invoke(null, new object[] { upper, lower, tip, target, rotation, hint });
        [Test]
        public void TwoBone_ReachesATargetInRange()
        {
            Chain(out Transform upper, out Transform lower, out Transform tip);
            Vector3 target = new Vector3(0.3f, -0.25f, 0.1f);
            Assert.IsTrue(TwoBone(upper, lower, tip, target, Quaternion.identity, new Vector3(0f, 0f, 1f)));
            Assert.Less(Vector3.Distance(tip.position, target), 1e-3f);
            Assert.AreEqual(0.3f, Vector3.Distance(upper.position, lower.position), 1e-4f);
            Assert.AreEqual(0.3f, Vector3.Distance(lower.position, tip.position), 1e-4f);
        }
        [Test]
        public void TwoBone_BendsTowardTheHint()
        {
            Chain(out Transform upper, out Transform lower, out Transform tip);
            Vector3 target = new Vector3(0f, -0.4f, 0f);
            TwoBone(upper, lower, tip, target, Quaternion.identity, new Vector3(0f, -0.2f, 1f));
            Assert.Greater(lower.position.z, 0.1f);
            TwoBone(upper, lower, tip, target, Quaternion.identity, new Vector3(0f, -0.2f, -1f));
            Assert.Less(lower.position.z, -0.1f);
        }
        [Test]
        public void TwoBone_OutOfReach_PointsAtTheTarget()
        {
            Chain(out Transform upper, out Transform lower, out Transform tip);
            Vector3 target = new Vector3(2f, 0f, 0f);
            Assert.IsTrue(TwoBone(upper, lower, tip, target, Quaternion.identity, Vector3.forward));
            Assert.Greater(Vector3.Dot((tip.position - upper.position).normalized, Vector3.right), 0.999f);
            Assert.Less(tip.position.x, 0.6f);
        }
        [Test]
        public void TwoBone_SetsTheTipRotation()
        {
            Chain(out Transform upper, out Transform lower, out Transform tip);
            Quaternion rotation = Quaternion.Euler(10f, 70f, -30f);
            TwoBone(upper, lower, tip, new Vector3(0.2f, -0.3f, 0.2f), rotation, Vector3.forward);
            Assert.Less(Quaternion.Angle(tip.rotation, rotation), 0.01f);
        }
        [Test]
        public void OnlyBonesTheNetworkRewrites_AreWritable()
        {
            Assert.IsTrue(RemoteBoneJobSystem.IsSyncedBone(HumanBodyBones.RightHand));
            Assert.IsTrue(RemoteBoneJobSystem.IsSyncedBone(HumanBodyBones.UpperChest));
            Assert.IsTrue(RemoteBoneJobSystem.IsSyncedBone(HumanBodyBones.LeftLittleDistal));
            Assert.IsFalse(RemoteBoneJobSystem.IsSyncedBone(HumanBodyBones.Hips), "hips go through SetHips");
            Assert.IsFalse(RemoteBoneJobSystem.IsSyncedBone(HumanBodyBones.LeftEye));
            Assert.IsFalse(RemoteBoneJobSystem.IsSyncedBone(HumanBodyBones.RightEye));
            Assert.IsFalse(RemoteBoneJobSystem.IsSyncedBone(HumanBodyBones.Jaw));
            Assert.IsFalse(RemoteBoneJobSystem.IsSyncedBone(HumanBodyBones.LastBone));
        }
        [Test]
        public void EveryWritableBone_FitsTheMaskBesideTheHips()
        {
            Assert.AreEqual(RemoteBoneJobSystem.ScriptHipsBit, RemoteBoneJobSystem.ScriptBit(HumanBodyBones.Hips));
            Assert.Less(Basis.Network.Core.Compression.BasisBoneRotationCompression.SyncBoneCount, RemoteBoneJobSystem.ScriptHipsBit);
            for (HumanBodyBones bone = HumanBodyBones.LeftUpperLeg; bone < HumanBodyBones.LastBone; bone++)
            {
                int bit = RemoteBoneJobSystem.ScriptBit(bone);
                Assert.AreEqual(RemoteBoneJobSystem.IsSyncedBone(bone), bit >= 0, bone.ToString());
                if (bit >= 0) Assert.Less(bit, RemoteBoneJobSystem.ScriptHipsBit, bone.ToString());
            }
        }
        [Test]
        public void WithNothingPosed_TheJobsSkipTheScriptPath()
        {
            Assume.That(RemoteBoneJobSystem.ScriptPosedCount == 0);
            Assert.IsFalse(RemoteBoneJobSystem.ScriptPoseActive);
            Assert.AreEqual(0ul, RemoteBoneJobSystem.GetScriptMask(1));
        }
        [Test]
        public void EveryBox_CanReachIt()
        {
            foreach (Type boxType in Boxes) Assert.IsTrue(Box(boxType).CheckTypeAllowed(typeof(BasisRemotePoseShim)), boxType.Name);
        }
        [Test]
        public void OutsideThePass_NothingReadsOrWrites()
        {
            BasisRemotePoseShim shim = Host("remote pose shim").AddComponent<BasisRemotePoseShim>();
            Assert.IsFalse(shim.SetBoneRotation(1, HumanBodyBones.Head, Quaternion.identity));
            Assert.IsFalse(shim.SetBoneLocalRotation(1, HumanBodyBones.Head, Quaternion.identity));
            Assert.IsFalse(shim.SetHips(1, Vector3.zero, Quaternion.identity));
            Assert.IsFalse(shim.SolveLimb(1, HumanBodyBones.RightHand, Vector3.zero, Quaternion.identity, Vector3.forward, 1f));
            Assert.IsFalse(shim.TryGetBone(1, HumanBodyBones.Head, out _, out _));
            Assert.IsFalse(shim.Track(1), "no interpreted caller");
            Assert.IsFalse(shim.CanPose(1));
        }
        [Test]
        public void AskingForIt_ReadsAsChangingOtherPlayers()
        {
            List<BasisCilboxPermissionText.Line> lines = BasisCilboxPermissionText.Describe(new[] { BasisCilboxPermissionText.MethodPrefix + typeof(BasisRemotePoseShim).FullName + "." + nameof(BasisRemotePoseShim.SolveLimb) });
            Assert.AreEqual(1, lines.Count);
            Assert.IsFalse(lines[0].Severe);
            Assert.AreEqual(BasisLocalization.Get("settings.cilboxPermissions.cap.otherPlayersPose"), lines[0].Text);
        }
    }
}
