using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisSpineProneTests
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
        Transform[] BuildBones(string[] names, Vector3[] positions)
        {
            DisposeRig();
            root = new GameObject("ProneSpineRig");
            var bones = new Transform[names.Length];
            Transform parent = root.transform;
            for (int i = 0; i < names.Length; i++)
            {
                var go = new GameObject(names[i]);
                go.transform.SetPositionAndRotation(positions[i], Quaternion.identity);
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
        // A body lying face-down along +Z: hips at the back near the floor, head raised at the front.
        Transform[] BuildLyingBones()
        {
            return BuildBones(new[] { "Hips", "Spine", "Chest", "Neck", "Head" }, new[]
                {
                    new Vector3(0f, 0.12f, -0.55f),
                    new Vector3(0f, 0.14f, -0.30f),
                    new Vector3(0f, 0.17f, -0.05f),
                    new Vector3(0f, 0.22f, 0.18f),
                    new Vector3(0f, 0.28f, 0.32f),
                });
        }
        BasisEerieMovement ProneJob(Transform[] bones)
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
            // The stale standing-model target the virtual spine would emit: hips stacked under a
            // floor-height head. Honouring it is exactly the failure this flag exists to prevent.
            job.targetPositionHips = new Vector3(0f, 0.62f, 0.32f);
            BasisEeriePlanner.Bind(ref job);
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { prone = true });
            return job;
        }
        [Test]
        public void ProneBodyPose_AlignedYawLeavesTheAnimationPoseUntouched()
        {
            var bones = BuildLyingBones();
            _chain = BindChainTipFirst(bones);
            var job = ProneJob(bones);
            // targetRotationHips is identity = facing +Z, the same direction the lying body points.

            Vector3 hipsBefore = skeleton.Stream.GetPosition(job.handleHips);
            Quaternion hipsRotBefore = skeleton.Stream.GetRotation(job.handleHips);

            // Target exactly on the head: aligned yaw and zero carry must leave the pose untouched.
            job.targetPositionHead = bones[4].position;
            job.targetRotationHead = Quaternion.Euler(0f, 20f, 0f);

            job.poseStream = skeleton.Stream;
            job.SolveSpine();

            float posDelta = (skeleton.Stream.GetPosition(job.handleHips) - hipsBefore).magnitude;
            float rotDelta = Quaternion.Angle(skeleton.Stream.GetRotation(job.handleHips), hipsRotBefore);
            TestContext.WriteLine($"hips pos delta {posDelta * 1000f:F3} mm, rot delta {rotDelta:F4} deg");
            Assert.Less(posDelta, 1e-4f, "prone must leave the animation's pelvis untouched");
            Assert.Less(rotDelta, 1e-2f, "prone must leave the animation's pelvis rotation untouched");
        }
        [Test]
        public void ProneBodyPose_HeadIsStillPinnedToTheGaze()
        {
            var bones = BuildLyingBones();
            _chain = BindChainTipFirst(bones);
            var job = ProneJob(bones);

            Quaternion gaze = Quaternion.Euler(0f, 20f, 0f);
            Vector3 headTarget = bones[4].position + new Vector3(0f, 0.002f, -0.01f);
            job.targetPositionHead = headTarget;
            job.targetRotationHead = gaze;

            job.poseStream = skeleton.Stream;
            job.SolveSpine();

            float rotErr = Quaternion.Angle(skeleton.Stream.GetRotation(_chain[0]), gaze);
            float posErr = (skeleton.Stream.GetPosition(_chain[0]) - headTarget).magnitude;
            TestContext.WriteLine($"head rot err {rotErr:F4} deg, pos err {posErr * 1000f:F2} mm");
            Assert.Less(rotErr, 0.1f, "a prone player's head must still be pinned to the gaze");
            Assert.Less(posErr, 0.01f, "a prone player's head must still reach its target");
        }
        [Test]
        public void ProneBodyPose_BodySwingsToTheTorsoYawAboutTheHead()
        {
            var bones = BuildLyingBones();
            _chain = BindChainTipFirst(bones);
            var job = ProneJob(bones);
            job.targetRotationHips = Quaternion.Euler(0f, 90f, 0f);

            Vector3 headBefore = skeleton.Stream.GetPosition(job.handleHead);
            Vector3 hipsBefore = skeleton.Stream.GetPosition(job.handleHips), flatBefore = headBefore - hipsBefore;
            flatBefore.y = 0f;
            float radiusBefore = flatBefore.magnitude;
            Quaternion gaze = Quaternion.Euler(0f, 90f, 0f);
            job.targetPositionHead = headBefore;
            job.targetRotationHead = gaze;

            job.poseStream = skeleton.Stream;
            job.SolveSpine();

            Vector3 hipsAfter = skeleton.Stream.GetPosition(job.handleHips);
            Vector3 headAfter = skeleton.Stream.GetPosition(_chain[0]), bodyFwd = headAfter - hipsAfter;
            bodyFwd.y = 0f;
            float yawErr = Vector3.Angle(bodyFwd, Vector3.right);
            Vector3 flatAfter = headAfter - hipsAfter;
            flatAfter.y = 0f;
            TestContext.WriteLine($"body yaw err {yawErr:F3} deg, head moved {(headAfter - headBefore).magnitude * 1000f:F2} mm");
            Assert.Less(yawErr, 1f, "the lying body must swing to the torso yaw");
            Assert.Less((headAfter - headBefore).magnitude, 0.01f, "the swing must pivot about the head");
            Assert.AreEqual(radiusBefore, flatAfter.magnitude, 1e-3f, "the swing must be rigid, not a stretch");
            Assert.AreEqual(hipsBefore.y, hipsAfter.y, 1e-4f, "a yaw swing must not change the hips height");
        }
        [Test]
        public void ProneBodyPose_BodyIsCarriedToTheHeadTargetColumn()
        {
            var bones = BuildLyingBones();
            _chain = BindChainTipFirst(bones);
            var job = ProneJob(bones);

            Vector3 headBefore = skeleton.Stream.GetPosition(job.handleHead);
            Vector3 hipsBefore = skeleton.Stream.GetPosition(job.handleHips), flatBefore = headBefore - hipsBefore;
            flatBefore.y = 0f;
            float radiusBefore = flatBefore.magnitude;
            Vector3 carry = new Vector3(0.3f, 0f, 0.2f);
            job.targetPositionHead = headBefore + carry;
            job.targetRotationHead = Quaternion.identity;

            job.poseStream = skeleton.Stream;
            job.SolveSpine();

            Vector3 headAfter = skeleton.Stream.GetPosition(_chain[0]);
            Vector3 hipsAfter = skeleton.Stream.GetPosition(job.handleHips), flatAfter = headAfter - hipsAfter;
            flatAfter.y = 0f;
            TestContext.WriteLine($"radius {radiusBefore:F3} -> {flatAfter.magnitude:F3} m, hips moved {(hipsAfter - hipsBefore).magnitude:F3} m");
            Assert.Less((headAfter - (headBefore + carry)).magnitude, 0.01f, "the head must reach the target");
            Assert.Less((hipsAfter - (hipsBefore + carry)).magnitude, 1e-3f, "the hips must carry by the same horizontal offset");
            Assert.AreEqual(radiusBefore, flatAfter.magnitude, 5e-3f, "the body must be carried with the head, not folded to reach it");
        }
        [Test]
        public void DefaultOff_HipsStillFollowTheTarget()
        {
            var bones = BuildBones(new[] { "Hips", "Spine", "Chest", "Neck", "Head" }, new[]
                {
                    new Vector3(0f, 0.95f, 0f),
                    new Vector3(0f, 1.06f, 0f),
                    new Vector3(0f, 1.21f, 0f),
                    new Vector3(0f, 1.45f, 0f),
                    new Vector3(0f, 1.57f, 0f),
                });
            _chain = BindChainTipFirst(bones);
            var job = ProneJob(bones);
            job.spineStretchMax = 0.03f;
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { hipsTracked = true });

            Vector3 hipsTarget = bones[0].position + new Vector3(0.1f, 0f, 0.05f);
            job.targetPositionHips = hipsTarget;
            job.targetPositionHead = bones[4].position - new Vector3(0f, 0.002f, 0f);
            job.targetRotationHead = Quaternion.Euler(10f, 25f, 0f);

            job.poseStream = skeleton.Stream;
            job.SolveSpine();

            float posDelta = (skeleton.Stream.GetPosition(job.handleHips) - hipsTarget).magnitude;
            TestContext.WriteLine($"hips-to-target delta {posDelta * 1000f:F3} mm");
            Assert.Less(posDelta, 1e-4f, "with proneBodyPose off the hips placement must behave exactly as before");
        }
    }
}
