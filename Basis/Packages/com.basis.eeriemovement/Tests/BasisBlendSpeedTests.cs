using Basis.IK.Motion;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Tests.IK
{
    public sealed class BasisBlendSpeedTests
    {
        // The shipping virtual-spine speeds (BasisSettingsDefaults.VSpine*RotationSpeed).
        static readonly (string name, float speed)[] k_SpineBlends =
        {
            ("neck", 40f), ("chest", 25f), ("spine", 30f), ("hips", 20f), ("torsoYaw", 8f),
        };
        // ------------------------------------------------------------------ virtual spine
        [Test]
        public void VirtualSpine_EveryRotationBlend_RunsAtTheSameSpeedOnEveryHeadset()
        {
            foreach ((string name, float speed) in k_SpineBlends)
            {
                float s = speed;
                BasisInvarianceResult r = BasisMotionResponse.Invariance((dt, frames) =>
                    {
                        var y = new float[frames];
                        float x = 0f;
                        for (int i = 0; i < frames; i++)
                        {
                            // The REAL shipping function, not a mirror of it. A mirrored formula drifts the
                            // moment someone retunes the original, and then the gate guards nothing.
                            x += BasisSmoothingProfiles.FramerateIndependentAlpha(s, dt) * (1f - x);
                            y[i] = x;
                        }
                        return y;
                    }, 0.8f, name);

                Assert.IsTrue(r.Ok, r.Error);
                Assert.Less(r.WorstDeviation, BasisMotionResponse.MaxFramerateDeviation, $"the {name} blend behaves differently at different framerates -- {r}");
                Assert.Less(r.T63SpreadMs, 4f, $"the {name} blend's time constant depends on the headset -- {r}");
            }
        }
        [Test]
        public void VirtualSpine_LegacySaturateForm_Fails_SoTheGateCannotRotIntoATautology()
        {
            // The paired negative for the test above. This is the code that shipped.
            //
            // ⚠ THE BUG'S MAGNITUDE SCALES WITH SPEED, and that is worth understanding before touching the
            // thresholds. For small alpha, saturate(dt*s) and 1-exp(-r*dt) both tend to dt*s -- they only
            // diverge once alpha gets large. Measured deviation across the shipping speeds:
            //
            //     speed  8 (torsoYaw) -> 0.011      speed 30 (spine) -> 0.053
            //     speed 20 (hips)     -> 0.031      speed 40 (neck)  -> 0.077
            //     the fixed form      -> 0.0000 at every speed
            //
            // So the four FAST blends broke the absolute gate in production; torsoYaw at 8 slipped under it.
            // The first version of this test asserted all five exceeded the gate and duly failed on torsoYaw.
            // The claim that holds at EVERY speed is the relative one: the legacy form is framerate-dependent
            // and the fix is not. That is what is asserted here; the absolute claim is asserted separately,
            // below, only for the blends where it is true.
            foreach ((string name, float speed) in k_SpineBlends)
            {
                float s = speed;
                BasisInvarianceResult legacy = BasisMotionResponse.Invariance((dt, frames) => Chase(dt, frames, BasisMotionResponse.LegacySaturateAlpha(s, dt)), 0.8f, name);
                BasisInvarianceResult fixedForm = BasisMotionResponse.Invariance((dt, frames) => Chase(dt, frames, BasisSmoothingProfiles.FramerateIndependentAlpha(s, dt)), 0.8f, name);

                Assert.Greater(legacy.WorstDeviation, 10f * Mathf.Max(fixedForm.WorstDeviation, 1e-4f), $"the invariance metric can no longer tell saturate(dt*{s}) from the fix -- it has stopped " + $"measuring anything. legacy={legacy.WorstDeviation:F4} fixed={fixedForm.WorstDeviation:F4}");
            }
        }
        [Test]
        public void VirtualSpine_TheFastBlends_ReallyDidBreakTheShippingGate()
        {
            // Documents that this was a REAL production defect, not a theoretical one: neck, chest, spine and
            // hips all exceeded the framerate gate as shipped. (torsoYaw at speed 8 is deliberately excluded
            // -- see the note in the test above; its deviation is real but small.)
            foreach ((string name, float speed) in k_SpineBlends)
            {
                if (speed < 20f) continue;
                float s = speed;
                BasisInvarianceResult r = BasisMotionResponse.Invariance((dt, frames) => Chase(dt, frames, BasisMotionResponse.LegacySaturateAlpha(s, dt)), 0.8f, name);

                Assert.Greater(r.WorstDeviation, BasisMotionResponse.MaxFramerateDeviation, $"{name} (speed {s}) was supposed to be one of the blends that broke the gate as shipped. {r}");
            }
        }
        static float[] Chase(float dt, int frames, float alpha)
        {
            var y = new float[frames];
            float x = 0f;
            for (int i = 0; i < frames; i++) { x += alpha * (1f - x); y[i] = x; }
            return y;
        }
        [Test]
        public void VirtualSpine_Fix_IsABitForBitNoOp_AtTheReferenceFramerate()
        {
            // The compatibility claim, pinned. The *RotationSpeed values are user-facing sliders with
            // PERSISTED values, so the fix deliberately preserves what the number MEANS ("the per-frame
            // fraction at 90 Hz") rather than redefining it. At 90 Hz the new function must therefore
            // reproduce the old one exactly -- nobody's tuning changes, it just stops depending on their
            // hardware. Same discipline as the foot-IK scale fix: exactly a no-op at the reference,
            // proportional everywhere else.
            const float refDt = 1f / 90f;
            for (float speed = 0f; speed <= 100f; speed += 0.5f)
            {
                float legacy = BasisMotionResponse.LegacySaturateAlpha(speed, refDt);
                float fixedAlpha = BasisSmoothingProfiles.FramerateIndependentAlpha(speed, refDt);
                Assert.AreEqual(legacy, fixedAlpha, 1e-4f, $"at the reference 90 Hz the fix must reproduce the old behaviour exactly, but speed={speed} " + $"gives {fixedAlpha:F5} where the old code gave {legacy:F5} -- every persisted user setting " +"would silently change meaning");
            }
        }
        [Test]
        public void VirtualSpine_NeckStillBlends_AtLowFramerate_InsteadOfSnapping()
        {
            // The nastiest half of the old bug, and the one a spread metric alone would miss.
            //
            // `saturate` clamps once dt*speed >= 1. At the default NeckRotationSpeed of 40 that happens at
            // 40 fps, so at or below 40 fps the old code produced alpha = 1.0 -- the neck did not blend at
            // all, it SNAPPED, every single frame. Standalone under load and desktop mode both live there.
            // The smoothing switched itself off exactly when it was most needed.
            foreach (int fps in new[] { 20, 30, 40, 60 })
            {
                float dt = 1f / fps, legacy = BasisMotionResponse.LegacySaturateAlpha(40f, dt);
                float now = BasisSmoothingProfiles.FramerateIndependentAlpha(40f, dt);

                if (fps <= 40)
                    Assert.AreEqual(1f, legacy, 1e-6f, $"sanity: the old form should have been snapping at {fps} fps");

                Assert.Less(now, 0.995f, $"at {fps} fps the neck alpha is {now:F4} -- a blend that is complete in one frame is not a blend");
            }
        }
        [Test]
        public void VirtualSpine_LagDoesNotGrowWithFramerate_BeyondTheSamplingFloor()
        {
            float Lag(int fps, System.Func<float, float, float> alpha)
            {
                float dt = 1f / fps;
                int n = 10 * fps;
                var inp = new float[n];
                var outp = new float[n];
                float x = 0f;
                for (int i = 0; i < n; i++)
                {
                    inp[i] = Mathf.Sin(2f * Mathf.PI * 1.0f * (i + 1) * dt);
                    x += alpha(40f, dt) * (inp[i] - x);
                    outp[i] = x;
                }
                return BasisMotionResponse.Sine(inp, outp, dt, 1.0f).LagMs;
            }

            const float samplingFloorMs = (1f / 72f - 1f / 144f) * 0.5f * 1000f;   // 3.47 ms, irreducible

            float now72 = Lag(72, BasisSmoothingProfiles.FramerateIndependentAlpha);
            float now144 = Lag(144, BasisSmoothingProfiles.FramerateIndependentAlpha);
            float nowDiff = Mathf.Abs(now144 - now72), old72 = Lag(72, BasisMotionResponse.LegacySaturateAlpha);
            float old144 = Lag(144, BasisMotionResponse.LegacySaturateAlpha), oldDiff = Mathf.Abs(old144 - old72);

            Assert.Less(nowDiff, samplingFloorMs + 1f, $"the fixed blend's lag varies by {nowDiff:F2} ms across framerates (72Hz:{now72:F1} " + $"144Hz:{now144:F1}), which is MORE than the {samplingFloorMs:F2} ms sampling floor -- so " +"something beyond discrete sampling is still framerate-dependent");

            Assert.Greater(oldDiff, 2f * nowDiff, $"the legacy form is supposed to lag far more unevenly than the fix (legacy {oldDiff:F2} ms vs " + $"fixed {nowDiff:F2} ms); if it no longer does, this gate is testing nothing");

            Assert.Greater(old144, old72 * 1.3f, $"and its lag is supposed to GROW with framerate (72Hz:{old72:F1} 144Hz:{old144:F1}) -- the " +"perverse fingerprint of saturate(dt*speed)");
        }
        // ------------------------------------------------------------------ foot swing rotation
        // WHERE TO PROBE THE SWING -- this choice is load-bearing, and getting it wrong hides the bug.
        //
        // The self-referential form's divergence is NOT uniform across the swing. `ease` runs 0 -> 1, and once
        // it approaches 1 the slerp snaps home in a single frame regardless of how many frames preceded it. So
        // both framerates converge to the same place by the end, and the error only exists EARLY. Measured
        // convergence gap between 72 and 144 Hz through an identical 0.5 s step:
        //
        //     t=0.11  gap 0.042        t=0.39  gap 0.101
        //     t=0.19  gap 0.161        t=0.50  gap 0.012     <-- almost nothing left
        //     t=0.28  gap 0.224  (max) t=0.69  gap 0.000     <-- gone entirely
        //
        // THAT is why the bug survived: anyone eyeballing "does the foot end up in the right place?" watches
        // it converge perfectly. The first version of this test probed at t=0.50 and duly measured a gap of
        // 0.012 -- it was looking at precisely the part of the swing where the defect hides.
        //
        // 10 ticks at 72 Hz and 20 at 144 Hz put both sims at the same instant (144 = 2 x 72, so the clocks
        // coincide EXACTLY -- no interpolation, nothing to explain away) at t = 0.278, the worst case.
        const int probe72 = 10, probe144 = 20;
        [Test]
        public void FootSwing_RotationAtTheSameStepProgress_IsIdenticalAtAnyFramerate()
        {
            quaternion at72 = RunSwingTo(72, probe72);
            quaternion at144 = RunSwingTo(144, probe144);   // same elapsed time, same step progress

            float err = math.degrees(2f * math.acos(math.clamp(math.abs(math.dot(at72.value, at144.value)), -1f, 1f)));
            Assert.Less(err, 0.5f, $"the foot is in a different pose mid-swing at 72 Hz vs 144 Hz (off by {err:F2} deg). " + "The swing rotation is converging by frame count instead of by step progress -- i.e. it is " +"chasing its own previous output instead of blending from stepStartRot.");
        }
        [Test]
        public void FootSwing_SelfReferentialForm_Fails_SoTheGateCannotRotIntoATautology()
        {
            // The paired negative: the form that shipped. Same two framerates, same elapsed time, but the blend
            // feeds its own output back in -- so twice the frames means substantially more convergence. At the
            // probe point the two rates disagree by ~0.22 of the whole rotation. If this ever stops failing,
            // the test above is no longer proving anything.
            float a = LegacySelfReferentialConvergence(72, probe72);
            float b = LegacySelfReferentialConvergence(144, probe144);

            Assert.Greater(Mathf.Abs(a - b), 0.10f, $"the self-referential slerp is supposed to diverge across framerates (72Hz:{a:F3} 144Hz:{b:F3}); " +"if it no longer does, this gate is testing nothing");
        }
        static quaternion RunSwingTo(int fps, int ticks)
        {
            var feet = new NativeArray<BasisFootNativeState>(2, Allocator.Temp);
            var simState = new NativeArray<BasisFootSimState>(1, Allocator.Temp);
            var input = new NativeArray<BasisFootSimInput>(1, Allocator.Temp);
            var output = new NativeArray<BasisFootSimOutput>(1, Allocator.Temp);
            try
            {
                float3 up = new float3(0f, 1f, 0f), fwd = new float3(0f, 0f, 1f);
                quaternion startRot = quaternion.LookRotation(fwd, up);

                // A COMPLETE param set on purpose. Several of these are divisors inside the job
                // (fastSpeedRef, maxPlantedYawDegrees, stepDurFast); leaving them at the struct default of
                // zero produces NaN, and a NaN would propagate into simState and quietly make both
                // framerates "agree" -- the test would pass by being broken.
                var p = new BasisFootSimParams
                {
                    predictionFactor = 0.5f,
                    velocityBiasFactor = 0.1f,
                    leadOffsetFactor = 0.1f,
                    maxVelocityOffsetFraction = 0.3f,
                    maxPredictionFraction = 0.35f,

                    plantedLerpSpeed = 40f,
                    rotationLerpSpeed = 16f,
                    velocitySmoothAccel = 25f,
                    velocitySmoothDecel = 50f,
                    bodyFwdRateMoving = 6f,
                    bodyFwdRateStationary = 2.5f,
                    kneeHintLerpSpeed = 10f,

                    maxFootTiltDegrees = 35f,
                    maxFootYawDegrees = 18f,

                    stepArcLiftExp = 0.6f,
                    stepArcDropExp = 1.4f,
                    stepHeightMinFraction = 0.4f,
                    stepHeightStrideRefFraction = 0.45f,

                    idleSpeedThreshold = 0.1f,
                    idleBoostFraction = 0.5f,
                    maxPlantedYawDegrees = 20f,

                    idealSideEnforceFraction = 0.5f,
                    stepTargetSideFraction = 0.5f,
                    footSideEnforceFraction = 0.5f,
                    maxVerticalDriftFraction = 0.25f,
                    kneeForwardPushFraction = 0.3f,
                    kneeMinSideFraction = 0.1f,

                    bodyFwdHipsWeight = 0f,
                    bodyFwdChestWeight = 0f,
                    bodyFwdHeadWeight = 1f,
                    hipBobFraction = 0f,

                    footAlignLeft = quaternion.identity,
                    footAlignRight = quaternion.identity,

                    stanceWidth = 0.2f,
                    hipToFoot = 0.95f,
                    leftLegLen = 0.9f, rightLegLen = 0.9f,
                    leftThighLen = 0.45f, leftShinLen = 0.45f,
                    rightThighLen = 0.45f, rightShinLen = 0.45f,
                    footLength = 0.24f,
                    ankleHeight = 0.08f,

                    stepTriggerDist = 0.2f,
                    strideScale = 0.15f,
                    stepHeightCalc = 0.12f,
                    stepDurSlow = 0.40f,
                    stepDurFast = 0.25f,
                    raySphereRadius = 0.05f,
                    footHeightOffset = 0f,
                    fastSpeedRef = 3.5f,
                    rayCastRange = 2f,
                };

                var swinging = new BasisFootNativeState
                {
                    sideSign = -1,
                    thighLen = 0.45f, shinLen = 0.45f, legLength = 0.9f,
                    phase = 1,                                   // Stepping
                    stepStartPos = new float3(-0.1f, 0f, 0f),
                    stepTargetPos = new float3(-0.1f, 0f, 0.6f),
                    stepStartRot = startRot,
                    stepTimer = 0f,
                    stepDur = 0.5f,
                    currentPos = new float3(-0.1f, 0f, 0f),
                    currentRot = startRot,
                    plantedRot = startRot,
                    plantedPos = new float3(-0.1f, 0f, 0f),
                    plantedBodyFwd = fwd,
                    idealPos = new float3(-0.1f, 0f, 0f),
                    filteredNormal = up,
                };
                var planted = swinging;
                planted.sideSign = 1;
                planted.phase = 0;
                planted.plantedPos = planted.currentPos = new float3(0.1f, 0f, 0f);
                planted.plantedTime = 10f;

                feet[0] = swinging;
                feet[1] = planted;

                float3 hips = new float3(0f, 0.95f, 0f);

                // Every smoother seeded AT its fixed point, and the input held constant, so nothing in the
                // sim can drift between the two framerates except the thing under test.
                simState[0] = new BasisFootSimState
                {
                    prevHeadPos = hips,           // holds the HIPS despite the name -- zero velocity
                    smoothedVelocity = float3.zero,
                    smoothedBodyFwd = fwd,
                    smoothedBodyRight = new float3(1f, 0f, 0f),
                    prevBodyFwd = fwd,
                    prevRootFwd = fwd,
                    smoothedYawRateDeg = 0f,
                    wasAirborne = false,
                };

                var job = new BasisFootSimulateJob
                {
                    p = p, feet = feet, simState = simState, input = input, output = output,
                };

                float dt = 1f / fps;
                for (int i = 0; i < ticks; i++)
                {
                    input[0] = new BasisFootSimInput
                    {
                        dt = dt,
                        headPos = new float3(0f, 1.6f, 0f),
                        hipsPos = hips,
                        hipsRot = quaternion.identity,
                        chestRot = quaternion.identity,
                        headRot = quaternion.identity,
                        avatarForward = fwd,
                        avatarRight = new float3(1f, 0f, 0f),
                        hasChest = false,
                        groundHit = true,
                        groundPoint = float3.zero,
                        splayWhenCrouched = 0f,
                        playerUp = up,
                    };
                    job.Execute();
                }

                return feet[0].currentRot;
            }
            finally
            {
                // Dispose in a finally: an assertion failure mid-test must not leak native memory into the
                // rest of the EditMode run.
                feet.Dispose();
                simState.Dispose();
                input.Dispose();
                output.Dispose();
            }
        }
        static float LegacySelfReferentialConvergence(int fps, int ticks)
        {
            const float stepDur = 0.5f;
            float dt = 1f / fps, x = 0f;
            for (int i = 0; i < ticks; i++)
            {
                float t = Mathf.Clamp01((i + 1) * dt / stepDur), ease = t * t * (3f - 2f * t);
                x += ease * (1f - x);        // slerp(currentRot, liveRot, ease), scalarised
            }
            return x;
        }
    }
}
