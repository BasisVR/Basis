using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.IK.Mocap
{
    // "Does the procedural stepper walk like a human?" -- measured against real mocap, the foot analog of
    // BasisMocapAccuracy (which does it for the invented elbow/knee).
    //
    // The stepper (BasisFootSimulateJob) is PROCEDURAL: it synthesises feet from hips/head/chest, it never
    // consumes real foot data. So the comparison is: feed a walking clip's hips/head/chest -- the ONLY things
    // the stepper reads -- into the REAL job (Execute(), the shipping math), then score the SYNTHESISED feet
    // against the clip's REAL feet. The same contact definition is applied to both so it is apples-to-apples.
    //
    // A Python twin (scratchpad, session 39fde01c -- see project_basis_foot_mocap_comparison_harness) produced
    // the tuning this validates; keep the two in lock-step. Everything here is DIMENSIONLESS (fractions of leg
    // length / pendulum time), so the numbers transfer to any avatar size -- the whole point of the scale law.
    //
    // Literature (real human walking): duty ~0.60, double-support ~0.20-0.25 of the cycle, clearance ~0.15-0.19 L,
    // step width ~0.10-0.13 L. Measured on the CMU corpus the real numbers match (duty 0.55, dsup 0.25, clear 0.16).
    public struct BasisFootQualitySummary
    {
        public bool Ok;
        public string Error, Clip;
        public int Frames;
        public float Dt;
        public float LegLen;          // metres (normalised to ~0.85)
        public float VHat;            // v / sqrt(g L), the dimensionless speed
        // Real vs synthesised, all dimensionless.
        public float DutyReal, DutySyn;              // fraction of time a foot is planted
        public float DoubleSupportReal, DoubleSupportSyn;  // fraction of time BOTH feet are planted
        public float ClearReal, ClearSyn;            // median swing foot clearance / L
        public float StepLenReal, StepLenSyn;        // median swing foot travel / L
        public float CadenceReal, CadenceSyn;        // f * sqrt(L/g), dimensionless step frequency
        public float PlantedSlideSyn;                // median planted-foot horizontal move, mm/frame (should be ~0)
        public float ExtP50, ExtP95, ExtMax;         // synth foot-to-hips distance / standing reach
        public bool ChiralityOk;                     // synth-left really tracks the body's left side
    }
    // Result of the synthetic in-place-spin probe -- reproduces "fast hip rotation makes the feet step on
    // each other" headlessly (no BVH), mirror of scratchpad/spin.py.
    public struct BasisFootRotationResult
    {
        public float CrossMaxCm;   // worst lateral overlap toward the wrong side (>0 = scissored)
        public float CrossedPct;   // % of ticks scissored by > 2 cm
        public float MinGapCm;     // closest the feet get horizontally
        public float CollidePct;   // % of ticks the feet overlap (gap < foot length) = "stepping on each other"
        public int BothSteppingTicks;
        // While a foot is PLANTED, how much its own yaw follows the body's yaw. 0 = it holds in the world (a
        // planted foot must not pivot as you turn -- the whole point of freezing plantedRot); 1 = it spins with
        // the body (the OLD "preserveTip" behaviour). This exercises the foot ROTATION the driver now consumes.
        public float PlantedYawFollowFrac;
    }
    // Result of the synthetic straight-walk probe -- dimensionless gait metrics, no BVH needed.
    public struct BasisFootWalkResult
    {
        public float Duty, DoubleSupport, ClearFrac, CadenceHat, ExtP95, SlideMm;
        public int Steps;
    }
    public static class BasisMocapFootQuality
    {
        static readonly float3 Up = new float3(0, 1, 0);
        const float G = 9.81f;
        // ── naturalness gate thresholds (loose -- catch "that is not a walk", not subtle feel) ──
        // Double-support is a BAND on the SYNTH gait, not a per-clip differential: the stepper synthesises one
        // gait and cannot know THIS person's cadence -- a fast walker (v-hat ~0.5) legitimately has almost no
        // double support, and penalising the stepper for not matching that is wrong. It just has to be human.
        public const float MinDoubleSupport = 0.05f;      // near-zero = the "floaty" single-support tell
        public const float MaxDoubleSupport = 0.45f;      // too much = a shuffle
        public const float MinClearFrac = 0.09f;          // clearance / L must not be a dead shuffle
        public const float MaxClearFrac = 0.26f;          // ...nor a march
        public const float MaxPlantedSlideMm = 6f;        // a planted foot must stay put
        public const float MaxExtP95 = 1.22f;             // 95% of frames within reach (the tail is the no-flight-phase ceiling)
        public static BasisFootQualitySummary Run(BasisMotionClip clip)
        {
            var r = new BasisFootQualitySummary { Ok = false, Clip = clip?.Name };
            try
            {
                if (clip == null || clip.FrameCount < 8) { r.Error = "clip too short"; return r; }
                if (!clip.Has(BasisMocapJoint.Hips) || !clip.Has(BasisMocapJoint.Head) || !clip.Has(BasisMocapJoint.LeftFoot) || !clip.Has(BasisMocapJoint.RightFoot) || !clip.Has(BasisMocapJoint.LeftToes) || !clip.Has(BasisMocapJoint.RightToes) || !clip.Has(BasisMocapJoint.LeftUpperLeg) || !clip.Has(BasisMocapJoint.RightUpperLeg) || !clip.Has(BasisMocapJoint.LeftLowerLeg) || !clip.Has(BasisMocapJoint.RightLowerLeg))
                { r.Error = "clip is missing a required leg/foot/head joint"; return r; }

                int n = clip.FrameCount;
                float dt = clip.FrameTime;
                r.Frames = n; r.Dt = dt; r.Clip = clip.Name;

                BasisFootSimParams p = MeasureAndDerive(clip, out float legLen, out float groundY);
                r.LegLen = legLen;

                // ── run the REAL job over the clip ──
                float3[] synL = new float3[n], synR = new float3[n];
                bool[] plantedL = new bool[n], plantedR = new bool[n];
                RunStepper(clip, p, groundY, synL, synR, plantedL, plantedR);

                // mean horizontal hip speed -> v-hat
                float3 h0 = clip.Get(0, BasisMocapJoint.Hips).Position;
                float3 hEnd = clip.Get(n - 1, BasisMocapJoint.Hips).Position;
                float travel = math.length(Flat(hEnd - h0, Up)), vmean = travel / math.max(1e-3f, (n - 1) * dt);
                r.VHat = vmean / math.sqrt(G * legLen);

                int warm = Mathf.Clamp(Mathf.RoundToInt(0.6f / dt), 0, n - 4);

                // ── real feet ──
                bool[] cLr = Contacts(clip, BasisMocapJoint.LeftFoot, BasisMocapJoint.LeftToes, legLen, dt);
                bool[] cRr = Contacts(clip, BasisMocapJoint.RightFoot, BasisMocapJoint.RightToes, legLen, dt);
                float3[] realL = Track(clip, BasisMocapJoint.LeftFoot), realR = Track(clip, BasisMocapJoint.RightFoot);

                GaitStats(realL, cLr, legLen, dt, out float dutyLr, out float clrLr, out float slLr, out int stepsLr);
                GaitStats(realR, cRr, legLen, dt, out float dutyRr, out float clrRr, out float slRr, out int stepsRr);
                GaitStats(synL, plantedL, legLen, dt, out float dutyLs, out float clrLs, out float slLs, out int stepsLs);
                GaitStats(synR, plantedR, legLen, dt, out float dutyRs, out float clrRs, out float slRs, out int stepsRs);

                r.DutyReal = 0.5f * (dutyLr + dutyRr); r.DutySyn = 0.5f * (dutyLs + dutyRs);
                r.ClearReal = Median2(clrLr, clrRr); r.ClearSyn = Median2(clrLs, clrRs);
                r.StepLenReal = Median2(slLr, slRr); r.StepLenSyn = Median2(slLs, slRs);
                r.CadenceReal = (stepsLr + stepsRr) / (n * dt) * math.sqrt(legLen / G);
                r.CadenceSyn = (stepsLs + stepsRs) / (n * dt) * math.sqrt(legLen / G);
                r.DoubleSupportReal = BothFrac(cLr, cRr);
                r.DoubleSupportSyn = BothFrac(plantedL, plantedR);
                r.PlantedSlideSyn = 0.5f * (PlantedSlideMm(synL, plantedL) + PlantedSlideMm(synR, plantedR));

                // over-extension (foot-to-hips distance / natural standing reach), warm-up excluded
                float restReach = math.sqrt(p.stanceWidth * p.stanceWidth * 0.25f + p.hipToFoot * p.hipToFoot);
                var ext = new System.Collections.Generic.List<float>(2 * (n - warm));
                for (int f = warm; f < n; f++)
                {
                    float3 hp = clip.Get(f, BasisMocapJoint.Hips).Position;
                    ext.Add(math.length(synL[f] - hp) / restReach);
                    ext.Add(math.length(synR[f] - hp) / restReach);
                }
                r.ExtP50 = Percentile(ext, 50); r.ExtP95 = Percentile(ext, 95); r.ExtMax = Percentile(ext, 100);

                // chirality: synth-left must sit on the body's LEFT (negative body-right side), like the real left foot
                r.ChiralityOk = LateralSign(clip, synL, warm) < 0f && LateralSign(clip, synR, warm) > 0f;

                r.Ok = true;
            }
            catch (Exception e)
            {
                r.Ok = false; r.Error = e.Message;
            }
            return r;
        }
        public static (bool pass, string reason) Gate(in BasisFootQualitySummary s)
        {
            if (!s.Ok) return (false, s.Error);
            if (!s.ChiralityOk) return (false, "synth feet are on the wrong side (chirality/handedness bug)");
            if (s.DoubleSupportSyn < MinDoubleSupport || s.DoubleSupportSyn > MaxDoubleSupport)
                return (false, $"double-support {s.DoubleSupportSyn:F2} outside the human band [{MinDoubleSupport}, {MaxDoubleSupport}] (real here {s.DoubleSupportReal:F2})");
            if (s.ClearSyn < MinClearFrac) return (false, $"foot clearance {s.ClearSyn:F3} L is a dead shuffle (< {MinClearFrac})");
            if (s.ClearSyn > MaxClearFrac) return (false, $"foot clearance {s.ClearSyn:F3} L is a march (> {MaxClearFrac})");
            if (s.PlantedSlideSyn > MaxPlantedSlideMm) return (false, $"planted foot slides {s.PlantedSlideSyn:F1} mm/frame (> {MaxPlantedSlideMm})");
            if (s.ExtP95 > MaxExtP95) return (false, $"leg over-extends: ext p95 {s.ExtP95:F2} (> {MaxExtP95})");
            return (true, null);
        }
        // ───────────────────────── calibration + params (MUST mirror BasisLocalFootDriver) ─────────────────────────
        // The CMU skeleton has no authored T-pose; a real Basis avatar is measured from one with straight legs.
        // So take the correct bone LENGTHS (rigid, pose-independent) and synthesise that T-pose: feet under the
        // hip sockets, legs vertical. stanceWidth = hip-socket lateral separation (a splayed rest pose would give
        // a 5x-too-wide stance -- the measurement bug the Python twin found).
        static BasisFootSimParams MeasureAndDerive(BasisMotionClip clip, out float legLen, out float groundY)
        {
            Vector3 hips = clip.Get(0, BasisMocapJoint.Hips).Position;
            Vector3 lul = clip.Get(0, BasisMocapJoint.LeftUpperLeg).Position;
            Vector3 rul = clip.Get(0, BasisMocapJoint.RightUpperLeg).Position;
            Vector3 lll = clip.Get(0, BasisMocapJoint.LeftLowerLeg).Position;
            Vector3 rll = clip.Get(0, BasisMocapJoint.RightLowerLeg).Position;
            Vector3 lf = clip.Get(0, BasisMocapJoint.LeftFoot).Position;
            Vector3 rf = clip.Get(0, BasisMocapJoint.RightFoot).Position;
            Vector3 lt = clip.Get(0, BasisMocapJoint.LeftToes).Position;
            Vector3 rt = clip.Get(0, BasisMocapJoint.RightToes).Position;
            float leftThigh = Vector3.Distance(lul, lll), leftShin = Vector3.Distance(lll, lf);
            float rightThigh = Vector3.Distance(rul, rll), rightShin = Vector3.Distance(rll, rf);
            float leftLeg = leftThigh + leftShin, rightLeg = rightThigh + rightShin;
            float avgLeg = 0.5f * (leftLeg + rightLeg);
            legLen = avgLeg;

            float socketBelowHips = hips.y - 0.5f * (lul.y + rul.y);
            Vector3 sw = rul - lul; sw.y = 0f;
            float stanceWidth = Mathf.Max(0.04f, sw.magnitude), hipToFoot = socketBelowHips + avgLeg;
            float upperLegToFootVertical = avgLeg;
            float ankleHeight = Mathf.Max(0.005f, 0.5f * (Mathf.Abs(lf.y - lt.y) + Mathf.Abs(rf.y - rt.y)));
            float footLength = Mathf.Max(0.02f, 0.5f * (Vector3.Distance(lf, lt) + Vector3.Distance(rf, rt)));

            // flat floor for the stepper: robust low percentile of every foot/toe height
            var lows = new System.Collections.Generic.List<float>(clip.FrameCount * 4);
            for (int f = 0; f < clip.FrameCount; f++)
            {
                lows.Add(clip.Get(f, BasisMocapJoint.LeftFoot).Position.y);
                lows.Add(clip.Get(f, BasisMocapJoint.RightFoot).Position.y);
                lows.Add(clip.Get(f, BasisMocapJoint.LeftToes).Position.y);
                lows.Add(clip.Get(f, BasisMocapJoint.RightToes).Position.y);
            }
            groundY = Percentile(lows, 3);

            return Derive(leftThigh, leftShin, rightThigh, rightShin, stanceWidth, hipToFoot, upperLegToFootVertical, ankleHeight, footLength);
        }
        // The ONE place the clamp math lives on the harness side (both the mocap path and the synthetic
        // rotation probe route through it). MUST match BasisLocalFootDriver.DeriveStepParameters +
        // BasisFootIKSweep.BuildParams -- a tune only has to change one copy on this side.
        static BasisFootSimParams Derive(float leftThigh, float leftShin, float rightThigh, float rightShin, float stanceWidth, float hipToFoot, float upperLegToFootVertical, float ankleHeight, float footLength)
        {
            float leftLeg = leftThigh + leftShin, rightLeg = rightThigh + rightShin;
            float avgLeg = 0.5f * (leftLeg + rightLeg), avgShin = 0.5f * (leftShin + rightShin);
            const float refLeg = 0.87f;
            float pendulum = Mathf.PI * Mathf.Sqrt(avgLeg / G), speedRef = Mathf.Sqrt(avgLeg * G);
            // inspector defaults, kept in lock-step with the driver (tuned 2026-07-18 vs real walk)
            const float raySphereRadiusMul = 0.3f, footHeightOffsetMul = 0.2f, stepTriggerMul = 0.18f,
                        strideScaleMul = 0.15f, stepHeightMul = 0.30f, stepDurSlowMul = 0.30f, stepDurFastMul = 0.18f,
                        fastSpeedMul = 1.2f;

            float raySphereRadius = Mathf.Clamp(footLength * raySphereRadiusMul, avgLeg * (0.02f / refLeg), avgLeg * (0.12f / refLeg));
            float desiredOffset = ankleHeight * footHeightOffsetMul;
            float straightLegLimit = upperLegToFootVertical + ankleHeight - avgLeg;
            float footHeightOffset = Mathf.Clamp(Mathf.Min(desiredOffset, straightLegLimit), avgLeg * (0.001f / refLeg), avgLeg * (0.05f / refLeg));
            float stepTriggerDist = Mathf.Clamp(avgLeg * stepTriggerMul, avgLeg * (0.04f / refLeg), avgLeg * (0.18f / refLeg));
            float strideScale = Mathf.Clamp(avgLeg * strideScaleMul, avgLeg * (0.02f / refLeg), avgLeg * (0.22f / refLeg));
            float stepHeightCalc = Mathf.Clamp(avgShin * stepHeightMul, avgLeg * (0.03f / refLeg), avgLeg * (0.20f / refLeg));
            float stepDurSlow = Mathf.Clamp(pendulum * stepDurSlowMul, pendulum * (0.10f / 0.9356f), pendulum * (0.30f / 0.9356f));
            float stepDurFast = Mathf.Clamp(pendulum * stepDurFastMul, pendulum * (0.06f / 0.9356f), pendulum * (0.18f / 0.9356f));
            float fastSpeedRef = Mathf.Clamp(fastSpeedMul * speedRef, speedRef * (1.0f / 2.921f), speedRef * 2.5f);

            return new BasisFootSimParams
            {
                predictionFactor = 0.9f, velocityBiasFactor = 0.18f, leadOffsetFactor = 0.6f,
                maxVelocityOffsetFraction = 0.35f, maxPredictionFraction = 0.35f,
                plantedLerpSpeed = 40f, rotationLerpSpeed = 16f, velocitySmoothAccel = 25f, velocitySmoothDecel = 50f,
                bodyFwdRateMoving = 6f, bodyFwdRateStationary = 2.5f, kneeHintLerpSpeed = 10f,
                maxFootTiltDegrees = 35f, maxFootYawDegrees = 18f,
                stepArcLiftExp = 0.6f, stepArcDropExp = 1.4f, stepHeightMinFraction = 0.4f, stepHeightStrideRefFraction = 0.45f,
                idleSpeedThreshold = 0.05f, idleBoostFraction = 0.5f, maxPlantedYawDegrees = 20f,
                idealSideEnforceFraction = 0.3f, stepTargetSideFraction = 0.15f, footSideEnforceFraction = 0.2f,
                maxVerticalDriftFraction = 0.25f, kneeForwardPushFraction = 0.4f, kneeMinSideFraction = 0.05f,
                bodyFwdHipsWeight = 3f, bodyFwdChestWeight = 2f, bodyFwdHeadWeight = 1f, hipBobFraction = 0.02f,
                footAlignLeft = quaternion.identity, footAlignRight = quaternion.identity,
                stanceWidth = stanceWidth, hipToFoot = hipToFoot,
                leftLegLen = leftLeg, rightLegLen = rightLeg,
                leftThighLen = leftThigh, leftShinLen = leftShin, rightThighLen = rightThigh, rightShinLen = rightShin,
                footLength = footLength, ankleHeight = ankleHeight,
                stepTriggerDist = stepTriggerDist, strideScale = strideScale, stepHeightCalc = stepHeightCalc,
                stepDurSlow = stepDurSlow, stepDurFast = stepDurFast, raySphereRadius = raySphereRadius,
                footHeightOffset = footHeightOffset, fastSpeedRef = fastSpeedRef,
                rayCastRange = Mathf.Max(hipToFoot + ankleHeight, Mathf.Max(leftLeg, rightLeg)) + 1f,
            };
        }
        // A reference adult avatar (0.87 m leg) scaled by `scale` -- params without a mocap clip, for the
        // scale-invariance and synthetic rotation/crossover checks.
        public static BasisFootSimParams BuildReferenceParams(float scale)
        {
            float s = scale;
            return Derive(0.45f * s, 0.42f * s, 0.45f * s, 0.42f * s, 0.18f * s, 0.92f * s, 0.87f * s, 0.08f * s, 0.13f * s);
        }
        // ───────────────────────── drive the real job ─────────────────────────
        static void RunStepper(BasisMotionClip clip, BasisFootSimParams p, float groundY, float3[] outL, float3[] outR, bool[] plantedL, bool[] plantedR)
        {
            int n = clip.FrameCount;
            // constant avatar forward = frame-0 hips facing (physical locomotion: the playspace does not rotate)
            Quaternion hips0 = clip.Get(0, BasisMocapJoint.Hips).Rotation;
            float3 avatarFwd = math.normalizesafe(Flat(hips0 * Vector3.forward, Up), new float3(0, 0, 1));
            float3 avatarRight = math.normalizesafe(math.cross(Up, avatarFwd), new float3(1, 0, 0));
            float3 hipsPos0 = clip.Get(0, BasisMocapJoint.Hips).Position;
            bool hasChest = clip.Has(BasisMocapJoint.UpperChest);

            var feet = new NativeArray<BasisFootNativeState>(2, Allocator.Persistent);
            var simState = new NativeArray<BasisFootSimState>(1, Allocator.Persistent);
            var input = new NativeArray<BasisFootSimInput>(1, Allocator.Persistent);
            var output = new NativeArray<BasisFootSimOutput>(1, Allocator.Persistent);
            try
            {
                feet[0] = InitFoot(p, -1, hipsPos0, groundY, avatarRight);
                feet[1] = InitFoot(p, +1, hipsPos0, groundY, avatarRight);
                simState[0] = new BasisFootSimState
                {
                    prevHeadPos = hipsPos0,          // velocity source is the hips (see the job)
                    smoothedVelocity = float3.zero,
                    smoothedBodyFwd = avatarFwd,
                    smoothedBodyRight = avatarRight,
                    prevRootFwd = avatarFwd,
                };

                for (int f = 0; f < n; f++)
                {
                    float3 hp = clip.Get(f, BasisMocapJoint.Hips).Position;
                    input[0] = new BasisFootSimInput
                    {
                        dt = clip.FrameTime,
                        headPos = clip.Get(f, BasisMocapJoint.Head).Position,
                        hipsPos = hp,
                        hipsRot = clip.Get(f, BasisMocapJoint.Hips).Rotation,
                        chestRot = hasChest ? clip.Get(f, BasisMocapJoint.UpperChest).Rotation : clip.Get(f, BasisMocapJoint.Hips).Rotation,
                        headRot = clip.Get(f, BasisMocapJoint.Head).Rotation,
                        avatarForward = avatarFwd,
                        avatarRight = avatarRight,
                        hasChest = hasChest,
                        groundHit = true,
                        groundPoint = new float3(hp.x, groundY, hp.z),
                        splayWhenCrouched = 1f,
                        playerUp = Up,
                    };

                    new BasisFootSimulateJob { p = p, feet = feet, simState = simState, input = input, output = output }.Execute();

                    var lf = feet[0]; if (lf.wantsStep) { FinalizeStepFlat(ref lf, simState[0], p, hp, groundY); lf.wantsStep = false; feet[0] = lf; }
                    var rf = feet[1]; if (rf.wantsStep) { FinalizeStepFlat(ref rf, simState[0], p, hp, groundY); rf.wantsStep = false; feet[1] = rf; }

                    outL[f] = feet[0].currentPos; outR[f] = feet[1].currentPos;
                    plantedL[f] = feet[0].phase == 0; plantedR[f] = feet[1].phase == 0;
                }
            }
            finally { feet.Dispose(); simState.Dispose(); input.Dispose(); output.Dispose(); }
        }
        static BasisFootNativeState InitFoot(in BasisFootSimParams p, int sideSign, float3 hips, float groundY, float3 bodyRight)
        {
            float half = p.stanceWidth * 0.5f;
            float3 footXZ = Flat(hips, Up) + bodyRight * (sideSign * half);
            float3 planted = new float3(footXZ.x, groundY + p.footHeightOffset, footXZ.z);
            float thigh = sideSign < 0 ? p.leftThighLen : p.rightThighLen;
            return new BasisFootNativeState
            {
                sideSign = sideSign,
                thighLen = thigh, shinLen = sideSign < 0 ? p.leftShinLen : p.rightShinLen,
                legLength = sideSign < 0 ? p.leftLegLen : p.rightLegLen,
                phase = 0, plantedPos = planted, plantedRot = quaternion.identity,
                stepStartPos = planted, stepTargetPos = planted, stepStartRot = quaternion.identity,
                stepDur = p.stepDurSlow, idealPos = planted, filteredNormal = Up,
                currentPos = planted, currentRot = quaternion.identity,
                kneeHint = (hips + planted) * 0.5f,
            };
        }
        // Flat-ground FinalizeStep (mirror of BasisLocalFootDriver.FinalizeStep with the raycast collapsed to a
        // constant floor level -- a walk clip is on flat ground).
        static void FinalizeStepFlat(ref BasisFootNativeState f, in BasisFootSimState sim, in BasisFootSimParams p, float3 hips, float groundY)
        {
            float3 velFlat = Flat(sim.smoothedVelocity, Up);
            float speed = math.length(velFlat);
            float fastYawRef = math.max(1f, 0.5f * p.maxPlantedYawDegrees / math.max(0.01f, p.stepDurFast));
            float speedT = math.max(math.saturate(speed / p.fastSpeedRef), math.saturate(math.abs(sim.smoothedYawRateDeg) / fastYawRef));
            f.phase = 1;
            f.stepStartPos = f.currentPos;
            f.stepStartRot = f.currentRot;
            f.stepTimer = 0f;
            f.stepDur = math.lerp(p.stepDurSlow, p.stepDurFast, speedT);

            float3 tgt = Flat(f.predictedTargetXZ, Up);
            f.stepTargetPos = new float3(tgt.x, groundY + p.footHeightOffset, tgt.z);
            f.filteredNormal = Up;

            float3 rawR = math.cross(Up, sim.smoothedBodyFwd);
            rawR = math.lengthsq(rawR) < 0.001f ? new float3(1, 0, 0) : math.normalize(rawR);
            float3 stp = f.stepTargetPos, hGround = new float3(Flat(hips, Up).x, math.dot(stp, Up), Flat(hips, Up).z);
            EnforceSide(ref stp, hGround, rawR, f.sideSign, p.stanceWidth * p.stepTargetSideFraction);
            f.stepTargetPos = stp;
        }
        static void EnforceSide(ref float3 pos, float3 center, float3 bodyRight, int sideSign, float minDist)
        {
            float lateral = math.dot(pos - center, bodyRight);
            if (sideSign > 0 && lateral < sideSign * minDist) pos += bodyRight * (sideSign * minDist - lateral);
            else if (sideSign < 0 && lateral > -minDist) pos -= bodyRight * (lateral + minDist);
        }
        // ───────────────────────── synthetic rotation / crossover probe ─────────────────────────
        // Drive the REAL job through an in-place spin at omegaDps and measure the scissor. stick=false is a
        // PHYSICAL spin (playspace fixed, body yaws -- the hard case the anti-cross fix targets); stick=true is
        // a character-controller turn (root carries the yaw). No BVH -- uses BuildReferenceParams. Mirror of spin.py.
        public static BasisFootRotationResult RunSpin(in BasisFootSimParams p, float omegaDps, bool stick, float durSec = 6f, float dt = 1f / 90f)
        {
            int n = Mathf.Max(4, Mathf.RoundToInt(durSec / dt));
            float standH = p.hipToFoot + p.ankleHeight;
            float3 fwd0 = new float3(0, 0, 1), avatarRight0 = math.normalize(math.cross(Up, fwd0));
            float3 hips0 = new float3(0, standH, 0);

            var feet = new NativeArray<BasisFootNativeState>(2, Allocator.Persistent);
            var simState = new NativeArray<BasisFootSimState>(1, Allocator.Persistent);
            var input = new NativeArray<BasisFootSimInput>(1, Allocator.Persistent);
            var output = new NativeArray<BasisFootSimOutput>(1, Allocator.Persistent);
            var res = new BasisFootRotationResult { MinGapCm = float.MaxValue, CrossMaxCm = float.MinValue };
            try
            {
                feet[0] = InitFoot(p, -1, hips0, 0f, avatarRight0);
                feet[1] = InitFoot(p, +1, hips0, 0f, avatarRight0);
                simState[0] = new BasisFootSimState { prevHeadPos = hips0, smoothedBodyFwd = fwd0, smoothedBodyRight = avatarRight0, prevRootFwd = fwd0 };

                int warm = Mathf.Clamp(Mathf.RoundToInt(0.8f / dt), 0, n - 2);
                int crossed = 0, colliding = 0, both = 0, counted = 0;
                float prevFootYaw = 0f; bool prevPlanted = false; double footYawSum = 0, bodyYawSum = 0;
                for (int f = 0; f < n; f++)
                {
                    float th = math.radians(omegaDps * f * dt);
                    float3 fwd = new float3(math.sin(th), 0, math.cos(th));
                    quaternion rot = quaternion.AxisAngle(Up, th);
                    float3 av = stick ? fwd : fwd0, avr = math.normalize(math.cross(Up, av));
                    input[0] = new BasisFootSimInput
                    {
                        dt = dt, headPos = hips0, hipsPos = hips0, hipsRot = rot, chestRot = rot, headRot = rot,
                        avatarForward = av, avatarRight = avr, hasChest = true,
                        groundHit = true, groundPoint = float3.zero, splayWhenCrouched = 1f, playerUp = Up,
                    };
                    new BasisFootSimulateJob { p = p, feet = feet, simState = simState, input = input, output = output }.Execute();
                    var lf = feet[0]; if (lf.wantsStep) { FinalizeStepFlat(ref lf, simState[0], p, hips0, 0f); lf.wantsStep = false; feet[0] = lf; }
                    var rf = feet[1]; if (rf.wantsStep) { FinalizeStepFlat(ref rf, simState[0], p, hips0, 0f); rf.wantsStep = false; feet[1] = rf; }

                    // planted-foot yaw hold: how much the LEFT foot's own yaw follows the body while it is
                    // planted. Runs every frame (prev-tracking) but only accumulates after warm-up.
                    float3 lyfwd = math.mul(feet[0].currentRot, new float3(0, 0, 1));
                    float footYaw = Mathf.Atan2(lyfwd.x, lyfwd.z) * Mathf.Rad2Deg;
                    bool lPlanted = feet[0].phase == 0;
                    if (f >= warm && lPlanted && prevPlanted)
                    {
                        footYawSum += Mathf.Abs(Mathf.DeltaAngle(prevFootYaw, footYaw));
                        bodyYawSum += Mathf.Abs(omegaDps * dt);
                    }
                    prevFootYaw = footYaw; prevPlanted = lPlanted;

                    if (f < warm) continue;
                    float3 br = math.normalize(math.cross(Up, fwd)), d = feet[0].currentPos - feet[1].currentPos;
                    float crossSep = math.dot(d, br);        // >0 => left foot is on the right of the right foot
                    float gap = math.length(Flat(d, Up));
                    res.CrossMaxCm = math.max(res.CrossMaxCm, crossSep * 100f);
                    res.MinGapCm = math.min(res.MinGapCm, gap * 100f);
                    if (crossSep > 0.02f) crossed++;
                    if (gap < p.footLength) colliding++;
                    if (feet[0].phase == 1 && feet[1].phase == 1) both++;
                    counted++;
                }
                res.CrossedPct = counted > 0 ? 100f * crossed / counted : 0f;
                res.CollidePct = counted > 0 ? 100f * colliding / counted : 0f;
                res.BothSteppingTicks = both;
                res.PlantedYawFollowFrac = bodyYawSum > 1.0 ? (float)(footYawSum / bodyYawSum) : 0f;
            }
            finally { feet.Dispose(); simState.Dispose(); input.Dispose(); output.Dispose(); }
            return res;
        }
        public static float RunCrouchKneeSplay(in BasisFootSimParams p, float crouchFrac, float durSec = 1.5f, float dt = 1f / 90f)
        {
            int n = Mathf.Max(4, Mathf.RoundToInt(durSec / dt));
            float standH = p.hipToFoot + p.ankleHeight;
            float3 fwd = new float3(0, 0, 1), right = math.normalize(math.cross(Up, fwd));
            float3 hipsStand = new float3(0, standH, 0);

            var feet = new NativeArray<BasisFootNativeState>(2, Allocator.Persistent);
            var simState = new NativeArray<BasisFootSimState>(1, Allocator.Persistent);
            var input = new NativeArray<BasisFootSimInput>(1, Allocator.Persistent);
            var output = new NativeArray<BasisFootSimOutput>(1, Allocator.Persistent);
            try
            {
                feet[0] = InitFoot(p, -1, hipsStand, 0f, right);
                feet[1] = InitFoot(p, +1, hipsStand, 0f, right);
                simState[0] = new BasisFootSimState { prevHeadPos = hipsStand, smoothedBodyFwd = fwd, smoothedBodyRight = right, prevRootFwd = fwd };

                float3 hips = new float3(0, standH * crouchFrac, 0);
                for (int f = 0; f < n; f++)
                {
                    input[0] = new BasisFootSimInput
                    {
                        dt = dt, headPos = hips, hipsPos = hips, hipsRot = quaternion.identity,
                        chestRot = quaternion.identity, headRot = quaternion.identity,
                        avatarForward = fwd, avatarRight = right, hasChest = true,
                        groundHit = true, groundPoint = float3.zero, splayWhenCrouched = 1f, playerUp = Up,
                    };
                    new BasisFootSimulateJob { p = p, feet = feet, simState = simState, input = input, output = output }.Execute();
                }
                return math.dot(feet[1].kneeHint - feet[0].kneeHint, right);
            }
            finally { feet.Dispose(); simState.Dispose(); input.Dispose(); output.Dispose(); }
        }
        // A synthetic straight walk at dimensionless speed vhat = v / sqrt(g L) (walk ~0.2-0.45), returning the
        // same dimensionless gait metrics as the mocap path -- for scale-invariance, framerate-independence and
        // idle checks that need no BVH. No rotation, flat ground.
        public static BasisFootWalkResult RunWalk(in BasisFootSimParams p, float vhat, float durSec = 6f, float dt = 1f / 90f)
        {
            int n = Mathf.Max(8, Mathf.RoundToInt(durSec / dt));
            float avgLeg = 0.5f * (p.leftLegLen + p.rightLegLen), v = vhat * math.sqrt(G * avgLeg);
            float standH = p.hipToFoot + p.ankleHeight;
            float3 fwd = new float3(0, 0, 1), right = math.normalize(math.cross(Up, fwd));
            quaternion rot = quaternion.identity;

            var feet = new NativeArray<BasisFootNativeState>(2, Allocator.Persistent);
            var simState = new NativeArray<BasisFootSimState>(1, Allocator.Persistent);
            var input = new NativeArray<BasisFootSimInput>(1, Allocator.Persistent);
            var output = new NativeArray<BasisFootSimOutput>(1, Allocator.Persistent);
            var trackL = new float3[n]; var trackR = new float3[n];
            var plL = new bool[n]; var plR = new bool[n];
            try
            {
                float3 hips0 = new float3(0, standH, 0);
                feet[0] = InitFoot(p, -1, hips0, 0f, right);
                feet[1] = InitFoot(p, +1, hips0, 0f, right);
                simState[0] = new BasisFootSimState { prevHeadPos = hips0, smoothedBodyFwd = fwd, smoothedBodyRight = right, prevRootFwd = fwd };
                for (int f = 0; f < n; f++)
                {
                    float3 hips = new float3(0, standH, v * f * dt);
                    input[0] = new BasisFootSimInput
                    {
                        dt = dt, headPos = hips, hipsPos = hips, hipsRot = rot, chestRot = rot, headRot = rot,
                        avatarForward = fwd, avatarRight = right, hasChest = true,
                        groundHit = true, groundPoint = new float3(hips.x, 0, hips.z), splayWhenCrouched = 1f, playerUp = Up,
                    };
                    new BasisFootSimulateJob { p = p, feet = feet, simState = simState, input = input, output = output }.Execute();
                    var lf = feet[0]; if (lf.wantsStep) { FinalizeStepFlat(ref lf, simState[0], p, hips, 0f); lf.wantsStep = false; feet[0] = lf; }
                    var rf = feet[1]; if (rf.wantsStep) { FinalizeStepFlat(ref rf, simState[0], p, hips, 0f); rf.wantsStep = false; feet[1] = rf; }
                    trackL[f] = feet[0].currentPos; trackR[f] = feet[1].currentPos;
                    plL[f] = feet[0].phase == 0; plR[f] = feet[1].phase == 0;
                }

                GaitStats(trackL, plL, avgLeg, dt, out float dutyL, out float clrL, out float slL, out int stepsL);
                GaitStats(trackR, plR, avgLeg, dt, out float dutyR, out float clrR, out float slR, out int stepsR);
                int warm = Mathf.Clamp(Mathf.RoundToInt(0.8f / dt), 0, n - 2);
                float restReach = math.sqrt(p.stanceWidth * p.stanceWidth * 0.25f + p.hipToFoot * p.hipToFoot);
                var ext = new System.Collections.Generic.List<float>(2 * (n - warm));
                for (int f = warm; f < n; f++)
                {
                    float3 hp = new float3(0, standH, v * f * dt);
                    ext.Add(math.length(trackL[f] - hp) / restReach); ext.Add(math.length(trackR[f] - hp) / restReach);
                }
                return new BasisFootWalkResult
                {
                    Duty = 0.5f * (dutyL + dutyR),
                    DoubleSupport = BothFrac(plL, plR),
                    ClearFrac = Median2(clrL, clrR),
                    CadenceHat = (stepsL + stepsR) / (n * dt) * math.sqrt(avgLeg / G),
                    ExtP95 = Percentile(ext, 95),
                    SlideMm = 0.5f * (PlantedSlideMm(trackL, plL) + PlantedSlideMm(trackR, plR)),
                    Steps = stepsL + stepsR,
                };
            }
            finally { feet.Dispose(); simState.Dispose(); input.Dispose(); output.Dispose(); }
        }
        // ───────────────────────── metrics ─────────────────────────
        static float3[] Track(BasisMotionClip clip, BasisMocapJoint j)
        {
            var t = new float3[clip.FrameCount];
            for (int f = 0; f < clip.FrameCount; f++) t[f] = clip.Get(f, j).Position;
            return t;
        }
        // Real-foot contact = LOW and horizontally SLOW, against a PER-FOOT robust floor + hysteresis.
        static bool[] Contacts(BasisMotionClip clip, BasisMocapJoint foot, BasisMocapJoint toe, float L, float dt)
        {
            int n = clip.FrameCount;
            var mn = new float[n]; var lows = new System.Collections.Generic.List<float>(n);
            var fh = new float3[n];
            for (int f = 0; f < n; f++)
            {
                float fy = clip.Get(f, foot).Position.y, ty = clip.Get(f, toe).Position.y;
                mn[f] = math.min(fy, ty); lows.Add(mn[f]);
                float3 fp = clip.Get(f, foot).Position; fp.y = 0; fh[f] = fp;
            }
            float floor = Percentile(lows, 3);
            var raw = new bool[n];
            float vT = 0.25f * math.sqrt(G * L), hT = 0.08f * L;
            for (int f = 0; f < n; f++)
            {
                float v;
                if (n > 2) { int a = math.max(f - 1, 0), b = math.min(f + 1, n - 1); v = math.length(fh[b] - fh[a]) / ((b - a) * dt); }
                else v = 0f;
                raw[f] = (mn[f] - floor < hT) && (v < vT);
            }
            return Hysteresis(raw, math.max(2, Mathf.RoundToInt(0.03f / dt)));
        }
        static bool[] Hysteresis(bool[] raw, int minRun)
        {
            var o = (bool[])raw.Clone(); int n = raw.Length, i = 0;
            while (i < n)
            {
                int j = i; while (j < n && o[j] == o[i]) j++;
                if ((j - i) < minRun && i > 0) { for (int k = i; k < j; k++) o[k] = o[i - 1]; }
                i = j;
            }
            return o;
        }
        static void GaitStats(float3[] foot, bool[] planted, float L, float dt, out float duty, out float clearMed, out float stepLenMed, out int steps)
        {
            int n = foot.Length; int on = 0; for (int f = 0; f < n; f++) if (planted[f]) on++;
            duty = (float)on / n;
            var clears = new System.Collections.Generic.List<float>();
            var strides = new System.Collections.Generic.List<float>();
            int f2 = 1;
            while (f2 < n)
            {
                if (!planted[f2] && planted[f2 - 1])
                {
                    int s = f2; while (f2 < n && !planted[f2]) f2++;
                    if (f2 >= n) break;
                    if (f2 - s >= 2)
                    {
                        float3 a = foot[s - 1], b = foot[f2];
                        strides.Add(math.length(Flat(b - a, Up)) / L);
                        float top = math.min(a.y, b.y);
                        float lift = 0f; for (int k = s; k < f2; k++) lift = math.max(lift, foot[k].y - top);
                        clears.Add(lift / L);
                    }
                }
                else f2++;
            }
            steps = strides.Count;
            clearMed = Median(clears); stepLenMed = Median(strides);
        }
        static float BothFrac(bool[] a, bool[] b)
        {
            int both = 0; for (int i = 0; i < a.Length; i++) if (a[i] && b[i]) both++;
            return (float)both / a.Length;
        }
        static float PlantedSlideMm(float3[] foot, bool[] planted)
        {
            var d = new System.Collections.Generic.List<float>();
            for (int i = 1; i < foot.Length; i++)
                if (planted[i] && planted[i - 1]) d.Add(math.length(Flat(foot[i] - foot[i - 1], Up)) * 1000f);
            return Median(d);
        }
        static float LateralSign(BasisMotionClip clip, float3[] foot, int warm)
        {
            double s = 0; int c = 0;
            for (int f = warm; f < clip.FrameCount; f++)
            {
                Quaternion hips = clip.Get(f, BasisMocapJoint.Hips).Rotation;
                float3 fwd = math.normalizesafe(Flat(hips * Vector3.forward, Up), new float3(0, 0, 1));
                float3 rt = math.normalizesafe(math.cross(Up, fwd), new float3(1, 0, 0));
                float3 d = foot[f] - (float3)clip.Get(f, BasisMocapJoint.Hips).Position;
                s += math.dot(d, rt); c++;
            }
            return c > 0 ? (float)(s / c) : 0f;
        }
        // ───────────────────────── helpers ─────────────────────────
        static float3 Flat(float3 v, float3 up) => v - up * math.dot(v, up);
        static float Median2(float a, float b)
        {
            if (float.IsNaN(a)) return b; if (float.IsNaN(b)) return a; return 0.5f * (a + b);
        }
        static float Median(System.Collections.Generic.List<float> x)
        {
            if (x.Count == 0) return float.NaN;
            x.Sort(); return x[x.Count / 2];
        }
        static float Percentile(System.Collections.Generic.List<float> x, int pct)
        {
            if (x.Count == 0) return 0f;
            var s = new System.Collections.Generic.List<float>(x); s.Sort();
            int idx = Mathf.Clamp(Mathf.RoundToInt((pct / 100f) * (s.Count - 1)), 0, s.Count - 1);
            return s[idx];
        }
    }
}
