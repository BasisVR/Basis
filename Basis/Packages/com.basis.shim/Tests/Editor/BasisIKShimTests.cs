using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisIKShimTests
    {
        static readonly Type[] Boxes = { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) };
        static readonly Type Batches = typeof(BasisIKShim).Assembly.GetType("Cilbox.CilboxIKBatches", true);
        static int LiveCount => (int)Batches.GetProperty("LiveCount", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        static readonly Type[] Types = { typeof(BasisIKShim), typeof(BasisIKBatch), typeof(BasisIKLimb), typeof(BasisIKArm), typeof(BasisIKChain) };
        readonly List<GameObject> roots = new List<GameObject>();
        readonly List<BasisIKBatch> batches = new List<BasisIKBatch>();
        [TearDown]
        public void TearDown()
        {
            foreach (BasisIKBatch b in batches) b.Dispose();
            batches.Clear();
            foreach (GameObject g in roots) if (g != null) UnityEngine.Object.DestroyImmediate(g);
            roots.Clear();
        }
        BasisIKBatch Batch()
        {
            BasisIKBatch b = (BasisIKBatch)Batches.GetMethod("CreateHost", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            batches.Add(b);
            return b;
        }
        Transform Bone(string name, Transform parent, Vector3 position)
        {
            GameObject g = new GameObject(name);
            if (parent == null) roots.Add(g);
            else g.transform.SetParent(parent, false);
            g.transform.position = position;
            return g.transform;
        }
        Transform[] Limb(Vector3 origin)
        {
            Transform root = Bone("root", null, origin), mid = Bone("mid", root, origin + new Vector3(0f, -0.5f, 0.03f)), tip = Bone("tip", mid, origin + new Vector3(0f, -1f, 0f));
            return new[] { root, mid, tip };
        }
        static float Length(Transform a, Transform b) => (a.position - b.position).magnitude;
        [Test]
        public void Limb_ReachesTarget_KeepsBoneLengths()
        {
            Transform[] l = Limb(Vector3.zero);
            float upper = Length(l[0], l[1]), lower = Length(l[1], l[2]);
            BasisIKBatch b = Batch();
            BasisIKLimb limb = b.AddLimb(l[0], l[1], l[2]);
            Assert.NotNull(limb);
            Quaternion want = Quaternion.Euler(10f, 40f, 0f);
            Vector3 target = new Vector3(0.3f, -0.6f, 0.25f);
            Assert.IsTrue(limb.SetTarget(target, want));
            Assert.IsTrue(b.Run());
            Assert.Less((l[2].position - target).magnitude, 1e-3f);
            Assert.AreEqual(upper, Length(l[0], l[1]), 1e-4f);
            Assert.AreEqual(lower, Length(l[1], l[2]), 1e-4f);
            Assert.Less(Quaternion.Angle(l[2].rotation, want), 0.1f);
            Assert.Less((limb.TipPosition - target).magnitude, 1e-3f);
            Assert.Less((limb.MidPosition - l[1].position).magnitude, 1e-3f);
        }
        [Test]
        public void Limb_SolvesFromRest_EveryFrameTheSame()
        {
            Transform[] l = Limb(Vector3.zero);
            BasisIKBatch b = Batch();
            BasisIKLimb limb = b.AddLimb(l[0], l[1], l[2]);
            limb.SetTarget(new Vector3(0.2f, -0.7f, 0.2f));
            b.Run();
            Quaternion r0 = l[0].localRotation, r1 = l[1].localRotation;
            for (int i = 0; i < 5; i++) b.Run();
            Assert.Less(Quaternion.Angle(r0, l[0].localRotation), 1e-3f);
            Assert.Less(Quaternion.Angle(r1, l[1].localRotation), 1e-3f);
        }
        [Test]
        public void Limb_FollowsMovedParent()
        {
            Transform parent = Bone("parent", null, Vector3.zero);
            Transform root = Bone("root", parent, Vector3.zero), mid = Bone("mid", root, new Vector3(0f, -0.5f, 0.03f)), tip = Bone("tip", mid, new Vector3(0f, -1f, 0f));
            BasisIKBatch b = Batch();
            BasisIKLimb limb = b.AddLimb(root, mid, tip);
            parent.SetPositionAndRotation(new Vector3(5f, 2f, -3f), Quaternion.Euler(0f, 90f, 30f));
            parent.localScale = Vector3.one * 2f;
            Vector3 target = root.position + new Vector3(0.4f, -1.2f, 0.3f);
            limb.SetTarget(target);
            b.Run();
            Assert.Less((tip.position - target).magnitude, 2e-3f);
        }
        [Test]
        public void ZeroWeight_DisabledOrNoTarget_LeavesPose()
        {
            Transform[] l = Limb(Vector3.zero);
            Quaternion r0 = l[0].localRotation, r1 = l[1].localRotation;
            BasisIKBatch b = Batch();
            BasisIKLimb limb = b.AddLimb(l[0], l[1], l[2]);
            b.Run();
            limb.SetTarget(new Vector3(0.3f, -0.5f, 0.2f));
            limb.Weight = 0f;
            b.Run();
            limb.Weight = 1f;
            limb.Enabled = false;
            b.Run();
            Assert.AreEqual(r0, l[0].localRotation);
            Assert.AreEqual(r1, l[1].localRotation);
        }
        [Test]
        public void ScheduleThenComplete_AppliesOnComplete()
        {
            Transform[] l = Limb(Vector3.zero);
            BasisIKBatch b = Batch();
            BasisIKLimb limb = b.AddLimb(l[0], l[1], l[2]);
            Vector3 target = new Vector3(-0.2f, -0.8f, 0.1f);
            limb.SetTarget(target);
            Assert.IsTrue(b.Schedule());
            Assert.IsTrue(b.IsScheduled);
            b.Complete();
            Assert.IsFalse(b.IsScheduled);
            Assert.Less((l[2].position - target).magnitude, 1e-3f);
        }
        [Test]
        public void Arm_ReachesTarget()
        {
            Transform body = Bone("chest", null, new Vector3(0f, 1.4f, 0f));
            Transform upper = Bone("upper", body, new Vector3(0.2f, 1.45f, 0f)), lower = Bone("lower", upper, new Vector3(0.48f, 1.45f, 0f)), hand = Bone("hand", lower, new Vector3(0.74f, 1.45f, 0f));
            BasisIKBatch b = Batch();
            BasisIKArm arm = b.AddArm(body, upper, lower, hand, false);
            Assert.NotNull(arm);
            arm.Smoothing = 0f;
            Vector3 target = new Vector3(0.35f, 1.2f, 0.3f);
            arm.SetTarget(target, Quaternion.identity);
            b.Run();
            Assert.Less((hand.position - target).magnitude, 0.01f);
            Assert.Less((arm.HandPosition - target).magnitude, 0.01f);
        }
        [Test]
        public void Chain_TipFollowsTarget_RootStays()
        {
            Transform[] bones = new Transform[6];
            for (int i = 0; i < bones.Length; i++) bones[i] = Bone("c" + i, i == 0 ? null : bones[i - 1], new Vector3(0f, i * 0.2f, 0f));
            BasisIKBatch b = Batch();
            BasisIKChain chain = b.AddChain(bones);
            Assert.NotNull(chain);
            Vector3 root = bones[0].position, target = new Vector3(0.4f, 0.8f, 0.2f);
            chain.SetTarget(target);
            b.Run();
            Assert.Less((bones[0].position - root).magnitude, 1e-5f);
            Assert.Less((bones[5].position - target).magnitude, 0.1f);
        }
        [Test]
        public void Refuses_BadShapes_AndSharedWrites()
        {
            Transform[] l = Limb(Vector3.zero);
            Transform loose = Bone("loose", null, Vector3.one);
            BasisIKBatch b = Batch();
            Assert.IsNull(b.AddLimb(l[0], loose, l[2]));
            Assert.IsNull(b.AddLimb(l[0], l[1], l[1]));
            Assert.IsNull(b.AddChain(new[] { l[0], l[1], l[2] }));
            Assert.NotNull(b.AddLimb(l[0], l[1], l[2]));
            Assert.IsNull(b.AddLimb(l[0], l[1], l[2]));
            Assert.AreEqual(1, b.SolverCount);
        }
        [Test]
        public void Remove_And_Dispose_Release()
        {
            Transform[] l = Limb(Vector3.zero);
            BasisIKBatch b = Batch();
            BasisIKLimb limb = b.AddLimb(l[0], l[1], l[2]);
            Assert.IsTrue(limb.Remove());
            Assert.IsFalse(limb.IsValid);
            Assert.IsFalse(b.Run());
            Assert.NotNull(b.AddLimb(l[0], l[1], l[2]));
            int live = LiveCount;
            b.Dispose();
            Assert.AreEqual(live - 1, LiveCount);
            Assert.IsFalse(b.IsValid);
            Assert.IsNull(b.AddLimb(l[0], l[1], l[2]));
        }
        [Test]
        public void CreateBatch_RefusesNonScripts()
        {
            Assert.IsNull(BasisIKShim.CreateBatch(null));
            Assert.IsNull(BasisIKShim.CreateBatch(new GameObject("not a proxy").transform));
        }
        [Test]
        public void EveryBox_AllowsTheTypes()
        {
            foreach (Type boxType in Boxes)
            {
                GameObject host = new GameObject(boxType.Name);
                roots.Add(host);
                CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
                box.classes = new Dictionary<string, int>();
                foreach (Type t in Types) Assert.IsTrue(box.CheckTypeAllowed(t), boxType.Name + " " + t.Name);
            }
        }
        [Test]
        public void TwistHelpers_SplitSwingAndTwist()
        {
            Quaternion q = Quaternion.AngleAxis(30f, Vector3.right) * Quaternion.AngleAxis(50f, Vector3.up);
            Quaternion twist = BasisIKShim.ExtractTwist(q, Vector3.up), swing = BasisIKShim.ExtractSwing(q, Vector3.up);
            Assert.Less(Quaternion.Angle(swing * twist, q), 0.01f);
            Assert.AreEqual(90f, BasisIKShim.TwistAngle(Quaternion.AngleAxis(90f, Vector3.up), Vector3.up), 0.01f);
        }
    }
}
