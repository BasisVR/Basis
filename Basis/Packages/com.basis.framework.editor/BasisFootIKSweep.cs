using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Advanced offline simulation of the procedural foot-placement system (BasisFootSimulateJob + the
    // driver's FinalizeStep). The system is TEMPORAL -- a plant/step state machine driven by head+hips
    // motion -- so this doesn't grid a static target: it ticks the REAL job through a large battery of
    // scripted locomotion (walk/strafe/turn at many rates, crouch, jump, look up/down, slopes, stairs,
    // gaps) on synthetic ground and records, per frame, every foot-IK quantity and failure mode. The job
    // is run directly (Execute(), the managed path), so this exercises the exact shipping math; only
    // FinalizeStep / InitFoot / DeriveStepParameters are mirrored here (they SphereCast real colliders,
    // which the analytic ground replaces). Output: a wide per-tick CSV + a human-readable .report.txt.

    public enum BasisFootGroundKind { Flat, Slope, Steps, Gap }

    public struct BasisFootIKSweepConfig
    {
        // ── Nominal avatar (T-pose measurements the driver would derive) ──
        public float StanceWidth;
        public float HipToFoot;
        public float HeadAboveHips;   // head bone height above hips (drives velocity-from-head only)
        public float ThighLen, ShinLen;   // per leg (symmetric here)
        public float FootLength;
        public float AnkleHeight;
        public float UpperLegToFootVertical;

        // ── Tunables (mirror BasisLocalFootDriver inspector defaults) ──
        public float PredictionFactor, VelocityBiasFactor, LeadOffsetFactor, MaxVelocityOffsetFraction, MaxPredictionFraction;
        public float PlantedLerpSpeed, RotationLerpSpeed, VelocitySmoothAccel, VelocitySmoothDecel, BodyFwdRateMoving, BodyFwdRateStationary, KneeHintLerpSpeed;
        public float MaxFootTiltDegrees, MaxFootYawDegrees;
        public float RaySphereRadiusMul, FootHeightOffsetMul, StepTriggerMul, StrideScaleMul, StepHeightMul, StepDurSlowMul, StepDurFastMul, FastSpeedMul;
        public float StepArcLiftExp, StepArcDropExp, StepHeightMinFraction, StepHeightStrideRefFraction;
        public float IdleSpeedThreshold, IdleBoostFraction, MaxPlantedYawDegrees;
        public float IdealSideEnforceFraction, StepTargetSideFraction, FootSideEnforceFraction;
        public float MaxVerticalDriftFraction;
        public float KneeForwardPushFraction, KneeMinSideFraction;
        public float BodyFwdHipsWeight, BodyFwdChestWeight, BodyFwdHeadWeight;
        public float HipBobFraction;

        // ── Run options ──
        public float Dt;            // fixed timestep (s)
        public float Duration;      // seconds per scenario
        public float SlowSpeed, NormalSpeed, FastSpeed;   // locomotion speeds (m/s)
        public float TrackerNoise;  // per-axis head jitter (m) added to test jitter rejection
        public float Scale;         // avatar scale this config represents (measurements + speeds already multiplied by it)
        public float[] Scales;      // scales to run the full battery at -- the rescaling test (child → giant)

        public static BasisFootIKSweepConfig Default()
        {
            return new BasisFootIKSweepConfig
            {
                StanceWidth = 0.20f,
                HipToFoot = 0.92f,
                HeadAboveHips = 0.55f,
                ThighLen = 0.45f,
                ShinLen = 0.42f,
                FootLength = 0.13f,
                AnkleHeight = 0.08f,
                UpperLegToFootVertical = 0.84f,

                PredictionFactor = 0.9f,
                VelocityBiasFactor = 0.18f,
                LeadOffsetFactor = 0.6f,
                MaxVelocityOffsetFraction = 0.35f,
                MaxPredictionFraction = 0.35f,
                PlantedLerpSpeed = 40f,
                RotationLerpSpeed = 16f,
                VelocitySmoothAccel = 10f,
                VelocitySmoothDecel = 50f,
                BodyFwdRateMoving = 6f,
                BodyFwdRateStationary = 2.5f,
                KneeHintLerpSpeed = 10f,
                MaxFootTiltDegrees = 35f,
                MaxFootYawDegrees = 18f,
                RaySphereRadiusMul = 0.3f,
                FootHeightOffsetMul = 0.2f,
                StepTriggerMul = 0.18f,   // keep in lock-step with BasisLocalFootDriver (foot-vs-real tune 2026-07-18)
                StrideScaleMul = 0.15f,
                StepHeightMul = 0.30f,
                StepDurSlowMul = 0.30f,
                StepDurFastMul = 0.18f,
                FastSpeedMul = 1.2f,
                StepArcLiftExp = 0.6f,
                StepArcDropExp = 1.4f,
                StepHeightMinFraction = 0.4f,
                StepHeightStrideRefFraction = 0.45f,
                IdleSpeedThreshold = 0.05f,
                IdleBoostFraction = 0.5f,
                MaxPlantedYawDegrees = 20f,
                IdealSideEnforceFraction = 0.3f,
                StepTargetSideFraction = 0.15f,
                FootSideEnforceFraction = 0.2f,
                MaxVerticalDriftFraction = 0.25f,
                KneeForwardPushFraction = 0.4f,
                KneeMinSideFraction = 0.05f,
                BodyFwdHipsWeight = 3f,
                BodyFwdChestWeight = 2f,
                BodyFwdHeadWeight = 1f,
                HipBobFraction = 0.02f,

                Dt = 1f / 90f,
                Duration = 6f,
                SlowSpeed = 0.6f,
                NormalSpeed = 1.3f,
                FastSpeed = 2.6f,
                TrackerNoise = 0f,
                Scale = 1f,
                Scales = new[] { 0.5f, 1f, 2f },
            };
        }
    }

    // A tracked maximum together with WHERE it happened, so a bad number points straight at the frame.
    public struct BasisFootPeak
    {
        public float Value, Time, Speed;
        public void Consider(float v, float time, float speed) { if (v > Value) { Value = v; Time = time; Speed = speed; } }
    }

    // Per-scenario diagnostics: motion range, stepping stats, trigger breakdown, and every failure-mode
    // metric tracked as a peak (value + time/speed where it occurred). Report is the human-readable block.
    public struct BasisFootScenarioResult
    {
        public string Name, Group;
        public BasisFootGroundKind Ground;
        public int Ticks;
        public float Dt;

        public float SpeedMin, SpeedMax, SpeedSum;
        public float HipUpMin, HipUpMax;   // vertical hip travel (crouch/jump range)

        public int Steps, LeftSteps, RightSteps;
        public int DistTriggers, YawTriggers;
        public float StrideSum; public int StrideN;
        public float StepDurSum, StepDurMin, StepDurMax; public int StepDurN;
        public int AirborneTicks;
        public int SimAirborneTicks;   // ticks the JOB declared airborne (ground out of reach)
        public int BothSteppingTicks, BothEpisodes; public float BothFirstTime;

        public BasisFootPeak SlideMm, ExtRatio, PenMm, HoverMm, TiltDeg, YawDeg, PlantDriftM, CrossSepM, StepLiftMm;
        public int CrossoverTicks, CrossEpisodes, CrossoverTicksFastRot;
        public float MinKneeForwardM, MinKneeForwardTime;
        public float StanceMin, StanceMax;
        public bool HadNaN;

        public string Report;
    }

    public struct BasisFootIKSweepSummary
    {
        public bool Ok;
        public string Path;
        public string ReportPath;
        public int Rows;
        public int Scenarios;
        public BasisFootScenarioResult[] Results;

        // Worst across every scenario (what the gate reads).
        public float WorstPlantedSlideMm;
        public float WorstExtensionRatio;
        public float WorstPenetrationMm;
        public float WorstPlantedHoverMm;
        // Ticks the job declared AIRBORNE in a scenario whose hips never left standing height. Must be 0:
        // an airborne avatar stops using the floor and rides a hips-relative fallback, so a false airborne
        // silently lifts the whole player off the ground -- AND suppresses the hover metric that would have
        // caught it. This exists because exactly that happened (legLen-based reach test; hipToFoot > legLen).
        public int SimAirborneWhileGroundedTicks;
        public string SimAirborneWorstScenario;
        public int Crossovers, CrossoversTolerated;
        public int BothSteppingTicks;
        public string BothSteppingWorstScenario;
        public int BothSteppingWorstTicks;
        public float WorstTiltDeg;
        public float WorstYawDeg;
        public float WorstPlantToIdealM;
        public float WorstKneeBackwardM;   // -min(MinKneeForwardM): positive = knee went behind by this much
        public float WorstStepLiftMm;      // peak foot lift above the straight start->target line during a step
        public int TotalSteps;
        public int IdleSteps;              // steps fired during the idle group -- a gentle standing sway must not micro-step
        public float IdleKneeGainFwd, IdleKneeGainVert;   // solved-knee travel / body travel over a gentle idle sway (foot driver + leg solver)
        public bool HadNaN;

        // Echoed config so the gate can compare against the clamp limits used.
        public float ConfiguredMaxTiltDeg;
        public float ConfiguredMaxYawDeg;
        public float AvgLegLen;

        public string Error;
    }

    public static class BasisFootIKSweep
    {
        public const string DefaultFileName = "BasisFootIKSweep.csv";
        static readonly float3 Up = new float3(0f, 1f, 0f);

        public static string DefaultPath() => Path.Combine(Application.persistentDataPath, DefaultFileName);

        // A scripted body sample at time t: root position on the ground plane, body facing, head-only yaw
        // (look-around) and pitch (look up/down), a crouch amount (lowers hips) and an extra vertical offset
        // (jump / bob). Velocity is derived by the job from the head position delta, exactly as it ships.
        struct BodySample { public float3 RootXZ; public float FacingDeg, HeadYawDeg, HeadPitchDeg, Crouch, BodyUp; }

        struct Scenario
        {
            public string Name, Group;
            public BasisFootGroundKind Ground;
            public float SlopeDeg, StepRun, StepRise, GapStart, GapEnd;
            public float Duration;
            public System.Func<float, BodySample> Sample;
        }

        public static BasisFootIKSweepSummary Run(BasisFootIKSweepConfig cfg, string path)
        {
            var summary = new BasisFootIKSweepSummary { Ok = false, Path = path };
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                float[] scales = (cfg.Scales != null && cfg.Scales.Length > 0) ? cfg.Scales : new[] { 1f };
                var resultsList = new System.Collections.Generic.List<BasisFootScenarioResult>();
                int rows = 0;

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisFootIKSweep " + System.DateTime.UtcNow.ToString("o") +
                                " dt=" + F(cfg.Dt) + " noise=" + F(cfg.TrackerNoise) + " scales=" + string.Join("/", System.Array.ConvertAll(scales, x => x.ToString("0.##"))));
                    w.WriteLine(CsvHeader());
                    foreach (float scale in scales)
                    {
                        var cfgS = ScaleConfig(cfg, scale);
                        var scenarios = BuildScenarios(cfgS);
                        for (int i = 0; i < scenarios.Length; i++)
                        {
                            var sc = scenarios[i];
                            if (scale != 1f) { sc.Name += "@" + scale.ToString("0.##") + "x"; sc.Group += " x" + scale.ToString("0.##"); }
                            resultsList.Add(RunOne(cfgS, sc, w, ref rows));
                        }
                    }
                }

                var results = resultsList.ToArray();
                summary.Ok = true;
                summary.Path = path;
                summary.Rows = rows;
                summary.Scenarios = results.Length;
                summary.Results = results;
                summary.ConfiguredMaxTiltDeg = cfg.MaxFootTiltDegrees;
                summary.ConfiguredMaxYawDeg = cfg.MaxFootYawDegrees;
                summary.AvgLegLen = cfg.ThighLen + cfg.ShinLen;

                // End-to-end idle check: sway the hip through the real foot driver + leg solver and record
                // how far the SOLVED knee travels per unit of body sway (reported, not gated -- horizontal
                // sway is naturally ~0.5x; a vertical bob near full extension is where it amplifies).
                try
                {
                    summary.IdleKneeGainFwd = IdleKneeGain(cfg, t => new BodySample { RootXZ = Forward(0f) * (0.04f * Mathf.Sin(t * 1.1f)) });
                    summary.IdleKneeGainVert = IdleKneeGain(cfg, t => new BodySample { BodyUp = 0.02f * Mathf.Sin(t * 1.3f) });
                }
                catch { }

                for (int i = 0; i < results.Length; i++)
                {
                    var r = results[i];
                    summary.WorstPlantedSlideMm = Mathf.Max(summary.WorstPlantedSlideMm, r.SlideMm.Value);
                    summary.WorstExtensionRatio = Mathf.Max(summary.WorstExtensionRatio, r.ExtRatio.Value);
                    summary.WorstPenetrationMm = Mathf.Max(summary.WorstPenetrationMm, r.PenMm.Value);
                    summary.WorstPlantedHoverMm = Mathf.Max(summary.WorstPlantedHoverMm, r.HoverMm.Value);
                    // Hips never left standing height in this scenario => the sim must never call it airborne.
                    //
                    // Gap is excluded, and not as a convenience: the check's premise is "the hips did not rise, so
                    // there is ground under them and the feet must be using it". Over a GAP there is no ground to
                    // use -- the ray genuinely misses and `airborne` is the correct, intended answer. Counting it
                    // as a false airborne would make the gate cry wolf on the one scenario built to exercise the
                    // real airborne path, and a gate that fires when nothing is wrong gets ignored when something is.
                    if (r.Ground != BasisFootGroundKind.Gap && r.HipUpMax - r.HipUpMin < 0.02f && r.SimAirborneTicks > 0)
                    {
                        summary.SimAirborneWhileGroundedTicks += r.SimAirborneTicks;
                        if (string.IsNullOrEmpty(summary.SimAirborneWorstScenario)) summary.SimAirborneWorstScenario = r.Name;
                    }
                    summary.Crossovers += r.CrossoverTicks;
                    summary.CrossoversTolerated += r.CrossoverTicksFastRot;
                    summary.BothSteppingTicks += r.BothSteppingTicks;
                    if (r.BothSteppingTicks > summary.BothSteppingWorstTicks) { summary.BothSteppingWorstTicks = r.BothSteppingTicks; summary.BothSteppingWorstScenario = r.Name; }
                    summary.WorstTiltDeg = Mathf.Max(summary.WorstTiltDeg, r.TiltDeg.Value);
                    summary.WorstYawDeg = Mathf.Max(summary.WorstYawDeg, r.YawDeg.Value);
                    summary.WorstPlantToIdealM = Mathf.Max(summary.WorstPlantToIdealM, r.PlantDriftM.Value);
                    summary.WorstKneeBackwardM = Mathf.Max(summary.WorstKneeBackwardM, -r.MinKneeForwardM);
                    summary.WorstStepLiftMm = Mathf.Max(summary.WorstStepLiftMm, r.StepLiftMm.Value);
                    summary.TotalSteps += r.Steps;
                    if (r.Group != null && r.Group.StartsWith("idle")) summary.IdleSteps += r.Steps;
                    summary.HadNaN |= r.HadNaN;
                }

                Debug.Log($"[Foot idle] idleSteps={summary.IdleSteps} idleKneeGain fwd={summary.IdleKneeGainFwd:F2} vert={summary.IdleKneeGainVert:F2} (non-tolerated crossover {summary.Crossovers - summary.CrossoversTolerated})");

                summary.ReportPath = Path.ChangeExtension(path, null) + ".report.txt";
                WriteReport(summary, results);
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }
            return summary;
        }

        // ── One scenario: tick the real job over the scripted motion, finalize steps with the analytic
        //    ground, and accumulate per-frame diagnostics + the failure-mode peaks. ──
        static BasisFootScenarioResult RunOne(BasisFootIKSweepConfig cfg, Scenario sc, StreamWriter w, ref int rows)
        {
            var res = new BasisFootScenarioResult { Name = sc.Name, Group = sc.Group, Ground = sc.Ground };
            BasisFootSimParams p = BuildParams(cfg);
            float dt = Mathf.Max(1e-4f, cfg.Dt);
            res.Dt = dt;
            int n = Mathf.Max(2, Mathf.RoundToInt(sc.Duration / dt));

            var feet = new NativeArray<BasisFootNativeState>(2, Allocator.Persistent);
            var simState = new NativeArray<BasisFootSimState>(1, Allocator.Persistent);
            var input = new NativeArray<BasisFootSimInput>(1, Allocator.Persistent);
            var output = new NativeArray<BasisFootSimOutput>(1, Allocator.Persistent);
            var rng = new System.Random(91 + sc.Name.GetHashCode());

            try
            {
                BodySample s0 = sc.Sample(0f);
                Pose(cfg, sc, s0, out float3 hips0, out float3 head0, out float3 fwd0, out float3 bodyRight0);

                var left = InitFoot(cfg, sc, p, -1, hips0, fwd0, bodyRight0);
                var right = InitFoot(cfg, sc, p, +1, hips0, fwd0, bodyRight0);
                feet[0] = left; feet[1] = right;
                simState[0] = new BasisFootSimState
                {
                    prevHeadPos = head0,
                    prevHeadYaw = Yaw(fwd0),
                    smoothedVelocity = float3.zero,
                    smoothedBodyFwd = fwd0,
                    smoothedBodyRight = bodyRight0,
                };

                float3 prevL = feet[0].currentPos, prevR = feet[1].currentPos;
                bool prevLP = true, prevRP = true, prevBoth = false, prevCross = false;
                res.MinKneeForwardM = float.PositiveInfinity;
                res.SpeedMin = res.StepDurMin = res.StanceMin = res.HipUpMin = float.PositiveInfinity;

                var sb = new StringBuilder(640);

                for (int tick = 0; tick < n; tick++)
                {
                    float t = tick * dt;
                    BodySample s = sc.Sample(t);
                    Pose(cfg, sc, s, out float3 hips, out float3 head, out float3 fwd, out float3 bodyRight);
                    if (cfg.TrackerNoise > 0f)
                        head += new float3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)) * cfg.TrackerNoise;

                    quaternion yawQ = quaternion.AxisAngle(Up, math.radians(s.FacingDeg + s.HeadYawDeg));
                    float3 pitchAxis = math.mul(yawQ, new float3(1, 0, 0));
                    quaternion headRot = math.mul(quaternion.AxisAngle(pitchAxis, math.radians(-s.HeadPitchDeg)), yawQ);
                    quaternion facing = quaternion.AxisAngle(Up, math.radians(s.FacingDeg));
                    bool groundHit = GroundRaycast(sc, hips, out float3 gp, out _);

                    input[0] = new BasisFootSimInput
                    {
                        dt = dt,
                        headPos = head,
                        hipsPos = hips,
                        hipsRot = facing,
                        chestRot = facing,
                        headRot = headRot,
                        avatarForward = fwd,
                        avatarRight = bodyRight,
                        hasChest = true,
                        groundHit = groundHit,
                        groundPoint = groundHit ? gp : float3.zero,
                        splayWhenCrouched = 1f,
                        playerUp = Up,
                    };

                    var job = new BasisFootSimulateJob { p = p, feet = feet, simState = simState, input = input, output = output };
                    job.Execute();

                    // The job flags wantsStep; capture it before FinalizeStep clears it (= "a step started this tick").
                    bool lWanted = feet[0].wantsStep, rWanted = feet[1].wantsStep;

                    // Mirror CompleteSimulate: the driver places the step target on the ground.
                    var lf = feet[0];
                    if (lf.wantsStep) { FinalizeStep(ref lf, simState[0], p, sc, hips); lf.wantsStep = false; feet[0] = lf; }
                    var rf = feet[1];
                    if (rf.wantsStep) { FinalizeStep(ref rf, simState[0], p, sc, hips); rf.wantsStep = false; feet[1] = rf; }

                    left = feet[0]; right = feet[1];
                    bool lp = left.phase == 0, rp = right.phase == 0;

                    float3 velFlat = ProjectFlat(simState[0].smoothedVelocity);
                    float speed = math.length(velFlat);
                    res.SpeedSum += speed; res.SpeedMin = Mathf.Min(res.SpeedMin, speed); res.SpeedMax = Mathf.Max(res.SpeedMax, speed);
                    float hipUp = math.dot(hips, Up); res.HipUpMin = Mathf.Min(res.HipUpMin, hipUp); res.HipUpMax = Mathf.Max(res.HipUpMax, hipUp);

                    // Step starts this tick -> classify the trigger (dist vs yaw) + record stride / duration.
                    int lTrig = 0, rTrig = 0;
                    if (lWanted) { lTrig = Classify(left, p, speed); RecordStart(ref res, lTrig, HDist(left.stepStartPos, left.stepTargetPos), left.stepDur); }
                    if (rWanted) { rTrig = Classify(right, p, speed); RecordStart(ref res, rTrig, HDist(right.stepStartPos, right.stepTargetPos), right.stepDur); }

                    // Step completions (stepping -> planted).
                    if (lp && !prevLP) { res.Steps++; res.LeftSteps++; }
                    if (rp && !prevRP) { res.Steps++; res.RightSteps++; }

                    bool both = !lp && !rp;
                    if (!lp || !rp) res.AirborneTicks++;
                    if (both) { res.BothSteppingTicks++; if (!prevBoth) { res.BothEpisodes++; if (res.BothEpisodes == 1) res.BothFirstTime = t; } }

                    float3 hipsGround = ProjectFlat(hips) + Up * math.dot(GroundPoint(sc, hips), Up);
                    var lm = Measure(p, sc, left, hips, hipsGround, fwd, bodyRight, prevL, prevLP);
                    var rm = Measure(p, sc, right, hips, hipsGround, fwd, bodyRight, prevR, prevRP);

                    // Crossover (feet tangled): the left foot ended up on the +right side of the right foot.
                    float crossSep = math.dot(left.currentPos - right.currentPos, bodyRight);
                    bool cross = crossSep > 0.02f;
                    if (cross)
                    {
                        res.CrossoverTicks++; res.CrossSepM.Consider(crossSep, t, speed); if (!prevCross) res.CrossEpisodes++;
                        // Discrete stepping (no flight phase) can't avoid a brief scissor when the foot's ideal is
                        // swept faster than a step can cover -- by a fast spin (>120deg/s) or by translation+rotation
                        // combined (turn-while-strafe). Tolerate both; idealSweep is the foot-target speed, Froude-scaled by the gait.
                        float idealSweep = speed + math.abs(math.radians(simState[0].smoothedYawRateDeg)) * (p.stanceWidth * 0.5f);
                        if (math.abs(simState[0].smoothedYawRateDeg) > 120f || idealSweep > 0.3f * p.fastSpeedRef) res.CrossoverTicksFastRot++;
                    }

                    // Accumulate the failure-mode peaks (planted-only where the swing would false-trigger).
                    if (lm.slideValid) res.SlideMm.Consider(lm.slideMm, t, speed);
                    if (rm.slideValid) res.SlideMm.Consider(rm.slideMm, t, speed);
                    res.ExtRatio.Consider(lm.ext, t, speed); res.ExtRatio.Consider(rm.ext, t, speed);
                    // Hover means "a planted foot is floating above the floor it is supposed to be standing on".
                    // While the sim is AIRBORNE that is not a defect, it is the point: the ground is out of leg
                    // reach (a jump), so the feet ride the hips and hang below the body instead of staying welded
                    // to a floor they can no longer touch. Measuring hover there just reports the jump height.
                    // Read the flag the job itself set rather than re-deriving it here -- a mirrored copy of that
                    // rule is exactly what let the last two foot bugs hide from this sweep.
                    bool simAirborne = output[0].airborne;
                    if (simAirborne) res.SimAirborneTicks++;
                    if (lp) { res.PenMm.Consider(lm.penMm, t, speed); if (!simAirborne) res.HoverMm.Consider(lm.hoverMm, t, speed); }
                    if (rp) { res.PenMm.Consider(rm.penMm, t, speed); if (!simAirborne) res.HoverMm.Consider(rm.hoverMm, t, speed); }
                    // Tilt is the SLOPE clamp: "a foot standing on a surface must not tilt more than maxFootTiltDegrees
                    // to conform to it". That is a statement about a foot ON THE GROUND, so measure it planted-only.
                    // A SWINGING foot is deliberately pitched by the ankle (plantarflexed at toe-off, dorsiflexed for
                    // heel-strike, up to k_ToeOffDeg); scoring that against the slope clamp would flag the heel-strike
                    // pose as "slope clamp not holding", which it plainly is not.
                    //
                    // This does NOT weaken the gate: plantedRot is built with zero ankle pitch, and a planted foot's
                    // currentRot slerps to it, so every rotation the slope clamp actually governs is still measured.
                    if (lp) res.TiltDeg.Consider(lm.tilt, t, speed);
                    if (rp) res.TiltDeg.Consider(rm.tilt, t, speed);
                    if (lm.yawClamp) res.YawDeg.Consider(lm.yaw, t, speed);
                    if (rm.yawClamp) res.YawDeg.Consider(rm.yaw, t, speed);
                    float lDrift = HDist(left.plantedPos, left.idealPos), rDrift = HDist(right.plantedPos, right.idealPos);
                    res.PlantDriftM.Consider(lDrift, t, speed); res.PlantDriftM.Consider(rDrift, t, speed);
                    float kneeFwd = Mathf.Min(lm.kneeFwd, rm.kneeFwd);
                    if (kneeFwd < res.MinKneeForwardM) { res.MinKneeForwardM = kneeFwd; res.MinKneeForwardTime = t; }
                    float stance = HDist(left.currentPos, right.currentPos);
                    res.StanceMin = Mathf.Min(res.StanceMin, stance); res.StanceMax = Mathf.Max(res.StanceMax, stance);

                    // Step lift: vertical excursion of the stepping foot above the straight start->target line.
                    if (left.phase == 1 && left.stepDur > 0f) res.StepLiftMm.Consider(StepLift(left) * 1000f, t, speed);
                    if (right.phase == 1 && right.stepDur > 0f) res.StepLiftMm.Consider(StepLift(right) * 1000f, t, speed);

                    if (NaN(left.currentPos) || NaN(right.currentPos) || NaN(left.kneeHint) || NaN(right.kneeHint)) res.HadNaN = true;

                    // ── Wide per-tick CSV row ──
                    sb.Clear();
                    sb.Append(sc.Name).Append(',').Append(tick).Append(',');
                    Append(sb, t); Append(sb, speed); Append(sb, velFlat.x); Append(sb, velFlat.z);
                    float3 bf = ProjectFlat(simState[0].smoothedBodyFwd);
                    Append(sb, bf.x); Append(sb, bf.z);
                    sb.Append((int)sc.Ground).Append(',').Append(groundHit ? '1' : '0').Append(',');
                    Append(sb, hips.x); Append(sb, hips.y); Append(sb, hips.z);
                    Append(sb, output[0].hipBob);
                    WriteFoot(sb, left, lm, lWanted, lTrig, lDrift);
                    WriteFoot(sb, right, rm, rWanted, rTrig, rDrift);
                    Append(sb, stance);
                    sb.Append(both ? '1' : '0').Append(',').Append(cross ? '1' : '0');
                    w.WriteLine(sb.ToString());
                    rows++;

                    prevL = left.currentPos; prevR = right.currentPos; prevLP = lp; prevRP = rp; prevBoth = both; prevCross = cross;
                }

                res.Ticks = n;
                res.Report = BuildReport(res);
            }
            finally
            {
                feet.Dispose(); simState.Dispose(); input.Dispose(); output.Dispose();
            }
            return res;
        }

        struct FootMetric { public float slideMm, lateral, ext, penMm, hoverMm, tilt, yaw, kneeFwd; public bool slideValid, yawClamp; }

        static FootMetric Measure(in BasisFootSimParams p, in Scenario sc, in BasisFootNativeState f,
            float3 hips, float3 hipsGround, float3 fwd, float3 bodyRight, float3 prevPos, bool prevPlanted)
        {
            var m = new FootMetric();
            bool planted = f.phase == 0;

            // Skating: a planted foot must be world-locked; horizontal move between two planted ticks is slide.
            if (planted && prevPlanted) { m.slideMm = HDist(f.currentPos, prevPos) * 1000f; m.slideValid = true; }

            m.lateral = math.dot(f.currentPos - hipsGround, bodyRight);

            // Over-extension vs the natural standing reach (Hips bone sits above the upper-leg joint, so a
            // straight standing leg already reads hipToFoot > legLen; a foot left behind blows past this).
            float restDist = Mathf.Sqrt(p.stanceWidth * p.stanceWidth * 0.25f + p.hipToFoot * p.hipToFoot);
            m.ext = math.length(f.currentPos - hips) / Mathf.Max(1e-4f, restDist);

            // Ground penetration / planted hover (penetration only meaningful for a planted foot).
            float footUp = math.dot(f.currentPos, Up);
            if (GroundRaycast(sc, f.currentPos, out float3 gpt, out _))
            {
                float groundUp = math.dot(gpt, Up);
                float pen = groundUp - footUp;
                if (pen > 0f) m.penMm = pen * 1000f;
                if (planted) { float hover = footUp - (groundUp + p.footHeightOffset); if (hover > 0f) m.hoverMm = hover * 1000f; }
            }

            // Tilt (foot up vs world up): the slope clamp.
            float3 footUpAxis = math.mul(f.currentRot, new float3(0, 1, 0));
            m.tilt = Vector3.Angle(footUpAxis, Up);

            // Yaw (foot forward vs body forward): the toe-out clamp -- only a clamp sample late in a step
            // (a planted foot legitimately lags the body forward as it keeps turning; not a violation).
            float3 footFwd = ProjectFlat(math.mul(f.currentRot, new float3(0, 0, 1)));
            float3 bodyFwdFlat = ProjectFlat(fwd);
            if (math.lengthsq(footFwd) > 1e-6f && math.lengthsq(bodyFwdFlat) > 1e-6f)
            {
                m.yaw = Mathf.Abs(SignedAngle(math.normalize(bodyFwdFlat), math.normalize(footFwd), Up));
                m.yawClamp = f.phase == 1 && f.stepDur > 0f && (f.stepTimer / f.stepDur) > 0.8f;
            }

            // Knee-hint forward projection: the knee must sit in FRONT of the hip->foot midline.
            float3 mid = (hips + f.currentPos) * 0.5f;
            m.kneeFwd = math.dot(f.kneeHint - mid, math.normalizesafe(bodyFwdFlat));
            return m;
        }

        // dist > threshold short-circuits the yaw trigger (matches the job's `||`), so a step that fired
        // with dist <= threshold must have been the yaw trigger.
        static int Classify(in BasisFootNativeState f, in BasisFootSimParams p, float speed)
        {
            float dist = HDist(f.plantedPos, f.idealPos);
            float idleBoost = speed < p.idleSpeedThreshold ? p.stepTriggerDist * p.idleBoostFraction : 0f;
            float thr = p.stepTriggerDist + speed * p.strideScale + idleBoost;
            return dist > thr ? 1 : 2;   // 1 = distance, 2 = yaw
        }

        static void RecordStart(ref BasisFootScenarioResult res, int trig, float stride, float stepDur)
        {
            if (trig == 1) res.DistTriggers++; else res.YawTriggers++;
            res.StrideSum += stride; res.StrideN++;
            res.StepDurSum += stepDur; res.StepDurN++;
            res.StepDurMin = Mathf.Min(res.StepDurMin, stepDur);
            res.StepDurMax = Mathf.Max(res.StepDurMax, stepDur);
        }

        // ───────────────────────── human-readable per-scenario report ─────────────────────────

        static string BuildReport(in BasisFootScenarioResult r)
        {
            float meanSpeed = r.Ticks > 0 ? r.SpeedSum / r.Ticks : 0f;
            float cadence = (r.Dt > 0f && r.Ticks > 0) ? r.Steps / (r.Ticks * r.Dt) : 0f;
            float stride = r.StrideN > 0 ? r.StrideSum / r.StrideN : 0f;
            float stepDur = r.StepDurN > 0 ? r.StepDurSum / r.StepDurN : 0f;
            float airPct = r.Ticks > 0 ? 100f * r.AirborneTicks / r.Ticks : 0f;
            float hz = r.Dt > 0f ? 1f / r.Dt : 0f;

            var sb = new StringBuilder(640);
            sb.Append($"== {r.Name} ==  [{r.Group}] {r.Ground}  {r.Ticks} ticks @ {hz:F0}Hz ({r.Ticks * r.Dt:F1}s)\n");
            sb.Append($"   speed {Sane(r.SpeedMin):F2}..{r.SpeedMax:F2} (mean {meanSpeed:F2}) m/s   hipY {Sane(r.HipUpMin):F2}..{r.HipUpMax:F2} m\n");
            sb.Append($"   steps {r.Steps} (L{r.LeftSteps} R{r.RightSteps})  cadence {cadence:F1}/s  stride {stride:F2}m  stepDur {stepDur:F2}s [{Sane(r.StepDurMin):F2}..{r.StepDurMax:F2}]\n");
            sb.Append($"   triggers: dist {r.DistTriggers}, yaw {r.YawTriggers}   airborne {airPct:F0}% of ticks\n");
            if (r.BothSteppingTicks > 0)
                sb.Append($"   !! BOTH AIRBORNE: {r.BothSteppingTicks} ticks / {r.BothEpisodes} episode(s), first @ t={r.BothFirstTime:F2}s\n");
            if (r.CrossoverTicks > 0)
                sb.Append($"   !! CROSSOVER: {r.CrossoverTicks} ticks / {r.CrossEpisodes} episode(s), worst {r.CrossSepM.Value * 100f:F1}cm @ t={r.CrossSepM.Time:F2}\n");
            if (r.HadNaN) sb.Append("   !! NaN encountered\n");
            sb.Append($"   skate max {r.SlideMm.Value:F1}mm @t{r.SlideMm.Time:F2}(v{r.SlideMm.Speed:F1})   hover max {r.HoverMm.Value:F1}mm   pen max {r.PenMm.Value:F1}mm @t{r.PenMm.Time:F2}\n");
            sb.Append($"   ext max {r.ExtRatio.Value:F2}x @t{r.ExtRatio.Time:F2}(v{r.ExtRatio.Speed:F1})   plant->ideal drift max {r.PlantDriftM.Value * 100f:F0}cm @t{r.PlantDriftM.Time:F2}\n");
            sb.Append($"   tilt max {r.TiltDeg.Value:F0}deg   yaw(late-step) max {r.YawDeg.Value:F0}deg   knee fwd min {Sane(r.MinKneeForwardM) * 100f:F0}cm @t{r.MinKneeForwardTime:F2}   stance {Sane(r.StanceMin):F2}..{r.StanceMax:F2}m   stepLift max {r.StepLiftMm.Value:F0}mm @t{r.StepLiftMm.Time:F2}");
            return sb.ToString();
        }

        static void WriteReport(in BasisFootIKSweepSummary s, BasisFootScenarioResult[] results)
        {
            try
            {
                using (var rw = new StreamWriter(s.ReportPath, false, Encoding.UTF8))
                {
                    rw.WriteLine("BasisFootIKSweep advanced simulation report  " + System.DateTime.UtcNow.ToString("o"));
                    rw.WriteLine($"{s.Scenarios} scenarios, {s.Rows} ticks, {s.TotalSteps} steps total.");
                    rw.WriteLine($"WORST ACROSS ALL: skate {s.WorstPlantedSlideMm:F1}mm  ext {s.WorstExtensionRatio:F2}x  pen {s.WorstPenetrationMm:F1}mm  hover {s.WorstPlantedHoverMm:F1}mm  " +
                                 $"tilt {s.WorstTiltDeg:F0}deg  yaw {s.WorstYawDeg:F0}deg  drift {s.WorstPlantToIdealM * 100f:F0}cm");
                    rw.WriteLine($"  crossover {s.Crossovers} ticks  bothAirborne {s.BothSteppingTicks} ticks (worst {s.BothSteppingWorstScenario} {s.BothSteppingWorstTicks})  kneeBehind {s.WorstKneeBackwardM * 100f:F1}cm  stepLift {s.WorstStepLiftMm:F0}mm  NaN {s.HadNaN}");
                    rw.WriteLine();
                    string group = null;
                    foreach (var r in results)
                    {
                        if (r.Group != group) { group = r.Group; rw.WriteLine($"───────── {group} ─────────"); }
                        rw.WriteLine(r.Report);
                        rw.WriteLine();
                    }
                }
            }
            catch { }
        }

        public static string JoinReports(BasisFootScenarioResult[] results)
        {
            if (results == null) return "";
            var sb = new StringBuilder(8192);
            string group = null;
            foreach (var r in results)
            {
                if (r.Group != group) { group = r.Group; sb.Append("───────── ").Append(group).Append(" ─────────\n"); }
                sb.Append(r.Report).Append("\n\n");
            }
            return sb.ToString();
        }

        // ───────────────────────── scenario battery ─────────────────────────

        static Scenario[] BuildScenarios(BasisFootIKSweepConfig cfg)
        {
            float vs = cfg.SlowSpeed, vn = cfg.NormalSpeed, vf = cfg.FastSpeed;
            float creep = Mathf.Min(0.3f, vs * 0.5f), sprint = Mathf.Min(3.4f, vf * 1.35f);
            float dur = Mathf.Max(2f, cfg.Duration);

            BodySample B(float3 xz, float face = 0f, float yaw = 0f, float pitch = 0f, float crouch = 0f, float up = 0f)
                => new BodySample { RootXZ = xz, FacingDeg = face, HeadYawDeg = yaw, HeadPitchDeg = pitch, Crouch = crouch, BodyUp = up };

            Scenario Mk(string name, string group, BasisFootGroundKind g, System.Func<float, BodySample> f,
                float slope = 0f, float run = 0.3f, float rise = 0f, float gapA = 0f, float gapB = 0f)
                => new Scenario { Name = name, Group = group, Ground = g, Duration = dur, Sample = f,
                                  SlopeDeg = slope, StepRun = run, StepRise = rise, GapStart = gapA, GapEnd = gapB };

            // Constant-velocity travel along a heading, body facing forward.
            Scenario Walk(string name, float speed, float headingDeg)
            {
                float3 d = Forward(headingDeg);
                return Mk(name, "locomotion (speed)", BasisFootGroundKind.Flat, t => B(d * (speed * t)));
            }

            return new[]
            {
                // ── idle ── (a gentle standing weight-shift must keep the feet planted -- no micro-steps)
                Mk("idle", "idle", BasisFootGroundKind.Flat, t => B(new float3(0.01f * Mathf.Sin(t * 2f), 0f, 0.01f * Mathf.Cos(t * 1.7f)), yaw: 8f * Mathf.Sin(t))),
                Mk("idle-sway-fwd", "idle", BasisFootGroundKind.Flat, t => B(Forward(0f) * (0.04f * Mathf.Sin(t * 1.1f)))),
                Mk("idle-sway-side", "idle", BasisFootGroundKind.Flat, t => B(Forward(90f) * (0.04f * Mathf.Sin(t * 1.1f)))),
                Mk("idle-bob", "idle", BasisFootGroundKind.Flat, t => B(float3.zero, up: 0.02f * Mathf.Sin(t * 1.3f))),

                // ── locomotion across the speed range (fast & slow) ──
                Walk("creep", creep, 0f),
                Walk("walk-slow", vs, 0f),
                Walk("walk-normal", vn, 0f),
                Walk("walk-fast", vf, 0f),
                Walk("sprint", sprint, 0f),
                Walk("walk-backward", vn, 180f),
                Walk("strafe-left", vn, 270f),
                Walk("strafe-right", vn, 90f),
                Walk("diagonal", vn, 45f),

                // ── acceleration / deceleration (changing speed) ──
                Mk("speed-sweep", "accel/decel", BasisFootGroundKind.Flat, t => { float q = dur * 0.25f; float pos = t < q ? creep * t : t < 2f * q ? creep * q + vs * (t - q) : t < 3f * q ? creep * q + vs * q + vn * (t - 2f * q) : creep * q + vs * q + vn * q + vf * (t - 3f * q); return B(Forward(0f) * pos); }),
                Mk("start-stop", "accel/decel", BasisFootGroundKind.Flat, t => B(Forward(0f) * ((vn * dur) * Mathf.SmoothStep(0f, 1f, t / dur)))),
                Mk("reversal", "accel/decel", BasisFootGroundKind.Flat, t => { float half = dur * 0.5f; float z = t < half ? vn * t : vn * half - vn * (t - half); return B(new float3(0f, 0f, z)); }),

                // ── rotations ──
                Mk("turn-slow", "rotation", BasisFootGroundKind.Flat, t => B(float3.zero, face: (t / dur) * 180f)),
                Mk("turn-fast", "rotation", BasisFootGroundKind.Flat, t => B(float3.zero, face: (t / dur) * 720f)),
                Mk("spin", "rotation", BasisFootGroundKind.Flat, t => B(float3.zero, face: (t / dur) * 1080f)),
                Mk("circle-strafe", "rotation", BasisFootGroundKind.Flat, t => { float wv = math.radians(60f) * t; float rad = 1.2f; return B(new float3(rad * Mathf.Cos(wv) - rad, 0f, rad * Mathf.Sin(wv)), face: math.degrees(wv)); }),

                // ── yaw REVERSALS (mouse-look snap-back) ──
                // Every rotation scenario above holds ONE direction for its whole run, so nothing here ever
                // exercised a direction change. That is the regime where a signed, smoothed yaw rate is most
                // wrong: for ~tau after the flip the filter still reports the OLD direction, and the signed
                // step prediction aims the foot backwards. See k_YawReversalGain in BasisFootSimulateJob.
                // turn-reversal: one clean hard snap-back at the midpoint (+120 deg/s -> -120 deg/s).
                Mk("turn-reversal", "rotation", BasisFootGroundKind.Flat, t => { float half = dur * 0.5f; float f = t < half ? (t / half) * 360f : 360f - ((t - half) / half) * 360f; return B(float3.zero, face: f); }),
                // mouse-flick: repeated flicks, +/-90 deg at ~0.8 Hz => peak ~450 deg/s reversing every ~0.63 s,
                // so the filter spends a large fraction of every half-cycle with the wrong sign.
                Mk("mouse-flick", "rotation", BasisFootGroundKind.Flat, t => B(float3.zero, face: 90f * Mathf.Sin(t * 5f))),
                // ...and the same flick while walking, where a mis-aimed step target also strands the foot.
                Mk("mouse-flick-walk", "rotation", BasisFootGroundKind.Flat, t => B(Forward(0f) * (vn * t), face: 90f * Mathf.Sin(t * 5f))),
                Mk("strafe-and-turn", "rotation", BasisFootGroundKind.Flat, t => B(Forward(90f) * (vs * t), face: (t / dur) * 360f)),

                // ── head-only look (must NOT spin or pitch the feet) ──
                Mk("look-around", "head look", BasisFootGroundKind.Flat, t => B(float3.zero, yaw: 70f * Mathf.Sin(t * 1.5f))),
                Mk("look-up-down", "head look", BasisFootGroundKind.Flat, t => B(float3.zero, pitch: 60f * Mathf.Sin(t * 1.5f))),
                Mk("look-up-down-walk", "head look", BasisFootGroundKind.Flat, t => B(Forward(0f) * (vn * t), pitch: 50f * Mathf.Sin(t * 2f))),

                // ── vertical (up / down) ──
                Mk("crouch-stand", "vertical", BasisFootGroundKind.Flat, t => B(float3.zero, crouch: 0.5f * (1f - Mathf.Cos(t * 2f)))),
                Mk("crouch-walk", "vertical", BasisFootGroundKind.Flat, t => B(Forward(0f) * (vs * t), crouch: 0.7f)),
                Mk("duck", "vertical", BasisFootGroundKind.Flat, t => B(float3.zero, crouch: Mathf.Clamp01(Mathf.Sin(t * 3f) * 2f))),
                Mk("bob-vertical", "vertical", BasisFootGroundKind.Flat, t => B(float3.zero, up: 0.03f * Mathf.Sin(t * 5f))),
                Mk("jump", "vertical", BasisFootGroundKind.Flat, t => { float ph = (t % 2f) / 0.6f; float up = ph < 1f ? 0.3f * Mathf.Sin(ph * Mathf.PI) : 0f; return B(float3.zero, up: up); }),
                Mk("jump-forward", "vertical", BasisFootGroundKind.Flat, t => { float ph = (t % 2f) / 0.6f; float up = ph < 1f ? 0.3f * Mathf.Sin(ph * Mathf.PI) : 0f; return B(Forward(0f) * (vn * t), up: up); }),

                // ── ground variation ──
                Mk("slope-up", "ground", BasisFootGroundKind.Slope, t => B(Forward(0f) * (vs * t)), slope: 20f),
                Mk("slope-down", "ground", BasisFootGroundKind.Slope, t => B(Forward(0f) * (vs * t)), slope: -20f),
                Mk("stairs-up", "ground", BasisFootGroundKind.Steps, t => B(Forward(0f) * (vs * t)), run: 0.30f, rise: 0.05f),
                Mk("stairs-down", "ground", BasisFootGroundKind.Steps, t => B(Forward(0f) * (vs * t)), run: 0.30f, rise: -0.05f),
                Mk("gap", "ground", BasisFootGroundKind.Gap, t => B(Forward(0f) * (vn * t)), gapA: 1.2f, gapB: 1.8f),
            };
        }

        // ───────────────────────── pose synthesis ─────────────────────────

        static void Pose(BasisFootIKSweepConfig cfg, Scenario sc, BodySample s, out float3 hips, out float3 head, out float3 fwd, out float3 bodyRight)
        {
            fwd = Forward(s.FacingDeg);
            bodyRight = math.normalize(math.cross(Up, fwd));
            float3 rootXZ = new float3(s.RootXZ.x, 0f, s.RootXZ.z);
            float groundUp = math.dot(GroundPoint(sc, rootXZ), Up);
            float crouchDrop = s.Crouch * 0.25f * cfg.HipToFoot;
            hips = rootXZ + Up * (groundUp + cfg.HipToFoot - crouchDrop + s.BodyUp);
            head = hips + Up * cfg.HeadAboveHips;
        }

        static BasisFootNativeState InitFoot(BasisFootIKSweepConfig cfg, Scenario sc, in BasisFootSimParams p, int sideSign, float3 hips, float3 fwd, float3 bodyRight)
        {
            float half = p.stanceWidth * 0.5f;
            float3 footXZ = ProjectFlat(hips) + bodyRight * (sideSign * half);
            GroundRaycast(sc, footXZ, out float3 gpt, out float3 gn);
            float3 planted = gpt + gn * p.footHeightOffset;
            float thigh = sideSign < 0 ? p.leftThighLen : p.rightThighLen;
            float legLen = sideSign < 0 ? p.leftLegLen : p.rightLegLen;
            quaternion footAlign = sideSign < 0 ? p.footAlignLeft : p.footAlignRight;
            quaternion restRot = FootRotation(fwd, gn, p, footAlign);
            return new BasisFootNativeState
            {
                sideSign = sideSign,
                thighLen = thigh,
                shinLen = sideSign < 0 ? p.leftShinLen : p.rightShinLen,
                legLength = legLen,
                phase = 0,
                plantedPos = planted,
                plantedRot = restRot,
                stepStartPos = planted,
                stepTargetPos = planted,
                stepTimer = 0f,
                stepDur = p.stepDurSlow,
                idealPos = planted,
                filteredNormal = gn,
                currentPos = planted,
                currentRot = restRot,
                kneeHint = (hips + planted) * 0.5f + fwd * (thigh * 0.4f),
            };
        }

        // ───────────────────────── FinalizeStep (mirror of BasisLocalFootDriver.FinalizeStep) ─────────────────────────

        static void FinalizeStep(ref BasisFootNativeState f, in BasisFootSimState sim, in BasisFootSimParams p, in Scenario sc, float3 hips)
        {
            float3 velFlat = ProjectFlat(sim.smoothedVelocity);
            float speed = math.length(velFlat);
            float fastYawRef = math.max(1f, 0.5f * p.maxPlantedYawDegrees / math.max(0.01f, p.stepDurFast));
            float speedT = math.max(math.saturate(speed / p.fastSpeedRef), math.saturate(math.abs(sim.smoothedYawRateDeg) / fastYawRef));

            f.phase = 1;
            f.stepStartPos = f.currentPos;
            f.stepTimer = 0f;
            f.stepDur = math.lerp(p.stepDurSlow, p.stepDurFast, speedT);

            float3 targetXZ = f.predictedTargetXZ;
            if (GroundRaycast(sc, targetXZ, out float3 gpt, out float3 gn))
            {
                f.stepTargetPos = gpt + gn * p.footHeightOffset;
                f.filteredNormal = gn;
            }
            else
            {
                float hipsUpComp = math.dot(hips, Up);
                float targetUpComp = hipsUpComp - p.hipToFoot - p.ankleHeight + p.footHeightOffset;
                f.stepTargetPos = ProjectFlat(targetXZ) + Up * targetUpComp;
            }

            float3 bodyFwd = sim.smoothedBodyFwd;
            float3 rawR = math.normalize(math.cross(Up, bodyFwd));
            if (math.lengthsq(rawR) < 0.001f) rawR = new float3(1, 0, 0);

            float3 stp = f.stepTargetPos;
            float stpUpComp = math.dot(stp, Up);
            float3 hGround = ProjectFlat(hips) + Up * stpUpComp;
            EnforceSide(ref stp, hGround, rawR, f.sideSign, p.stanceWidth * p.stepTargetSideFraction);
            f.stepTargetPos = stp;
        }

        static void EnforceSide(ref float3 pos, float3 center, float3 bodyRight, int sideSign, float minDist)
        {
            float lateral = math.dot(pos - center, bodyRight);
            if (sideSign > 0 && lateral < sideSign * minDist) pos += bodyRight * (sideSign * minDist - lateral);
            else if (sideSign < 0 && lateral > -minDist) pos -= bodyRight * (lateral + minDist);
        }

        // Port of BasisFootSimulateJob.FootRotation (same math the driver's Quaternion version runs).
        // footAlign re-seats the body-derived frame into the foot BONE's rest orientation. The sweep's avatar is
        // synthetic and has no foot bone, so its align is identity -- frame == bone -- which keeps these numbers
        // directly comparable to the pre-footAlign baselines. This is only used to seed InitFoot; the swing/plant
        // rotations come from the REAL job, which the sweep runs via Execute().
        static quaternion FootRotation(float3 bodyFwd, float3 normal, in BasisFootSimParams p, quaternion footAlign)
        {
            if (math.lengthsq(normal) < 0.001f) normal = Up;
            float3 fwd = ProjectOnPlane(bodyFwd, normal);
            if (math.lengthsq(fwd) < 1e-6f) fwd = ProjectOnPlane(new float3(0, 0, 1), normal);
            fwd = math.normalize(fwd);

            quaternion surfaceRot = quaternion.LookRotation(fwd, normal);
            quaternion uprightRot = quaternion.LookRotation(fwd, Up);
            float tiltAngle = AngleBetween(uprightRot, surfaceRot);
            quaternion result = tiltAngle > 0.01f ? math.slerp(uprightRot, surfaceRot, math.saturate(p.maxFootTiltDegrees / tiltAngle)) : uprightRot;

            float3 footFwd = ProjectFlat(math.mul(result, new float3(0, 0, 1)));
            float3 bodyFwdFlat = ProjectFlat(bodyFwd);
            if (math.lengthsq(footFwd) > 1e-6f && math.lengthsq(bodyFwdFlat) > 1e-6f)
            {
                float yawAngle = SignedAngle(math.normalize(bodyFwdFlat), math.normalize(footFwd), Up);
                if (math.abs(yawAngle) > p.maxFootYawDegrees)
                {
                    float correction = math.clamp(yawAngle, -p.maxFootYawDegrees, p.maxFootYawDegrees) - yawAngle;
                    result = math.mul(quaternion.AxisAngle(Up, math.radians(correction)), result);
                }
            }
            return math.mul(result, footAlign);
        }

        // Returns a copy of the config scaled to an avatar `s`x the nominal size: all measurements AND
        // locomotion speeds multiply by s (a 2x avatar at 2x world speed has the same Froude number, so
        // the gait is self-similar and the same gates must hold). Scale tags the config for the clamp math.
        static BasisFootIKSweepConfig ScaleConfig(BasisFootIKSweepConfig c, float s)
        {
            if (s == 1f) { c.Scale = 1f; return c; }
            c.StanceWidth *= s; c.HipToFoot *= s; c.HeadAboveHips *= s;
            c.ThighLen *= s; c.ShinLen *= s; c.FootLength *= s; c.AnkleHeight *= s; c.UpperLegToFootVertical *= s;
            float vScale = Mathf.Sqrt(s);   // Froude: locomotion speed scales as sqrt(size) to match BuildParams' sqrt gait-time scaling; a linear *s over-drove scaled avatars (the strafe@2x scissor)
            c.SlowSpeed *= vScale; c.NormalSpeed *= vScale; c.FastSpeed *= vScale; c.TrackerNoise *= s;
            c.Scale = s;
            return c;
        }

        // ───────────────────────── param derivation (mirror of DeriveStepParameters) ─────────────────────────

        static BasisFootSimParams BuildParams(BasisFootIKSweepConfig c)
        {
            float legLen = c.ThighLen + c.ShinLen;
            float avgLeg = legLen;
            float avgShin = c.ShinLen;

            // Mirror the driver's scale-aware clamp bounds: lengths scale linearly with the avatar,
            // gait time/speed as sqrt (pendulum/Froude). c.Scale is the scale this config represents.
            // MUST mirror BasisLocalFootDriver.DeriveStepParameters. Bounds are fractions of the avatar's OWN
            // leg, so they are scale-correct by construction. (The sweep used to scale them by c.Scale while the
            // RUNTIME scaled them by avgLeg/baseAvgLeg -- which is always 1 -- so the sweep was validating a
            // scaled system the runtime never produced. Green gate, broken small avatars. Same law now.)
            const float k_RefLeg = 0.87f;

            float raySphereRadius = Mathf.Clamp(c.FootLength * c.RaySphereRadiusMul, avgLeg * (0.02f / k_RefLeg), avgLeg * (0.12f / k_RefLeg));
            float desiredOffset = c.AnkleHeight * c.FootHeightOffsetMul;
            float straightLegLimit = c.UpperLegToFootVertical + c.AnkleHeight - avgLeg;
            float footHeightOffset = Mathf.Clamp(Mathf.Min(desiredOffset, straightLegLimit), avgLeg * (0.001f / k_RefLeg), avgLeg * (0.05f / k_RefLeg));
            float stepTriggerDist = Mathf.Clamp(avgLeg * c.StepTriggerMul, avgLeg * (0.04f / k_RefLeg), avgLeg * (0.18f / k_RefLeg));
            float strideScale = Mathf.Clamp(avgLeg * c.StrideScaleMul, avgLeg * (0.02f / k_RefLeg), avgLeg * (0.22f / k_RefLeg));
            float stepHeightCalc = Mathf.Clamp(avgShin * c.StepHeightMul, avgLeg * (0.03f / k_RefLeg), avgLeg * (0.20f / k_RefLeg));
            float pendulum = Mathf.PI * Mathf.Sqrt(avgLeg / 9.81f);
            float stepDurSlow = Mathf.Clamp(pendulum * c.StepDurSlowMul, pendulum * (0.10f / 0.9356f), pendulum * (0.30f / 0.9356f));
            float stepDurFast = Mathf.Clamp(pendulum * c.StepDurFastMul, pendulum * (0.06f / 0.9356f), pendulum * (0.18f / 0.9356f));
            float speedRef = Mathf.Sqrt(avgLeg * 9.81f);
            float fastSpeedRef = Mathf.Clamp(c.FastSpeedMul * speedRef, speedRef * (1.0f / 2.921f), speedRef * 2.5f);
            float rayCastRange = Mathf.Max(c.HipToFoot + c.AnkleHeight, legLen) + 1.0f;

            return new BasisFootSimParams
            {
                predictionFactor = c.PredictionFactor,
                velocityBiasFactor = c.VelocityBiasFactor,
                leadOffsetFactor = c.LeadOffsetFactor,
                maxVelocityOffsetFraction = c.MaxVelocityOffsetFraction,
                maxPredictionFraction = c.MaxPredictionFraction,
                plantedLerpSpeed = c.PlantedLerpSpeed,
                rotationLerpSpeed = c.RotationLerpSpeed,
                velocitySmoothAccel = c.VelocitySmoothAccel,
                velocitySmoothDecel = c.VelocitySmoothDecel,
                bodyFwdRateMoving = c.BodyFwdRateMoving,
                bodyFwdRateStationary = c.BodyFwdRateStationary,
                kneeHintLerpSpeed = c.KneeHintLerpSpeed,
                maxFootTiltDegrees = c.MaxFootTiltDegrees,
                maxFootYawDegrees = c.MaxFootYawDegrees,
                stepArcLiftExp = c.StepArcLiftExp,
                stepArcDropExp = c.StepArcDropExp,
                stepHeightMinFraction = c.StepHeightMinFraction,
                stepHeightStrideRefFraction = c.StepHeightStrideRefFraction,
                idleSpeedThreshold = c.IdleSpeedThreshold,
                idleBoostFraction = c.IdleBoostFraction,
                maxPlantedYawDegrees = c.MaxPlantedYawDegrees,
                idealSideEnforceFraction = c.IdealSideEnforceFraction,
                stepTargetSideFraction = c.StepTargetSideFraction,
                footSideEnforceFraction = c.FootSideEnforceFraction,
                maxVerticalDriftFraction = c.MaxVerticalDriftFraction,
                kneeForwardPushFraction = c.KneeForwardPushFraction,
                kneeMinSideFraction = c.KneeMinSideFraction,
                bodyFwdHipsWeight = c.BodyFwdHipsWeight,
                bodyFwdChestWeight = c.BodyFwdChestWeight,
                bodyFwdHeadWeight = c.BodyFwdHeadWeight,
                hipBobFraction = c.HipBobFraction,
                stanceWidth = c.StanceWidth,
                hipToFoot = c.HipToFoot,
                leftLegLen = legLen,
                rightLegLen = legLen,
                leftThighLen = c.ThighLen,
                leftShinLen = c.ShinLen,
                rightThighLen = c.ThighLen,
                rightShinLen = c.ShinLen,
                footLength = c.FootLength,
                ankleHeight = c.AnkleHeight,
                // MUST be set explicitly: default(quaternion) is all-ZEROS, not identity, and a zero quaternion
                // would annihilate every foot rotation. The sweep's avatar is synthetic and has no foot bone, so
                // identity is the right value -- frame == bone, exactly the pre-footAlign behaviour.
                footAlignLeft = quaternion.identity,
                footAlignRight = quaternion.identity,
                stepTriggerDist = stepTriggerDist,
                strideScale = strideScale,
                stepHeightCalc = stepHeightCalc,
                stepDurSlow = stepDurSlow,
                stepDurFast = stepDurFast,
                raySphereRadius = raySphereRadius,
                footHeightOffset = footHeightOffset,
                fastSpeedRef = fastSpeedRef,
                rayCastRange = rayCastRange,
            };
        }

        // ───────────────────────── analytic ground ─────────────────────────

        // Ground point + normal directly below a position (replaces the live Physics raycast). Returns false
        // only in a Gap scenario's no-ground band, exercising the FinalizeStep fallback.
        static bool GroundRaycast(in Scenario sc, float3 pos, out float3 point, out float3 normal)
        {
            float x = pos.x, z = pos.z;
            switch (sc.Ground)
            {
                case BasisFootGroundKind.Slope:
                {
                    float a = math.radians(sc.SlopeDeg);
                    float y = Mathf.Tan(a) * z;
                    point = new float3(x, y, z);
                    normal = math.normalize(new float3(0f, Mathf.Cos(a), -Mathf.Sin(a)));
                    return true;
                }
                case BasisFootGroundKind.Steps:
                {
                    float run = Mathf.Max(0.05f, sc.StepRun);
                    float y = Mathf.Floor(Mathf.Max(0f, z) / run) * sc.StepRise;
                    point = new float3(x, y, z); normal = Up; return true;
                }
                case BasisFootGroundKind.Gap:
                {
                    if (z >= sc.GapStart && z <= sc.GapEnd) { point = float3.zero; normal = Up; return false; }
                    point = new float3(x, 0f, z); normal = Up; return true;
                }
                default:
                    point = new float3(x, 0f, z); normal = Up; return true;
            }
        }

        static float3 GroundPoint(in Scenario sc, float3 pos) { GroundRaycast(sc, pos, out float3 pt, out _); return pt; }

        // ───────────────────────── small math helpers ─────────────────────────

        // ── Combined idle pipeline: sway the hip through the REAL foot driver (planted foot + knee hint)
        //    AND the REAL leg solver (BasisLegSolveCore), returning solved-knee travel / body travel. The
        //    driver keeps the feet planted at idle, so this isolates the two-bone solver's near-extension
        //    sensitivity end-to-end -- the live leg gates only hold a steady pose, so a slow standing sway is
        //    otherwise untested. Left leg (symmetric idle). Reported, not gated.
        static float IdleKneeGain(BasisFootIKSweepConfig cfg, System.Func<float, BodySample> motion)
        {
            var p = BuildParams(cfg);
            float dt = Mathf.Max(1e-4f, cfg.Dt);
            var sc = new Scenario { Name = "idle-knee", Group = "idle", Ground = BasisFootGroundKind.Flat, Duration = Mathf.Max(3f, cfg.Duration), Sample = motion };
            int n = Mathf.Max(2, Mathf.RoundToInt(sc.Duration / dt));

            var feet = new NativeArray<BasisFootNativeState>(2, Allocator.Persistent);
            var simState = new NativeArray<BasisFootSimState>(1, Allocator.Persistent);
            var input = new NativeArray<BasisFootSimInput>(1, Allocator.Persistent);
            var output = new NativeArray<BasisFootSimOutput>(1, Allocator.Persistent);
            try
            {
                BodySample s0 = sc.Sample(0f);
                Pose(cfg, sc, s0, out float3 hips0, out float3 head0, out float3 fwd0, out float3 right0);
                feet[0] = InitFoot(cfg, sc, p, -1, hips0, fwd0, right0);
                feet[1] = InitFoot(cfg, sc, p, +1, hips0, fwd0, right0);
                simState[0] = new BasisFootSimState { prevHeadPos = head0, prevHeadYaw = Yaw(fwd0), smoothedVelocity = float3.zero, smoothedBodyFwd = fwd0, smoothedBodyRight = right0 };

                float half = p.stanceWidth * 0.5f;
                float3 root0 = hips0 + right0 * (-1f * half);   // left hip socket, sways rigidly with the body
                float3 foot0 = feet[0].plantedPos;
                float3 knee0 = RestKnee(root0, foot0, p.leftThighLen, p.leftShinLen, right0, fwd0);

                float3 kMin = new float3(1e9f), kMax = new float3(-1e9f), hMin = kMin, hMax = kMax;
                for (int tick = 0; tick < n; tick++)
                {
                    float t = tick * dt;
                    Pose(cfg, sc, sc.Sample(t), out float3 hips, out float3 head, out float3 fwd, out float3 right);
                    quaternion facing = quaternion.AxisAngle(Up, math.radians(0f));
                    bool gh = GroundRaycast(sc, hips, out float3 gp, out _);
                    input[0] = new BasisFootSimInput { dt = dt, headPos = head, hipsPos = hips, hipsRot = facing, chestRot = facing, headRot = facing, avatarForward = fwd, avatarRight = right, hasChest = true, groundHit = gh, groundPoint = gh ? gp : float3.zero, splayWhenCrouched = 1f, playerUp = Up };
                    new BasisFootSimulateJob { p = p, feet = feet, simState = simState, input = input, output = output }.Execute();
                    var lf = feet[0]; if (lf.wantsStep) { FinalizeStep(ref lf, simState[0], p, sc, hips); lf.wantsStep = false; feet[0] = lf; }
                    var rf = feet[1]; if (rf.wantsStep) { FinalizeStep(ref rf, simState[0], p, sc, hips); rf.wantsStep = false; feet[1] = rf; }

                    float3 d = hips - hips0;   // pre-IK leg translates with the body; the solver pulls the foot back to the plant
                    BasisLegSolveInput li = default;
                    li.Root = root0 + d; li.Mid = knee0 + d; li.Tip = foot0 + d;
                    li.RootRotation = Quaternion.identity; li.MidRotation = Quaternion.identity;
                    li.TargetPosition = feet[0].currentPos; li.TargetRotation = Quaternion.identity;
                    li.HintPosition = feet[0].kneeHint; li.HintWeight = 1f; li.TargetOffset = Quaternion.identity;
                    li.BendNormal = right;
                    BasisLegSolveCore.Solve(li, out BasisLegSolveResult lr);

                    float3 knee = lr.KneeSolved;
                    kMin = math.min(kMin, knee); kMax = math.max(kMax, knee);
                    hMin = math.min(hMin, hips); hMax = math.max(hMax, hips);
                }
                float body = math.length(hMax - hMin);
                return body > 1e-5f ? math.length(kMax - kMin) / body : 0f;
            }
            finally { feet.Dispose(); simState.Dispose(); input.Dispose(); output.Dispose(); }
        }

        // Planar two-bone rest knee, bulged toward fwd (the natural slight standing bend).
        static float3 RestKnee(float3 hip, float3 foot, float u, float l, float3 hingeRight, float3 fwd)
        {
            float3 chord = foot - hip;
            float d = math.min(math.length(chord), (u + l) * 0.999f);
            float3 along = math.normalizesafe(chord);
            float proj = (u * u + d * d - l * l) / (2f * math.max(1e-4f, d));
            float h = math.sqrt(math.max(0f, u * u - proj * proj));
            float3 perp = math.normalizesafe(math.cross(along, hingeRight));
            if (math.dot(perp, fwd) < 0f) perp = -perp;
            return hip + along * proj + perp * h;
        }

        static float3 Forward(float yawDeg) { float r = math.radians(yawDeg); return new float3(Mathf.Sin(r), 0f, Mathf.Cos(r)); }
        static float3 ProjectFlat(float3 v) => v - Up * math.dot(v, Up);
        static float3 ProjectOnPlane(float3 v, float3 n) => v - math.dot(v, n) * n;
        static float HDist(float3 a, float3 b) => math.length(ProjectFlat(a - b));
        // Foot lift above the straight start->target line at the current step phase (the arc height).
        static float StepLift(in BasisFootNativeState f)
        {
            float te = math.saturate(f.stepTimer / f.stepDur);
            float e = 1f - (1f - te) * (1f - te) * (1f - te);
            float baseUp = math.lerp(math.dot(f.stepStartPos, Up), math.dot(f.stepTargetPos, Up), e);
            return math.dot(f.currentPos, Up) - baseUp;
        }
        static float Yaw(float3 fwd) { float3 f = ProjectFlat(fwd); return math.lengthsq(f) < 1e-6f ? 0f : math.degrees(math.atan2(f.x, f.z)); }
        static bool NaN(float3 v) => float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z);
        static float Sane(float v) => float.IsInfinity(v) ? 0f : v;

        static float AngleBetween(quaternion a, quaternion b)
        {
            float dot = math.abs(math.dot(a.value, b.value));
            return math.degrees(2f * math.acos(math.min(dot, 1f)));
        }

        static float SignedAngle(float3 from, float3 to, float3 axis)
        {
            float angle = math.degrees(math.acos(math.clamp(math.dot(from, to), -1f, 1f)));
            return angle * math.sign(math.dot(axis, math.cross(from, to)));
        }

        static string CsvHeader()
        {
            const string foot = "phase,start,trig,prog,timer,x,y,z,rotyaw,slide_mm,lateral,ext,pen_mm,tilt,yaw,ideal_x,ideal_z,plant_x,plant_z,tgt_x,tgt_z,knee_x,knee_y,knee_z,drift";
            var sb = new StringBuilder(512);
            sb.Append("scenario,tick,time,speed,vx,vz,bfx,bfz,ground,ghit,hips_x,hips_y,hips_z,hip_bob,");
            foreach (var c in foot.Split(',')) sb.Append("l_").Append(c).Append(',');
            foreach (var c in foot.Split(',')) sb.Append("r_").Append(c).Append(',');
            sb.Append("stance_m,both_air,crossover");
            return sb.ToString();
        }

        static void WriteFoot(StringBuilder sb, in BasisFootNativeState f, in FootMetric m, bool started, int trig, float drift)
        {
            sb.Append(f.phase).Append(',').Append(started ? '1' : '0').Append(',').Append(trig).Append(',');
            float prog = f.phase == 1 && f.stepDur > 0f ? math.saturate(f.stepTimer / f.stepDur) : 0f;
            Append(sb, prog); Append(sb, f.stepTimer);
            Append(sb, f.currentPos.x); Append(sb, f.currentPos.y); Append(sb, f.currentPos.z);
            Append(sb, Yaw(math.mul(f.currentRot, new float3(0, 0, 1))));
            Append(sb, m.slideMm); Append(sb, m.lateral); Append(sb, m.ext); Append(sb, m.penMm); Append(sb, m.tilt); Append(sb, m.yaw);
            Append(sb, f.idealPos.x); Append(sb, f.idealPos.z);
            Append(sb, f.plantedPos.x); Append(sb, f.plantedPos.z);
            Append(sb, f.stepTargetPos.x); Append(sb, f.stepTargetPos.z);
            Append(sb, f.kneeHint.x); Append(sb, f.kneeHint.y); Append(sb, f.kneeHint.z);
            Append(sb, drift);
        }

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
