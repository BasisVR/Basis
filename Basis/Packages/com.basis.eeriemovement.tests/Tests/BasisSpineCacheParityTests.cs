using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisSpineCacheParityTests
    {
        GameObject root;
        BasisPoseSkeleton skeleton;
        NativeArray<BasisBoneHandle> _chain;
        [TearDown]
        public void TearDown()
        {
            DisposeRig();
        }
        void DisposeRig()
        {
            if (_chain.IsCreated)
            {
                _chain.Dispose();
            }
            skeleton?.Dispose();
            skeleton = null;
            if (root != null)
            {
                Object.DestroyImmediate(root);
                root = null;
            }
        }
        Transform[] BuildChainRig(float[] heights, bool[] twistBetween, float boneScale = 1f, float rootScale = 1f, Quaternion? boneRot = null)
        {
            DisposeRig();
            root = new GameObject("SpineCacheRig");
            root.transform.localScale = Vector3.one * rootScale;
            string[] names = { "Hips", "Spine", "Chest", "Neck", "Head" };
            var chainBones = new Transform[names.Length];
            Transform parent = root.transform;
            Quaternion rot = boneRot ?? Quaternion.identity;
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0 && twistBetween != null && twistBetween[i - 1])
                {
                    var twist = new GameObject(names[i - 1] + "Twist");
                    float midHeight = (heights[i - 1] + heights[i]) * 0.5f;
                    twist.transform.SetPositionAndRotation(new Vector3(0f, midHeight, 0f), rot);
                    twist.transform.SetParent(parent, true);
                    twist.transform.localScale = Vector3.one * boneScale;
                    parent = twist.transform;
                }
                var go = new GameObject(names[i]);
                go.transform.SetPositionAndRotation(new Vector3(0f, heights[i], 0f), rot);
                go.transform.SetParent(parent, true);
                go.transform.localScale = Vector3.one * boneScale;
                chainBones[i] = go.transform;
                parent = go.transform;
            }
            skeleton = new BasisPoseSkeleton();
            skeleton.Build(chainBones[0], chainBones);
            skeleton.GatherNow();
            return chainBones;
        }
        NativeArray<BasisBoneHandle> BindChainTipFirst(Transform[] chainRootFirst)
        {
            var chain = new NativeArray<BasisBoneHandle>(chainRootFirst.Length, Allocator.Persistent);
            for (int i = 0; i < chainRootFirst.Length; i++)
            {
                chain[i] = skeleton.Bind(chainRootFirst[chainRootFirst.Length - 1 - i]);
            }
            return chain;
        }
        BasisEerieMovement CcdJob()
        {
            return new BasisEerieMovement
            {
                spineMaxIterations = 20,
                spineTolerance = 0.001f,
                spineCCDRelax = 1.0f,
                spineTwistKeep = 0.25f,
                spineNeckTwistKeep = 0.9f,
                neckMaxConeDeg = 45f,
                maxChestDeltaDeg = 30f,
                thoracicBendStiffen = 0.3f,
                spineTautBandFrac = 0.015f,
                chestIkWeight = 0.5f,
                chestIkIterations = 8,
                chestIkHeadRestoreSweeps = 2,
                chestPullMaxDist = 0.5f,
                offsetRotationHead = Quaternion.identity,
                offsetRotationHips = Quaternion.identity,
                playerUp = Vector3.up,
                chestIkTarget = false,
                spineAnatomicalRom = false,
            };
        }
        BasisEerieMovement WireJob(Transform[] bones)
        {
            var job = CcdJob();
            job.chainHeadToSpine = _chain;
            job.chainChestIdx = 2;
            job.handleHips = skeleton.Bind(bones[0]);
            job.handleSpine = skeleton.Bind(bones[1]);
            job.handleChest = skeleton.Bind(bones[2]);
            job.handleNeck = skeleton.Bind(bones[3]);
            job.handleHead = skeleton.Bind(bones[4]);
            job.ikLockMode = BasisIKLockMode.LockHead;
            job.minHeadSpineHeight = 0.62f;
            job.tposeLengthNeckToHips = new Vector3(0f, 0.5f, 0f);
            job.offsetRotationChest = Quaternion.identity;
            job.targetRotationHips = Quaternion.identity;
            job.targetRotationChest = Quaternion.identity;
            job.targetPositionHips = bones[0].position;
            BasisEeriePlanner.Bind(ref job);
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { hipsTracked = true });
            return job;
        }
        static readonly float[] Heights = { 0.95f, 1.10f, 1.25f, 1.45f, 1.57f };
        void AssertStreamFinite(string context)
        {
            var stream = skeleton.Stream;
            for (int i = 0; i < stream.Count; i++)
            {
                Quaternion q = stream.LocalRotation[i];
                Assert.IsTrue(float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w), $"{context}: LocalRotation[{i}] is not finite: {q.x},{q.y},{q.z},{q.w}");
                Vector3 p = stream.LocalPosition[i];
                Assert.IsTrue(float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z), $"{context}: LocalPosition[{i}] is not finite: {p}");
                float magSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                Assert.Greater(magSq, 0.5f, $"{context}: LocalRotation[{i}] degenerated toward zero (magSq {magSq})");
            }
        }
        // ------------------------------------------------ the intermediate-bone (non-contiguous) path
        [Test]
        public void TwistBoneChain_HeadIsStillPinnedToTheGaze()
        {
            var bones = BuildChainRig(Heights, new[] { true, true, true, true });
            _chain = BindChainTipFirst(bones);
            var job = WireJob(bones);

            Quaternion gaze = Quaternion.Euler(15f, 30f, 0f);
            Vector3 headTarget = bones[4].position + new Vector3(0.05f, -0.03f, 0.04f);
            job.targetPositionHead = headTarget;
            job.targetRotationHead = gaze;

            job.poseStream = skeleton.Stream;
            job.SolveSpine();

            AssertStreamFinite("twist-bone chain");
            float rotErr = Quaternion.Angle(skeleton.Stream.GetRotation(_chain[0]), gaze);
            float posErr = (skeleton.Stream.GetPosition(_chain[0]) - headTarget).magnitude;
            TestContext.WriteLine($"head rot err {rotErr:F4} deg, pos err {posErr * 1000f:F2} mm");
            Assert.Less(rotErr, 0.1f, "with twist bones between every chain link the head must still be pinned to the gaze");
            Assert.Less(posErr, 0.01f, "with twist bones between every chain link the head must still reach its target");
        }
        [Test]
        public void TwistBoneChain_MatchesTheContiguousChainsReach()
        {
            var bones = BuildChainRig(Heights, null);
            _chain = BindChainTipFirst(bones);
            var job = WireJob(bones);
            Quaternion gaze = Quaternion.Euler(-10f, 25f, 5f);
            Vector3 headTarget = bones[4].position + new Vector3(-0.06f, -0.04f, 0.05f);
            job.targetPositionHead = headTarget;
            job.targetRotationHead = gaze;
            job.poseStream = skeleton.Stream;
            job.SolveSpine();
            float contiguousErr = (skeleton.Stream.GetPosition(_chain[0]) - headTarget).magnitude;

            var twistBones = BuildChainRig(Heights, new[] { false, true, true, false });
            _chain = BindChainTipFirst(twistBones);
            var twistJob = WireJob(twistBones);
            headTarget = twistBones[4].position + new Vector3(-0.06f, -0.04f, 0.05f);
            twistJob.targetPositionHead = headTarget;
            twistJob.targetRotationHead = gaze;
            twistJob.poseStream = skeleton.Stream;
            twistJob.SolveSpine();
            AssertStreamFinite("mixed twist-bone chain");
            float twistErr = (skeleton.Stream.GetPosition(_chain[0]) - headTarget).magnitude;

            TestContext.WriteLine($"contiguous {contiguousErr * 1000f:F2} mm vs twist-bone {twistErr * 1000f:F2} mm");
            Assert.Less(twistErr, 0.01f, "the twist-bone chain must reach the same target the contiguous chain reaches");
        }
        // ------------------------------------------------ degenerate targets must never write NaN
        [Test]
        public void TargetAtTheRoot_WritesOnlyFiniteRotations()
        {
            var bones = BuildChainRig(Heights, new[] { true, false, true, false });
            _chain = BindChainTipFirst(bones);
            var job = WireJob(bones);

            job.targetPositionHead = bones[0].position;
            job.targetRotationHead = Quaternion.identity;
            job.poseStream = skeleton.Stream;
            job.SolveSpine();
            AssertStreamFinite("target at root");
        }
        [Test]
        public void TargetAtTheCurrentTip_WritesOnlyFiniteRotations()
        {
            var bones = BuildChainRig(Heights, new[] { true, true, false, false });
            _chain = BindChainTipFirst(bones);
            var job = WireJob(bones);

            job.targetPositionHead = bones[4].position;
            job.targetRotationHead = Quaternion.Euler(0f, 180f, 0f);
            job.poseStream = skeleton.Stream;
            job.SolveSpine();
            AssertStreamFinite("target at current tip");
        }
        [Test]
        public void RepeatedSolves_StayFinite_WhenTheTargetWhipsAcrossFrames()
        {
            var bones = BuildChainRig(Heights, new[] { true, true, true, true });
            _chain = BindChainTipFirst(bones);
            var job = WireJob(bones);

            var targets = new[]
            {
                bones[4].position,
                bones[4].position + new Vector3(0f, -3f, 0f),
                bones[0].position,
                bones[4].position + new Vector3(50f, 0f, 0f),
                bones[4].position + new Vector3(-50f, 40f, -20f),
                bones[4].position,
            };
            for (int frame = 0; frame < targets.Length; frame++)
            {
                job.targetPositionHead = targets[frame];
                job.targetRotationHead = Quaternion.Euler(frame * 47f, frame * 91f, frame * 13f);
                job.poseStream = skeleton.Stream;
                job.SolveSpine();
                AssertStreamFinite($"whip frame {frame}");
            }
        }
        // ------------------------------------------------ real-rig conventions
        [Test]
        public void BlenderScaledRig_WithRolledBind_StaysFiniteAndPinned()
        {
            Quaternion rolled = Quaternion.Euler(-90f, 0f, 0f);
            var bones = BuildChainRig(Heights, new[] { true, true, true, true }, boneScale: 1f, rootScale: 1f, boneRot: rolled);
            _chain = BindChainTipFirst(bones);
            var job = WireJob(bones);
            job.offsetRotationHips = rolled;

            Quaternion gaze = Quaternion.Euler(20f, -35f, 0f);
            Vector3 headTarget = bones[4].position + new Vector3(0.04f, -0.05f, 0.06f);
            job.targetPositionHead = headTarget;
            job.targetRotationHead = gaze;
            job.poseStream = skeleton.Stream;
            job.SolveSpine();

            AssertStreamFinite("rolled bind rig");
            float posErr = (skeleton.Stream.GetPosition(_chain[0]) - headTarget).magnitude;
            TestContext.WriteLine($"pos err {posErr * 1000f:F2} mm");
            Assert.Less(posErr, 0.01f, "a rolled-bind rig with twist bones must still reach the head target");
        }
        [Test]
        public void TinyBoneScaleUnderBigRoot_StaysFinite()
        {
            var bones = BuildChainRig(Heights, new[] { false, true, true, true }, boneScale: 1f, rootScale: 100f);
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i].localScale = Vector3.one * (i == 0 ? 0.01f : 1f);
            }
            skeleton.GatherNow();
            _chain = BindChainTipFirst(bones);
            var job = WireJob(bones);

            job.targetPositionHead = bones[4].position + new Vector3(0.02f, -0.02f, 0.02f);
            job.targetRotationHead = Quaternion.Euler(5f, 10f, 0f);
            job.poseStream = skeleton.Stream;
            job.SolveSpine();
            AssertStreamFinite("tiny bone scale under big root");
        }
    }
}
