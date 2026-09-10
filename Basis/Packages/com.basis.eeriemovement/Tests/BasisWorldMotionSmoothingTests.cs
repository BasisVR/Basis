using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public sealed class BasisWorldMotionSmoothingTests
    {
        const float Dt = 1f / 90f, WalkSpeed = 1.5f, TurnDegPerSec = 45f;
        static readonly Vector3 LocalHead = new Vector3(0.4f, 1.6f, 0.3f);
        static readonly float3 Up = new float3(0f, 1f, 0f), Fwd = new float3(0f, 0f, 1f), Right = new float3(1f, 0f, 0f);
        static BasisEerieMovement SpringJob(NativeArray<BasisChestSpringState> spring)
        {
            var job = new BasisEerieMovement { chestSpring = spring, chestSpringHz = 12f, chestSpringDamping = 1f };
            job.plan.hasChestSpring = true;
            job.poseStream.deltaTime = Dt;
            job.poseStream.AnchorRotation = quaternion.identity;
            job.poseStream.AnchorScale = new float3(1f);
            return job;
        }
        static void PlayspaceAt(float t, out Vector3 pos, out Quaternion rot)
        {
            pos = new Vector3(0f, 0f, WalkSpeed * t);
            rot = Quaternion.AngleAxis(TurnDegPerSec * t, Vector3.up);
        }
        static float RunSpringWalk(bool anchorFollowsPlayspace, Vector3 localStepAfterFrame60, out float steady, out float stepFrameLag)
        {
            var spring = new NativeArray<BasisChestSpringState>(1, Allocator.TempJob);
            try
            {
                var job = SpringJob(spring);
                float worst = 0f;
                steady = stepFrameLag = 0f;
                for (int frame = 0; frame < 240; frame++)
                {
                    PlayspaceAt(frame * Dt, out Vector3 playspacePos, out Quaternion playspaceRot);
                    Vector3 target = playspacePos + playspaceRot * (LocalHead + (frame >= 60 ? localStepAfterFrame60 : Vector3.zero));
                    if (anchorFollowsPlayspace)
                    {
                        job.poseStream.AnchorPosition = playspacePos;
                        job.poseStream.AnchorRotation = playspaceRot;
                    }
                    float lag = (job.ApplyChestSpring(target) - target).magnitude;
                    if (frame == 60) stepFrameLag = lag;
                    if (frame < 30) continue;
                    if (lag > worst) worst = lag;
                    steady = lag;
                }
                return worst;
            }
            finally { spring.Dispose(); }
        }
        static float SnapTurnLag(bool anchorFollowsPlayspace)
        {
            var spring = new NativeArray<BasisChestSpringState>(1, Allocator.TempJob);
            try
            {
                var job = SpringJob(spring);
                float lag = 0f;
                for (int frame = 0; frame < 90; frame++)
                {
                    Quaternion playspaceRot = frame < 60 ? Quaternion.identity : Quaternion.AngleAxis(45f, Vector3.up);
                    Vector3 target = playspaceRot * LocalHead;
                    if (anchorFollowsPlayspace) job.poseStream.AnchorRotation = playspaceRot;
                    float l = (job.ApplyChestSpring(target) - target).magnitude;
                    if (frame == 60) lag = l;
                }
                return lag;
            }
            finally { spring.Dispose(); }
        }
        [Test]
        public void AWalkingTurningPlayspace_DoesNotExciteTheChestSpring()
        {
            float worst = RunSpringWalk(true, Vector3.zero, out _, out _);
            Assert.Less(worst, 1e-3f, $"the chest spring trailed a playspace that only walked and turned by {worst * 1000f:F2} mm");
        }
        [Test]
        public void WorldSpaceSpring_TrailsTheHeadWhileWalking()
        {
            RunSpringWalk(false, Vector3.zero, out float steady, out _);
            Assert.Greater(steady, 0.015f, $"a world-space spring is supposed to trail a 1.5 m/s walk by about 2v/omega (4 cm at 12 Hz); got {steady * 1000f:F1} mm, so this negative gate is testing nothing");
        }
        [Test]
        public void ASnapTurn_MovesTheChestSpringWithThePlayspace()
        {
            float lag = SnapTurnLag(true);
            Assert.Less(lag, 1e-4f, $"a 45 deg snap turn left the chest spring {lag * 1000f:F2} mm behind the head in the turn frame");
        }
        [Test]
        public void WorldSpaceSpring_SwingsThroughASnapTurn()
        {
            float lag = SnapTurnLag(false);
            Assert.Greater(lag, 0.1f, $"a world-space spring should be far behind the head in the snap frame (the head chord is ~38 cm); got {lag * 1000f:F1} mm, so this negative gate is testing nothing");
        }
        [Test]
        public void HeadMotionInsideThePlayspace_IsStillSprung()
        {
            RunSpringWalk(true, new Vector3(0.1f, 0f, 0f), out float settled, out float stepFrameLag);
            Assert.Greater(stepFrameLag, 0.05f, $"a 10 cm head step inside the playspace should still be sprung, not passed through; the spring only lagged {stepFrameLag * 1000f:F1} mm");
            Assert.Less(settled, 1e-3f, $"the spring should have settled onto the moved head two seconds later; it is still {settled * 1000f:F2} mm off");
        }
        static BasisFootSimParams WalkParams() => new BasisFootSimParams
        {
            predictionFactor = 0.5f, velocityBiasFactor = 0.1f, leadOffsetFactor = 0.1f, maxVelocityOffsetFraction = 0.3f, maxPredictionFraction = 0.35f,
            plantedLerpSpeed = 40f, rotationLerpSpeed = 16f, velocitySmoothAccel = 25f, velocitySmoothDecel = 50f, bodyFwdRateMoving = 6f, bodyFwdRateStationary = 2.5f, kneeHintLerpSpeed = 10f,
            maxFootTiltDegrees = 35f, maxFootYawDegrees = 18f, stepArcLiftExp = 0.6f, stepArcDropExp = 1.4f, stepHeightMinFraction = 0.4f, stepHeightStrideRefFraction = 0.45f,
            idleSpeedThreshold = 0.1f, idleBoostFraction = 0.5f, maxPlantedYawDegrees = 20f, idealSideEnforceFraction = 0.5f, stepTargetSideFraction = 0.5f, footSideEnforceFraction = 0.5f,
            maxVerticalDriftFraction = 0.25f, kneeForwardPushFraction = 0.3f, kneeMinSideFraction = 0.1f, bodyFwdHipsWeight = 0f, bodyFwdChestWeight = 0f, bodyFwdHeadWeight = 1f, hipBobFraction = 0f,
            footAlignLeft = quaternion.identity, footAlignRight = quaternion.identity,
            stanceWidth = 0.2f, hipToFoot = 0.95f, leftLegLen = 0.9f, rightLegLen = 0.9f, leftThighLen = 0.45f, leftShinLen = 0.45f, rightThighLen = 0.45f, rightShinLen = 0.45f,
            footLength = 0.24f, ankleHeight = 0.08f, stepTriggerDist = 0.2f, strideScale = 0.15f, stepHeightCalc = 0.12f, stepDurSlow = 0.40f, stepDurFast = 0.25f,
            raySphereRadius = 0.05f, footHeightOffset = 0f, fastSpeedRef = 3.5f, rayCastRange = 2f,
        };
        static BasisFootNativeState PlantedFoot(int sideSign, float3 hips, float halfStance, float thigh, float shin)
        {
            float3 planted = new float3(sideSign * halfStance, 0f, hips.z);
            quaternion rot = quaternion.LookRotation(Fwd, Up);
            return new BasisFootNativeState
            {
                sideSign = sideSign, thighLen = thigh, shinLen = shin, legLength = thigh + shin, phase = 0, plantedPos = planted, plantedRot = rot, plantedBodyFwd = Fwd,
                stepStartPos = planted, stepTargetPos = planted, stepStartRot = rot, landRot = rot, stepDur = 0.4f, plantedTime = 10f, idealPos = planted, filteredNormal = Up,
                currentPos = planted, currentRot = rot, kneeHint = (hips + planted) * 0.5f + Fwd * (thigh * 0.4f),
            };
        }
        static void FinalizeStepFlat(ref BasisFootNativeState f, in BasisFootSimState sim, in BasisFootSimParams p, float3 hips)
        {
            float fastYawRef = math.max(1f, 0.5f * p.maxPlantedYawDegrees / math.max(0.01f, p.stepDurFast));
            f.phase = 1;
            f.stepStartPos = f.currentPos;
            f.stepStartRot = f.currentRot;
            f.stepTimer = 0f;
            f.stepDur = math.lerp(p.stepDurSlow, p.stepDurFast, math.saturate(f.stepUrgency));
            f.stepArcScale = BasisFootSimulateJob.TurnStepArcFloor * math.saturate(math.abs(sim.smoothedYawRateDeg) / fastYawRef);
            f.stepTargetPos = new float3(f.predictedTargetXZ.x, p.footHeightOffset, f.predictedTargetXZ.z);
            f.filteredNormal = Up;
            float3 rawR = math.cross(Up, sim.smoothedBodyFwd);
            rawR = math.lengthsq(rawR) < 0.001f ? Right : math.normalize(rawR);
            float3 stp = f.stepTargetPos, hGround = new float3(hips.x, stp.y, hips.z);
            float lateral = math.dot(stp - hGround, rawR), minDist = p.stanceWidth * p.stepTargetSideFraction;
            if (f.sideSign > 0 && lateral < minDist) stp += rawR * (minDist - lateral);
            else if (f.sideSign < 0 && lateral > -minDist) stp -= rawR * (lateral + minDist);
            f.stepTargetPos = stp;
        }
        static BasisFootSimInput SimInput(float3 hips) => new BasisFootSimInput
        {
            dt = Dt, headPos = hips + Up * 0.65f, hipsPos = hips, hipsRot = quaternion.identity, chestRot = quaternion.identity, headRot = quaternion.identity, avatarForward = Fwd, avatarRight = Right,
            hasChest = false, groundHit = true, groundPoint = new float3(hips.x, 0f, hips.z), splayWhenCrouched = 0f, playerUp = Up,
        };
        static float RunFootSim(bool carry, System.Func<int, float3> hipsAt, int frames, int measureFrom, out int steps, out float jumpFrameHintDelta, int jumpFrame)
        {
            var p = WalkParams();
            var feet = new NativeArray<BasisFootNativeState>(2, Allocator.TempJob);
            var simState = new NativeArray<BasisFootSimState>(1, Allocator.TempJob);
            var input = new NativeArray<BasisFootSimInput>(1, Allocator.TempJob);
            var output = new NativeArray<BasisFootSimOutput>(1, Allocator.TempJob);
            steps = 0;
            jumpFrameHintDelta = 0f;
            try
            {
                float3 hips0 = hipsAt(0);
                feet[0] = PlantedFoot(-1, hips0, p.stanceWidth * 0.5f, p.leftThighLen, p.leftShinLen);
                feet[1] = PlantedFoot(+1, hips0, p.stanceWidth * 0.5f, p.rightThighLen, p.rightShinLen);
                simState[0] = new BasisFootSimState { prevHeadPos = hips0, hasPrevHeadPos = carry, smoothedBodyFwd = Fwd, smoothedBodyRight = Right, prevBodyFwd = Fwd, prevRootFwd = Fwd };
                var job = new BasisFootSimulateJob { p = p, feet = feet, simState = simState, input = input, output = output };
                double sum = 0;
                int count = 0;
                for (int frame = 0; frame < frames; frame++)
                {
                    float3 hips = hipsAt(frame);
                    if (!carry)
                    {
                        var s = simState[0];
                        s.hasPrevHeadPos = false;
                        simState[0] = s;
                    }
                    float3 hintBefore = feet[0].kneeHint;
                    input[0] = SimInput(hips);
                    job.Execute();
                    if (frame == jumpFrame) jumpFrameHintDelta = math.dot(feet[0].kneeHint - hintBefore, Fwd);
                    for (int i = 0; i < 2; i++)
                    {
                        var f = feet[i];
                        if (!f.wantsStep) continue;
                        FinalizeStepFlat(ref f, simState[0], p, hips);
                        f.wantsStep = false;
                        feet[i] = f;
                        steps++;
                    }
                    if (frame < measureFrom) continue;
                    for (int i = 0; i < 2; i++)
                    {
                        sum += math.dot(feet[i].kneeHint - (hips + feet[i].currentPos) * 0.5f, Fwd);
                        count++;
                    }
                }
                return count > 0 ? (float)(sum / count) : 0f;
            }
            finally { feet.Dispose(); simState.Dispose(); input.Dispose(); output.Dispose(); }
        }
        static float3 WalkingHips(int frame) => new float3(0f, 0.95f, WalkSpeed * frame * Dt);
        static float3 JumpingHips(int frame) => new float3(0f, 0.95f, frame >= 90 ? 1f : 0f);
        [Test]
        public void AWalkingBody_DoesNotDragTheSimKneeHintBehindTheLeg()
        {
            var p = WalkParams();
            float push = 0.5f * (p.leftThighLen + p.rightThighLen) * p.kneeForwardPushFraction;
            float carried = RunFootSim(true, WalkingHips, 450, 180, out int steps, out _, -1);
            Assert.GreaterOrEqual(steps, 6, $"the sim was supposed to walk for 5 s at {WalkSpeed} m/s but only took {steps} steps, so this test is not measuring a gait");
            Assert.AreEqual(push, carried, 0.04f, $"over a 3 s walk the sim knee hint should sit its forward push ({push * 100f:F1} cm) ahead of the hip-foot midpoint on average; it averaged {carried * 100f:F1} cm");
        }
        [Test]
        public void WithoutTheHipsCarry_TheSimKneeHintTrailsTheWalk()
        {
            var p = WalkParams();
            float expectedLag = WalkSpeed / p.kneeHintLerpSpeed;
            float carried = RunFootSim(true, WalkingHips, 450, 180, out _, out _, -1), uncarried = RunFootSim(false, WalkingHips, 450, 180, out _, out _, -1);
            Assert.Greater(carried - uncarried, 0.6f * expectedLag, $"smoothing the knee hint in world space should trail a {WalkSpeed} m/s walk by about v/rate = {expectedLag * 100f:F1} cm; the carried and uncarried runs only differ by {(carried - uncarried) * 100f:F1} cm, so this negative gate is testing nothing");
        }
        [Test]
        public void ABodyDisplacement_CarriesTheSimKneeHintTheSameFrame()
        {
            RunFootSim(true, JumpingHips, 91, 91, out _, out float delta, 90);
            Assert.Greater(delta, 0.9f, $"the hips moved 1 m in one frame but the knee hint only moved {delta * 100f:F1} cm with them");
        }
        [Test]
        public void WithoutTheHipsCarry_TheSimKneeHintIsLeftBehind()
        {
            RunFootSim(false, JumpingHips, 91, 91, out _, out float delta, 90);
            Assert.Less(delta, 0.2f, $"without the carry the knee hint should only lerp a fraction of the way in the displacement frame; it moved {delta * 100f:F1} cm, so this negative gate is testing nothing");
        }
        static readonly Vector3 ShoulderLocal = new Vector3(0.18f, 1.45f, 0f), ElbowLocal = new Vector3(0.21f, 1.2f, 0.05f), HandLocal = new Vector3(0.24f, 0.96f, 0.12f);
        static BasisSwivelFrame BodyFrame(Quaternion body) => BasisSwivelHintCore.BuildFrame(body * new Vector3(-0.18f, 1.45f, 0f), body * ShoulderLocal, body * new Vector3(0f, 0.95f, 0f), body * new Vector3(0f, 1.45f, 0f));
        static bool SolveHint(Quaternion body, ref BasisArmSlotState state, out Vector3 hintRel)
        {
            Vector3 shoulder = body * ShoulderLocal, hand = body * HandLocal;
            bool ok = BasisArmHintCore.Solve(BodyFrame(body), shoulder, body * ElbowLocal, hand, hand, false, body, true, ref state, true, 1.25f, Dt, out Vector3 hint);
            hintRel = hint - shoulder;
            return ok;
        }
        [Test]
        public void ASnapTurn_CarriesTheElbowHintWithTheBody()
        {
            var state = default(BasisArmSlotState);
            Vector3 before = default;
            for (int i = 0; i < 30; i++) Assert.IsTrue(SolveHint(Quaternion.identity, ref state, out before), "the elbow model must accept the test pose");
            Quaternion turn = Quaternion.AngleAxis(45f, Vector3.up);
            Assert.IsTrue(SolveHint(turn, ref state, out Vector3 after), "the elbow model must accept the turned pose");
            float deg = Vector3.Angle(turn * before, after);
            Assert.Less(deg, 0.5f, $"after a 45 deg snap turn with the arm still relative to the body, the elbow hint should have turned with the body; it is {deg:F2} deg off");
        }
        [Test]
        public void TheSwingCap_WithoutTheBodyCarry_HoldsTheElbowInTheOldHeading()
        {
            float armLen = (ElbowLocal - ShoulderLocal).magnitude + (HandLocal - ElbowLocal).magnitude;
            Assert.IsTrue(BasisSwivelHintCore.ArmHint(BodyFrame(Quaternion.identity), ShoulderLocal, HandLocal, armLen, false, out Vector3 hint, out float cond), "the elbow model must accept the test pose");
            Vector3 prevAxis = (HandLocal - ShoulderLocal).normalized, prevBend = (hint - ShoulderLocal).normalized;
            Quaternion turn = Quaternion.AngleAxis(45f, Vector3.up);
            Vector3 curAxis = turn * prevAxis, rawBend = turn * prevBend;
            float slew = BasisElbowSwingCapCore.SlewCapRad(Dt);
            Vector3 uncarried = BasisElbowSwingCapCore.Apply(prevBend, prevAxis, curAxis, rawBend, BasisElbowSwingCapCore.MaxGain, 0f, cond, slew);
            Vector3 carried = BasisElbowSwingCapCore.Apply(turn * prevBend, turn * prevAxis, curAxis, rawBend, BasisElbowSwingCapCore.MaxGain, 0f, cond, slew);
            Assert.Greater(Vector3.Angle(uncarried, rawBend), 20f, $"fed last frame's world-space bend across a snap turn the cap should hold the elbow near its old heading (the trap the body carry closes); it let it within {Vector3.Angle(uncarried, rawBend):F1} deg, so this negative gate is testing nothing");
            Assert.Less(Vector3.Angle(carried, rawBend), 0.1f, $"with the previous bend and axis carried by the body's rotation the cap must see no hand travel and pass the turned bend through; it is {Vector3.Angle(carried, rawBend):F2} deg off");
        }
    }
}
