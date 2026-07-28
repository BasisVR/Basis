using Basis.IK;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// "Play space offset stops the calibration from locking in legs (hips as well, and other things)."
    ///
    /// The play-space mover's vertical drag (BasisLocalPlayspaceMover.VerticalOffset) is injected into
    /// EVERY device pose in BasisInput.ComputeUnscaledDeviceCoord, so the whole tracked body — head and
    /// trackers alike — shifts up or down in bone-sim local space. The virtual spine, however, measured
    /// "standing" against FLOOR-ANCHORED T-pose heights (StandingHeadLocalY / StandingHipsLocalY /
    /// ChestTposeY / SpineTposeY, all Y-above-floor at scale). A uniform tracking-space shift therefore
    /// read as a POSTURE change:
    ///
    ///   * dragged DOWN: the head sits below "standing", so BasisPelvisPostureModel reads a phantom
    ///     squat/bend and holds the pelvis up to HALF the drag above the player's real hips. The whole
    ///     untracked leg chain hangs off that pelvis (Hips → UpperLeg → LowerLeg → Foot,
    ///     BasisLocalAvatarDriver:387-397), so every leg bone inherits the error — and the calibration
    ///     lock-in guides can never latch a leg or hip tracker onto a bone that is a constant half-metre
    ///     away from the body wearing the trackers.
    ///   * dragged EITHER way: chest and spine are hard-pinned to their T-pose Y
    ///     (ApplyPosition*TorsoLock's `desired.y = tposeY`), wrong by the FULL drag.
    ///
    /// The fix threads SpineSolveParams.TrackingLiftY (VerticalOffset x DeviceScale) into every standing
    /// reference, making the spine laws equivariant under a play-space shift: the SAME pose dragged up or
    /// down must produce the SAME body, just shifted. These gates drive the REAL BasisVirtualSpineSolveJob.
    ///
    /// House rule: each fix gate is PAIRED with a negative that reproduces the shipped defect and asserts
    /// it fails the same measurement, so the gate cannot rot into a tautology.
    /// </summary>
    public sealed class BasisVirtualSpinePlayspaceLiftTests
    {
        const int Head = 0, Neck = 1, Chest = 2, Spine = 3, Hips = 4;
        const int BoneCount = 5;

        const float StandingHeadY = 1.60f;
        const float StandingHipsY = 0.95f;
        const float ChestTposeY = 1.30f;
        const float SpineTposeY = 1.10f;
        const float NeckDrop = 0.12f;
        const float LenTotal = 0.65f;

        struct TorsoPose
        {
            public float3 HipsPos;
            public float3 ChestPos;
            public float3 SpinePos;
        }

        /// <summary>
        /// Ticks the REAL job once with the head at <paramref name="headPos"/> (already carrying any
        /// play-space lift, exactly as a live device pose would) and the standing references lifted by
        /// <paramref name="trackingLiftY"/>. Positions are direct writes in the job, so one tick settles them.
        /// </summary>
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

        /// <summary>
        /// THE HEADLINE GATE. The same standing player, with the play space dragged 0.8 m down or up, is
        /// still just a standing player — the torso must shift by EXACTLY the drag, on every synthesized
        /// bone. This is what latches the leg/hip lock-in guides back onto the trackers: the bones and the
        /// trackers ride the same shift instead of parting by up to half a metre.
        /// </summary>
        [Test]
        public void AVerticalSpaceDrag_ShiftsTheWholeTorsoWithTheBody()
        {
            TorsoPose baseline = RunSpineOnce(StandingHead(0f), 0f);

            foreach (float lift in new[] { -0.8f, -0.25f, 0.25f, 0.8f, 1.5f })
            {
                TorsoPose dragged = RunSpineOnce(StandingHead(lift), lift);

                Assert.AreEqual(baseline.HipsPos.y + lift, dragged.HipsPos.y, 0.005f,
                    $"hips did not ride a {lift:+0.00;-0.00} m space drag with the body -- the untracked leg "
                    + "chain hangs off this pelvis, so every leg bone is wrong by the same gap and the "
                    + "calibration lock-in guides can never latch the leg/hip trackers.");
                Assert.AreEqual(baseline.ChestPos.y + lift, dragged.ChestPos.y, 0.005f,
                    $"chest did not ride a {lift:+0.00;-0.00} m space drag -- its Y-pin is still anchored to "
                    + "the floor-relative T-pose height instead of the lifted tracking space.");
                Assert.AreEqual(baseline.SpinePos.y + lift, dragged.SpinePos.y, 0.005f,
                    $"spine did not ride a {lift:+0.00;-0.00} m space drag.");
            }
        }

        /// <summary>
        /// THE PAIRED NEGATIVE. Reproduce the shipped defect — devices dragged 0.8 m down but the standing
        /// references left un-lifted (TrackingLiftY = 0) — and assert the misread is LARGE on the same
        /// measurement: the posture model holds the pelvis a phantom-squat height above the body, and the
        /// chest pin misses by the full drag. If this ever stops failing, the gate above measures nothing.
        /// </summary>
        [Test]
        public void WithTheLiftUnplumbed_ADownwardDrag_ReadsAsAPhantomSquat()
        {
            const float drag = -0.8f;
            TorsoPose baseline = RunSpineOnce(StandingHead(0f), 0f);
            TorsoPose broken = RunSpineOnce(StandingHead(drag), 0f);

            float hipsError = Mathf.Abs((baseline.HipsPos.y + drag) - broken.HipsPos.y);
            Assert.Greater(hipsError, 0.15f,
                $"the un-lifted law only misplaced the hips by {hipsError * 100f:F1} cm on a {-drag * 100f:F0} cm "
                + "downward drag -- the phantom-squat misread this suite guards against is no longer "
                + "reproducible, so the equivariance gate is not being exercised.");

            float chestError = Mathf.Abs((baseline.ChestPos.y + drag) - broken.ChestPos.y);
            Assert.AreEqual(-drag, chestError, 0.01f,
                "the un-lifted chest pin should miss by exactly the drag (it is pinned to the floor-relative "
                + "T-pose Y); if it no longer does, the chest gate above is not measuring the pin.");
        }

        /// <summary>
        /// A REAL crouch must read identically with and without a space drag: the posture model's inputs
        /// are body-relative, so drag + crouch must equal crouch, shifted. Guards against the fix
        /// over-correcting (e.g. lifting the normalisation denominators, which would change how deep the
        /// same physical crouch reads once dragged).
        /// </summary>
        [Test]
        public void ARealCrouch_ReadsTheSame_WithOrWithoutASpaceDrag()
        {
            const float crouch = 0.35f;

            TorsoPose standingFlat = RunSpineOnce(StandingHead(0f), 0f);
            TorsoPose crouchFlat = RunSpineOnce(new float3(0f, StandingHeadY - crouch, 0f), 0f);
            float pelvisDropFlat = standingFlat.HipsPos.y - crouchFlat.HipsPos.y;

            Assert.Greater(pelvisDropFlat, 0.05f,
                "sanity: this crouch should move the pelvis at all, or the invariance below is vacuous");

            foreach (float lift in new[] { -0.8f, 0.8f })
            {
                TorsoPose standingLifted = RunSpineOnce(StandingHead(lift), lift);
                TorsoPose crouchLifted = RunSpineOnce(new float3(0f, StandingHeadY + lift - crouch, 0f), lift);
                float pelvisDropLifted = standingLifted.HipsPos.y - crouchLifted.HipsPos.y;

                Assert.AreEqual(pelvisDropFlat, pelvisDropLifted, 0.01f,
                    $"the same {crouch * 100f:F0} cm crouch produced a different pelvis drop under a "
                    + $"{lift:+0.00;-0.00} m space drag -- the posture law is no longer play-space equivariant.");
            }
        }
    }
}
