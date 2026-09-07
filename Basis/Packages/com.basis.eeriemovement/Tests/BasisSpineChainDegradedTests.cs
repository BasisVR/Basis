using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisSpineChainDegradedTests
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
        Transform[] BuildBones(string[] names, float[] heights, Quaternion[] worldRots = null)
        {
            DisposeRig();
            root = new GameObject("DegradedChainRig");
            var bones = new Transform[names.Length];
            Transform parent = root.transform;
            for (int i = 0; i < names.Length; i++)
            {
                var go = new GameObject(names[i]);
                go.transform.SetPositionAndRotation(new Vector3(0f, heights[i], 0f), worldRots != null ? worldRots[i] : Quaternion.identity);
                go.transform.SetParent(parent, true);
                bones[i] = go.transform;
                parent = go.transform;
            }
            skeleton = new BasisPoseSkeleton();
            skeleton.Build(bones[0], bones);
            skeleton.GatherNow();
            return bones;
        }
        NativeArray<BasisBoneHandle> BindChainTipFirst(Transform[] chainRootFirst)
        {
            var chain = new NativeArray<BasisBoneHandle>(chainRootFirst.Length, Allocator.Persistent);
            for (int i = 0; i < chainRootFirst.Length; i++)
            {
                chain[i] = chainRootFirst[chainRootFirst.Length - 1 - i] != null ? skeleton.Bind(chainRootFirst[chainRootFirst.Length - 1 - i]) : BasisBoneHandle.Unbound;
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
        // ------------------------------------------------ optional bones must not switch the head pin off
        [Test]
        public void NecklessChain_HeadIsStillPinnedToTheGaze()
        {
            var bones = BuildBones(new[] { "Hips", "Spine", "Chest", "Head" }, new[] { 0.95f, 1.06f, 1.21f, 1.57f });
            _chain = BindChainTipFirst(bones);

            var job = CcdJob();
            job.chainHeadToSpine = _chain;
            job.chainChestIdx = 1;
            job.handleHips = skeleton.Bind(bones[0]);
            job.handleSpine = skeleton.Bind(bones[1]);
            job.handleChest = skeleton.Bind(bones[2]);
            job.handleHead = skeleton.Bind(bones[3]);
            job.ikLockMode = BasisIKLockMode.LockHead;
            job.minHeadSpineHeight = 0.62f;
            job.tposeLengthNeckToHips = new Vector3(0f, 0.5f, 0f);
            job.offsetRotationChest = Quaternion.identity;
            job.targetRotationHips = Quaternion.identity;
            job.targetRotationChest = Quaternion.identity;
            job.targetPositionHips = bones[0].position;
            BasisEeriePlanner.Bind(ref job);
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { hipsTracked = true });

            Quaternion gaze = Quaternion.Euler(15f, 30f, 0f);
            Vector3 headTarget = bones[3].position - new Vector3(0f, 0.002f, 0f);
            job.targetPositionHead = headTarget;
            job.targetRotationHead = gaze;

            job.poseStream = skeleton.Stream;
            job.SolveSpine();

            float rotErr = Quaternion.Angle(skeleton.Stream.GetRotation(_chain[0]), gaze);
            float posErr = (skeleton.Stream.GetPosition(_chain[0]) - headTarget).magnitude;
            TestContext.WriteLine($"head rot err {rotErr:F4} deg, pos err {posErr * 1000f:F2} mm");
            Assert.Less(rotErr, 0.1f, "a neckless avatar's head must still be pinned to the HMD gaze");
            Assert.Less(posErr, 0.01f, "a neckless avatar's head must still reach its target");
        }
        [Test]
        public void MinimalChain_HeadIsStillPinnedToTheGaze()
        {
            var bones = BuildBones(new[] { "Hips", "Spine", "Head" }, new[] { 0.95f, 1.21f, 1.57f });
            _chain = BindChainTipFirst(bones);

            var job = CcdJob();
            job.chainHeadToSpine = _chain;
            job.chainChestIdx = -1;
            job.handleHips = skeleton.Bind(bones[0]);
            job.handleSpine = skeleton.Bind(bones[1]);
            job.handleHead = skeleton.Bind(bones[2]);
            job.ikLockMode = BasisIKLockMode.LockHead;
            job.minHeadSpineHeight = 0.62f;
            job.tposeLengthNeckToHips = new Vector3(0f, 0.5f, 0f);
            job.offsetRotationChest = Quaternion.identity;
            job.targetRotationHips = Quaternion.identity;
            job.targetRotationChest = Quaternion.identity;
            job.targetPositionHips = bones[0].position;
            BasisEeriePlanner.Bind(ref job);
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { hipsTracked = true });

            Quaternion gaze = Quaternion.Euler(-10f, -40f, 0f);
            Vector3 headTarget = bones[2].position - new Vector3(0f, 0.002f, 0f);
            job.targetPositionHead = headTarget;
            job.targetRotationHead = gaze;

            job.poseStream = skeleton.Stream;
            job.SolveSpine();

            float rotErr = Quaternion.Angle(skeleton.Stream.GetRotation(_chain[0]), gaze);
            float posErr = (skeleton.Stream.GetPosition(_chain[0]) - headTarget).magnitude;
            TestContext.WriteLine($"head rot err {rotErr:F4} deg, pos err {posErr * 1000f:F2} mm");
            Assert.Less(rotErr, 0.1f, "a hips/spine/head-only avatar's head must still be pinned to the HMD gaze");
            Assert.Less(posErr, 0.01f, "a hips/spine/head-only avatar's head must still reach its target");
        }
        [Test]
        public void ChestlessChain_ChestTargetDoesNotPullTheNeck()
        {
            var bones = BuildBones(new[] { "Hips", "Spine", "Neck", "Head" }, new[] { 0.95f, 1.21f, 1.45f, 1.57f });
            _chain = BindChainTipFirst(bones);
            int neckIdx = skeleton.Bind(bones[2]).Index;

            var job = CcdJob();
            job.chainHeadToSpine = _chain;
            job.chainChestIdx = -1;
            job.handleHips = skeleton.Bind(bones[0]);
            job.handleSpine = skeleton.Bind(bones[1]);
            job.handleNeck = skeleton.Bind(bones[2]);
            job.handleHead = skeleton.Bind(bones[3]);
            BasisEeriePlanner.Bind(ref job);

            Vector3 headTarget = bones[3].position - new Vector3(0f, 0.002f, 0f);

            job.chestIkTarget = false;
            skeleton.GatherNow();
            job.poseStream = skeleton.Stream;
            job.SolveSequentialSpineIK(headTarget, Quaternion.identity);
            Quaternion neckWithoutChestIk = skeleton.Stream.GetWorldRotation(neckIdx);

            job.chestIkTarget = true;
            job.targetPositionChestRaw = bones[2].position + new Vector3(0f, -0.1f, 0.3f);
            skeleton.GatherNow();
            job.poseStream = skeleton.Stream;
            job.SolveSequentialSpineIK(headTarget, Quaternion.identity);
            Quaternion neckWithChestIk = skeleton.Stream.GetWorldRotation(neckIdx);

            Assert.Less(Quaternion.Angle(neckWithoutChestIk, neckWithChestIk), 1e-4f,"with no chest bone the chest IK target must be inert, not re-aimed at the neck");
        }
        [Test]
        public void UnboundHandleInsideTheChain_LeavesThePoseUntouched()
        {
            var bones = BuildBones(new[] { "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head" }, new[] { 0.95f, 1.06f, 1.21f, 1.33f, 1.45f, 1.57f });
            _chain = BindChainTipFirst(new[] { bones[0], bones[1], bones[2], bones[3], null, bones[5] });

            var job = CcdJob();
            job.chainHeadToSpine = _chain;
            BasisEeriePlanner.Bind(ref job);

            Quaternion gaze = Quaternion.Euler(0f, 25f, 0f);
            job.poseStream = skeleton.Stream;
            job.SolveSequentialSpineIK(bones[5].position, gaze);

            Assert.Less(Quaternion.Angle(skeleton.Stream.GetWorldRotation(skeleton.Bind(bones[5]).Index), Quaternion.identity), 1e-4f,"a chain with a genuinely unbound handle must stay a no-op");
        }
        // -------------------------------------------- the CCD twist axis must not be a rig convention
        static readonly string[] FullNames = { "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head" };
        static readonly float[] FullHeights = { 0.95f, 1.06f, 1.21f, 1.33f, 1.45f, 1.57f };
        (Quaternion neck, Quaternion chest, Vector3 tip, Quaternion head) SolveOnBind(Quaternion hipsWorldRest, Quaternion hipsBind)
        {
            var rots = new Quaternion[6];
            for (int i = 0; i < 6; i++)
            {
                rots[i] = i == 0 ? hipsWorldRest : Quaternion.identity;
            }
            var bones = BuildBones(FullNames, FullHeights, rots);
            _chain = BindChainTipFirst(bones);

            var job = CcdJob();
            job.chainHeadToSpine = _chain;
            job.handleHips = skeleton.Bind(bones[0]);
            job.offsetRotationHips = hipsBind;
            BasisEeriePlanner.Bind(ref job);
            BasisEeriePlanner.Frame(ref job, default);

            Vector3 target = bones[5].position + new Vector3(0.10f, -0.06f, 0.04f);
            job.poseStream = skeleton.Stream;
            job.SolveSequentialSpineIK(target, Quaternion.identity);

            return (skeleton.Stream.GetWorldRotation(skeleton.Bind(bones[4]).Index), skeleton.Stream.GetWorldRotation(skeleton.Bind(bones[2]).Index), skeleton.Stream.GetPosition(_chain[0]), skeleton.Stream.GetRotation(_chain[0]));
        }
        [Test]
        public void RolledHipsBind_SolvesIdenticallyToTheCanonicalRig()
        {
            var canonical = SolveOnBind(Quaternion.identity, Quaternion.identity);
            Quaternion blenderRoll = Quaternion.Euler(-90f, 0f, 0f);
            var rolled = SolveOnBind(blenderRoll, blenderRoll);

            float neckDelta = Quaternion.Angle(canonical.neck, rolled.neck);
            float chestDelta = Quaternion.Angle(canonical.chest, rolled.chest);
            float tipDelta = (canonical.tip - rolled.tip).magnitude;
            TestContext.WriteLine($"neck {neckDelta:F4} deg, chest {chestDelta:F4} deg, tip {tipDelta * 1000f:F3} mm");

            Assert.Less(neckDelta, 0.1f, "the solved neck must not depend on the hips bone's bind convention");
            Assert.Less(chestDelta, 0.1f, "the solved chest must not depend on the hips bone's bind convention");
            Assert.Less(tipDelta, 0.001f, "the solved head position must not depend on the hips bone's bind convention");
        }
        [Test]
        public void UnsetHipsBind_FallsBackToTheRawBoneFrame()
        {
            var withIdentity = SolveOnBind(Quaternion.identity, Quaternion.identity);
            var withDefault = SolveOnBind(Quaternion.identity, default);

            Assert.Less(Quaternion.Angle(withIdentity.neck, withDefault.neck), 1e-3f,"a zero (unset) hips bind must solve exactly like the identity bind");
            Assert.Less(Quaternion.Angle(withIdentity.chest, withDefault.chest), 1e-3f);
        }
    }
}
