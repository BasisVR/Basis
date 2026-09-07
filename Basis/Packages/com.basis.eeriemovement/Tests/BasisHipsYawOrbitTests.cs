using Basis.IK;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Tests.IK
{
    public sealed class BasisHipsYawOrbitTests
    {
        const int Head = 0, Neck = 1, Chest = 2, Spine = 3, Hips = 4;
        const int BoneCount = 5;
        // A T-posed adult, in player-local space. The only number that drives this whole file is the
        // 9 cm of FORWARD offset between the eye and the neck -- that is the lever the turn sweeps.
        static readonly float3 k_Hips = new float3(0f, 0.95f, 0f), spine = new float3(0f, 1.05f, 0f);
        static readonly float3 k_Chest = new float3(0f, 1.25f, 0f), k_Neck = new float3(0f, 1.45f, 0f);
        static readonly float3 k_Head = new float3(0f, 1.52f, 0f), eye = new float3(0f, 1.60f, 0.09f);
        static readonly float eyeLeverXZ = math.length(new float2(eye.x - k_Neck.x, eye.z - k_Neck.z));
        static void HeadTurn(float yawDeg, out float3 eyePos, out quaternion eyeRot)
        {
            eyeRot = quaternion.AxisAngle(new float3(0f, 1f, 0f), math.radians(yawDeg));
            eyePos = k_Neck + math.mul(eyeRot, eye - k_Neck);
        }
        static float3[] RunSpine(float dt, int frames, System.Func<int, float> yawAt, float deOrbit, float deadzoneDeg)
        {
            var states = new NativeArray<BasisBoneSimState>(BoneCount, Allocator.Temp);
            var solve = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.Temp);
            try
            {
                for (int i = 0; i < BoneCount; i++)
                    states[i] = new BasisBoneSimState { OutgoingRotation = quaternion.identity, LastRunRotation = quaternion.identity };
                solve[0] = default;

                var hipsTrack = new float3[frames];
                for (int i = 0; i < frames; i++)
                {
                    HeadTurn(yawAt(i), out float3 eyePos, out quaternion eyeRot);

                    new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
                    {
                        States = states,
                        State = solve,
                        P = MakeParams(dt, eyePos, eyeRot, deOrbit, deadzoneDeg),
                        IdxHead = Head,
                        IdxNeck = Neck,
                        IdxChest = Chest,
                        IdxSpine = Spine,
                        IdxHips = Hips,
                    }.Execute();

                    hipsTrack[i] = states[Hips].OutgoingPosition;
                }
                return hipsTrack;
            }
            finally
            {
                states.Dispose();
                solve.Dispose();
            }
        }
        static float3[] RunSpineWalking(float dt, int frames, float yawDeg, float speed, float deOrbit)
        {
            var states = new NativeArray<BasisBoneSimState>(BoneCount, Allocator.Temp);
            var solve = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.Temp);
            try
            {
                for (int i = 0; i < BoneCount; i++)
                    states[i] = new BasisBoneSimState { OutgoingRotation = quaternion.identity, LastRunRotation = quaternion.identity };
                solve[0] = default;

                var hipsTrack = new float3[frames];
                for (int i = 0; i < frames; i++)
                {
                    HeadTurn(yawDeg, out float3 eyePos, out quaternion eyeRot);
                    eyePos += new float3(0f, 0f, speed * i * dt);

                    new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
                    {
                        States = states,
                        State = solve,
                        P = MakeParams(dt, eyePos, eyeRot, deOrbit, 0f),
                        IdxHead = Head,
                        IdxNeck = Neck,
                        IdxChest = Chest,
                        IdxSpine = Spine,
                        IdxHips = Hips,
                    }.Execute();

                    hipsTrack[i] = states[Hips].OutgoingPosition;
                }
                return hipsTrack;
            }
            finally
            {
                states.Dispose();
                solve.Dispose();
            }
        }
        static BasisVirtualSpineCore.SpineSolveParams MakeParams(float dt, float3 eyePos, quaternion eyeRot, float deOrbit, float deadzoneDeg)
        {
            float lenTotal = k_Neck.y - k_Hips.y;

            return new BasisVirtualSpineCore.SpineSolveParams
            {
                Dt = dt,
                Scale = 1f,
                ParentMatrix = float4x4.identity,
                ParentRotation = quaternion.identity,
                EyeRot = eyeRot,
                EyePos = eyePos,

                // Every torso target resolves to the eye in the shipping driver, so the whole chain is
                // composed off this one pose plus the T-pose offsets below.
                HeadTargetPos = eyePos,
                HeadTargetRot = eyeRot,
                NeckTargetPos = eyePos,
                NeckTargetRot = eyeRot,
                ChestTargetPos = eyePos,
                ChestTargetRot = eyeRot,
                SpineTargetPos = eyePos,
                SpineTargetRot = eyeRot,

                HeadScaledOffset = k_Head - eye,
                NeckScaledOffset = k_Neck - eye,
                ChestScaledOffset = k_Chest - eye,
                SpineScaledOffset = spine - eye,

                ChestTposeY = k_Chest.y,
                SpineTposeY = spine.y,
                TposeHips = k_Hips,

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
                HipsForwardBias = 0f,
                NeckExtensionDamp = 0.65f,
                TorsoYawDeadzoneDeg = deadzoneDeg,
                TorsoYawBlendSpeed = 8f,

                HipsFreeze = 0,
                IsLocomoting = 0,

                LenTotal = lenTotal,
                TChest = (k_Neck.y - k_Chest.y) / lenTotal,
                TSpine = (k_Neck.y - spine.y) / lenTotal,

                StandingHipsLocalY = k_Neck.y - lenTotal,
                StandingHeadLocalY = k_Head.y,

                // The three arms the pelvis placement is built from, exactly as the driver bakes them.
                GazeSwingLever = eye - k_Head,
                TposeNeckMinusEyeY = k_Neck.y - eye.y,
                GazeSwingRemoval = deOrbit,
                HipsAnchorOffsetLocal = new float3(k_Hips.x - eye.x, 0f, k_Hips.z - eye.z),
                HeadRestFromEyeLocal = new float3(k_Head.x - eye.x, 0f, k_Head.z - eye.z),
                YawPivotFromEyeLocal = new float3(k_Neck.x - eye.x, 0f, k_Neck.z - eye.z),

                PostureModel = 1,
                HipsCompressionStrength = 0.85f,
                HipsMaxDropMeters = 0.30f,
            };
        }
        static float WorstSlide(float3[] hips)
        {
            float worst = 0f;
            for (int i = 0; i < hips.Length; i++)
            {
                float2 d = new float2(hips[i].x - hips[0].x, hips[i].z - hips[0].z);
                worst = math.max(worst, math.length(d));
            }
            return worst;
        }
        static System.Func<int, float> Turn(float dt, float turnSecs, float deg) => i => deg * Mathf.Clamp01(i * dt / turnSecs);
        // ------------------------------------------------------------------ the gates
        [Test]
        public void APureHeadTurn_DoesNotSlideThePelvisSideways()
        {
            const int fps = 90;
            const float dt = 1f / fps, holdSecs = 0.70f;

            var report = new StringBuilder();
            report.AppendLine("PURE HEAD TURN -> PELVIS SLIDE (the body is byte-identical on every row)");
            report.AppendLine($"  eye sits {eyeLeverXZ * 100f:F1} cm in front of the neck's yaw axis; the artefact is");
            report.AppendLine("  2 * lever * sin(yaw lag / 2), so it scales with BOTH how far and how FAST you turn");
            report.AppendLine();
            report.AppendLine($"{"turn deg",10} {"over",8} {"de-orbited",12} {"legacy",12}");
            report.AppendLine(new string('-', 44));

            float worstFixed = 0f, bestLegacy = 0f;
            foreach (float turnSecs in new[] { 0.15f, 0.30f, 0.60f })
            {
                int frames = Mathf.RoundToInt((turnSecs + holdSecs) * fps);
                foreach (float deg in new[] { 45f, 90f, 120f })
                {
                    float slideFixed = WorstSlide(RunSpine(dt, frames, Turn(dt, turnSecs, deg), 1f, 0f));
                    float slideLegacy = WorstSlide(RunSpine(dt, frames, Turn(dt, turnSecs, deg), 0f, 0f));

                    worstFixed = math.max(worstFixed, slideFixed);
                    bestLegacy = math.max(bestLegacy, slideLegacy);
                    report.AppendLine($"{deg,10:F0} {turnSecs,7:F2}s {slideFixed * 100f,10:F2} cm {slideLegacy * 100f,10:F2} cm");
                }
            }
            Debug.Log(report.ToString());

            Assert.Less(worstFixed, 0.005f, $"a head turn slid the pelvis {worstFixed * 100f:F2} cm sideways. The body did not move: every " + "millimetre of that is the HMD's orbit about the neck leaking into the stance leash.");

            // PAIRED NEGATIVE: GazeSwingRemoval = 0 is the pre-fix law exactly (leash on the raw eye, anchor
            // arm measured from the eye). It must still jut, or this gate is measuring nothing.
            Assert.Greater(bestLegacy, 0.02f, $"the pre-fix leash is supposed to carry the pelvis sideways on a head turn (worst " + $"{bestLegacy * 100f:F2} cm) -- if it no longer does, this gate is testing nothing");
        }
        [Test]
        public void AHeadHeldInsideTheYawDeadzone_LeavesThePelvisStandingWhereItWas()
        {
            const int fps = 90;
            const float dt = 1f / fps, turnSecs = 0.30f, holdSecs = 2.0f;
            const float held = 40f;   // inside the 45 deg cone, so the torso never breaks and never relocks
            int frames = Mathf.RoundToInt((turnSecs + holdSecs) * fps);
            float3[] fixedHips = RunSpine(dt, frames, Turn(dt, turnSecs, held), 1f, 45f);
            float3[] legacyHips = RunSpine(dt, frames, Turn(dt, turnSecs, held), 0f, 45f);
            float settledFixed = math.length(new float2(fixedHips[frames - 1].x - fixedHips[0].x, fixedHips[frames - 1].z - fixedHips[0].z));
            float settledLegacy = math.length(new float2(legacyHips[frames - 1].x - legacyHips[0].x, legacyHips[frames - 1].z - legacyHips[0].z));

            Debug.Log($"head held at {held:F0} deg inside the deadzone for {holdSecs:F1} s -- pelvis offset: " + $"de-orbited {settledFixed * 100f:F2} cm, legacy {settledLegacy * 100f:F2} cm " + $"(geometry says 2*{eyeLeverXZ * 100f:F1}*sin({held / 2f:F0} deg) = {2f * eyeLeverXZ * math.sin(math.radians(held * 0.5f)) * 100f:F2} cm)");

            Assert.Less(settledFixed, 0.005f, $"the pelvis settled {settledFixed * 100f:F2} cm from where the user is standing while they simply " + "looked to one side. A deadzone that holds the torso still must not also hold the pelvis out.");

            Assert.Greater(settledLegacy, 0.03f, $"the pre-fix law is supposed to park the pelvis to one side of a held turn (it parked it " + $"{settledLegacy * 100f:F2} cm) -- if it no longer does, this gate is testing nothing");
        }
        [Test]
        public void WithAConstantHeadYaw_TheDeOrbitIsExactlyANoOp()
        {
            const int fps = 90;
            const float dt = 1f / fps, speed = 1.2f;
            int frames = Mathf.RoundToInt(2.0f * fps);
            float3[] deOrbited = RunSpineWalking(dt, frames, 30f, speed, 1f);
            float3[] legacy = RunSpineWalking(dt, frames, 30f, speed, 0f);
            float worst = 0f;
            for (int i = 0; i < frames; i++)
            {
                worst = math.max(worst, math.length(deOrbited[i] - legacy[i]));
            }

            Debug.Log($"walking 2 s at {speed:F1} m/s with the head held 30 deg off-axis: worst pelvis difference " + $"between the de-orbited and legacy laws = {worst * 1000f:F4} mm");

            Assert.Less(worst, 1e-5f, $"the de-orbit moved the pelvis by {worst * 1000f:F4} mm on a motion with no head-yaw change. " + "It is supposed to cancel exactly there -- if it does not, it is not a pure de-orbit and the " + "existing stance-leash gates are no longer covering the shipping law.");
        }
        [Test]
        public void RealTravelIsStillFollowed_TheDeOrbitRemovesAnArcNotAWalk()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 0.60f, dist = 0.40f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps);

            var states = new NativeArray<BasisBoneSimState>(BoneCount, Allocator.Temp);
            var solve = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.Temp);
            float carried;
            try
            {
                for (int i = 0; i < BoneCount; i++)
                    states[i] = new BasisBoneSimState { OutgoingRotation = quaternion.identity, LastRunRotation = quaternion.identity };
                solve[0] = default;

                float first = 0f, last = 0f;
                for (int i = 0; i < frames; i++)
                {
                    HeadTurn(0f, out float3 eyePos, out quaternion eyeRot);
                    eyePos += new float3(dist * Mathf.Clamp01(i * dt / moveSecs), 0f, 0f);   // a sideways STEP

                    new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
                    {
                        States = states,
                        State = solve,
                        P = MakeParams(dt, eyePos, eyeRot, 1f, 0f),
                        IdxHead = Head,
                        IdxNeck = Neck,
                        IdxChest = Chest,
                        IdxSpine = Spine,
                        IdxHips = Hips,
                    }.Execute();

                    if (i == 0) first = states[Hips].OutgoingPosition.x;
                    last = states[Hips].OutgoingPosition.x;
                }
                carried = last - first;
            }
            finally
            {
                states.Dispose();
                solve.Dispose();
            }

            Assert.Greater(carried, 0.9f * dist, $"the pelvis carried only {carried * 100f:F1} cm of a {dist * 100f:F0} cm sideways step. The " + "de-orbit must remove the head's ARC and nothing else -- a user who steps must keep their hips.");
        }
    }
}
