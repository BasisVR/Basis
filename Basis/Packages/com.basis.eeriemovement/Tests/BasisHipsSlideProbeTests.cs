using Basis.IK;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Tests.IK
{
    public sealed class BasisHipsSlideProbeTests
    {
        const int Head = 0, Neck = 1, Chest = 2, Spine = 3, Hips = 4;
        const int BoneCount = 5;
        const float StandingHeadY = 1.60f;
        const float StanceRadius = 0.12f * StandingHeadY;   // 19.2 cm
        const float FollowFrac = 0.25f;
        static float3[] RunSpine(float dt, int frames, System.Func<int, float3> headAt, bool bothFeetTracked)
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
                    float3 head = headAt(i);
                    var p = MakeParams(dt, head, bothFeetTracked);

                    new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
                    {
                        States = states,
                        State = solve,
                        P = p,
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
        static BasisVirtualSpineCore.SpineSolveParams MakeParams(float dt, float3 head, bool bothFeetTracked)
        {
            return new BasisVirtualSpineCore.SpineSolveParams
            {
                Dt = dt,
                Scale = 1f,
                ParentMatrix = float4x4.identity,
                ParentRotation = quaternion.identity,
                EyeRot = quaternion.identity,

                // ⚠️ THE LEASH TRACKS THE EYE, NOT THE HEAD -- and this fixture did not set it, so EyePos sat at
                // (0,0,0) every frame and the support base could never move no matter what the follow law did.
                // The pelvis read 0.00 cm on EVERY gate below, which is why three of them had been failing since
                // the anchor moved from the head bone to the eye device: the head bone ORBITS the eye when the
                // view yaws, so the leash was re-anchored onto the eye and the fixture was not updated.
                // ⚠️ That re-anchoring only moved the orbit rather than removing it -- an HMD is itself a hand's
                // width in front of the neck and sweeps its own arc on a turn (BasisHipsYawOrbitTests measures
                // it). Nothing in THIS fixture sees that: every gate below holds EyeRot at identity, and the
                // de-orbit is an exact no-op at constant yaw by construction.
                // HeadScaledOffset is zero here, so head bone == input, and eye == head models a rig whose
                // viewpoint sits on the head bone -- which is what every other field in this fixture assumes.
                EyePos = head,
                HeadTargetPos = head,
                HeadTargetRot = quaternion.identity,
                NeckTargetPos = head,
                NeckTargetRot = quaternion.identity,
                ChestTargetPos = head,
                ChestTargetRot = quaternion.identity,
                SpineTargetPos = head,
                SpineTargetRot = quaternion.identity,

                HeadScaledOffset = float3.zero,
                NeckScaledOffset = new float3(0f, -0.12f, 0f),
                ChestScaledOffset = float3.zero,
                SpineScaledOffset = float3.zero,

                ChestTposeY = 1.30f,
                SpineTposeY = 1.10f,
                TposeHips = new float3(0f, 0.95f, 0f),

                LeftFootPos = new float3(-0.10f, 0f, 0f),
                RightFootPos = new float3(0.10f, 0f, 0f),
                LeftFootTracked = (byte)(bothFeetTracked ? 1 : 0),
                RightFootTracked = (byte)(bothFeetTracked ? 1 : 0),

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

                LenTotal = 0.65f,
                TChest = 0.35f,
                TSpine = 0.65f,

                StandingHipsLocalY = 0.95f,
                StandingHeadLocalY = StandingHeadY,
                PostureModel = 1,
                HipsCompressionStrength = 0.85f,
                HipsMaxDropMeters = 0.30f,
            };
        }
        static System.Func<int, float3> Ramp(float dt, float moveSecs, float dist) => i =>
        {
            float t = Mathf.Clamp01(i * dt / moveSecs);
            return new float3(0f, StandingHeadY, dist * t);
        };
        static (float driftM, float restMs) DriftAfterStop(float3[] hips, float dt, int stopFrame)
        {
            float3 settled = hips[hips.Length - 1];
            float drift = math.abs(settled.z - hips[stopFrame].z), restMs = 0f;
            for (int i = hips.Length - 1; i >= stopFrame; i--)
            {
                if (math.abs(hips[i].z - settled.z) > 0.002f) { restMs = (i + 1 - stopFrame) * dt * 1000f; break; }
            }
            return (drift, restMs);
        }
        // ------------------------------------------------------------------ the gates
        [Test]
        public void TheFollowLawIsContinuous_WithNoThresholdAnywhere()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 0.30f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps);

            // Straddles the stance radius on purpose -- 19.2 cm is where every previous form changed mode.
            float[] moves = { 0.06f, 0.10f, 0.14f, 0.17f, 0.19f, 0.21f, 0.24f, 0.30f, 0.40f };
            var carried = new float[moves.Length];
            for (int i = 0; i < moves.Length; i++)
            {
                float3[] hips = RunSpine(dt, frames, Ramp(dt, moveSecs, moves[i]), false);
                carried[i] = (hips[frames - 1].z - hips[0].z) / moves[i];
            }

            for (int i = 1; i < moves.Length; i++)
            {
                float jump = Mathf.Abs(carried[i] - carried[i - 1]);
                Assert.Less(jump, 0.10f, $"the pelvis carried {carried[i - 1] * 100f:F0}% of a {moves[i - 1] * 100f:F0} cm move but " + $"{carried[i] * 100f:F0}% of a {moves[i] * 100f:F0} cm one -- a {jump * 100f:F0} point step for " + "2 cm more travel. That cliff is a mode switch, and it is what reads as stop-start.");
            }

            for (int i = 0; i < moves.Length; i++)
            {
                Assert.Greater(carried[i], 0.85f, $"the pelvis carried only {carried[i] * 100f:F0}% of a {moves[i] * 100f:F0} cm move -- smooth, but " + "smoothly not following. This assert is what stops the continuity gate above from being " + "satisfied by a pelvis that simply never moves.");
            }

            // PAIRED NEGATIVE: the latch form, reproduced inline. Nothing at all happens inside the radius, then
            // it drags the base across in two frames. Never call this from shipping code -- it is the bug.
            var latch = new float[moves.Length];
            for (int i = 0; i < moves.Length; i++)
            {
                float b = 0f;
                bool stepping = false;
                for (int f = 0; f < frames; f++)
                {
                    float head = Mathf.Clamp01(f * dt / moveSecs) * moves[i], d = Mathf.Abs(head - b);
                    if (!stepping) { if (d > StanceRadius) stepping = true; }
                    else if (d < StanceRadius * 0.15f) stepping = false;
                    b = Mathf.Lerp(b, head, 1f - Mathf.Exp(-(stepping ? 200f : 0.35f) * dt));
                }
                latch[i] = Mathf.Lerp(b, moves[i], FollowFrac) / moves[i];
            }
            float worstLatchJump = 0f;
            for (int i = 1; i < moves.Length; i++)
            {
                worstLatchJump = Mathf.Max(worstLatchJump, Mathf.Abs(latch[i] - latch[i - 1]));
            }
            Assert.Greater(worstLatchJump, 0.30f, $"the latch form is supposed to have a cliff at the stance radius (its worst step was " + $"{worstLatchJump * 100f:F0} points) -- if it no longer does, this gate is testing nothing");
        }
        [Test]
        public void TheBaseIsSoftWhenItIsAlreadyUnderTheHead_NotASnap()
        {
            const int fps = 90;
            const float dt = 1f / fps, nudge = 0.01f;
            int frames = Mathf.RoundToInt(1.0f * fps);

            // A 1 cm step input, held. Nothing ramps -- this is the impulse response.
            float3[] hips = RunSpine(dt, frames, i => new float3(0f, StandingHeadY, i == 0 ? 0f : nudge), false);
            float afterOneFrame = (hips[1].z - hips[0].z) / nudge;
            Assert.Less(afterOneFrame, 0.5f, $"the pelvis took {afterOneFrame * 100f:F0}% of a 1 cm head nudge in a single frame. The support " + "base is being welded to the head, so tracker noise goes straight into the pelvis.");

            float afterASecond = (hips[frames - 1].z - hips[0].z) / nudge;
            Assert.Greater(afterASecond, 0.9f, $"the pelvis only reached {afterASecond * 100f:F0}% of a 1 cm nudge after a second -- soft has " + "become stuck, which is the dead zone this law exists to remove.");
        }
        [Test]
        public void AWalk_KeepsThePelvisUnderTheWalker_NotTrailingBehindIt()
        {
            const int fps = 90;
            const float dt = 1f / fps, speed = 1.2f, secs = 3f;
            int frames = Mathf.RoundToInt(secs * fps);
            float3[] hips = RunSpine(dt, frames, i => new float3(0f, StandingHeadY, speed * i * dt), false);
            float headEnd = speed * (frames - 1) * dt, lag = headEnd - (hips[frames - 1].z - hips[0].z);

            Assert.Less(lag, 0.06f, $"the pelvis trailed {lag * 100f:F1} cm behind a {speed:F1} m/s walk. The support base is not " + "keeping up, so the body is permanently behind the player while they move.");

            // PAIRED NEGATIVE: the deadband law, reproduced inline. Its base stalls at head - radius and stays
            // there for the whole walk. Never call this from shipping code -- it is the bug.
            float baseline = 0f;
            for (int i = 0; i < frames; i++)
            {
                float head = speed * i * dt;
                float over = Mathf.Max(0f, Mathf.Abs(head - baseline) - StanceRadius) / StanceRadius;
                baseline = Mathf.Lerp(baseline, head, 1f - Mathf.Exp(-200f * over * over * dt));
            }
            float oldLag = headEnd - Mathf.Lerp(baseline, headEnd, FollowFrac);
            Assert.Greater(oldLag, 0.10f, $"the deadband law is supposed to trail the walker badly (it trailed {oldLag * 100f:F1} cm) -- if it " + "no longer does, this gate is testing nothing");
        }
        [Test]
        public void AStep_MovesTheSupportBase_ButStillSettlesPromptly()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 2.0f, step = 0.30f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps), stopFrame = Mathf.RoundToInt(moveSecs * fps);

            Assert.Greater(step, StanceRadius, "sanity: this test is only meaningful if the step is OUTSIDE the radius");

            float3[] hips = RunSpine(dt, frames, Ramp(dt, moveSecs, step), false);
            (float drift, float restMs) = DriftAfterStop(hips, dt, stopFrame);

            // The base DID follow — the pelvis ended up well past the pure-counterbalance answer.
            float moved = hips[frames - 1].z - hips[0].z;
            Assert.Greater(moved, FollowFrac * step, $"the pelvis only moved {moved * 100f:F2} cm on a {step * 100f:F0} cm step -- the support base is " + "NOT following, so a user who walks would be left behind their own legs");

            Assert.Less(drift, 0.025f, $"the pelvis drifted {drift * 100f:F2} cm after the head stopped");
            Assert.Less(restMs, 300f, $"the pelvis took {restMs:F0} ms to settle after the head stopped");
        }
        [Test]
        public void TrackerJitter_DoesNotRatchetTheSupportBaseOutward()
        {
            const int fps = 90;
            const float dt = 1f / fps;
            const int seconds = 10;
            int frames = seconds * fps;

            var rng = new Unity.Mathematics.Random(12345u);
            // Park the head exactly ON the boundary -- the worst case -- plus 3 mm of jitter, a pessimistic
            // reading of a real tracker.
            float3[] hips = RunSpine(dt, frames, i =>
            {
                float3 j = rng.NextFloat3(new float3(-0.003f), new float3(0.003f));
                return new float3(j.x, StandingHeadY + j.y, StanceRadius + j.z);
            }, bothFeetTracked: false);

            float creep = math.abs(hips[frames - 1].z - hips[fps].z);   // drift over the last 9 s

            Assert.Less(creep, 0.01f, $"the support base is ratcheting outward under tracker jitter -- the pelvis crept " + $"{creep * 100f:F2} cm in {seconds} s of standing still. The squared-exceedance pull exists " + "precisely to make this impossible.");
        }
        [Test]
        public void WithBothFeetTracked_ThePelvisStillHasNoDriftAtAll()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 2.0f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps), stopFrame = Mathf.RoundToInt(moveSecs * fps);

            foreach (float dist in new[] { 0.15f, 0.30f })
            {
                (float drift, _) = DriftAfterStop(RunSpine(dt, frames, Ramp(dt, moveSecs, dist), true), dt, stopFrame);
                Assert.Less(drift, 0.002f, $"the both-feet-tracked path drifted {drift * 100f:F2} cm on a {dist * 100f:F0} cm move. That " + "path reads the feet midpoint directly and must remain exact.");
            }
        }
        [Test]
        public void ALateralShift_TheHipsCarryMostOfIt_NotTheSagittalCounterbalance()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 1.0f, shift = 0.15f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps), stopFrame = Mathf.RoundToInt(moveSecs * fps);

            Assert.Less(shift, StanceRadius, "sanity: the shift is smaller than the stance radius, so under any of " + "the switch-based laws this was the case that got left behind");

            // Eye rotation is identity in MakeParams, so the torso faces +Z and +X is pure lateral.
            float3[] latHips = RunSpine(dt, frames, i =>
            {
                float t = Mathf.Clamp01(i * dt / moveSecs);
                return new float3(shift * t, StandingHeadY, 0f);
            }, bothFeetTracked: false);

            float movedX = latHips[stopFrame].x - latHips[0].x;

            Assert.Greater(movedX, 0.6f * shift, $"the hips carried only {movedX * 100f:F2} cm of a {shift * 100f:F0} cm sideways shift -- a weight " + "shift must keep the pelvis under the head, not lag it the way a forward bend does.");
            Assert.Less(movedX, 1.05f * shift, $"the hips moved {movedX * 100f:F2} cm on a {shift * 100f:F0} cm shift -- they must not overshoot the head.");

            // (b) PAIRED NEGATIVE: the shipped-before law followed BOTH axes at CounterbalanceFollowFrac, so on
            // this shift it carried only ~3.75 cm. Reproduced ONLY so this negative can bite; never call from
            // shipping code. If the job ever regresses to it, movedX collapses toward this and (a) fires.
            float oldIsotropic = FollowFrac * shift;
            Assert.Greater(movedX, oldIsotropic + 0.04f, $"the shipped hips ({movedX * 100f:F2} cm) are no better than the old isotropic 0.25 follow " + $"({oldIsotropic * 100f:F2} cm) -- the anisotropic lateral follow is not in effect.");
        }
        [Test]
        public void AStep_LandsTheSupportBaseUnderTheWalker_NotAStanceRadiusShort()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 0.5f, step = 0.30f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps);

            Assert.Greater(step, StanceRadius, "sanity: this test is only meaningful if the step is OUTSIDE the radius");

            float3[] hips = RunSpine(dt, frames, Ramp(dt, moveSecs, step), false);
            float carried = hips[frames - 1].z - hips[0].z;

            Assert.Greater(carried, 0.95f * step, $"the pelvis carried {carried * 100f:F2} cm of a {step * 100f:F0} cm step -- it must end up under the " + "walker. A base that stops at the detection threshold leaves the pelvis (and the legs hanging off " + "it) a stance radius behind, permanently.");

            // PAIRED NEGATIVE: the deadband pull, reproduced inline. It stalls at head - radius, so it carries
            // only baseline + FollowFrac*radius. Never call this from shipping code -- it is the bug.
            float baseline = 0f;
            for (int i = 0; i < frames; i++)
            {
                float headZ = Mathf.Clamp01(i * dt / moveSecs) * step;
                float over = Mathf.Max(0f, Mathf.Abs(headZ - baseline) - StanceRadius) / StanceRadius;
                baseline = Mathf.Lerp(baseline, headZ, 1f - Mathf.Exp(-200f * over * over * dt));
            }
            float oldCarried = Mathf.Lerp(baseline, step, FollowFrac);
            Assert.Less(oldCarried, 0.85f * step, $"the deadband pull is supposed to stall short of the walker (it carried {oldCarried * 100f:F2} cm of " + $"{step * 100f:F0} cm) -- if it no longer does, this gate is testing nothing");
        }
        [Test]
        public void APersistentHeightOffset_DoesNotInflateTheStanceRadius()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 0.5f;
            const float seatedHeadY = StandingHeadY - 0.45f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps), stopFrame = Mathf.RoundToInt(moveSecs * fps);

            // (a) A 25 cm move is OUTSIDE the true 19.2 cm radius, so it is a step and must be carried. With the
            //     T-pose-referenced radius (50.7 cm) it fell inside, and the pelvis carried only ~7.5 cm of it.
            const float step = 0.25f;
            Assert.Greater(step, StanceRadius, "sanity: the move must be outside the UN-inflated radius");
            float3[] stepped = RunSpine(dt, frames, i => new float3(0f, seatedHeadY, step * Mathf.Clamp01(i * dt / moveSecs)), false);
            float carried = stepped[stopFrame].z - stepped[0].z;
            Assert.Greater(carried, 0.8f * step, $"sat {(StandingHeadY - seatedHeadY) * 100f:F0} cm low, the pelvis carried only {carried * 100f:F2} cm " + $"of a {step * 100f:F0} cm move. A constant head-height offset is being read as a crouch and " + "inflating the stance radius, so the support base never follows.");

            // (b) PAIRED NEGATIVE: what the seated user actually got -- the deadband pull against a radius the
            //     T-pose reference had inflated to 50.7 cm, so the move never even registered as travel.
            //     Reproduced ONLY so this negative can bite; never call either half from shipping code.
            float inflatedRadius = StanceRadius + 0.70f * (StandingHeadY - seatedHeadY), baseline = 0f;
            for (int i = 0; i <= stopFrame; i++)
            {
                float head = step * Mathf.Clamp01(i * dt / moveSecs);
                float over = Mathf.Max(0f, Mathf.Abs(head - baseline) - inflatedRadius) / inflatedRadius;
                baseline = Mathf.Lerp(baseline, head, 1f - Mathf.Exp(-200f * over * over * dt));
            }
            float oldCarried = Mathf.Lerp(baseline, step, FollowFrac);
            Assert.Less(oldCarried, 0.5f * step, $"the T-pose-referenced radius ({inflatedRadius * 100f:F0} cm) is supposed to swallow a " + $"{step * 100f:F0} cm move whole (it carried {oldCarried * 100f:F2} cm) -- if it no longer does, " + "this gate is testing nothing");
        }
    }
}
