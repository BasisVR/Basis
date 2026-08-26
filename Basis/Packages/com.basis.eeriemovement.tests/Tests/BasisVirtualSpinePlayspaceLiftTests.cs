using Basis.IK;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Tests.IK
{
    public sealed class BasisVirtualSpinePlayspaceLiftTests
    {
        const int Head = 0, Neck = 1, Chest = 2, Spine = 3, Hips = 4;
        const int BoneCount = 5;
        const float StandingHeadY = 1.60f, StandingHipsY = 0.95f, ChestTposeY = 1.30f, SpineTposeY = 1.10f;
        const float NeckDrop = 0.12f, LenTotal = 0.65f;
        struct TorsoPose
        {
            public float3 HipsPos, ChestPos, SpinePos;
        }
        static TorsoPose RunSpineOnce(float3 headPos, float trackingLiftY)
        {
            var states = new NativeArray<BasisBoneSimState>(BoneCount, Allocator.Temp);
            var solve = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.Temp);
            try
            {
                for (int i = 0; i < BoneCount; i++)
                    states[i] = new BasisBoneSimState { OutgoingRotation = quaternion.identity, LastRunRotation = quaternion.identity };
                solve[0] = default;

                new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
                {
                    States = states,
                    State = solve,
                    P = MakeParams(headPos, trackingLiftY),
                    IdxHead = Head,
                    IdxNeck = Neck,
                    IdxChest = Chest,
                    IdxSpine = Spine,
                    IdxHips = Hips,
                }.Execute();

                return new TorsoPose
                {
                    HipsPos = states[Hips].OutgoingPosition,
                    ChestPos = states[Chest].OutgoingPosition,
                    SpinePos = states[Spine].OutgoingPosition,
                };
            }
            finally
            {
                states.Dispose();
                solve.Dispose();
            }
        }
        static BasisVirtualSpineCore.SpineSolveParams MakeParams(float3 headPos, float trackingLiftY)
        {
            return new BasisVirtualSpineCore.SpineSolveParams
            {
                Dt = 1f / 90f,
                Scale = 1f,
                TrackingLiftY = trackingLiftY,
                ParentMatrix = float4x4.identity,
                ParentRotation = quaternion.identity,
                EyeRot = quaternion.identity,

                HeadTargetPos = headPos,
                HeadTargetRot = quaternion.identity,
                NeckTargetPos = headPos,
                NeckTargetRot = quaternion.identity,
                ChestTargetPos = headPos,
                ChestTargetRot = quaternion.identity,
                SpineTargetPos = headPos,
                SpineTargetRot = quaternion.identity,

                HeadScaledOffset = float3.zero,
                NeckScaledOffset = new float3(0f, -NeckDrop, 0f),
                ChestScaledOffset = float3.zero,
                SpineScaledOffset = float3.zero,

                ChestTposeY = ChestTposeY,
                SpineTposeY = SpineTposeY,
                TposeHips = new float3(0f, StandingHipsY, 0f),

                LeftFootTracked = 0,
                RightFootTracked = 0,

                // Shipping defaults (BasisSettingsDefaults.VSpine*).
                ChestPitchFrac = 0.30f,
                ChestRollFrac = 0.30f,
                SpinePitchFrac = 0.10f,
                SpineRollFrac = 0.10f,
                NeckRotationSpeed = 40f,
                ChestRotationSpeed = 25f,
                SpineRotationSpeed = 30f,
                HipsRotationSpeed = 20f,
                HipsForwardBias = 0.02f,
                TorsoYawDeadzoneDeg = 45f,
                TorsoYawBlendSpeed = 8f,

                HipsFreeze = 0,
                IsLocomoting = 0,

                LenTotal = LenTotal,
                TChest = 0.35f,
                TSpine = 0.65f,

                StandingHipsLocalY = StandingHipsY,
                StandingHeadLocalY = StandingHeadY,
                PostureModel = 1,
                HipsCompressionStrength = 0.85f,
                HipsMaxDropMeters = 0.30f,
            };
        }
        static float3 StandingHead(float lift) => new float3(0f, StandingHeadY + lift, 0f);
        [Test]
        public void AVerticalSpaceDrag_ShiftsTheWholeTorsoWithTheBody()
        {
            TorsoPose baseline = RunSpineOnce(StandingHead(0f), 0f);

            foreach (float lift in new[] { -0.8f, -0.25f, 0.25f, 0.8f, 1.5f })
            {
                TorsoPose dragged = RunSpineOnce(StandingHead(lift), lift);

                Assert.AreEqual(baseline.HipsPos.y + lift, dragged.HipsPos.y, 0.005f, $"hips did not ride a {lift:+0.00;-0.00} m space drag with the body -- the untracked leg " + "chain hangs off this pelvis, so every leg bone is wrong by the same gap and the " + "calibration lock-in guides can never latch the leg/hip trackers.");
                Assert.AreEqual(baseline.ChestPos.y + lift, dragged.ChestPos.y, 0.005f, $"chest did not ride a {lift:+0.00;-0.00} m space drag -- its Y-pin is still anchored to " + "the floor-relative T-pose height instead of the lifted tracking space.");
                Assert.AreEqual(baseline.SpinePos.y + lift, dragged.SpinePos.y, 0.005f, $"spine did not ride a {lift:+0.00;-0.00} m space drag.");
            }
        }
        [Test]
        public void WithTheLiftUnplumbed_ADownwardDrag_ReadsAsAPhantomSquat()
        {
            const float drag = -0.8f;
            TorsoPose baseline = RunSpineOnce(StandingHead(0f), 0f), broken = RunSpineOnce(StandingHead(drag), 0f);
            float hipsError = Mathf.Abs((baseline.HipsPos.y + drag) - broken.HipsPos.y);
            Assert.Greater(hipsError, 0.15f, $"the un-lifted law only misplaced the hips by {hipsError * 100f:F1} cm on a {-drag * 100f:F0} cm " + "downward drag -- the phantom-squat misread this suite guards against is no longer " + "reproducible, so the equivariance gate is not being exercised.");

            float chestError = Mathf.Abs((baseline.ChestPos.y + drag) - broken.ChestPos.y);
            Assert.AreEqual(-drag, chestError, 0.01f,
                "the un-lifted chest pin should miss by exactly the drag (it is pinned to the floor-relative "
                + "T-pose Y); if it no longer does, the chest gate above is not measuring the pin.");
        }
        [Test]
        public void ARealCrouch_ReadsTheSame_WithOrWithoutASpaceDrag()
        {
            const float crouch = 0.35f;
            TorsoPose standingFlat = RunSpineOnce(StandingHead(0f), 0f);
            TorsoPose crouchFlat = RunSpineOnce(new float3(0f, StandingHeadY - crouch, 0f), 0f);
            float pelvisDropFlat = standingFlat.HipsPos.y - crouchFlat.HipsPos.y;

            Assert.Greater(pelvisDropFlat, 0.05f,"sanity: this crouch should move the pelvis at all, or the invariance below is vacuous");

            foreach (float lift in new[] { -0.8f, 0.8f })
            {
                TorsoPose standingLifted = RunSpineOnce(StandingHead(lift), lift);
                TorsoPose crouchLifted = RunSpineOnce(new float3(0f, StandingHeadY + lift - crouch, 0f), lift);
                float pelvisDropLifted = standingLifted.HipsPos.y - crouchLifted.HipsPos.y;

                Assert.AreEqual(pelvisDropFlat, pelvisDropLifted, 0.01f, $"the same {crouch * 100f:F0} cm crouch produced a different pelvis drop under a " + $"{lift:+0.00;-0.00} m space drag -- the posture law is no longer play-space equivariant.");
            }
        }
    }
}
