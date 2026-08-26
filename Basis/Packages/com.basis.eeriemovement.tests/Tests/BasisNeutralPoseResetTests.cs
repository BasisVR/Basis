using Basis.IK;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisNeutralPoseResetTests
    {
        const float rotationToleranceDeg = 0.01f, positionTolerance = 1e-4f;
        GameObject root;
        BasisPoseSkeleton skeleton;
        NativeArray<BasisBoneHandle> chain;
        Transform hips, spine, chest, neck, head, leftShoulder, leftUpperArm, leftLowerArm, leftHand, rightShoulder, rightUpperArm, rightLowerArm, rightHand, leftUpperLeg, leftLowerLeg, leftFoot, rightUpperLeg, rightLowerLeg, rightFoot;
        [TearDown]
        public void TearDown()
        {
            if (chain.IsCreated) chain.Dispose();
            skeleton?.Dispose();
            skeleton = null;
            if (root != null) Object.DestroyImmediate(root);
            root = null;
        }
        static Transform Bone(string name, Transform parent, Vector3 worldPosition)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(worldPosition, Quaternion.identity);
            go.transform.SetParent(parent, true);
            return go.transform;
        }
        void BuildRig()
        {
            root = new GameObject("NeutralPoseRig");
            hips = Bone("Hips", root.transform, new Vector3(0f, 0.95f, 0f));
            spine = Bone("Spine", hips, new Vector3(0f, 1.10f, 0f));
            chest = Bone("Chest", spine, new Vector3(0f, 1.25f, 0f));
            neck = Bone("Neck", chest, new Vector3(0f, 1.45f, 0f));
            head = Bone("Head", neck, new Vector3(0f, 1.57f, 0f));
            leftShoulder = Bone("LeftShoulder", chest, new Vector3(-0.05f, 1.40f, 0f));
            leftUpperArm = Bone("LeftUpperArm", leftShoulder, new Vector3(-0.18f, 1.40f, 0f));
            leftLowerArm = Bone("LeftLowerArm", leftUpperArm, new Vector3(-0.46f, 1.40f, 0f));
            leftHand = Bone("LeftHand", leftLowerArm, new Vector3(-0.72f, 1.40f, 0f));
            rightShoulder = Bone("RightShoulder", chest, new Vector3(0.05f, 1.40f, 0f));
            rightUpperArm = Bone("RightUpperArm", rightShoulder, new Vector3(0.18f, 1.40f, 0f));
            rightLowerArm = Bone("RightLowerArm", rightUpperArm, new Vector3(0.46f, 1.40f, 0f));
            rightHand = Bone("RightHand", rightLowerArm, new Vector3(0.72f, 1.40f, 0f));
            leftUpperLeg = Bone("LeftUpperLeg", hips, new Vector3(-0.09f, 0.90f, 0f));
            leftLowerLeg = Bone("LeftLowerLeg", leftUpperLeg, new Vector3(-0.09f, 0.48f, 0f));
            leftFoot = Bone("LeftFoot", leftLowerLeg, new Vector3(-0.09f, 0.08f, 0f));
            rightUpperLeg = Bone("RightUpperLeg", hips, new Vector3(0.09f, 0.90f, 0f));
            rightLowerLeg = Bone("RightLowerLeg", rightUpperLeg, new Vector3(0.09f, 0.48f, 0f));
            rightFoot = Bone("RightFoot", rightLowerLeg, new Vector3(0.09f, 0.08f, 0f));
            Transform[] bones = { hips, spine, chest, neck, head, leftShoulder, leftUpperArm, leftLowerArm, leftHand, rightShoulder, rightUpperArm, rightLowerArm, rightHand, leftUpperLeg, leftLowerLeg, leftFoot, rightUpperLeg, rightLowerLeg, rightFoot };
            skeleton = new BasisPoseSkeleton();
            skeleton.Build(hips, bones);
            skeleton.GatherNow();
            Transform[] chainRootFirst = { hips, spine, chest, neck, head };
            chain = new NativeArray<BasisBoneHandle>(chainRootFirst.Length, Allocator.Persistent);
            for (int i = 0; i < chainRootFirst.Length; i++)
            {
                chain[i] = skeleton.Bind(chainRootFirst[chainRootFirst.Length - 1 - i]);
            }
        }
        BasisEerieMovement Job()
        {
            var job = new BasisEerieMovement
            {
                chainHeadToSpine = chain,
                chainChestIdx = 2,
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
                ikLockMode = BasisIKLockMode.LockHead,
                minHeadSpineHeight = 0.62f,
                tposeLengthNeckToHips = new Vector3(0f, 0.5f, 0f),
                offsetRotationHead = Quaternion.identity,
                offsetRotationHips = Quaternion.identity,
                offsetRotationChest = Quaternion.identity,
                offsetRotationLeftHand = Quaternion.identity,
                offsetRotationRightHand = Quaternion.identity,
                offsetRotationLeftFoot = Quaternion.identity,
                offsetRotationRightFoot = Quaternion.identity,
                targetRotationHips = Quaternion.identity,
                targetRotationChest = Quaternion.identity,
                targetPositionHips = hips.position,
                targetPositionHead = head.position + new Vector3(0.04f, -0.03f, 0.05f),
                targetRotationHead = Quaternion.Euler(15f, 30f, 0f),
                playerUp = Vector3.up,
                chestIkTarget = false,
                spineAnatomicalRom = false,
                targetPositionLeftHand = new Vector3(-0.25f, 1.15f, 0.35f),
                targetRotationLeftHand = Quaternion.Euler(20f, -30f, 10f),
                hintPositionLeftHand = new Vector3(-0.35f, 1.05f, 0.05f),
                targetPositionLeftLowerLeg = new Vector3(-0.09f, 0.25f, 0.20f),
                targetRotationLeftLowerLeg = Quaternion.Euler(10f, 0f, 0f),
                hintPositionLeftLowerLeg = new Vector3(-0.09f, 0.50f, 0.25f),
                kneeBendPrefLeft = Vector3.forward,
                kneeAnteriorRef = Vector3.forward,
            };
            job.handleHips = skeleton.Bind(hips);
            job.handleSpine = skeleton.Bind(spine);
            job.handleChest = skeleton.Bind(chest);
            job.handleNeck = skeleton.Bind(neck);
            job.handleHead = skeleton.Bind(head);
            job.handleLeftShoulder = skeleton.Bind(leftShoulder);
            job.handleLeftUpperArm = skeleton.Bind(leftUpperArm);
            job.handleLeftLowerArm = skeleton.Bind(leftLowerArm);
            job.handleLeftHand = skeleton.Bind(leftHand);
            job.handleRightShoulder = skeleton.Bind(rightShoulder);
            job.handleRightUpperArm = skeleton.Bind(rightUpperArm);
            job.handleRightLowerArm = skeleton.Bind(rightLowerArm);
            job.handleRightHand = skeleton.Bind(rightHand);
            job.handleLeftUpperLeg = skeleton.Bind(leftUpperLeg);
            job.handleLeftLowerLeg = skeleton.Bind(leftLowerLeg);
            job.handleLeftFoot = skeleton.Bind(leftFoot);
            job.handleRightUpperLeg = skeleton.Bind(rightUpperLeg);
            job.handleRightLowerLeg = skeleton.Bind(rightLowerLeg);
            job.handleRightFoot = skeleton.Bind(rightFoot);
            BasisEeriePlanner.Bind(ref job);
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { hipsTracked = true, leftHandWeight = 1f, leftElbowTracked = true, leftLegTracked = true, leftKneeTracked = true });
            job.poseStream = skeleton.Stream;
            return job;
        }
        static void RollAboutBoneAxis(Transform bone, Transform child, float degrees)
        {
            Vector3 axis = (child.position - bone.position).normalized;
            bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
        }
        void Capture(Transform[] bones, out Quaternion[] rotations, out Vector3[] positions)
        {
            rotations = new Quaternion[bones.Length];
            positions = new Vector3[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                BasisBoneHandle handle = skeleton.Bind(bones[i]);
                rotations[i] = skeleton.Stream.GetRotation(handle);
                positions[i] = skeleton.Stream.GetPosition(handle);
            }
        }
        static void AssertSame(Transform[] bones, Quaternion[] restRotations, Vector3[] restPositions, Quaternion[] rotations, Vector3[] positions)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                Assert.LessOrEqual(Quaternion.Angle(restRotations[i], rotations[i]), rotationToleranceDeg, bones[i].name + ": solve from a garbage incoming pose must match the solve from the neutral pose");
                Assert.LessOrEqual(Vector3.Distance(restPositions[i], positions[i]), positionTolerance, bones[i].name + ": position drifted between the neutral and garbage starts");
            }
        }
        [Test]
        public void ArmSolve_IgnoresIncomingRollOnTheChain()
        {
            BuildRig();
            Transform[] arm = { leftUpperArm, leftLowerArm, leftHand };
            var job = Job();
            job.SolveHand(true);
            Capture(arm, out Quaternion[] restRotations, out Vector3[] restPositions);
            Assert.LessOrEqual(Vector3.Distance(restPositions[2], job.targetPositionLeftHand), 1e-3f, "reachable target must be reached");
            RollAboutBoneAxis(leftUpperArm, leftLowerArm, 180f);
            RollAboutBoneAxis(leftLowerArm, leftHand, 90f);
            leftHand.rotation = Quaternion.Euler(0f, 0f, 180f) * leftHand.rotation;
            skeleton.GatherNow();
            Assert.Greater(Quaternion.Angle(skeleton.Stream.GetRotation(job.handleLeftUpperArm), Quaternion.identity), 90f, "the rig must actually carry the garbage roll into the stream");
            job.poseStream = skeleton.Stream;
            job.SolveHand(true);
            Capture(arm, out Quaternion[] rotations, out Vector3[] positions);
            AssertSame(arm, restRotations, restPositions, rotations, positions);
        }
        [Test]
        public void LegSolve_IgnoresIncomingRollOnTheChain()
        {
            BuildRig();
            Transform[] leg = { leftUpperLeg, leftLowerLeg, leftFoot };
            var job = Job();
            job.SolveLeg(0);
            Capture(leg, out Quaternion[] restRotations, out Vector3[] restPositions);
            Assert.LessOrEqual(Vector3.Distance(restPositions[2], job.targetPositionLeftLowerLeg), 1e-3f, "reachable target must be reached");
            RollAboutBoneAxis(leftUpperLeg, leftLowerLeg, 180f);
            RollAboutBoneAxis(leftLowerLeg, leftFoot, 90f);
            leftFoot.rotation = Quaternion.Euler(0f, 180f, 0f) * leftFoot.rotation;
            skeleton.GatherNow();
            Assert.Greater(Quaternion.Angle(skeleton.Stream.GetRotation(job.handleLeftUpperLeg), Quaternion.identity), 90f, "the rig must actually carry the garbage roll into the stream");
            job.poseStream = skeleton.Stream;
            job.SolveLeg(0);
            Capture(leg, out Quaternion[] rotations, out Vector3[] positions);
            AssertSame(leg, restRotations, restPositions, rotations, positions);
        }
        [Test]
        public void SpineSolve_IgnoresIncomingBendOnTheChain()
        {
            BuildRig();
            Transform[] torso = { hips, spine, chest, neck, head };
            var job = Job();
            job.SolveSpine();
            Capture(torso, out Quaternion[] restRotations, out Vector3[] restPositions);
            hips.rotation = Quaternion.Euler(0f, 0f, 180f);
            spine.localRotation = Quaternion.Euler(35f, 0f, 0f);
            chest.localRotation = Quaternion.Euler(-20f, 40f, 0f);
            neck.localRotation = Quaternion.Euler(0f, 0f, 90f);
            head.localRotation = Quaternion.Euler(60f, 0f, 0f);
            skeleton.GatherNow();
            Assert.Greater(Quaternion.Angle(skeleton.Stream.GetRotation(job.handleChest), Quaternion.identity), 90f, "the rig must actually carry the garbage bend into the stream");
            job.poseStream = skeleton.Stream;
            job.SolveSpine();
            Capture(torso, out Quaternion[] rotations, out Vector3[] positions);
            AssertSame(torso, restRotations, restPositions, rotations, positions);
        }
        [Test]
        public void Build_CapturesRestRotationsAndResetRestoresThem()
        {
            BuildRig();
            BasisBoneHandle upperArm = skeleton.Bind(leftUpperArm);
            Quaternion rest = skeleton.Stream.LocalRotation[upperArm.Index];
            leftUpperArm.localRotation = Quaternion.Euler(10f, 20f, 30f);
            skeleton.GatherNow();
            Assert.Greater(Quaternion.Angle(skeleton.Stream.LocalRotation[upperArm.Index], rest), 1f);
            skeleton.Stream.ResetToRest(upperArm);
            Assert.LessOrEqual(Quaternion.Angle(skeleton.Stream.LocalRotation[upperArm.Index], rest), rotationToleranceDeg);
            Assert.LessOrEqual(Quaternion.Angle(skeleton.Stream.RestLocalRotation[upperArm.Index], rest), rotationToleranceDeg);
            skeleton.Stream.ResetToRest(BasisBoneHandle.Unbound);
        }
        [Test]
        public void ArmSolve_IgnoresIncomingTranslationAndScale()
        {
            BuildRig();
            Transform[] arm = { leftUpperArm, leftLowerArm, leftHand };
            var job = Job();
            job.SolveHand(true);
            Capture(arm, out Quaternion[] restRotations, out Vector3[] restPositions);
            leftUpperArm.localScale = new Vector3(1.3f, 1.3f, 1.3f);
            leftLowerArm.localPosition += new Vector3(0f, 0.05f, 0.03f);
            leftHand.localPosition *= 0.8f;
            skeleton.GatherNow();
            Assert.Greater(Vector3.Distance(skeleton.Stream.LocalPosition[job.handleLeftLowerArm.Index], skeleton.Stream.RestLocalPosition[job.handleLeftLowerArm.Index]), 0.01f, "the rig must actually carry the garbage translation into the stream");
            Assert.Greater(skeleton.Stream.LocalScale[job.handleLeftUpperArm.Index].x, 1.2f, "the rig must actually carry the garbage scale into the stream");
            job.poseStream = skeleton.Stream;
            job.SolveHand(true);
            Capture(arm, out Quaternion[] rotations, out Vector3[] positions);
            AssertSame(arm, restRotations, restPositions, rotations, positions);
        }
        [Test]
        public void ResetToRest_RestoresPositionAndScale_ButKeepsTranslationFreeBones()
        {
            BuildRig();
            BasisBoneHandle lowerArm = skeleton.Bind(leftLowerArm), hipsHandle = skeleton.Bind(hips);
            Vector3 restPosition = skeleton.Stream.RestLocalPosition[lowerArm.Index], restScale = skeleton.Stream.RestLocalScale[lowerArm.Index];
            leftLowerArm.localPosition += new Vector3(0.02f, 0.04f, 0f);
            leftLowerArm.localScale = new Vector3(2f, 2f, 2f);
            hips.localPosition += new Vector3(0f, -0.1f, 0.2f);
            skeleton.GatherNow();
            Vector3 hipsGathered = skeleton.Stream.LocalPosition[hipsHandle.Index];
            skeleton.Stream.ResetToRest(lowerArm);
            skeleton.Stream.ResetToRest(hipsHandle);
            Assert.LessOrEqual(Vector3.Distance(skeleton.Stream.LocalPosition[lowerArm.Index], restPosition), positionTolerance);
            Assert.LessOrEqual(Vector3.Distance(skeleton.Stream.LocalScale[lowerArm.Index], restScale), positionTolerance);
            Assert.LessOrEqual(Vector3.Distance(skeleton.Stream.LocalPosition[hipsHandle.Index], hipsGathered), positionTolerance, "translation-free bones keep their gathered position");
        }
    }
}
