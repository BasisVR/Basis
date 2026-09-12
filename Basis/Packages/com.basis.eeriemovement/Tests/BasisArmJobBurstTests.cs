using Basis.IK;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisArmJobBurstTests
    {
        GameObject root;
        BasisPoseSkeleton skeleton;
        NativeArray<BasisBoneHandle> chain;
        NativeArray<BasisArmState> armState;
        NativeArray<BasisSpineRestFrame> restFrames;
        NativeArray<BasisChestSpringState> chestSpring;
        NativeArray<BasisLegSlotState> legState;
        NativeArray<BasisLegDiagnostics> legDiagnostics;
        BasisIKGizmoRecorder gizmos;
        Transform hips, spine, chest, neck, head, leftShoulder, leftUpperArm, leftLowerArm, leftLowerArmTwist, leftHand, rightShoulder, rightUpperArm, rightLowerArm, rightHand, leftUpperLeg, leftLowerLeg, leftFoot, rightUpperLeg, rightLowerLeg, rightFoot;
        const float twistPosition = 0.6f;
        bool burstWasEnabled, burstWasSynchronous;
        [SetUp]
        public void SetUp()
        {
            burstWasEnabled = BurstCompiler.Options.EnableBurstCompilation;
            burstWasSynchronous = BurstCompiler.Options.EnableBurstCompileSynchronously;
            BurstCompiler.Options.EnableBurstCompilation = true;
            BurstCompiler.Options.EnableBurstCompileSynchronously = true;
        }
        [TearDown]
        public void TearDown()
        {
            BurstCompiler.Options.EnableBurstCompilation = burstWasEnabled;
            BurstCompiler.Options.EnableBurstCompileSynchronously = burstWasSynchronous;
            if (chain.IsCreated) chain.Dispose();
            if (armState.IsCreated) armState.Dispose();
            if (restFrames.IsCreated) restFrames.Dispose();
            if (chestSpring.IsCreated) chestSpring.Dispose();
            if (legState.IsCreated) legState.Dispose();
            if (legDiagnostics.IsCreated) legDiagnostics.Dispose();
            gizmos.Dispose();
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
            root = new GameObject("ArmJobRig");
            hips = Bone("Hips", root.transform, new Vector3(0f, 0.95f, 0f));
            spine = Bone("Spine", hips, new Vector3(0f, 1.10f, 0f));
            chest = Bone("Chest", spine, new Vector3(0f, 1.25f, 0f));
            neck = Bone("Neck", chest, new Vector3(0f, 1.45f, 0f));
            head = Bone("Head", neck, new Vector3(0f, 1.57f, 0f));
            leftShoulder = Bone("LeftShoulder", chest, new Vector3(-0.05f, 1.40f, 0f));
            leftUpperArm = Bone("LeftUpperArm", leftShoulder, new Vector3(-0.18f, 1.40f, 0f));
            leftLowerArm = Bone("LeftLowerArm", leftUpperArm, new Vector3(-0.46f, 1.40f, 0f));
            leftHand = Bone("LeftHand", leftLowerArm, new Vector3(-0.72f, 1.40f, 0f));
            leftLowerArmTwist = Bone("LeftLowerArmTwist", leftLowerArm, Vector3.Lerp(leftLowerArm.position, leftHand.position, twistPosition));
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
            Transform[] bones = { hips, spine, chest, neck, head, leftShoulder, leftUpperArm, leftLowerArm, leftLowerArmTwist, leftHand, rightShoulder, rightUpperArm, rightLowerArm, rightHand, leftUpperLeg, leftLowerLeg, leftFoot, rightUpperLeg, rightLowerLeg, rightFoot };
            skeleton = new BasisPoseSkeleton();
            skeleton.Build(hips, bones);
            skeleton.GatherNow();
            Transform[] chainRootFirst = { hips, spine, chest, neck, head };
            chain = new NativeArray<BasisBoneHandle>(chainRootFirst.Length, Allocator.Persistent);
            for (int i = 0; i < chainRootFirst.Length; i++) chain[i] = skeleton.Bind(chainRootFirst[chainRootFirst.Length - 1 - i]);
            armState = new NativeArray<BasisArmState>(BasisEerieMovement.armCount, Allocator.Persistent);
            restFrames = new NativeArray<BasisSpineRestFrame>(chainRootFirst.Length, Allocator.Persistent);
            chestSpring = new NativeArray<BasisChestSpringState>(1, Allocator.Persistent);
            legState = new NativeArray<BasisLegSlotState>(2, Allocator.Persistent);
            legDiagnostics = new NativeArray<BasisLegDiagnostics>(2, Allocator.Persistent);
            gizmos = default;
            gizmos.Create(64, 16);
        }
        BasisEerieMovement Job(Vector3 leftTarget, Vector3 rightTarget)
        {
            var job = new BasisEerieMovement
            {
                chainHeadToSpine = chain, chainChestIdx = 2, spineMaxIterations = 20, spineTolerance = 0.001f, spineCCDRelax = 1.0f, spineTwistKeep = 0.25f, spineNeckTwistKeep = 0.9f, neckMaxConeDeg = 45f, maxChestDeltaDeg = 30f,
                thoracicBendStiffen = 0.3f, spineTautBandFrac = 0.015f, chestIkWeight = 0.5f, chestIkIterations = 8, chestIkHeadRestoreSweeps = 2, chestPullMaxDist = 0.5f, ikLockMode = BasisIKLockMode.LockHead, minHeadSpineHeight = 0.62f,
                tposeLengthNeckToHips = new Vector3(0f, 0.5f, 0f), offsetRotationHead = Quaternion.identity, offsetRotationHips = Quaternion.identity, offsetRotationChest = Quaternion.identity, offsetRotationLeftHand = Quaternion.identity, offsetRotationRightHand = Quaternion.identity,
                offsetRotationLeftFoot = Quaternion.identity, offsetRotationRightFoot = Quaternion.identity, offsetRotationLeftShoulder = Quaternion.identity, offsetRotationRightShoulder = Quaternion.identity, targetRotationHips = Quaternion.identity, targetRotationChest = Quaternion.identity,
                targetPositionHips = hips.position, targetPositionHead = head.position, targetRotationHead = Quaternion.identity, playerUp = Vector3.up,
                targetPositionLeftHand = leftTarget, targetRotationLeftHand = Quaternion.Euler(20f, -30f, 10f), targetPositionRightHand = rightTarget, targetRotationRightHand = Quaternion.Euler(20f, 30f, -10f),
                tposeShoulderToHandLeft = 0.67f, tposeShoulderToHandRight = 0.67f, tposeClavicleLenLeft = 0.13f, tposeClavicleLenRight = 0.13f,
                shoulderSolveEnabled = true, shoulderShrugEnabled = true, shoulderElevationFactor = 1f, shoulderProtractionFactor = 1f, shoulderMaxDeg = 30f,
                armJointLimits = true, armReachSoftness = 0.06f, armSwivelSmoothTime = 0.08f, armSwivelMaxRateDeg = 720f, armSwivelSwitchDwell = 0.2f, armPriorWeight = 1f, armPreviousWeight = 0.25f,
                forearmPronationMaxDeg = 95f, forearmSupinationMaxDeg = 90f, humeralInternalMaxDeg = 70f, humeralExternalMaxDeg = 90f, wristFlexionMaxDeg = 80f, wristExtensionMaxDeg = 70f, wristRadialMaxDeg = 20f, wristUlnarMaxDeg = 30f,
                collisionsEnabled = true, protectElbow = true, chestRadius = 0.055f, collisionSkin = 0.025f, chestArmSwingFactor = 0.3f, chestArmSwingMaxDeg = 15f, chestFollowChestShare = 0.6f,
                lowerArmTwistFraction = 1f, upperArmTwistFraction = 1f,
                armState = armState, chainSpineRestFrames = restFrames, chestSpring = chestSpring, legState = legState, legDiagnostics = legDiagnostics, gizmos = gizmos,
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
            job.handleLeftLowerArmTwist = skeleton.Bind(leftLowerArmTwist);
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
            job.slotPositions.Length = BasisEerieMovement.Count;
            job.slotRotations.Length = BasisEerieMovement.Count;
            job.slotOffsets.Length = BasisEerieMovement.Count;
            job.slotWeights.Length = BasisEerieMovement.Count;
            for (int i = 0; i < BasisEerieMovement.Count; i++) { job.slotRotations[i] = Quaternion.identity; job.slotOffsets[i] = Quaternion.identity; }
            BasisEeriePlanner.Bind(ref job);
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { hipsTracked = true, leftHandWeight = 1f, rightHandWeight = 1f, deltaTime = 1f / 90f });
            job.poseStream = skeleton.Stream;
            job.poseStream.deltaTime = 1f / 90f;
            return job;
        }
        [Test]
        public void ArmPass_RunsUnderBurst_AndLandsBothHands()
        {
            BuildRig();
            Vector3 left = new Vector3(-0.25f, 1.15f, 0.35f), right = new Vector3(0.30f, 1.60f, 0.20f);
            var job = Job(left, right);
            Assert.That(job.plan.leftArm.solve && job.plan.rightArm.solve, Is.True);
            Assert.That(job.plan.hasArmState, Is.True);
            for (int frame = 0; frame < 3; frame++)
            {
                job.Schedule().Complete();
            }
            Assert.That(Vector3.Distance(job.poseStream.GetPosition(job.handleLeftHand), left), Is.LessThan(0.01f), "left hand did not land on its target through the Burst job");
            Assert.That(Vector3.Distance(job.poseStream.GetPosition(job.handleRightHand), right), Is.LessThan(0.01f), "right hand did not land on its target through the Burst job");
            BasisArmState l = armState[BasisEerieMovement.armLeft], r = armState[BasisEerieMovement.armRight];
            Assert.That(l.Seeded && r.Seeded, Is.True, "the job must carry per-arm swivel state across frames");
            Assert.That(float.IsFinite(l.SwivelDeg) && float.IsFinite(r.SwivelDeg), Is.True);
            Vector3 leftElbow = job.poseStream.GetPosition(job.handleLeftLowerArm), rightElbow = job.poseStream.GetPosition(job.handleRightLowerArm);
            Assert.That(leftElbow.y, Is.LessThan((job.poseStream.GetPosition(job.handleLeftUpperArm).y + left.y) * 0.5f), "a hand in front of the chest must hang the elbow below the arm line");
            Assert.That(Vector3.Distance(leftElbow, job.poseStream.GetPosition(job.handleLeftUpperArm)), Is.EqualTo(0.28f).Within(0.01f), "upper arm length changed inside the job");
            Assert.That(Vector3.Distance(rightElbow, job.poseStream.GetPosition(job.handleRightUpperArm)), Is.EqualTo(0.28f).Within(0.01f));
        }
        [Test]
        public void ArmPass_ManagedAndBurst_Agree()
        {
            BuildRig();
            Vector3 left = new Vector3(-0.20f, 1.25f, 0.30f), right = new Vector3(0.35f, 1.10f, 0.25f);
            var job = Job(left, right);
            job.Schedule().Complete();
            Vector3 burstLeft = job.poseStream.GetPosition(job.handleLeftLowerArm), burstRight = job.poseStream.GetPosition(job.handleRightLowerArm);
            armState[0] = default;
            armState[1] = default;
            skeleton.GatherNow();
            job.poseStream = skeleton.Stream;
            job.poseStream.deltaTime = 1f / 90f;
            job.ProcessAnimation();
            Vector3 managedLeft = job.poseStream.GetPosition(job.handleLeftLowerArm), managedRight = job.poseStream.GetPosition(job.handleRightLowerArm);
            Assert.That(Vector3.Distance(burstLeft, managedLeft), Is.LessThan(1e-3f), "Burst and managed arm solves disagree on the left elbow");
            Assert.That(Vector3.Distance(burstRight, managedRight), Is.LessThan(1e-3f), "Burst and managed arm solves disagree on the right elbow");
        }
        const float shoulderDt = 1f / 90f;
        void ShoulderFrame(ref BasisEerieMovement job, bool tracked, float weight)
        {
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { hipsTracked = true, leftHandWeight = 1f, rightHandWeight = 1f, deltaTime = shoulderDt, leftShoulderTracked = tracked, leftShoulderWeight = weight });
            skeleton.GatherNow();
            job.poseStream = skeleton.Stream;
            job.poseStream.deltaTime = shoulderDt;
            job.ProcessAnimation();
        }
        [Test]
        public void ShoulderTracker_FullWeight_DrivesTheClavicle_AndFadesBackWhenLost()
        {
            BuildRig();
            Quaternion trackerRot = Quaternion.Euler(0f, 0f, 20f);
            var job = Job(new Vector3(-0.25f, 1.15f, 0.35f), new Vector3(0.30f, 1.60f, 0.20f));
            job.shoulderTrackerBlendTime = 0.25f;
            job.targetRotationLeftShoulder = trackerRot;
            ShoulderFrame(ref job, false, 0f);
            Quaternion rhythm = job.poseStream.GetRotation(job.handleLeftShoulder);
            Assert.That(job.plan.leftShoulder, Is.EqualTo(BasisEerieShoulderMode.Solve));
            Assert.That(Quaternion.Angle(rhythm, trackerRot), Is.GreaterThan(5f), "the test tracker pose must differ from the solved shoulder");
            ShoulderFrame(ref job, true, 1f);
            Assert.That(job.plan.leftShoulder, Is.EqualTo(BasisEerieShoulderMode.Tracker));
            float firstStep = Quaternion.Angle(rhythm, job.poseStream.GetRotation(job.handleLeftShoulder));
            Assert.That(firstStep, Is.LessThan(2f), "a tracker appearing must not pop the clavicle");
            Assert.That(firstStep, Is.GreaterThan(0.1f), "the blend must start moving toward the tracker on the first frame");
            float previous = Quaternion.Angle(job.poseStream.GetRotation(job.handleLeftShoulder), trackerRot);
            for (int frame = 0; frame < 40; frame++)
            {
                ShoulderFrame(ref job, true, 1f);
                float remaining = Quaternion.Angle(job.poseStream.GetRotation(job.handleLeftShoulder), trackerRot);
                Assert.That(remaining, Is.LessThanOrEqualTo(previous + 1e-3f), "the clavicle must approach the tracker monotonically");
                previous = remaining;
            }
            Assert.That(previous, Is.LessThan(0.05f), "with a full-weight tracker the clavicle must sit exactly on it");
            Assert.That(armState[BasisEerieMovement.armLeft].ShoulderBlend, Is.EqualTo(1f));
            ShoulderFrame(ref job, false, 0f);
            Assert.That(Quaternion.Angle(job.poseStream.GetRotation(job.handleLeftShoulder), trackerRot), Is.LessThan(2f), "losing the tracker must hold its last pose and fade, not snap to the solve");
            for (int frame = 0; frame < 40; frame++) ShoulderFrame(ref job, false, 0f);
            Assert.That(Quaternion.Angle(job.poseStream.GetRotation(job.handleLeftShoulder), rhythm), Is.LessThan(0.05f), "with the tracker gone the clavicle must return to the solved shoulder");
            Assert.That(armState[BasisEerieMovement.armLeft].ShoulderBlend, Is.EqualTo(0f));
        }
        [Test]
        public void ShoulderTracker_PartialWeight_BlendsTowardTheTracker_AndRunsUnderBurst()
        {
            BuildRig();
            Quaternion trackerRot = Quaternion.Euler(0f, 0f, 20f);
            var job = Job(new Vector3(-0.25f, 1.15f, 0.35f), new Vector3(0.30f, 1.60f, 0.20f));
            job.shoulderTrackerBlendTime = 0.25f;
            job.targetRotationLeftShoulder = trackerRot;
            ShoulderFrame(ref job, false, 0f);
            Quaternion rhythm = job.poseStream.GetRotation(job.handleLeftShoulder);
            for (int frame = 0; frame < 40; frame++) ShoulderFrame(ref job, true, 0.5f);
            Quaternion half = job.poseStream.GetRotation(job.handleLeftShoulder);
            float total = Quaternion.Angle(rhythm, trackerRot);
            Assert.That(Quaternion.Angle(half, rhythm), Is.EqualTo(total * 0.5f).Within(0.3f));
            Assert.That(Quaternion.Angle(half, trackerRot), Is.EqualTo(total * 0.5f).Within(0.3f));
            BasisEeriePlanner.Frame(ref job, new BasisEerieFrameFacts { hipsTracked = true, leftHandWeight = 1f, rightHandWeight = 1f, deltaTime = shoulderDt, leftShoulderTracked = true, leftShoulderWeight = 0.5f });
            skeleton.GatherNow();
            job.poseStream = skeleton.Stream;
            job.poseStream.deltaTime = shoulderDt;
            job.Schedule().Complete();
            Assert.That(Quaternion.Angle(job.poseStream.GetRotation(job.handleLeftShoulder), half), Is.LessThan(0.05f), "Burst and managed shoulder blends disagree");
        }
        static float WristResidualDeg(in BasisEerieMovement job)
        {
            Quaternion fore = job.poseStream.GetRotation(job.handleLeftLowerArm), hand = job.poseStream.GetRotation(job.handleLeftHand);
            Vector3 axis = job.poseStream.GetPosition(job.handleLeftHand) - job.poseStream.GetPosition(job.handleLeftLowerArm);
            Quaternion foreInv = Quaternion.Inverse(fore);
            return BasisTwistSolveCore.SignedTwistAngleDeg(foreInv * hand, foreInv * axis.normalized);
        }
        void Settle(ref BasisEerieMovement job, int frames)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                skeleton.GatherNow();
                job.poseStream = skeleton.Stream;
                job.poseStream.deltaTime = 1f / 90f;
                job.ProcessAnimation();
            }
        }
        [Test]
        public void ForearmRoll_FollowsTheHandTwist_AndTheWristKeepsOnlyItsShare()
        {
            BuildRig();
            Vector3 left = new Vector3(-0.28f, 1.18f, 0.34f), right = new Vector3(0.30f, 1.60f, 0.20f);
            var job = Job(left, right);
            Settle(ref job, 30);
            Quaternion foreBefore = job.poseStream.GetRotation(job.handleLeftLowerArm);
            Vector3 axis = (job.poseStream.GetPosition(job.handleLeftHand) - job.poseStream.GetPosition(job.handleLeftLowerArm)).normalized;
            Vector3 handBefore = job.poseStream.GetPosition(job.handleLeftHand);
            float residualBefore = WristResidualDeg(job);
            job.targetRotationLeftHand = Quaternion.AngleAxis(60f, axis) * job.targetRotationLeftHand;
            Settle(ref job, 30);
            float residualAfter = WristResidualDeg(job);
            Assert.That(Mathf.Abs(residualBefore), Is.LessThanOrEqualTo(BasisArmSolveCore.WristKeepMaxDeg + 1f), "the wrist carried more axial twist than a carpus can before the roll");
            Assert.That(Mathf.Abs(residualAfter), Is.LessThanOrEqualTo(BasisArmSolveCore.WristKeepMaxDeg + 1f), $"the wrist was left pinching {residualAfter:F1} deg after a 60 deg controller roll");
            Quaternion foreAfter = job.poseStream.GetRotation(job.handleLeftLowerArm);
            float carried = BasisTwistSolveCore.SignedTwistAngleDeg(foreAfter * Quaternion.Inverse(foreBefore), axis);
            Assert.That(Mathf.Abs(carried), Is.GreaterThan(40f), $"the forearm only carried {carried:F1} deg of a 60 deg controller roll");
            Assert.That(Mathf.Abs(carried), Is.LessThan(62f), $"the forearm over-rolled, carrying {carried:F1} deg of a 60 deg controller roll");
            Assert.That(Quaternion.Angle(job.poseStream.GetRotation(job.handleLeftHand), job.targetRotationLeftHand * job.offsetRotationLeftHand), Is.LessThan(0.1f), "the roll moved the hand off the controller");
            Assert.That(Vector3.Distance(job.poseStream.GetPosition(job.handleLeftHand), handBefore), Is.LessThan(0.02f), "a pure forearm roll must not move the hand");
            Assert.That(Vector3.Distance(job.poseStream.GetPosition(job.handleLeftHand), left), Is.LessThan(0.01f), "the hand left its target");
            Quaternion managed = job.poseStream.GetRotation(job.handleLeftLowerArm);
            skeleton.GatherNow();
            job.poseStream = skeleton.Stream;
            job.poseStream.deltaTime = 1f / 90f;
            job.Schedule().Complete();
            Assert.That(Quaternion.Angle(job.poseStream.GetRotation(job.handleLeftLowerArm), managed), Is.LessThan(0.2f), "Burst and managed disagree on the forearm roll");
        }
        [Test]
        public void TheForearmTwistBone_CarriesTheShareItsPositionAlongTheBoneEarns()
        {
            BuildRig();
            Vector3 left = new Vector3(-0.28f, 1.18f, 0.34f), right = new Vector3(0.30f, 1.60f, 0.20f);
            var job = Job(left, right);
            Assert.That(job.plan.leftArm.lowerTwist, Is.True, "the rig must exercise the forearm twist bone");
            Settle(ref job, 30);
            Vector3 axis = (job.poseStream.GetPosition(job.handleLeftHand) - job.poseStream.GetPosition(job.handleLeftLowerArm)).normalized;
            job.targetRotationLeftHand = Quaternion.AngleAxis(60f, axis) * job.targetRotationLeftHand;
            Settle(ref job, 30);
            Quaternion fore = job.poseStream.GetRotation(job.handleLeftLowerArm), foreInv = Quaternion.Inverse(fore);
            Vector3 axisLocal = foreInv * (job.poseStream.GetPosition(job.handleLeftHand) - job.poseStream.GetPosition(job.handleLeftLowerArm)).normalized;
            float wrist = BasisTwistSolveCore.SignedTwistAngleDeg(foreInv * job.poseStream.GetRotation(job.handleLeftHand), axisLocal);
            float carried = BasisTwistSolveCore.SignedTwistAngleDeg(foreInv * job.poseStream.GetRotation(job.handleLeftLowerArmTwist), axisLocal);
            Assert.That(Mathf.Abs(wrist), Is.GreaterThan(3f), "anti-vacuity: the wrist must still hold a real share for the twist bone to split");
            Assert.That(carried, Is.EqualTo(wrist * twistPosition).Within(1f), $"a twist bone {twistPosition * 100f:F0}% along the forearm must carry that share of the wrist twist, not a flat fraction of it");
        }
    }
}
