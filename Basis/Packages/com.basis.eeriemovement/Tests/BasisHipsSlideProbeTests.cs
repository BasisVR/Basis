using Basis.IK;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Tests.IK
{
    /// <summary>
    /// "When the user stops, does the BODY stop?"
    ///
    /// Reported: "the legs are super duper smooth and lagging behind the rest of the trackers... when a
    /// real tracker is present but no role, they slide around way after the motion is complete."
    ///
    /// The legs hang off the pelvis — LeftUpperLeg's bone target IS the Hips control
    /// (BasisLocalAvatarDriver:387), and the FBIK leg root is the hips socket that SolveSpine writes
    /// before SolveLegs reads it. A pelvis that is still sliding IS a pair of legs that are still sliding.
    ///
    /// And the pelvis's horizontal position came 75% from HeadBaselineXZ, which was a 1 Hz LOW-PASS of the
    /// head (tau = 159 ms). A low-pass's output keeps travelling toward a STOPPED input for ~4 tau. So
    /// when the user stopped moving, the pelvis did not: measured on this very job, it drifted a further
    /// 7.9 cm over 589 ms.
    ///
    /// It fired precisely when the feet had no ROLE, because the branch above it — both feet tracked —
    /// puts the pelvis on the feet midpoint instead, which is exact and instant (measured: 0.00 cm).
    /// A real tracker with no role assigned leaves HasTracked = HasNoTracker (BasisInput.SetRealTrackers),
    /// so it takes the estimator path. That is the whole bug.
    ///
    /// The fix replaces the temporal low-pass with a SPATIAL one: the support base is frozen while the
    /// head is within a stance radius of it (that is a LEAN — the feet have not moved, so neither has the
    /// base) and only follows once the head goes further than a lean can reach (a STEP).
    ///
    /// House rule: every gate asserting the fix is correct is PAIRED with one that drives the OLD form and
    /// asserts it FAILS. Without the pair, a bug that made the metric always return zero would leave every
    /// test green and the gate silently dead.
    /// </summary>
    public sealed class BasisHipsSlideProbeTests
    {
        const int Head = 0, Neck = 1, Chest = 2, Spine = 3, Hips = 4;
        const int BoneCount = 5;

        const float StandingHeadY = 1.60f;
        /// <summary>StanceRadiusFrac (0.12) x StandingHeadY. Beyond this the solver calls it a step.</summary>
        const float StanceRadius = 0.12f * StandingHeadY;   // 19.2 cm
        /// <summary>CounterbalanceFollowFrac — how much of the lean the pelvis is supposed to carry.</summary>
        const float FollowFrac = 0.25f;

        /// <summary>Ticks the REAL BasisVirtualSpineSolveJob. Returns the solved hips position per frame.</summary>
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

        /// <summary>Head ramps forward `dist` over `moveSecs`, then holds still.</summary>
        static System.Func<int, float3> Ramp(float dt, float moveSecs, float dist) => i =>
        {
            float t = Mathf.Clamp01(i * dt / moveSecs);
            return new float3(0f, StandingHeadY, dist * t);
        };

        /// <summary>Metres the pelvis travels AFTER the head has already come to rest, and how long for.</summary>
        static (float driftM, float restMs) DriftAfterStop(float3[] hips, float dt, int stopFrame)
        {
            float3 settled = hips[hips.Length - 1];
            float drift = math.abs(settled.z - hips[stopFrame].z);

            float restMs = 0f;
            for (int i = hips.Length - 1; i >= stopFrame; i--)
            {
                if (math.abs(hips[i].z - settled.z) > 0.002f) { restMs = (i + 1 - stopFrame) * dt * 1000f; break; }
            }
            return (drift, restMs);
        }

        // ------------------------------------------------------------------ the gates

        /// <summary>
        /// THE HEADLINE GATE. Lean the head 15 cm forward (INSIDE the 19.2 cm stance radius — the feet have
        /// not moved) over 400 ms, then hold perfectly still for two seconds.
        ///
        /// The pelvis must be DONE when the head is. Not "mostly done", not "settling" — done. There is
        /// nothing left for it to chase, because the user is standing exactly where they were standing.
        /// </summary>
        [Test]
        public void ALean_LeavesNoPelvisDrift_OnceTheHeadHasStopped()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 2.0f, lean = 0.15f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps);
            int stopFrame = Mathf.RoundToInt(moveSecs * fps);

            Assert.Less(lean, StanceRadius, "sanity: this test is only meaningful if the lean is INSIDE the radius");

            (float drift, float restMs) = DriftAfterStop(RunSpine(dt, frames, Ramp(dt, moveSecs, lean), false), dt, stopFrame);

            Assert.Less(drift, 0.005f,
                $"the pelvis kept travelling {drift * 100f:F2} cm after the head had already stopped. The support "
                + "base is being chased instead of held -- this is the 'legs slide around way after the motion is "
                + "complete' report, and the legs hang off this pelvis.");
            Assert.Less(restMs, 60f,
                $"the pelvis took {restMs:F0} ms to come to rest after the head stopped.");
        }

        /// <summary>
        /// The paired negative, and the one that matters most: prove the fix did not simply FREEZE the
        /// pelvis. A pelvis that never moves also has zero drift, and would sail through the gate above.
        ///
        /// The counterbalance must still be there: hips displaced by CounterbalanceFollowFrac of the lean,
        /// arriving WITH the head and HOLDING once it stops.
        /// </summary>
        [Test]
        public void ALean_StillCounterbalances_TheFixIsNotJustAFrozenPelvis()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 2.0f, lean = 0.15f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps);
            int stopFrame = Mathf.RoundToInt(moveSecs * fps);

            float3[] hips = RunSpine(dt, frames, Ramp(dt, moveSecs, lean), false);

            float moved = hips[stopFrame].z - hips[0].z;
            float expected = FollowFrac * lean;   // support base frozen => hips carry exactly this much

            Assert.Greater(moved, 0.5f * expected,
                $"the pelvis only moved {moved * 100f:F2} cm on a {lean * 100f:F0} cm lean -- it should carry "
                + $"~{expected * 100f:F2} cm of it (CounterbalanceFollowFrac). The drift gate has been passed by "
                + "FREEZING the pelvis rather than by fixing the estimator.");
            Assert.Less(moved, 2.0f * expected,
                $"the pelvis moved {moved * 100f:F2} cm on a {lean * 100f:F0} cm lean -- far more than the "
                + $"~{expected * 100f:F2} cm counterbalance. The support base is following the lean, which is "
                + "exactly what it must not do.");

            // ...and it must still be there two seconds later, not have crept into the head.
            float held = hips[frames - 1].z - hips[0].z;
            Assert.AreEqual(moved, held, 0.005f,
                $"the counterbalance decayed while the user simply held the lean: {moved * 100f:F2} cm at the stop, "
                + $"{held * 100f:F2} cm two seconds later. A held lean must hold its posture.");
        }

        /// <summary>
        /// THE PAIRED NEGATIVE FOR THE LAW ITSELF. Drives the 1 Hz low-pass that shipped and asserts it
        /// FAILS the drift gate. If this ever stops failing, the gate above is measuring nothing.
        /// </summary>
        [Test]
        public void TheOldLowPass_Fails_SoTheGateCannotRotIntoATautology()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 2.0f, lean = 0.15f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps);
            int stopFrame = Mathf.RoundToInt(moveSecs * fps);

            // The code that shipped: HeadBaselineXZ = lerp(HeadBaselineXZ, headXZ, 1 - exp(-2*pi*1*dt)).
            // Reproduced ONLY so this negative can bite. Never call this from shipping code -- it is the bug.
            var hips = new float3[frames];
            float baseline = 0f;
            for (int i = 0; i < frames; i++)
            {
                float headZ = Mathf.Clamp01(i * dt / moveSecs) * lean;
                baseline = Mathf.Lerp(baseline, headZ, 1f - Mathf.Exp(-2f * Mathf.PI * 1.0f * dt));
                hips[i] = new float3(0f, 0f, Mathf.Lerp(baseline, headZ, FollowFrac));
            }

            (float drift, float restMs) = DriftAfterStop(hips, dt, stopFrame);

            Assert.Greater(drift, 0.005f,
                $"the 1 Hz low-pass is supposed to keep dragging the pelvis after the head stops (it drifted "
                + $"{drift * 100f:F2} cm) -- if it no longer does, this gate is testing nothing");
            Assert.Greater(restMs, 60f,
                $"the 1 Hz low-pass is supposed to take hundreds of ms to settle (it took {restMs:F0} ms)");
        }

        /// <summary>
        /// A STEP — head goes 30 cm, past the stance radius, so the user cannot have got there without
        /// moving their feet. The support base is now allowed to follow. It must still be DONE promptly:
        /// a base that eases in for half a second is the original bug wearing a different hat.
        /// </summary>
        [Test]
        public void AStep_MovesTheSupportBase_ButStillSettlesPromptly()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 2.0f, step = 0.30f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps);
            int stopFrame = Mathf.RoundToInt(moveSecs * fps);

            Assert.Greater(step, StanceRadius, "sanity: this test is only meaningful if the step is OUTSIDE the radius");

            float3[] hips = RunSpine(dt, frames, Ramp(dt, moveSecs, step), false);
            (float drift, float restMs) = DriftAfterStop(hips, dt, stopFrame);

            // The base DID follow — the pelvis ended up well past the pure-counterbalance answer.
            float moved = hips[frames - 1].z - hips[0].z;
            Assert.Greater(moved, FollowFrac * step,
                $"the pelvis only moved {moved * 100f:F2} cm on a {step * 100f:F0} cm step -- the support base is "
                + "NOT following, so a user who walks would be left behind their own legs");

            Assert.Less(drift, 0.025f, $"the pelvis drifted {drift * 100f:F2} cm after the head stopped");
            Assert.Less(restMs, 300f, $"the pelvis took {restMs:F0} ms to settle after the head stopped");
        }

        /// <summary>
        /// THE RATCHET CHECK — the failure mode a naive leash would have, and the reason the pull scales
        /// with the SQUARED exceedance.
        ///
        /// Hold a lean right AT the stance radius, with tracker jitter. Any pull that is linear in the
        /// exceedance (or a hard clamp) drags the base outward on every jitter sample that lands outside
        /// and never drags it back — so the support base walks away from a user who is standing still.
        /// </summary>
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

            Assert.Less(creep, 0.01f,
                $"the support base is ratcheting outward under tracker jitter -- the pelvis crept "
                + $"{creep * 100f:F2} cm in {seconds} s of standing still. The squared-exceedance pull exists "
                + "precisely to make this impossible.");
        }

        /// <summary>
        /// THE NO-REGRESSION GATE. When both feet own roles the support base is KNOWN (the feet midpoint),
        /// the estimator is never consulted, and there was never any drift. Nothing about this change may
        /// touch that path.
        /// </summary>
        [Test]
        public void WithBothFeetTracked_ThePelvisStillHasNoDriftAtAll()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 2.0f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps);
            int stopFrame = Mathf.RoundToInt(moveSecs * fps);

            foreach (float dist in new[] { 0.15f, 0.30f })
            {
                (float drift, _) = DriftAfterStop(RunSpine(dt, frames, Ramp(dt, moveSecs, dist), true), dt, stopFrame);
                Assert.Less(drift, 0.002f,
                    $"the both-feet-tracked path drifted {drift * 100f:F2} cm on a {dist * 100f:F0} cm move. That "
                    + "path reads the feet midpoint directly and must remain exact.");
            }
        }

        /// <summary>
        /// THE LATERAL GATE — headset-only, no foot roles. Shift the head 15 cm SIDEWAYS (X), INSIDE the
        /// stance radius so the feet have not moved, and hold. A sideways shift is a WEIGHT SHIFT, not a bend:
        /// a real pelvis stays stacked over the feet, so the hips must carry MOST of it — not the 25% sagittal
        /// counterbalance, which left the pelvis behind the head and skewed the torso into a phantom rotation
        /// (the "hips are a bit behind / look rotated" report).
        ///
        /// Three things at once, so the gate cannot be passed the wrong way:
        ///   (a) the hips carry most of the sideways shift (and do not overshoot the head);
        ///   (b) ANISOTROPY — the SAME move FORWARD still counterbalances at ~25%, so the fix is not merely
        ///       "follow everything more" (which would soften the bend feel the counterbalance exists for);
        ///   (c) PAIRED NEGATIVE — the old isotropic 0.25 law, reproduced inline, lags this shift; the real
        ///       job must beat it by a real margin, or the gate is measuring nothing.
        /// </summary>
        [Test]
        public void ALateralShift_TheHipsCarryMostOfIt_NotTheSagittalCounterbalance()
        {
            const int fps = 90;
            const float dt = 1f / fps, moveSecs = 0.40f, holdSecs = 1.0f, shift = 0.15f;
            int frames = Mathf.RoundToInt((moveSecs + holdSecs) * fps);

            Assert.Less(shift, StanceRadius, "sanity: the shift must be INSIDE the radius (a shift, not a step)");

            // Eye rotation is identity in MakeParams, so the torso faces +Z and +X is pure lateral.
            float3[] latHips = RunSpine(dt, frames, i =>
            {
                float t = Mathf.Clamp01(i * dt / moveSecs);
                return new float3(shift * t, StandingHeadY, 0f);
            }, bothFeetTracked: false);

            float movedX = latHips[frames - 1].x - latHips[0].x;

            Assert.Greater(movedX, 0.6f * shift,
                $"the hips carried only {movedX * 100f:F2} cm of a {shift * 100f:F0} cm sideways shift -- a weight "
                + "shift must keep the pelvis under the head, not lag it the way a forward bend does.");
            Assert.Less(movedX, 1.05f * shift,
                $"the hips moved {movedX * 100f:F2} cm on a {shift * 100f:F0} cm shift -- they must not overshoot the head.");

            // (b) ANISOTROPY: the same magnitude move FORWARD must still counterbalance at ~25%.
            float3[] fwdHips = RunSpine(dt, frames, Ramp(dt, moveSecs, shift), bothFeetTracked: false);
            float movedZ = fwdHips[frames - 1].z - fwdHips[0].z;   // forward bias is constant, so it cancels in the delta
            Assert.Less(movedZ, 0.5f * movedX,
                $"forward follow ({movedZ * 100f:F2} cm) is not clearly less than lateral ({movedX * 100f:F2} cm) -- "
                + "the follow is not anisotropic, it just moves more in every direction.");

            // (c) PAIRED NEGATIVE: the shipped-before law followed BOTH axes at CounterbalanceFollowFrac, so on
            // this shift it carried only ~3.75 cm. Reproduced ONLY so this negative can bite; never call from
            // shipping code. If the job ever regresses to it, movedX collapses toward this and (a) fires.
            float oldIsotropic = FollowFrac * shift;
            Assert.Greater(movedX, oldIsotropic + 0.04f,
                $"the shipped hips ({movedX * 100f:F2} cm) are no better than the old isotropic 0.25 follow "
                + $"({oldIsotropic * 100f:F2} cm) -- the anisotropic lateral follow is not in effect.");
        }
    }
}
