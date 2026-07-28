namespace Basis.IK.Debugging
{
    // Pass/fail quality gates over the IK sweep summaries -- the assertion layer that turns the
    // sweeps from "did it run" into "did the IK math pass". Thresholds are set above acceptable and
    // below broken; calibrate them against a known-good baseline run, then a regression trips a gate.
    // Each gate returns (pass, reason) where reason names the failing metric and its value.
    public static class BasisIKTestGates
    {
        // --- tunable thresholds (calibrate against a known-good run) ---
        public const float ArmMaxTrackerSensDegPerCm = 90f; // elbow swivel per cm hand jitter -> oscillation
        public const float ArmMaxMeanAlignErrDeg = 12f;     // mean angle solved-elbow vs tracker pole (follow)
        public const float ArmMaxElbowMeanAlignErrDeg = 8f; // mean angle solved-elbow vs the LOOKUP (no-tracker) pole; drift off the natural down/back pole
        public const int ArmMaxElbowUpFlips = 0;            // forward, non-overhead, EXTENDED (reach>0.55) reaches whose elbow flips hard UP (|swivel|>120). Zero-tolerance: the fixed lookup is clean (0) at every density; folded near-body reaches are excluded (elbow-up is natural there). Pre-fix: 4-7.
        public const float ArmTrackerMaxDevDeg = 15f;       // a real elbow tracker (HintIsTracker) must reproduce the natural pose across realistic mounts/sizes. BasisArmSolveCore re-conditions its short-but-physical pole (positions-only -> pronation-safe), so it follows. Measured worst 0 deg / mean 0 over 360 combos (2026-06-16); 15 is a regression guard (a broken floor drags the elbow ~40-50 deg off).
        // --- chicken-wing elbow flare (no elbow tracker): turning the controllers inward pushes the derived
        //     elbow OUT toward the half-T-pose mark and clamps it there. It must never cross the halfway line to
        //     straight-out-to-the-side, must be a no-op when not rolled in, and the push-out must engage. ---
        public const float ChickenWingMaxSwivelDeg = 45f;   // the half-T-pose cap (matches the runtime ElbowFlareMaxDeg default): full flare lands the elbow here
        public const float ChickenWingClampTolDeg = 8f;     // solver slack allowed over the cap at full flare (the hint is followed, not pinned exactly)
        public const float ChickenWingMaxRegressDeg = 0.5f; // engagement 0 must reproduce the plain lookup solve (a true no-op off the chicken-wing)
        public const float ChickenWingMinPushDeg = 5f;      // full inward roll must push a tucked elbow at least this far OUT (a dead/backwards coupling fails)
        public const float ElbowMinClearedFraction = 0.55f; // protect must clear most of the clearable set (ceiling ~0.64)
        public const float ElbowMaxMeanResidualPenMm = 25f; // mean leftover torso penetration after the push
        public const float ElbowMaxSensDegPerCm = 90f;      // final elbow swivel per cm hand jitter
        public const float ShoulderMaxAngleDeg = 60f;
        public const float ShoulderMaxRestPoseDeg = 2f;        // girdle at the bind arm direction must be ~0 (no idle shoulder shift)
        public const float ShoulderMaxTwistLeakDeg = 1f;       // girdle must not rotate about the arm axis (clavicle following humeral roll -- the reverted artifact); projected to ~0, so 1 deg is a real guard
        public const float ShoulderMaxClampOvershootDeg = 0.5f;// applied girdle must hold under the configured clamp
        public const float ShoulderMaxFrameInvarErrDeg = 0.5f; // chest-frame rotation must carry the result rigidly (no world-orientation leak)
        public const float ShoulderMaxSymmetryErrDeg = 1f;     // left mirrors right
        public const float ShoulderMaxAdjacentJumpDeg = 8f;    // smooth arm motion -> smooth girdle (the solve is a continuous function of direction)
        public const float ShoulderMaxNoisyJitterDeg = 8f;     // a jittery elbow tracker must not whip the girdle
        public const float ShoulderMinBentArmGainDeg = 4f;     // the elbow tracker must elevate a bent-arm raise MORE than the hand fallback can (the headline win)
        // --- scapulohumeral coupling reduction (the floaty/trailing-elbow regression fix). The girdle -- and the
        //     elbow riding it -- must swing a BOUNDED, REDUCED amount vs the pre-fix coupling. ---
        public const float ShoulderCoupleMaxAppliedDeg = 27f;     // shipped girdle-swing ceiling (clamp 25 + slack). Above = the elbow can trail far.
        public const float ShoulderCoupleMinReductionFrac = 0.3f; // shipped must swing the root >=30% less than the legacy coupling across the engaged raise (computed ~0.375).
        public const float HeadMaxNeckDeg = 90f;
        public const float TrajMaxPopDeg = 45f;             // swivel jump on smooth motion (discontinuity)
        public const float TrajMaxRoughDeg = 15f;           // swivel roughness under tracking noise (jitter)
        public const float TemporalMaxRoughDeg = 3f;        // clean 2nd-diff on smooth motion = stepping/jitter as the hand glides (per-frame feedback)
        public const float LegInvertHintSafeConeDeg = 50f;  // hint within this of nominal must never bend the knee backward. Measured onset is ~54 deg (lift pose + posterior down-hint, ~30-35 deg off the leg axis), so 50 is the data-driven safe envelope with margin; beyond it a grossly-wrong pole can still invert (solver forward-clamp would be needed to push it further).
        public const float LegMaxSwivelRoundTripDeg = 12f;  // hint+foot both present: max "out and back" knee-pole swing on smooth foot motion (the twist/untwist). Measured 2 deg on the clean default paths (both sides), so 12 is a wide regression guard below a visible flip. A round trip that returns is a transient pole-axis blend flip the per-frame pop gate misses.
        // --- leg hint quality (thresholds calibrated against the 2026-06-16 known-good run; measured value in each comment) ---
        public const float LegMaxFootErrorM = 0.002f;       // reachable foot must land on target: the hint rotates ABOUT the hip->foot axis so reach is preserved (measured 0.0mm). 2mm guards a gross reach regression; a miss = the solve disturbed reach.
        public const float LegMaxHintFollowDeg = 8f;        // mean angle the solved knee pole sits off the COMMANDED hint pole, well-conditioned (measured 0 deg, max 0 over 7445). 8 matches the arm elbow-align gate; above = the knee isn't following the knee tracker.
        public const float LegMaxHintSensDegPerCm = 60f;    // 99.9th-pct knee-swivel change per cm of hint jitter (measured p99.9 28-30, max 69-138 at boundary outliers). Percentile not max -- the max chases pole-collapse outliers that climb with density (mirrors the arm/protect sens gates).
        // --- leg temporal forward-anchor + noise amplification (calibrated to the 2026-06-16 known-good run) ---
        public const float LegInvertTemporalMinFwdFrac = 0.3f;    // on a good-hint smooth path the knee must stay this anterior (baseline min fwd 0.66); catches a near-extension forward-anchor regression before it inverts (reverted fade sagged to -0.17).
        public const float LegMaxKneeJitterWellCondM = 0.015f;    // well-conditioned (singular-excluded) knee jitter under 3mm foot noise (baseline 7mm); 15mm is a 2x regression guard on the typical-pose noise->knee amplification.
        public const float LegMaxKneeJitterRawM = 0.085f;         // raw worst incl. near-singular poses (baseline 68-70mm). 85mm catches a near-extension destabilization (the reverted fade pushed it to 90-93mm) while clearing the deterministic baseline.
        // --- leg straight-stance ("standing knees cave inward") -- UNCALIBRATED, calibrate after the first run ---
        public const float LegStraightStanceMaxInwardFrac = 0.3f; // standing knee-pole inward (toward-centerline) component; >this = the knee caves in at full extension (forward ~0 expected).
        public const float LegStraightStanceMaxFlipSens = 0.5f;   // how far a tiny uneven-feet/waist-tilt swings the standing knee inward; high = near-singular coin-flip (the asymmetric flicker).
        public const float LegStanceFlickerMaxStepDeg = 3f;       // STATEFUL: worst per-frame knee-swivel jump under 3mm foot noise at a near-straight reach (measured 0.1deg -- the solver is stable offline). 3 is a wide guard against a solver destabilization.
        public const float LegStanceFlickerMaxRangeDeg = 8f;      // STATEFUL: total knee-swivel wander over the run (measured 0deg). 8 catches a slow drift (e.g. an un-anchoring fix); two metrics so a fix can't trade flicker for drift.
        // --- leg twist when standing still/straight (knee-swivel smoothing) -- calibrate after the first run ---
        public const float LegTwistMinRawDeg = 2f;          // standing yaw jitter must visibly roll the RAW near-straight leg (sweep is exercising the bug)
        public const float LegTwistMaxSmoothedFrac = 0.6f;  // smoothing must cut the worst standing twist below this fraction of raw
        public const float LegTwistMinTurnTrackFrac = 0.7f; // a real turn must still carry the smoothed swivel (not frozen)
        public const float LegTwistMaxTurnLagDeg = 20f;     // steady turn-tracking lag ceiling (over-smoothing guard)
        // --- tracker-driven bend normal (the knee bend plane rides the lower-leg tracker instead of fixed hips-right) ---
        public const float BendNormalParityMaxDeg = 0.5f;        // at the calibration pose the tracker normal must reproduce the fixed normal exactly -- a no-op there
        public const float BendNormalMaxTrackingErrDeg = 1f;     // the tracker normal must stay on the shin's medial axis as the shin yaws (it rides the tracker)
        public const float BendNormalMinDemonstratedErrDeg = 20f;// the fixed hips-right normal it replaces must drift at least this far off the shin axis under yaw, or the sweep range isn't exercising the fix
        // --- virtual-spine hips compression (the touch-toes-folds-knees + seated-hips-too-low fix). The pelvis no
        //     longer sinks the full head drop; the spine shortens instead. Thresholds derive from the deterministic
        //     sweep geometry + the saturating model (computed values in comments). ---
        public const float SpineCompMinKneeStraighterDeg = 5f;    // engaged band: compression must keep the planted-foot knee this much straighter than rigid (computed ~10 at the engage boundary). <this = not engaging.
        public const float SpineCompMinDeepLeanKneeDeg = 70f;     // deepest lean: knee interior angle must hold >= this (computed ~75 on; rigid folds to the 20 clamp). <this = still buckling.
        public const float SpineCompMaxSinkFracOfDrop = 0.6f;     // peak pelvis sink must stay under this fraction of the rigid full-drop sink (computed ~0.45). >this = pelvis still chases the head down.
        public const float SpineCompMaxAboveStandingDevM = 1e-4f; // head at/above standing must leave the rigid pose untouched (no idle posture change).
        public const float SpineCompMaxStepDiscontinuityM = 0.05f;// compressed hips height moves smoothly over the head ramp (computed ~0.014/step). >this = a pop.
        // --- tracker placement / discovery gates (calibrate against a known-good run) ---
        public const float TrackerPlacementCleanMinFraction = 0.85f; // overall clean (no-jitter) correctness floor across ALL archetypes incl. extreme; below = broken
        public const float TrackerPlacementCoreMinFraction = 1.0f;   // common "core" archetypes must classify every tracker cleanly
        // --- multi-tracker ("double hip") rotation fusion ---
        public const float MultiTrackerMaxErrDeg = 2f;              // fused hip must track body rotation within this of a single tracker
        public const float MultiTrackerProjectionRigidTolDeg = 1f;  // the projection math must be exact for a RIGID pair (regression guard)
        public const float MultiTrackerTemporalMaxErrDeg = 3f;      // fused rotation must track the body within this during motion (a single tracker is ~0)
        public const float MultiTrackerTemporalMaxSnapDeg = 2f;     // frame-to-frame step jump above this = a visible rotation snap
        // --- foot placement / procedural stepping gates (calibrate against a known-good run) ---
        public const float FootMaxPlantedSlideMm = 15f;   // a planted foot is world-locked; horizontal move >this/tick = skating
        public const float FootMaxExtensionRatio = 1.18f;  // hips->foot / standing reach; above = leg can't reach (foot left behind, visible stretch)
        public const float FootMaxPenetrationMm = 30f;     // planted foot driven below the floor
        public const float FootMaxPlantedHoverMm = 60f;    // planted foot floating above the floor
        public const float FootTiltSlackDeg = 2f;          // headroom over the configured tilt clamp -- the slope clamp must hold
        public const float FootYawSlackDeg = 6f;           // headroom over the configured yaw clamp (sampled late in a step, where the clamp is live)
        public const float FootMaxKneeBehindM = 0.02f;     // knee hint may sit at most this far behind the hip->foot line before the knee would invert
        public const int FootMaxIdleSteps = 0;             // a gentle standing weight-shift must not trigger a step (the foot stays planted)

        public static (bool pass, string reason) GateArm(in BasisArmIKSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Rows <= 0) return (false, "no rows");
            if (s.ReachablePoints <= 0) return (false, "no reachable points");
            if (s.TrackerMeanAlignErrDeg > ArmMaxMeanAlignErrDeg)
                return (false, $"tracker meanAlignErr {s.TrackerMeanAlignErrDeg:F1} > {ArmMaxMeanAlignErrDeg} deg (tracker not followed)");
            // Gate on the 99.9th-percentile tracker sens, not the raw max: even after excluding the
            // pole-collapse singularity, the max chases the worst well-conditioned boundary pose, which
            // sharpens (not spreads) with grid density and so climbs forever. The percentile catches
            // WIDESPREAD tracker jitter and is density-stable (mirrors the protect sens gate).
            if (s.TrackerSens99DegPerCm > ArmMaxTrackerSensDegPerCm)
                return (false, $"tracker sens p99.9 {s.TrackerSens99DegPerCm:F0} > {ArmMaxTrackerSensDegPerCm} deg/cm (widespread jitter; max {s.TrackerMaxSensDegPerCm:F0} at boundary outliers)");
            return (true, $"reach={s.ReachablePoints} alignErr={s.TrackerMeanAlignErrDeg:F1} sensP99.9={s.TrackerSens99DegPerCm:F0} (max {s.TrackerMaxSensDegPerCm:F0})");
        }

        // Elbow DIRECTION (not the torso-collision "elbow protect"): the no-tracker lookup pole must keep
        // the elbow tucked behind/below the hand, and the solve must actually land it there. A flip = the
        // elbow ends up in front of / on the wrong side from where the lookup asked -- the artifact where
        // "the elbow is out in front instead of naturally behind the hand". Driven by the same arm sweep.
        public static (bool pass, string reason) GateArmElbowDirection(in BasisArmIKSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.ReachablePoints <= 0) return (false, "no reachable points");
            if (s.LookupElbowUpCount > 0)
                return (false, $"{s.LookupElbowUpCount} reachable poses point the elbow UP (chicken-wing)");
            if (s.LookupElbowFlipCount > ArmMaxElbowUpFlips)
                return (false, $"{s.LookupElbowFlipCount} extended forward reaches flip the elbow hard UP (|swivel|>120) instead of behind/below -- elbow in front / wrong side (align mean {s.LookupMeanAlignErrDeg:F1}, max {s.LookupMaxAlignErrDeg:F0})");
            if (s.LookupMeanAlignErrDeg > ArmMaxElbowMeanAlignErrDeg)
                return (false, $"mean elbow-direction error {s.LookupMeanAlignErrDeg:F1} > {ArmMaxElbowMeanAlignErrDeg} deg (elbow drifting off the natural pole)");
            // Anatomical flexion: the solved elbow angle must never leave the human range (no over-flex / hyperextension).
            float minFlex = Basis.IK.BasisArmSolveCore.MinElbowAngleDeg;
            float maxFlex = Basis.IK.BasisArmSolveCore.MaxElbowAngleDeg;
            if (s.LookupMinElbowAngleDeg < minFlex - 1f || s.LookupMaxElbowAngleDeg > maxFlex + 1f)
                return (false, $"elbow flexion {s.LookupMinElbowAngleDeg:F0}..{s.LookupMaxElbowAngleDeg:F0} deg leaves the human range [{minFlex:F0},{maxFlex:F0}] (over-flex / hyperextension)");
            return (true, $"extUpFlips={s.LookupElbowFlipCount} elbowUp={s.LookupElbowUpCount} alignMean={s.LookupMeanAlignErrDeg:F1} flex={s.LookupMinElbowAngleDeg:F0}..{s.LookupMaxElbowAngleDeg:F0}");
        }

        // Chicken-wing flare (no elbow tracker): turning the controllers inward pushes the derived elbow OUT
        // toward the half-T-pose mark and HARD-CLAMPS it there -- it must never cross the halfway line to
        // straight-out-to-the-side ("won't feel right"), must be a no-op when the controller isn't rolled in,
        // and the push-out must actually engage (a dead / backwards roll coupling fails). Driven by RunChickenWing.
        public static (bool pass, string reason) GateArmChickenWing(in BasisArmChickenWingSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.ReachablePoints <= 0) return (false, "no reachable chicken-wing poses");
            if (s.WorstOverCapDeg > ChickenWingClampTolDeg)
                return (false, $"chicken-wing elbow reaches {s.MaxFullFlareSwivelDeg:F1} deg, {s.WorstOverCapDeg:F1} past the {s.CapDeg:F0} deg half-T-pose cap > {ChickenWingClampTolDeg} (crosses the halfway mark)");
            if (s.MaxRegressDeg > ChickenWingMaxRegressDeg)
                return (false, $"flare moved a neutral (no-roll) elbow by {s.MaxRegressDeg:F2} deg > {ChickenWingMaxRegressDeg} (must be a no-op off the chicken-wing)");
            if (s.PushSamples > 0 && s.MeanPushDeg < ChickenWingMinPushDeg)
                return (false, $"full inward roll pushed the elbow out only {s.MeanPushDeg:F1} deg < {ChickenWingMinPushDeg} (push-out coupling dead/backwards; {s.PushedOutCount}/{s.PushSamples} poses moved out)");
            return (true, $"cap={s.CapDeg:F0} worstFlare={s.MaxFullFlareSwivelDeg:F1} (over {s.WorstOverCapDeg:F1}) push={s.MeanPushDeg:F1} ({s.PushedOutCount}/{s.PushSamples}) regress={s.MaxRegressDeg:F2}");
        }

        // A REAL elbow tracker must give a NATURAL bend for any reasonable mount / body size: a tracker strapped
        // to the no-tracker natural arm must reproduce that pose (it must never make the elbow LESS natural than
        // no tracker). A large worst deviation is the "elbow tracker looks unnatural in some setups" -- the raw
        // tracker pole is weakly conditioned for that placement and the elbow collapses toward world-down.
        public static (bool pass, string reason) GateArmTrackerNaturalness(in BasisArmTrackerNaturalnessSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Combos <= 0) return (false, "no placement/size combos");
            if (s.WorstDevDeg > ArmTrackerMaxDevDeg)
                return (false, $"a strapped elbow tracker drives the bend {s.WorstDevDeg:F0} deg off the natural pose at {s.WorstWhere} > {ArmTrackerMaxDevDeg} ({s.OverCount}/{s.Combos} combos over; mean {s.MeanDevDeg:F1}) -- unnatural for that mount/body size");
            return (true, $"worst {s.WorstDevDeg:F0} deg off natural at {s.WorstWhere} (mean {s.MeanDevDeg:F1}, {s.Combos} combos)");
        }

        public static (bool pass, string reason) GateElbow(in BasisElbowProtectSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Rows <= 0) return (false, "no rows");
            if (s.EngagedPoints <= 0) return (false, "protect never engaged (sweep not exercising it)");
            float clearedFrac = (float)s.ClearedPoints / s.EngagedPoints;
            if (clearedFrac < ElbowMinClearedFraction)
                return (false, $"cleared {clearedFrac:P0} of engaged < {ElbowMinClearedFraction:P0} (protect not clearing torso)");
            if (s.MeanResidualPenMm > ElbowMaxMeanResidualPenMm)
                return (false, $"mean residual {s.MeanResidualPenMm:F0}mm > {ElbowMaxMeanResidualPenMm}mm (elbow left buried)");
            // Gate on the 99.9th-percentile sens, not the raw max: the max chases a handful of protect
            // engage/disengage boundary discontinuities that sharpen (not spread) with grid density, so it
            // climbs forever as you densify. The percentile catches WIDESPREAD oscillation and is stable.
            if (s.Sens99DegPerCm > ElbowMaxSensDegPerCm)
                return (false, $"sens p99.9 {s.Sens99DegPerCm:F0} > {ElbowMaxSensDegPerCm} deg/cm (widespread oscillation; max {s.MaxSensDegPerCm:F0} at boundary outliers)");
            return (true, $"cleared={clearedFrac:P0} resid={s.MeanResidualPenMm:F0}mm sensP99.9={s.Sens99DegPerCm:F0} (max {s.MaxSensDegPerCm:F0})");
        }

        // Scapulohumeral shoulder pre-solve: the girdle must engage, stay under the clamp, sit still at
        // the bind pose (no idle shift), never twist about the arm (the reverted clavicle-roll artifact),
        // stay rigid in the chest frame, mirror left/right, rise monotonically as the arm raises, move
        // smoothly under smooth/ noisy arm input, and -- the headline -- the elbow tracker must change a
        // bent-arm result the hand fallback can't see.
        public static (bool pass, string reason) GateShoulder(in BasisShoulderSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Rows <= 0) return (false, "no rows");
            if (s.Engaged <= 0) return (false, "shoulder never engaged");
            if (s.MaxShoulderAngleDeg > ShoulderMaxAngleDeg)
                return (false, $"max shoulder {s.MaxShoulderAngleDeg:F0} > {ShoulderMaxAngleDeg} deg");
            if (s.ClampOvershootDeg > ShoulderMaxClampOvershootDeg)
                return (false, $"girdle exceeds its clamp by {s.ClampOvershootDeg:F1} deg > {ShoulderMaxClampOvershootDeg} (clamp not holding)");
            if (s.RestPoseMaxDeg > ShoulderMaxRestPoseDeg)
                return (false, $"girdle moves {s.RestPoseMaxDeg:F1} deg at the bind arm pose > {ShoulderMaxRestPoseDeg} (idle shoulder would shift)");
            if (s.MaxTwistLeakDeg > ShoulderMaxTwistLeakDeg)
                return (false, $"girdle twists {s.MaxTwistLeakDeg:F1} deg about the arm axis > {ShoulderMaxTwistLeakDeg} (clavicle following humeral roll -- the reverted artifact)");
            if (s.MaxFrameInvarErrDeg > ShoulderMaxFrameInvarErrDeg)
                return (false, $"chest-frame rigidity off by {s.MaxFrameInvarErrDeg:F1} deg > {ShoulderMaxFrameInvarErrDeg} (solve leaks world orientation)");
            if (s.SymmetryMaxErrDeg > ShoulderMaxSymmetryErrDeg)
                return (false, $"left/right asymmetry {s.SymmetryMaxErrDeg:F1} deg > {ShoulderMaxSymmetryErrDeg}");
            if (s.MonotonicViolations > 0)
                return (false, $"{s.MonotonicViolations} reversals: raising the arm reduced girdle elevation below the clamp");
            if (s.MaxAdjacentJumpDeg > ShoulderMaxAdjacentJumpDeg)
                return (false, $"girdle jumps {s.MaxAdjacentJumpDeg:F0} deg on smooth arm motion > {ShoulderMaxAdjacentJumpDeg}");
            if (s.NoisyMaxJitterDeg > ShoulderMaxNoisyJitterDeg)
                return (false, $"girdle jitters {s.NoisyMaxJitterDeg:F0} deg under elbow-tracker noise > {ShoulderMaxNoisyJitterDeg}");
            float bentGain = s.BentArmElbowElevationDeg - s.BentArmHandElevationDeg;
            if (bentGain < ShoulderMinBentArmGainDeg)
                return (false, $"elbow tracker barely changes a bent-arm shoulder (elbow {s.BentArmElbowElevationDeg:F1} vs hand {s.BentArmHandElevationDeg:F1} deg; gain {bentGain:F1} < {ShoulderMinBentArmGainDeg})");
            return (true, $"engaged={s.Engaged} maxAngle={s.MaxShoulderAngleDeg:F0} rest={s.RestPoseMaxDeg:F1} twist={s.MaxTwistLeakDeg:F1} frame={s.MaxFrameInvarErrDeg:F1} sym={s.SymmetryMaxErrDeg:F1} jump={s.MaxAdjacentJumpDeg:F0} jitter={s.NoisyMaxJitterDeg:F0} bentGain={bentGain:F1}");
        }

        public static (bool pass, string reason) GateLeg(in BasisLegIKSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Rows <= 0) return (false, "no rows");
            if (s.ReachablePoints <= 0) return (false, "no reachable points");
            if (float.IsNaN(s.MaxSwivelShiftDeg) || s.MaxSwivelShiftDeg > 180f)
                return (false, $"knee swivel shift {s.MaxSwivelShiftDeg:F0} out of range");
            // Reach is primary: the weight-scaled hint only rotates the knee about the hip->foot axis, so a
            // reachable foot must stay on target. A miss = the hint pass moved the foot (reach regressed).
            if (s.MaxFootErrorReachableM > LegMaxFootErrorM)
                return (false, $"foot misses a reachable target by {s.MaxFootErrorReachableM * 100f:F1}cm > {LegMaxFootErrorM * 100f:F0}cm (the hint pass disturbed reach)");
            // The knee must actually go to the knee tracker: solved pole vs commanded hint pole, well-conditioned.
            if (s.HintFollowSamples > 0 && s.MeanHintFollowDeg > LegMaxHintFollowDeg)
                return (false, $"knee pole sits {s.MeanHintFollowDeg:F0}deg off the hint (mean over {s.HintFollowSamples}, max {s.MaxHintFollowDeg:F0}) > {LegMaxHintFollowDeg} -- knee doesn't follow the hint");
            // Pole must not whip under a shaky knee tracker. Percentile (widespread), not the boundary-outlier max.
            if (s.HintSensSamples > 0 && s.HintSensP999DegPerCm > LegMaxHintSensDegPerCm)
                return (false, $"knee swivel sens p99.9 {s.HintSensP999DegPerCm:F0} > {LegMaxHintSensDegPerCm} deg/cm (widespread pole jitter; max {s.HintSensMaxDegPerCm:F0} at boundary outliers)");
            return (true, $"reach={s.ReachablePoints} maxSwivelShift={s.MaxSwivelShiftDeg:F0} footErr={s.MaxFootErrorReachableM * 1000f:F1}mm follow(mean={s.MeanHintFollowDeg:F0} max={s.MaxHintFollowDeg:F0} n={s.HintFollowSamples}) sensP99.9={s.HintSensP999DegPerCm:F0}(max {s.HintSensMaxDegPerCm:F0})");
        }

        // Inhuman knee detection: the knee must never bend backward (posterior to the hip->ankle line).
        // The gate lives on WELL-CONDITIONED hints -- a reasonable hint (within the safe cone) and any
        // reachable target under a good hint must stay human; a backward knee there means a tracker can
        // invert the pole and break the pose. Pole-on-limb singularities are excluded (and reported), the
        // way the trajectory gates exclude their kinematic singularities -- the solver blends those forward.
        public static (bool pass, string reason) GateLegInversion(in BasisLegInversionSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.HintSamples <= 0) return (false, "no hint samples (sweep not exercising the knee)");
            if (s.SafeConeInversions > 0)
                return (false, $"{s.SafeConeInversions}/{s.SafeConeSamples} well-conditioned knee inversions within hint cone (onset {s.OnsetDeviationDeg:F0} deg) -- pole flips the knee backward");
            if (s.TargetInversions > 0)
                return (false, $"{s.TargetInversions}/{s.TargetReachable} reachable targets bend the knee backward with a good hint (inhuman pose)");
            // Max-flexion limit: a human knee can't fold past the solver's MinKneeInteriorDeg clamp (calf
            // through thigh). The flexion pass pulls the foot to the hip; the solved interior must hold there.
            float flexLimit = Basis.IK.BasisLegSolveCore.MinKneeInteriorDeg;
            if (s.FlexClampSamples > 0 && s.MinKneeFlexDeg < flexLimit - 3f)
                return (false, $"knee over-folds to {s.MinKneeFlexDeg:F0} deg interior < {flexLimit:F0} deg limit (calf through thigh -- clamp not holding)");
            string onset = float.IsNaN(s.OnsetDeviationDeg) ? "none" : $"{s.OnsetDeviationDeg:F0}deg";
            return (true, $"safe cone clean, onset={onset} natural={s.TargetInversions}/{s.TargetReachable} singular={s.SingularInversions}/{s.SingularSamples} minFlex={s.MinKneeFlexDeg:F0}/{flexLimit:F0}deg");
        }

        // Temporal knee inversion: on smooth foot motion with a good hint the knee must never cross to the
        // backward (posterior) side mid-motion. Pole-noise crossings are reported, not gated (enough jitter
        // always breaks it) -- they show how much tracker shake the knee tolerates before it transiently flips.
        public static (bool pass, string reason) GateLegInversionTemporal(in BasisLegInversionTemporalSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.Crossings > 0)
                return (false, $"knee flips backward {s.Crossings}x mid-motion on a good hint (min fwd {s.MinFwdFrac:F2}) -- transient inversion");
            // Not just "never fully inverts" -- the knee must stay COMFORTABLY anterior on a good hint. Known-good
            // baseline min fwd is 0.66; a regression that lets it sag toward posterior (e.g. the reverted hint-fade
            // dropped it to -0.17 without ever "crossing") is caught here before it becomes a visible back-bend.
            if (s.MinFwdFrac < LegInvertTemporalMinFwdFrac)
                return (false, $"knee sags toward posterior mid-motion (min fwd {s.MinFwdFrac:F2} < {LegInvertTemporalMinFwdFrac}) -- near-extension forward-anchor weakened, not yet a full inversion but heading there");
            return (true, $"clean (min fwd {s.MinFwdFrac:F2} >= {LegInvertTemporalMinFwdFrac}); under pole noise: {s.NoisyCrossings} flips (min fwd {s.NoisyMinFwdFrac:F2})");
        }

        // Crouch (foot planted, hips lowering): a good forward hint must keep the knee anterior with no swivel
        // snap through the whole crouch range. A snap = the knee pole flipping mid-crouch -- the "leg shoots up
        // inverted" artifact. Posterior knee or any snap fails.
        public static (bool pass, string reason) GateLegCrouch(in BasisLegCrouchSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.PosteriorInversions > 0)
                return (false, $"knee bent backward on {s.PosteriorInversions} crouch steps (worst {s.WorstScenario}, min fwd {s.MinFwdFrac:F2})");
            if (s.Snaps > 0)
                return (false, $"knee pole snapped >90deg on {s.Snaps} crouch steps -- leg flips/shoots up mid-crouch (worst jump {s.WorstSwivelJumpDeg:F0}deg @ {s.WorstScenario}, onset crouchFrac {s.OnsetCrouchFrac:F2})");
            return (true, $"clean through crouch: {s.Scenarios} placements, worst swivel jump {s.WorstSwivelJumpDeg:F0}deg, worst swivel {s.WorstSwivelDeg:F0}deg, min fwd {s.MinFwdFrac:F2}");
        }

        // Tracker-driven bend normal: the knee bend plane must follow the lower-leg tracker so a yawed shin
        // still bends in its OWN plane (the fix for the pole flip the forward-pole offset was patching). Gate
        // the deterministic core contract -- a no-op at the calibration pose (parity ~0), the tracker normal
        // stays on the shin's medial axis as the shin yaws, and the fixed normal it replaces visibly drifts
        // off that axis (or the sweep range isn't exercising the fix). The degenerate-pole solved-knee plane
        // error is reported for insight, not hard-gated (it depends on solver-internal blend behavior).
        public static (bool pass, string reason) GateBendNormal(in BasisBendNormalSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.ParityMaxDeg > BendNormalParityMaxDeg)
                return (false, $"not a no-op at calibration: tracker normal differs {s.ParityMaxDeg:F1}deg from the fixed normal at yaw 0 (> {BendNormalParityMaxDeg})");
            if (s.WorstTrackingErrNewDeg > BendNormalMaxTrackingErrDeg)
                return (false, $"tracker normal not following the leg: {s.WorstTrackingErrNewDeg:F1}deg off the shin medial axis (> {BendNormalMaxTrackingErrDeg})");
            if (s.WorstTrackingErrOldDeg < BendNormalMinDemonstratedErrDeg)
                return (false, $"sweep range too small: fixed normal only drifts {s.WorstTrackingErrOldDeg:F0}deg off the shin axis (< {BendNormalMinDemonstratedErrDeg}) -- fix not exercised");
            return (true, $"no-op@calib ({s.ParityMaxDeg:F1}deg); tracker normal tracks shin within {s.WorstTrackingErrNewDeg:F1}deg vs fixed {s.WorstTrackingErrOldDeg:F0}deg drift; degenerate-pole knee plane err new {s.WorstNormalNewPlaneErrDeg:F0} vs old {s.WorstNormalOldPlaneErrDeg:F0}deg");
        }

        // Pole round-trip: with BOTH a knee hint and a moving foot target present, smooth foot motion must not
        // make the knee pole swing out and rotate BACK to where it was -- the "knee twists then untwists"
        // artifact the user sees when a hint tracker and a foot tracker are both live. This is distinct from a
        // one-frame pop (GateTrajectory): the excursion can ramp out and back over many frames, each step tiny,
        // so the pop gate misses it. Measured on the clean (noise-free) path, away from the fold/extension
        // singularities; an excursion that returns is a transient pole-axis blend flip (pole-colinear or
        // near-extension blend to the bend-normal engaging and releasing as the foot sweeps).
        public static (bool pass, string reason) GateLegSwivelRoundTrip(bool ok, string error, float worstRoundTripDeg, string worstPath)
        {
            if (!ok) return (false, string.IsNullOrEmpty(error) ? "did not run" : error);
            if (float.IsNaN(worstRoundTripDeg))
                return (false, "round-trip NaN");
            if (worstRoundTripDeg > LegMaxSwivelRoundTripDeg)
                return (false, $"knee pole swings out {worstRoundTripDeg:F0} deg and rotates back > {LegMaxSwivelRoundTripDeg} on smooth motion ('{worstPath}') -- pole twists then untwists (transient blend flip with hint+foot both present)");
            return (true, $"max round-trip {worstRoundTripDeg:F0} deg ('{worstPath}')");
        }

        // Standing knees caving inward: at full extension (standing) the knee pole is near-singular, so the
        // solver leans on the BendNormal fallback and a tiny asymmetry (uneven feet / a few degrees of waist
        // tilt) can swing the knee toward the body centerline -- the "knees bend inward, can't tell which side"
        // artifact. The knee must point forward at standing, and must not be flip-sensitive to that tilt.
        public static (bool pass, string reason) GateLegStraightStance(in BasisLegStraightStanceSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Samples <= 0) return (false, "no samples");
            string onset = float.IsNaN(s.OnsetReach) ? "none" : $"{s.OnsetReach:F2}";
            if (s.WorstInwardFrac > LegStraightStanceMaxInwardFrac)
                return (false, $"knee caves INWARD at standing (inward frac {s.WorstInwardFrac:F2} > {LegStraightStanceMaxInwardFrac}, onset reach {onset}) -- knees bend toward the centerline at full extension");
            if (s.FlipSensitivity > LegStraightStanceMaxFlipSens)
                return (false, $"standing knee is flip-sensitive: a tiny uneven-feet/waist-tilt swings the pole inward by {s.FlipSensitivity:F2} (to {s.WorstPerturbedInwardFrac:F2}) > {LegStraightStanceMaxFlipSens} -- near-singular coin-flip (the L/R asymmetry)");
            return (true, $"forward (minFwd {s.MinForwardFrac:F2}, worstInward {s.WorstInwardFrac:F2}); under tilt worstInward {s.WorstPerturbedInwardFrac:F2} flipSens {s.FlipSensitivity:F2}");
        }

        // STATEFUL straight-stance flicker: hold near-straight + per-frame foot noise, feed the previous knee.
        // The knee swivel must neither FAST-oscillate (WorstStepDeg = the visible flicker) nor slowly WANDER
        // (WorstSwivelRangeDeg = drift). Two metrics so a fix can't trade one for the other -- this is the test
        // the stateless straight-stance check couldn't be (it re-seeds forward each step). UNCALIBRATED.
        public static (bool pass, string reason) GateLegStraightStanceFlicker(in BasisLegStraightStanceTemporalSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.WorstStepDeg > LegStanceFlickerMaxStepDeg)
                return (false, $"standing knee FLICKERS {s.WorstStepDeg:F0} deg/frame under foot noise (reach {s.AtReach:F2}, {s.Zigzags} oscillations) > {LegStanceFlickerMaxStepDeg} -- the straight-leg outward/inward flip");
            if (s.WorstSwivelRangeDeg > LegStanceFlickerMaxRangeDeg)
                return (false, $"standing knee WANDERS {s.WorstSwivelRangeDeg:F0} deg of swivel over the run > {LegStanceFlickerMaxRangeDeg} -- slow drift (e.g. a fade-style fix that un-anchors the knee)");
            return (true, $"steady: maxStep {s.WorstStepDeg:F1} deg/frame, range {s.WorstSwivelRangeDeg:F0} deg, {s.Zigzags} oscillations (reach {s.AtReach:F2})");
        }

        // Knee jitter under foot-target noise (the leg has no swivel rate-limiter, so foot-tracker noise
        // propagates into the knee). wellCond (singular-excluded) is the typical-pose figure; raw includes the
        // near-extension singular poses where a tiny foot move swings the knee a lot. Both gated now so a
        // near-extension destabilization -- e.g. a hint change that un-anchors the straight-leg knee -- is caught
        // instead of slipping through as a reported number (which is how the reverted fade's 68->90mm hid).
        public static (bool pass, string reason) GateLegKneeJitter(bool ok, string error, float wellCondM, float rawM, float noiseM)
        {
            if (!ok) return (false, string.IsNullOrEmpty(error) ? "did not run" : error);
            if (wellCondM > LegMaxKneeJitterWellCondM)
                return (false, $"well-conditioned knee jitter {wellCondM * 1000f:F0}mm from {noiseM * 1000f:F0}mm foot noise > {LegMaxKneeJitterWellCondM * 1000f:F0}mm (typical-pose noise->knee amplification regressed)");
            if (rawM > LegMaxKneeJitterRawM)
                return (false, $"raw knee jitter {rawM * 1000f:F0}mm (incl. near-singular) from {noiseM * 1000f:F0}mm foot noise > {LegMaxKneeJitterRawM * 1000f:F0}mm (near-extension knee destabilized -- straight-leg flicker/drift)");
            return (true, $"wellCond {wellCondM * 1000f:F0}mm raw {rawM * 1000f:F0}mm from {noiseM * 1000f:F0}mm foot noise");
        }

        // Standing leg twist: with no foot trackers the leg runs near full extension, where the solver's bend
        // axis is the raw hips-yaw bend normal and the knee pole follows the hips-derived hint -- so hips-yaw
        // jitter rolls the near-straight leg about the hip->foot axis ("legs twist when I stand still/straight").
        // SmoothKneeSwivel (One-Euro on the OUTPUT knee swivel) must collapse that jitter yet still track a real
        // turn. UNCALIBRATED -- calibrate the thresholds against the first known-good run.
        public static (bool pass, string reason) GateLegTwist(in BasisLegTwistSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Rows <= 0) return (false, "no rows");
            if (s.WorstRawP2PDeg < LegTwistMinRawDeg)
                return (false, $"standing yaw jitter barely rolled the raw leg (worst {s.WorstRawP2PDeg:F1} deg @ ext {s.WorstExt:F2} < {LegTwistMinRawDeg}) -- sweep not exercising the twist");
            if (s.WorstSmoothedP2PDeg > s.WorstRawP2PDeg * LegTwistMaxSmoothedFrac)
                return (false, $"knee-swivel smoothing left {s.WorstSmoothedP2PDeg:F1} deg of standing twist > {LegTwistMaxSmoothedFrac:P0} of raw {s.WorstRawP2PDeg:F1} (@ ext {s.WorstExt:F2})");
            if (System.Math.Abs(s.TurnSmoothChangeDeg) < System.Math.Abs(s.TurnRawChangeDeg) * LegTwistMinTurnTrackFrac)
                return (false, $"smoothed knee swivel doesn't track a real turn (|{s.TurnSmoothChangeDeg:F0}| of |{s.TurnRawChangeDeg:F0}| deg < {LegTwistMinTurnTrackFrac:P0}) -- over-smoothed/frozen");
            if (s.TurnMaxLagDeg > LegTwistMaxTurnLagDeg)
                return (false, $"turn-tracking lag {s.TurnMaxLagDeg:F0} deg > {LegTwistMaxTurnLagDeg} (over-smoothed)");
            return (true, $"standing twist raw {s.WorstRawP2PDeg:F1}->smoothed {s.WorstSmoothedP2PDeg:F1} deg @ ext {s.WorstExt:F2} ({s.WorstReductionFrac:P0}); turn tracks {s.TurnSmoothChangeDeg:F0}/{s.TurnRawChangeDeg:F0} deg lag {s.TurnMaxLagDeg:F0}");
        }

        public static (bool pass, string reason) GateHead(in BasisHeadSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Rows <= 0) return (false, "no rows");
            if (float.IsNaN(s.MaxNeckDeg) || s.MaxNeckDeg > HeadMaxNeckDeg)
                return (false, $"max neck {s.MaxNeckDeg:F0} > {HeadMaxNeckDeg} deg");
            if (float.IsNaN(s.ExtremeOnsetPitch)) return (false, "extreme region never engaged");
            return (true, $"maxNeck={s.MaxNeckDeg:F0} extremeOnset={s.ExtremeOnsetPitch:F0}");
        }

        // Trajectory gates take fields (not the struct) so the same gate serves arm/elbow/head trajectory
        // summaries. Pops = swivel jumps on smooth hand motion; rough = swivel jitter under tracking noise.
        public static (bool pass, string reason) GateTrajectory(bool ok, string error, float worstPopDeg, float worstRoughDeg)
        {
            if (!ok) return (false, string.IsNullOrEmpty(error) ? "did not run" : error);
            if (worstPopDeg > TrajMaxPopDeg)
                return (false, $"pop {worstPopDeg:F0} > {TrajMaxPopDeg} deg (snap on smooth motion)");
            if (worstRoughDeg > TrajMaxRoughDeg)
                return (false, $"rough {worstRoughDeg:F1} > {TrajMaxRoughDeg} deg (jitter under noise)");
            return (true, $"pop={worstPopDeg:F0} rough={worstRoughDeg:F1}");
        }

        // Per-frame feedback drive: worstRoughDeg is the clean 2nd-difference (stepping as the hand glides),
        // the live-only jitter the stateless scan can't see.
        public static (bool pass, string reason) GateTemporal(bool ok, string error, float worstPopDeg, float worstRoughDeg)
        {
            if (!ok) return (false, string.IsNullOrEmpty(error) ? "did not run" : error);
            if (worstPopDeg > TrajMaxPopDeg)
                return (false, $"pop {worstPopDeg:F0} > {TrajMaxPopDeg} deg");
            if (worstRoughDeg > TemporalMaxRoughDeg)
                return (false, $"glide-jitter {worstRoughDeg:F2} > {TemporalMaxRoughDeg} deg/step (elbow steps as the hand glides)");
            return (true, $"pop={worstPopDeg:F0} glideJitter={worstRoughDeg:F2}");
        }

        // Tracker placement DISCOVERY: synthetic constellations over many body archetypes must map
        // each tracker to the role it was placed for. Hard invariants (a stale/origin tracker never
        // binds; no CLEAN tracker crosses to the opposite body side) gate unconditionally; jittered
        // cross-side is reported, not gated (heavy noise at a narrow stance is inherently ambiguous). Correctness
        // is gated strictly on the common "core" archetypes and at a tunable floor overall; extreme
        // archetypes are reported (CSV + confusions) rather than failing the gate.
        public static (bool pass, string reason) GateTrackerPlacement(in BasisTrackerPlacementSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Trackers <= 0) return (false, "no trackers");
            if (s.NearOriginLeaks > 0)
                return (false, $"{s.NearOriginLeaks} stale/origin tracker(s) bound to a role (must never happen)");
            if (s.CleanCrossSideLeaks > 0)
                return (false, $"{s.CleanCrossSideLeaks} clean tracker(s) bound to the opposite body side ({s.TopConfusions})");
            if (s.CoreCleanFraction < TrackerPlacementCoreMinFraction)
                return (false, $"core archetypes clean {s.CoreCleanFraction:P0} < {TrackerPlacementCoreMinFraction:P0} ({s.CoreCleanCorrect}/{s.CoreCleanTrackers}; {s.TopConfusions})");
            if (s.CleanCorrectFraction < TrackerPlacementCleanMinFraction)
                return (false, $"clean correctness {s.CleanCorrectFraction:P0} < {TrackerPlacementCleanMinFraction:P0} ({s.CleanCorrect}/{s.CleanTrackers}; {s.TopConfusions})");
            return (true, $"clean {s.CleanCorrectFraction:P0} (core {s.CoreCleanFraction:P0}) overall {s.Correct}/{s.Trackers} misassign={s.Misassigned} unassigned={s.Unassigned} crossSide(jittered)={s.CrossSideLeaks} heightBias={s.HeightBiasPts:F1}pts(short {s.ShortAccuracy:F0}%/tall {s.TallAccuracy:F0}%) minMargin={s.MinCorrectMargin:F1}");
        }

        // Multi-tracker ("double hip") rotation fusion: the fused virtual hip must track the body's rotation
        // since calibration the same as a single tracker would (the calibration offset cancels, so a correct
        // fusion reads ~0). Two layers: a regression guard that the projection math is exact for a RIGID pair,
        // then the shipping behavior under a device-churn re-prime must stay within tolerance -- if it doesn't,
        // the gate names the robust convention the sweep found.
        public static (bool pass, string reason) GateMultiTrackerRotation(in BasisMultiTrackerRotationSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Rows <= 0) return (false, "no rows");
            int stable = s.StableConvIndex, cur = s.CurrentConvIndex, best = s.BestConvIndex;
            if (s.RigidWorstErrDeg[stable] > MultiTrackerProjectionRigidTolDeg)
                return (false, $"projected fusion off by {s.RigidWorstErrDeg[stable]:F1} deg for a RIGID pair (t-spread {s.TSpreadDeg[stable]:F1}) -- the projection math is wrong, not just the prime");
            // Gate on SYSTEMATIC error (the persistent hip offset), not transient flex passthrough -- the
            // latter is bounded by strap slop for every convention and isn't the bug being chased.
            float curSys = s.RigidWorstErrDeg[cur];
            float bestSys = s.RigidWorstErrDeg[best];
            string onset = float.IsNaN(s.OnsetYawDeg[cur]) ? "never" : $"{s.OnsetYawDeg[cur]:F0}deg yaw";
            if (curSys > MultiTrackerMaxErrDeg)
                return (false, $"re-prime convention '{s.ConvNames[cur]}' carries {curSys:F1} deg SYSTEMATIC hip-rotation error > {MultiTrackerMaxErrDeg} (onset {onset}); robust convention '{s.ConvNames[best]}' is {bestSys:F1} deg (flex passthrough: cur {s.FlexWorstErrDeg[cur]:F1}, best {s.FlexWorstErrDeg[best]:F1})");
            return (true, $"'{s.ConvNames[cur]}' systematic {curSys:F1} deg; best '{s.ConvNames[best]}' {bestSys:F1} deg (flex passthrough cur {s.FlexWorstErrDeg[cur]:F1})");
        }

        // Multi-tracker DYNAMIC behavior: synthetic body-motion trajectories driven frame-by-frame through the
        // real stateful fusion. A single tracker tracks the body ~1:1 with no extra lag or snap; the fused pair
        // must too. Failure here is the live "funky snap / lag" -- the pairing rotation low-pass and confidence
        // blend deviating from rigid tracking during motion -- which the static convention sweep can't see.
        public static (bool pass, string reason) GateMultiTrackerRotationTemporal(in BasisMultiTrackerRotationTemporalSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Frames <= 0) return (false, "no frames");
            if (s.WorstTrackErrDeg > MultiTrackerTemporalMaxErrDeg)
                return (false, $"fused rotation trails the body by up to {s.WorstTrackErrDeg:F1} deg in motion > {MultiTrackerTemporalMaxErrDeg} (a single tracker is ~0) -- the pairing low-pass/blend lags; lower PairingRotationHalfLife or drop the extra low-pass");
            if (s.WorstSnapDeg > MultiTrackerTemporalMaxSnapDeg)
                return (false, $"frame-to-frame step jumps up to {s.WorstSnapDeg:F1} deg > {MultiTrackerTemporalMaxSnapDeg} (a visible rotation snap; overshoot up to {s.WorstOvershootDeg:F1} deg)");
            return (true, $"tracks within {s.WorstTrackErrDeg:F1} deg, snap {s.WorstSnapDeg:F1} deg, overshoot {s.WorstOvershootDeg:F1} deg");
        }

        // --- calibration offset / rotation / height math tolerances ---
        public const float CalibMaxPosErrM = 5e-4f;         // offset + scale round-trip position error (metres)
        public const float CalibMaxRotErrDeg = 0.1f;        // offset + rotation-calibration round-trip error (degrees)
        public const float CalibMaxFeelHeightErrM = 1e-3f;  // DeviceScale feel: viewpoint must land on the avatar eye when the denominator is true (metres)
        public const float CalibMaxFeelFactorErr = 1e-3f;   // too-tall ratio of an under-bridged eye reference must equal E/(E-shortfall)

        // Calibration math: the offset capture↔apply, device-scale, and per-effector rotation formulas
        // must round-trip / land on their targets within float tolerance, and the scale modifier must
        // sanitize bad overrides. A failure means a calibration formula changed in a way that no longer
        // reproduces the bone/height it was derived from.
        public static (bool pass, string reason) GateCalibrationMath(in BasisCalibrationMathSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.MaxOffsetPosErr > CalibMaxPosErrM || s.MaxRigidFollowErr > CalibMaxPosErrM || s.MaxScalePosErr > CalibMaxPosErrM)
                return (false, $"offset/scale round-trip off: offsetPos={s.MaxOffsetPosErr:F5} follow={s.MaxRigidFollowErr:F5} scalePos={s.MaxScalePosErr:F5} m > {CalibMaxPosErrM} m");
            if (s.MaxOffsetRotErrDeg > CalibMaxRotErrDeg || s.MaxRotCalErrDeg > CalibMaxRotErrDeg)
                return (false, $"rotation off: offsetRot={s.MaxOffsetRotErrDeg:F3} rotCal={s.MaxRotCalErrDeg:F3} deg > {CalibMaxRotErrDeg} (rotation calibration would leak orientation)");
            if (s.ScaleModifierMismatches > 0)
                return (false, $"{s.ScaleModifierMismatches} scale-modifier sanitization/FinalScale mismatches");
            if (s.MaxFeelHeightErr > CalibMaxFeelHeightErrM || s.MaxFeelFactorErr > CalibMaxFeelFactorErr)
                return (false, $"feel height off: viewpoint err={s.MaxFeelHeightErr:F5} m > {CalibMaxFeelHeightErrM} or too-tall ratio err={s.MaxFeelFactorErr:F5} > {CalibMaxFeelFactorErr} (DeviceScale no longer lands the viewpoint on the avatar eye)");
            return (true, $"offsetPos={s.MaxOffsetPosErr:F5}m rotCal={s.MaxRotCalErrDeg:F3}deg scalePos={s.MaxScalePosErr:F5}m feel={s.MaxFeelHeightErr:F5}m cases={s.Cases} fails={s.Failures}");
        }

        // Procedural foot placement (BasisFootSimulateJob): a temporal stepping system, so the gate reads
        // the worst per-frame metric across the scripted locomotion battery (walk/strafe/turn/stop/slope/
        // stairs/gap). The invariants: feet never lift together or tangle, a planted foot is world-locked
        // (no skate) and on the ground, the leg can always reach its foot, the tilt/yaw clamps hold, and the
        // knee hint never falls behind the leg. NaN anywhere = the sim blew up.
        public static (bool pass, string reason) GateFoot(in BasisFootIKSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Rows <= 0 || s.Scenarios <= 0) return (false, "no rows");
            if (s.HadNaN) return (false, "NaN foot position / knee hint (the sim blew up)");
            // Checked EARLY and before hover on purpose: a false airborne makes the feet abandon the floor for a
            // hips-relative fallback (the whole avatar hovers) AND suppresses the hover metric that would have
            // reported it, so it can mask itself. Standing still is not airborne.
            if (s.SimAirborneWhileGroundedTicks > 0)
                return (false, $"sim reported AIRBORNE for {s.SimAirborneWhileGroundedTicks} ticks in scenarios where the hips never left standing height (first: {s.SimAirborneWorstScenario}) -- the feet abandon the floor and the avatar hovers");
            if (s.BothSteppingTicks > 0)
                return (false, $"both feet airborne for {s.BothSteppingTicks} ticks (worst: {s.BothSteppingWorstScenario} {s.BothSteppingWorstTicks}) -- a foot must never lift while the other is stepping");
            if (s.Crossovers - s.CrossoversTolerated > 0)
                return (false, $"feet crossed/tangled on {s.Crossovers - s.CrossoversTolerated} ticks (+{s.CrossoversTolerated} tolerated during fast spin) -- left foot ended up right of the right foot");
            if (s.IdleSteps > FootMaxIdleSteps)
                return (false, $"foot micro-stepped {s.IdleSteps} times during a gentle standing sway > {FootMaxIdleSteps} (an idle weight-shift should not lift a foot -- the leg should stay planted)");
            if (s.WorstExtensionRatio > FootMaxExtensionRatio)
                return (false, $"foot over-extended to {s.WorstExtensionRatio:F2}x standing reach > {FootMaxExtensionRatio:F2} (foot left behind -- the leg stretches)");
            if (s.WorstPlantedSlideMm > FootMaxPlantedSlideMm)
                return (false, $"planted-foot slide {s.WorstPlantedSlideMm:F0}mm/tick > {FootMaxPlantedSlideMm}mm (skating)");
            if (s.WorstPenetrationMm > FootMaxPenetrationMm)
                return (false, $"planted foot {s.WorstPenetrationMm:F0}mm below ground > {FootMaxPenetrationMm}mm");
            if (s.WorstPlantedHoverMm > FootMaxPlantedHoverMm)
                return (false, $"planted foot floats {s.WorstPlantedHoverMm:F0}mm above ground > {FootMaxPlantedHoverMm}mm");
            if (s.WorstTiltDeg > s.ConfiguredMaxTiltDeg + FootTiltSlackDeg)
                return (false, $"foot tilt {s.WorstTiltDeg:F0} > clamp {s.ConfiguredMaxTiltDeg:F0}+{FootTiltSlackDeg} deg (slope clamp not holding)");
            if (s.WorstYawDeg > s.ConfiguredMaxYawDeg + FootYawSlackDeg)
                return (false, $"foot yaw {s.WorstYawDeg:F0} > clamp {s.ConfiguredMaxYawDeg:F0}+{FootYawSlackDeg} deg (toe-out clamp not holding)");
            if (s.WorstKneeBackwardM > FootMaxKneeBehindM)
                return (false, $"knee hint {s.WorstKneeBackwardM * 100f:F1}cm behind the leg > {FootMaxKneeBehindM * 100f:F0}cm (knee would bend backward)");
            return (true, $"slide {s.WorstPlantedSlideMm:F1}mm ext {s.WorstExtensionRatio:F2} pen {s.WorstPenetrationMm:F0}mm tilt {s.WorstTiltDeg:F0} yaw {s.WorstYawDeg:F0} drift {s.WorstPlantToIdealM * 100f:F0}cm steps {s.TotalSteps} idleSteps {s.IdleSteps} idleKneeGain(fwd {s.IdleKneeGainFwd:F1} vert {s.IdleKneeGainVert:F1})");
        }

        // --- twist (swing-twist decomposition) ---
        public const float TwistMaxErrDeg = 0.5f;        // pure-twist recovery + partial-twist blend error
        public const float TwistMaxAxisMisalignDeg = 1f; // extracted twist axis vs bone axis with a swing present
        public const float TwistMaxConcentrationRatio = 1.25f; // even-distribution: worst per-span twist rate / the even ideal (1 = perfect). >this = roll piling up at one joint (candy-wrapper). Old fixed-fraction hit ~5-9x at wrist-end bones.
        public const float TwistMaxEvennessErrDeg = 2f;  // twist bone's deviation from the linear (even) roll gradient

        // BasisTwistSolveCore: a pure twist about the bone axis must be recovered exactly, the Fraction must
        // blend it linearly, a perpendicular swing must not tilt the extracted twist axis, and the singular
        // inputs (no fraction / zero bone vector) must no-op.
        public static (bool pass, string reason) GateTwist(in BasisTwistSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.SingularityFailures > 0)
                return (false, $"{s.SingularityFailures} singular cases applied a twist (must no-op)");
            if (s.MaxPureTwistErrDeg > TwistMaxErrDeg || s.MaxFractionErrDeg > TwistMaxErrDeg)
                return (false, $"twist recovery off: pure={s.MaxPureTwistErrDeg:F3} frac={s.MaxFractionErrDeg:F3} deg > {TwistMaxErrDeg}");
            if (s.MaxAxisMisalignDeg > TwistMaxAxisMisalignDeg)
                return (false, $"swing tilted the extracted twist axis by {s.MaxAxisMisalignDeg:F2} deg > {TwistMaxAxisMisalignDeg} (swing-twist not separating)");
            // Even distribution: a single twist bone must take a share == its position along the bone, so the
            // roll spreads as a linear gradient instead of piling up at one joint (the candy-wrapper).
            if (s.DistributionCases > 0 && s.MaxConcentrationRatio > TwistMaxConcentrationRatio)
                return (false, $"twist piles up at one joint: concentration {s.MaxConcentrationRatio:F2}x the even rate > {TwistMaxConcentrationRatio} (candy-wrapper -- distribution not position-proportional)");
            if (s.DistributionCases > 0 && s.MaxEvennessErrDeg > TwistMaxEvennessErrDeg)
                return (false, $"twist gradient off linear by {s.MaxEvennessErrDeg:F1} deg > {TwistMaxEvennessErrDeg} (uneven distribution)");
            return (true, $"pure={s.MaxPureTwistErrDeg:F3} frac={s.MaxFractionErrDeg:F3} axisMis={s.MaxAxisMisalignDeg:F2} deg conc={s.MaxConcentrationRatio:F2}x even={s.MaxEvennessErrDeg:F1} cases={s.Cases}/{s.DistributionCases}");
        }

        // --- virtual spine + remote bone chain ---
        public const float SpineMaxErr = 1e-3f;       // chain fraction / hips offset / yaw-flatness (metres or unitless)
        public const float SpineMaxYawDegErr = 0.1f;  // YawDegrees recovery + yaw-extraction idempotence (degrees)
        public const float RemoteBoneMaxErr = 1e-3f;  // remote FK composition / segment-length / scale-linearity (metres)

        // Virtual spine solve helpers: the chest/spine must sit on the neck→hips segment at their fractions
        // (no inversion), hips must drop the spine length below the neck with the pelvic bias, and yaw
        // extraction must strip pitch/roll. A failure means the torso synthesis geometry changed.
        public static (bool pass, string reason) GateSpine(in BasisSpineSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.ChainMonotonicFails > 0)
                return (false, $"{s.ChainMonotonicFails} chains put the chest farther from the neck than the spine (inverted)");
            if (s.MaxChainFracErr > SpineMaxErr)
                return (false, $"chest/spine off their neck→hips fraction by {s.MaxChainFracErr:F4} > {SpineMaxErr}");
            if (s.MaxHipsYErr > SpineMaxErr || s.MaxHipsXZErr > SpineMaxErr || s.MaxHipsFreezeErr > SpineMaxErr)
                return (false, $"hips position off: y={s.MaxHipsYErr:F4} xz={s.MaxHipsXZErr:F4} freeze={s.MaxHipsFreezeErr:F4} m > {SpineMaxErr}");
            if (s.MaxYawFlatErr > SpineMaxErr)
                return (false, $"extracted yaw not pitch/roll-free (forward.y={s.MaxYawFlatErr:F4} > {SpineMaxErr})");
            if (s.MaxYawIdempotentErr > SpineMaxYawDegErr || s.MaxYawDegErr > SpineMaxYawDegErr)
                return (false, $"yaw off: idempotence={s.MaxYawIdempotentErr:F3} degRecovery={s.MaxYawDegErr:F3} deg > {SpineMaxYawDegErr}");
            return (true, $"chainFrac={s.MaxChainFracErr:F4} hipsY={s.MaxHipsYErr:F4} yawFlat={s.MaxYawFlatErr:F4} yawDeg={s.MaxYawDegErr:F3} cases={s.Cases}");
        }

        // Remote head-chain FK: each child must compose as parent + headRot*offset, segment lengths must be
        // rotation-preserved and scale linearly, and outputs must stay finite at extreme/zero scale. (Hand/
        // foot end-effector drift is a play-mode measurement, not covered here.)
        public static (bool pass, string reason) GateRemoteBone(in BasisRemoteBoneSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.NaNCount > 0)
                return (false, $"{s.NaNCount} remote chains produced non-finite positions");
            if (s.MaxCompErr > RemoteBoneMaxErr || s.MaxSegLenErr > RemoteBoneMaxErr || s.MaxScaleErr > RemoteBoneMaxErr)
                return (false, $"remote FK off: comp={s.MaxCompErr:F4} segLen={s.MaxSegLenErr:F4} scale={s.MaxScaleErr:F4} m > {RemoteBoneMaxErr}");
            return (true, $"comp={s.MaxCompErr:F4} segLen={s.MaxSegLenErr:F4} scale={s.MaxScaleErr:F4} cases={s.Cases}");
        }

        // --- body self-collision capsule geometry ---
        public const float CapsuleMaxGeomErr = 1e-3f;      // closest-point param / fixed-point / symmetry (m, exact math)
        public const float CapsuleMaxKktResidual = 1e-2f;  // interior closest point perpendicular to its segment (unit dot)
        public const float CapsuleMaxDepthErrM = 1e-3f;    // reported push depth vs the real overlap (m)
        public const float CapsuleMaxResidualPenMm = 2f;   // leftover penetration after the push must clear in one step
        public const float CapsuleMaxPushOutMm = 1f;       // PushOutFromCapsule must land on the radius surface

        // Capsule collision primitives (BasisFullIKConstraintJob.ClosestPointOnSegment /
        // SegmentSegmentClosestPoints / CapsuleCapsuleResolve / PushOutFromCapsule): the geometry that
        // keeps the hand/elbow out of the torso. These are exact, so the gate is on exact certificates --
        // closest points on their segments and perpendicular (KKT), symmetric under swap, the push depth
        // equals the overlap and resolves it, and never NaN at degenerate (point/parallel/coincident) inputs.
        public static (bool pass, string reason) GateCapsuleCollision(in BasisCapsuleCollisionSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.OverlapCases <= 0) return (false, "no penetrating pairs (sweep not exercising the resolve)");
            if (s.NaNCount > 0)
                return (false, $"{s.NaNCount} cases produced non-finite closest points / push (degenerate input not handled)");
            if (s.MaxClosestParamErr > CapsuleMaxGeomErr || s.MaxFixedPointErr > CapsuleMaxGeomErr || s.MaxSymmetryErr > CapsuleMaxGeomErr)
                return (false, $"closest-point off: param={s.MaxClosestParamErr:F5} fixedPoint={s.MaxFixedPointErr:F5} sym={s.MaxSymmetryErr:F5} m > {CapsuleMaxGeomErr}");
            if (s.MaxKktResidual > CapsuleMaxKktResidual)
                return (false, $"interior closest point not perpendicular to its segment (kkt {s.MaxKktResidual:F4} > {CapsuleMaxKktResidual}) -- not the true minimum");
            if (s.MaxPenetrationDepthErr > CapsuleMaxDepthErrM)
                return (false, $"push depth off by {s.MaxPenetrationDepthErr:F5} m > {CapsuleMaxDepthErrM} (over/under-resolves penetration)");
            if (s.MaxResidualPenMm > CapsuleMaxResidualPenMm)
                return (false, $"{s.MaxResidualPenMm:F1}mm penetration left after the push on a shallow overlap > {CapsuleMaxResidualPenMm}mm (MTD push not separating its design regime)");
            if (s.MaxPushOutSurfaceErrMm > CapsuleMaxPushOutMm)
                return (false, $"push-out lands {s.MaxPushOutSurfaceErrMm:F1}mm off the radius surface > {CapsuleMaxPushOutMm}mm");
            return (true, $"param={s.MaxClosestParamErr:F5} kkt={s.MaxKktResidual:F4} sym={s.MaxSymmetryErr:F5} depth={s.MaxPenetrationDepthErr:F5}m resid={s.MaxResidualPenMm:F1}mm pushout={s.MaxPushOutSurfaceErrMm:F1}mm overlap={s.OverlapCases}/{s.Cases} (deep/crossing {s.DeepOverlapCases}, worst {s.MaxDeepResidualPenMm:F0}mm, reported)");
        }

        // --- FBIK spine safety clamps ---
        public const float SpineClampMaxPosErrM = 1e-4f;     // distance/idempotence (m, exact clamps)
        public const float SpineClampMaxAngleErrDeg = 0.1f;  // bend/rotation limit overshoot + idempotence
        public const float SpineClampMaxAboveHeadM = 1e-3f;  // deep crouch: hips must never sit above the head (the flip)
        public const float SpineClampMaxCrouchStepM = 0.3f;  // deep crouch: hips must not jump/teleport as the head passes hip level

        // FBIK spine clamps (ClampHipsAroundHead / EnforceSpineBendLimit / AntiContortionist /
        // MitigateSpineBuckling / ClampRotation): the guards that keep the torso from contorting. Each
        // encodes a hard limit -- the gate proves the limit holds, the clamp is idempotent (re-clamping a
        // clamped pose moves nothing) and a within-limit pose is left untouched. (Virtual-spine synthesis
        // is GateSpine; this is the head/hips sanity layer.)
        public static (bool pass, string reason) GateSpineClamp(in BasisSpineClampSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.NaNCount > 0)
                return (false, $"{s.NaNCount} cases produced non-finite output");
            if (s.MaxBendOverDeg > SpineClampMaxAngleErrDeg)
                return (false, $"EnforceSpineBendLimit leaves a {s.MaxBendOverDeg:F2} deg over-bend > {SpineClampMaxAngleErrDeg} (limit not holding)");
            if (s.MaxClampRotOverDeg > SpineClampMaxAngleErrDeg)
                return (false, $"ClampRotation result {s.MaxClampRotOverDeg:F2} deg past the limit > {SpineClampMaxAngleErrDeg}");
            if (s.MaxHipsClampOverErrM > SpineClampMaxPosErrM || s.MaxHipsClampDirErrDeg > SpineClampMaxAngleErrDeg)
                return (false, $"ClampHipsAroundHead off: dist {s.MaxHipsClampOverErrM:F5} m / dir {s.MaxHipsClampDirErrDeg:F2} deg (distance clamp or ray drifted)");
            if (s.MaxAntiContortDeficitM > SpineClampMaxPosErrM)
                return (false, $"AntiContortionist left hips {s.MaxAntiContortDeficitM:F5} m inside its min distance > {SpineClampMaxPosErrM}");
            if (s.MaxBucklingHorizM > SpineClampMaxPosErrM || s.MaxBucklingPushErrM > SpineClampMaxPosErrM)
                return (false, $"MitigateSpineBuckling off: horiz drift {s.MaxBucklingHorizM:F5} / push err {s.MaxBucklingPushErrM:F5} m (not a pure vertical push)");
            float idem = s.MaxHipsClampIdempotentM;
            if (s.MaxBendIdempotentM > idem) idem = s.MaxBendIdempotentM;
            if (s.MaxAntiContortIdempotentM > idem) idem = s.MaxAntiContortIdempotentM;
            if (idem > SpineClampMaxPosErrM || s.MaxClampRotIdempotentDeg > SpineClampMaxAngleErrDeg)
                return (false, $"a clamp is not idempotent: pos {idem:F5} m / rot {s.MaxClampRotIdempotentDeg:F2} deg (re-clamping a clamped pose moves it)");
            if (s.MaxHipsAboveHeadM > SpineClampMaxAboveHeadM)
                return (false, $"deep crouch: ClampHipsAroundHead drove the hips {s.MaxHipsAboveHeadM * 100f:F1}cm ABOVE the head > {SpineClampMaxAboveHeadM * 100f:F1}cm (the body flips / hips fly up)");
            if (s.MaxBendAboveHeadM > SpineClampMaxAboveHeadM)
                return (false, $"deep crouch: EnforceSpineBendLimit left the hips {s.MaxBendAboveHeadM * 100f:F1}cm above the head > {SpineClampMaxAboveHeadM * 100f:F1}cm (inverted)");
            if (s.MaxChainAboveHeadM > SpineClampMaxAboveHeadM)
                return (false, $"deep crouch: the composed clamp chain left the hips {s.MaxChainAboveHeadM * 100f:F1}cm above the head > {SpineClampMaxAboveHeadM * 100f:F1}cm (pipeline still flips)");
            if (s.MaxCrouchStepM > SpineClampMaxCrouchStepM)
                return (false, $"deep crouch: hips jump {s.MaxCrouchStepM * 100f:F0}cm as the head passes hip level > {SpineClampMaxCrouchStepM * 100f:F0}cm (sideways/up fling, not a smooth crouch)");
            return (true, $"bendOver={s.MaxBendOverDeg:F2}° rotOver={s.MaxClampRotOverDeg:F2}° hips={s.MaxHipsClampOverErrM:F5}m antiDef={s.MaxAntiContortDeficitM:F5}m idem={idem:F5}m crouch(above={s.MaxHipsAboveHeadM * 100f:F1}cm chain={s.MaxChainAboveHeadM * 100f:F1}cm step={s.MaxCrouchStepM * 100f:F0}cm) cases={s.Cases}");
        }

        // --- hip hinge (pelvis pitch sharing forward lean) ---
        // ApplyHipHinge must engage only past the onset, cap at the configured max, rotate the pelvis by
        // exactly that much about a horizontal axis, grow monotonically with lean, and no-op when disabled
        // or below onset. A disabled-but-moved or non-monotonic result is a tuning/structure regression.
        public static (bool pass, string reason) GateHipHinge(in BasisHipHingeSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.EngagedCases <= 0) return (false, "hinge never engaged (sweep not exercising it)");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite results");
            if (s.DisabledMoves > 0)
                return (false, $"{s.DisabledMoves} cases pitched the pelvis while disabled or below onset (must no-op)");
            if (s.MaxOverAddDeg > 0.05f)
                return (false, $"pelvis pitch {s.MaxOverAddDeg:F2} deg past the cap (MaxAddDeg not holding)");
            if (s.MaxAngleMatchErrDeg > 0.05f)
                return (false, $"applied rotation off the reported add by {s.MaxAngleMatchErrDeg:F2} deg");
            if (s.MaxAxisDotUp > 1e-2f)
                return (false, $"hinge axis not horizontal (axis·up {s.MaxAxisDotUp:F3}) -- pelvis yaws/rolls instead of pitching");
            if (s.MonotonicViolations > 0)
                return (false, $"{s.MonotonicViolations} cases where pitch dropped as lean grew (non-monotonic response)");
            return (true, $"engaged={s.EngagedCases} over={s.MaxOverAddDeg:F2}° match={s.MaxAngleMatchErrDeg:F2}° axisUp={s.MaxAxisDotUp:F3} cases={s.Cases}");
        }

        // --- chest-follow spring (implicit Euler stability) ---
        // The spring uses implicit Euler so it is unconditionally stable. The gate is exactly that claim:
        // it must NEVER diverge across the hz/damping/fps grid (incl. low fps where explicit Euler would),
        // well-damped configs must settle on the target, and over-damped configs must not overshoot.
        public static (bool pass, string reason) GateChestSpring(in BasisChestSpringSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Configs <= 0) return (false, "no configs");
            if (s.DivergedCount > 0)
                return (false, $"{s.DivergedCount}/{s.Configs} configs diverged (NaN/blow-up) -- implicit Euler must be unconditionally stable");
            if (s.MaxFinalErrSettling > 0.05f)
                return (false, $"well-damped configs settle {s.MaxFinalErrSettling:F3} off the target > 0.05 (not converging)");
            if (s.MaxOverdampedOvershoot > 0.1f)
                return (false, $"over-damped configs overshoot by {s.MaxOverdampedOvershoot:F3} > 0.1 (should be monotone)");
            return (true, $"stable {s.Configs}/{s.Configs} (explicit Euler would diverge on {s.ExplicitDivergedCount}); settleErr={s.MaxFinalErrSettling:F3} overdampedOvershoot={s.MaxOverdampedOvershoot:F3}");
        }

        // --- crouch body offset (sit-back when squatting) ---
        // ApplyCrouchBodyOffset must move the hips back by exactly the corpus curve (EvaluateSetback), land
        // them on the rest-length sphere once engaged, leak nothing laterally, grow monotonically with depth,
        // and never move while standing, below the deadzone, or disabled.
        public static (bool pass, string reason) GateCrouchOffset(in BasisCrouchOffsetSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.AppliedCases <= 0) return (false, "offset never engaged (sweep not exercising it)");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite results");
            if (s.StandingMoves > 0)
                return (false, $"{s.StandingMoves} cases moved the hips while standing/disabled (must no-op)");
            if (s.MaxMagErrM > 1e-4f)
                return (false, $"backward slide off the corpus curve by {s.MaxMagErrM:F6} m");
            if (s.MaxSphereErrM > 1e-4f)
                return (false, $"hips leave the rest-length sphere by {s.MaxSphereErrM:F6} m (spine would stretch/compress)");
            if (s.MaxLateralLeakM > 1e-4f)
                return (false, $"slide leaks {s.MaxLateralLeakM:F6} m sideways off hips-back");
            if (s.MonotonicViolations > 0)
                return (false, $"{s.MonotonicViolations} cases where the setback shrank as depth grew (non-monotonic)");
            return (true, $"applied={s.AppliedCases} magErr={s.MaxMagErrM:F6}m sphere={s.MaxSphereErrM:F6}m lat={s.MaxLateralLeakM:F6}m cases={s.Cases}");
        }

        // Virtual-spine hips compression: a deep head drop (touch toes / sit) must NOT sink the pelvis the full
        // rigid distance -- the spine shortens so the planted-foot knee stays straighter and the seated hips stay
        // up. Verified end-to-end through the leg solve. At/above standing it must be a no-op.
        public static (bool pass, string reason) GateSpineCompression(in BasisSpineCompressionSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no engaged cases (sweep not exercising the compression band)");
            if (s.NanCount > 0) return (false, $"{s.NanCount} non-finite hips results");
            if (s.AboveStandingMaxDevM > SpineCompMaxAboveStandingDevM)
                return (false, $"pose shifted {s.AboveStandingMaxDevM:F5} m while head >= standing > {SpineCompMaxAboveStandingDevM} (idle posture must not change)");
            if (s.MinKneeStraighterDeg < SpineCompMinKneeStraighterDeg)
                return (false, $"knee only {s.MinKneeStraighterDeg:F1} deg straighter than rigid in the engaged band < {SpineCompMinKneeStraighterDeg} (compression not engaging)");
            if (s.KneeAtDeepLeanOnDeg < SpineCompMinDeepLeanKneeDeg)
                return (false, $"deep-lean knee folds to {s.KneeAtDeepLeanOnDeg:F0} deg < {SpineCompMinDeepLeanKneeDeg} (still buckling; rigid was {s.KneeAtDeepLeanOffDeg:F0})");
            float sinkFrac = s.MaxPelvisSinkRigidM > 1e-4f ? s.MaxPelvisSinkM / s.MaxPelvisSinkRigidM : 0f;
            if (sinkFrac > SpineCompMaxSinkFracOfDrop)
                return (false, $"pelvis sinks {s.MaxPelvisSinkM:F3} m = {sinkFrac:P0} of the rigid drop > {SpineCompMaxSinkFracOfDrop:P0} (hips still pulled down)");
            if (s.MaxStepDiscontinuityM > SpineCompMaxStepDiscontinuityM)
                return (false, $"compressed hips jump {s.MaxStepDiscontinuityM:F3} m between steps > {SpineCompMaxStepDiscontinuityM} (pop)");
            return (true, $"deepLeanKnee={s.KneeAtDeepLeanOnDeg:F0}/{s.KneeAtDeepLeanOffDeg:F0}deg(on/rigid) kneeGain>={s.MinKneeStraighterDeg:F1}deg sink={s.MaxPelvisSinkM:F3}m({sinkFrac:P0} of rigid) cases={s.Cases}");
        }

        // Scapulohumeral coupling: with no shoulder tracker the girdle swing the elbow rides must stay bounded and
        // be meaningfully smaller than the pre-fix coupling, so the elbow tracks the hand instead of trailing.
        public static (bool pass, string reason) GateShoulderCouple(in BasisShoulderCoupleSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.EngagedCases <= 0) return (false, "girdle never engaged (sweep not raising the arm)");
            if (s.NanCount > 0) return (false, $"{s.NanCount} non-finite results");
            if (s.MaxAppliedShippedDeg > ShoulderCoupleMaxAppliedDeg)
                return (false, $"girdle swings {s.MaxAppliedShippedDeg:F0} deg > {ShoulderCoupleMaxAppliedDeg} (elbow can trail far)");
            if (s.MinReductionFrac < ShoulderCoupleMinReductionFrac)
                return (false, $"shipped girdle only {s.MinReductionFrac:P0} less than legacy < {ShoulderCoupleMinReductionFrac:P0} (coupling not reduced enough)");
            if (s.MonotonicViolations > 0)
                return (false, $"{s.MonotonicViolations} non-monotonic steps as the arm raises (girdle pop)");
            return (true, $"appliedMax={s.MaxAppliedShippedDeg:F0}deg(legacy {s.MaxAppliedLegacyDeg:F0}) reduction>={s.MinReductionFrac:P0} engaged={s.EngagedCases}");
        }

        // --- spine bend distribution (per-axis spine/upperChest) ---
        public const float SpineBendTwistMaxJumpDeg = 30f; // raw twist step across center; a branch snap is ~180-360
        public const float SpineBendLookDownBendDriftDeg = 0.5f; // forward bend must not move as the gaze pitches (the fade is twist-only)
        public const float SpineBendMaxCouplingErrDeg = 0.05f; // bend->twist coupling: euler.y vs coupling*euler.z, and zero at coupling=0

        // DistributeSpineBend: the asymmetric flexion clamp must hold per axis, the rest deadband must zero
        // the bend (pitch/roll), the squish multiplier must stay in [1-boost, 1+boost], PelvicTwistRouting
        // must keep the 25/75 spine:upperChest yaw split, and -- the documented bug -- the spine twist must
        // stay CONTINUOUS as the head yaws across center with a yawed hips bind (no +/-360 snap).
        public static (bool pass, string reason) GateSpineBend(in BasisSpineBendSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite results");
            if (s.TwistMaxJumpDeg > SpineBendTwistMaxJumpDeg)
                return (false, $"spine twist jumps {s.TwistMaxJumpDeg:F0} deg across center > {SpineBendTwistMaxJumpDeg} (atan2 branch snap -- the hips-bind cancellation regressed)");
            if (s.LookDownTwistMaxJumpDeg > SpineBendTwistMaxJumpDeg)
                return (false, $"spine twist jumps {s.LookDownTwistMaxJumpDeg:F0} deg looking down through vertical > {SpineBendTwistMaxJumpDeg} (the vertical-gaze azimuth fade regressed -- chest snaps out of the way)");
            if (s.LookDownBendDriftDeg > SpineBendLookDownBendDriftDeg)
                return (false, $"forward bend drifts {s.LookDownBendDriftDeg:F2} deg as the gaze pitches > {SpineBendLookDownBendDriftDeg} (the vertical-gaze twist fade bled into the lean)");
            if (s.MaxClampOverDeg > 0.05f)
                return (false, $"bend exceeds the asymmetric flexion clamp by {s.MaxClampOverDeg:F2} deg (limit not holding)");
            if (s.MaxDeadbandLeakDeg > 0.05f)
                return (false, $"{s.MaxDeadbandLeakDeg:F2} deg of bend leaks through the rest deadband (micro-misalignment amplified)");
            if (s.MaxSquishErr > 1e-3f)
                return (false, $"squish multiplier off the [1-boost,1+boost] range by {s.MaxSquishErr:F4}");
            if (s.MaxYawSplitErr > 1e-3f)
                return (false, $"PelvicTwistRouting off the 25/75 spine:upperChest split by {s.MaxYawSplitErr:F4}");
            if (s.MaxCouplingBaselineDeg > SpineBendMaxCouplingErrDeg)
                return (false, $"a forward-facing lateral lean adds {s.MaxCouplingBaselineDeg:F3} deg of twist at coupling=0 > {SpineBendMaxCouplingErrDeg} (spurious yaw -- coupling not isolated)");
            if (s.MaxCouplingErrDeg > SpineBendMaxCouplingErrDeg)
                return (false, $"bend->twist coupling off by {s.MaxCouplingErrDeg:F3} deg > {SpineBendMaxCouplingErrDeg} (euler.y != coupling * euler.z -- not a proportion of the lateral bend)");
            return (true, $"twistJump={s.TwistMaxJumpDeg:F1}° lookDownTwistJump={s.LookDownTwistMaxJumpDeg:F1}° lookDownBendDrift={s.LookDownBendDriftDeg:F2}° clampOver={s.MaxClampOverDeg:F2}° deadband={s.MaxDeadbandLeakDeg:F2}° squish={s.MaxSquishErr:F4} yawSplit={s.MaxYawSplitErr:F4} coupling(err={s.MaxCouplingErrDeg:F3}° base={s.MaxCouplingBaselineDeg:F3}°) cases={s.Cases}");
        }

        // --- spine CCD twist shaping (graded + orientation-independent: a lateral head reach must BEND) ---
        public const float SpineTwistMaxInvariantErrDeg = 0.5f;     // ShapeReachStep identity / residual twist+swing / blend
        public const float SpineTwistMinReproDeg = 5f;             // keep=1 must actually provoke the corkscrew
        public const float SpineTwistMaxKeptFraction = 0.7f;       // graded peak twist vs keep=1 -- the shape must cut it
        public const float SpineTwistMaxLumbarKeptFraction = 0.55f; // graded lumbar twist vs keep=1 -- the lumbar must stiffen
        public const float SpineTwistMaxJumpDeg = 8f;             // graded twist must stay continuous across center
        public const float SpineTwistMaxReachErrM = 0.03f;        // bending instead of twisting must still reach (on-sphere => slop only)
        public const float SpineTwistMaxOrientationSpreadDeg = 2f; // upright vs lying-down result must match (body-relative axis)
        public const float SpineTwistMaxMidBendIncreaseDeg = 0.5f; // thoracic stiffening (even curvature) must not INCREASE the mid-joint bend

        // BasisTwistSolveCore.ShapeReachStep + the graded spine CCD it drives. Pass A: the swing/twist split is
        // exact (twistKeep=1/swingScale=1 no-ops, twistKeep=0 removes the twist, swingScale=0 removes the swing,
        // the twist blends linearly). Pass B: the documented bug -- a forward-pitched spine reaching a laterally-
        // swept head pours the offset into axial twist that flips sign across center ("hips left, head right, it
        // flips"). The gate proves keep=1 reproduces that corkscrew, the graded shape cuts it (and stiffens the
        // lumbar specifically), keeps it continuous, still reaches, and -- the lying-down requirement -- gives
        // the SAME result upright and supine because the twist axis is the body's hips-up, not world-up (Pass C
        // is the negative control: relaxing about world-up while prone leaves the corkscrew intact).
        public static (bool pass, string reason) GateSpineTwist(in BasisSpineTwistSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Cases <= 0) return (false, "no cases");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite results");
            if (s.MaxIdentityErrDeg > SpineTwistMaxInvariantErrDeg || s.MaxResidualTwistDeg > SpineTwistMaxInvariantErrDeg
                || s.MaxResidualSwingDeg > SpineTwistMaxInvariantErrDeg || s.MaxBlendErrDeg > SpineTwistMaxInvariantErrDeg
                || s.MaxSwingBlendErrDeg > SpineTwistMaxInvariantErrDeg)
                return (false, $"ShapeReachStep off: identity={s.MaxIdentityErrDeg:F3} residTwist={s.MaxResidualTwistDeg:F3} residSwing={s.MaxResidualSwingDeg:F3} twistBlend={s.MaxBlendErrDeg:F3} swingBlend={s.MaxSwingBlendErrDeg:F3} deg > {SpineTwistMaxInvariantErrDeg} (swing-twist not separating)");
            if (s.ReproMaxTwistDeg < SpineTwistMinReproDeg)
                return (false, $"keep=1 only twisted {s.ReproMaxTwistDeg:F1} deg < {SpineTwistMinReproDeg} (sweep not provoking the corkscrew -- it would miss a regression)");
            float kept = s.ReproMaxTwistDeg > 1e-3f ? s.GradedMaxTwistDeg / s.ReproMaxTwistDeg : 1f;
            if (kept > SpineTwistMaxKeptFraction)
                return (false, $"graded shape kept {kept:P0} of the axial twist ({s.GradedMaxTwistDeg:F1}/{s.ReproMaxTwistDeg:F1} deg) > {SpineTwistMaxKeptFraction:P0} (still corkscrews instead of bending)");
            float lumbarKept = s.ReproLumbarTwistDeg > 1e-3f ? s.GradedLumbarTwistDeg / s.ReproLumbarTwistDeg : 1f;
            if (lumbarKept > SpineTwistMaxLumbarKeptFraction)
                return (false, $"lumbar still kept {lumbarKept:P0} of its twist ({s.GradedLumbarTwistDeg:F1}/{s.ReproLumbarTwistDeg:F1} deg) > {SpineTwistMaxLumbarKeptFraction:P0} (lower back not stiffened -- grading not applied)");
            if (s.GradedMaxJumpDeg > SpineTwistMaxJumpDeg)
                return (false, $"graded twist jumps {s.GradedMaxJumpDeg:F1} deg across center > {SpineTwistMaxJumpDeg} (flips side instead of bending through)");
            if (s.GradedMaxReachErrM > SpineTwistMaxReachErrM)
                return (false, $"graded reach left a {s.GradedMaxReachErrM * 100f:F1} cm gap > {SpineTwistMaxReachErrM * 100f:F0} cm (bending can't reach -- shape too aggressive)");
            if (s.OrientationSpreadDeg > SpineTwistMaxOrientationSpreadDeg)
                return (false, $"upright vs lying-down twist differs by {s.OrientationSpreadDeg:F1} deg > {SpineTwistMaxOrientationSpreadDeg} (twist axis not body-relative -- breaks lying down)");
            if (s.WorldUpSupineTwistDeg < SpineTwistMinReproDeg)
                return (false, $"negative control weak: supine-about-world-up twisted only {s.WorldUpSupineTwistDeg:F1} deg < {SpineTwistMinReproDeg} (control not exercising the prone corkscrew -- can't prove the axis matters)");
            if (s.GradedMidBendDeg > s.UniformMidBendDeg + SpineTwistMaxMidBendIncreaseDeg)
                return (false, $"even curvature backwards: mid-thoracic stiffening INCREASED the mid-joint bend ({s.UniformMidBendDeg:F1}->{s.GradedMidBendDeg:F1} deg) instead of redistributing it to the lumbar/cervical");
            return (true, $"lumbar/neck keep {s.TestedLumbarKeep:F2}/{s.TestedCervicalKeep:F2} twist {s.ReproMaxTwistDeg:F1}->{s.GradedMaxTwistDeg:F1}° (kept {kept:P0}; lumbar {s.ReproLumbarTwistDeg:F1}->{s.GradedLumbarTwistDeg:F1}°, neck {s.GradedCervicalTwistDeg:F1}°) jump {s.GradedMaxJumpDeg:F1}° reach {s.GradedMaxReachErrM * 100f:F1}cm orient±{s.OrientationSpreadDeg:F1}° worldUpProne {s.WorldUpSupineTwistDeg:F1}° midBend {s.UniformMidBendDeg:F1}->{s.GradedMidBendDeg:F1}° swingBlend={s.MaxSwingBlendErrDeg:F3}° cases={s.Cases}");
        }

        // --- elbow swivel One-Euro filter ---
        public const float SwivelMaxStepOvershootDeg = 2f;   // first-order low-pass must not overshoot a step
        public const float SwivelMaxStepFinalErrDeg = 1f;    // must converge to a held value
        public const float SwivelMaxNoiseRejectRatio = 0.9f; // output must be smoother than the noisy input

        // SmoothElbowSwivel's One-Euro filter: it exists to kill elbow jitter without lag. The gate checks
        // the qualitative invariants (no calibration needed): never NaN, no overshoot on a step, converges,
        // and the smoothed output is rougher-than-input rejection ratio < 1. Ramp lag / glide jitter are
        // reported for tuning.
        public static (bool pass, string reason) GateSwivelFilter(in BasisSwivelFilterSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite samples (filter blew up)");
            if (s.MaxStepOvershootDeg > SwivelMaxStepOvershootDeg)
                return (false, $"step overshoot {s.MaxStepOvershootDeg:F2} deg > {SwivelMaxStepOvershootDeg} (low-pass should not overshoot)");
            if (s.StepFinalErrDeg > SwivelMaxStepFinalErrDeg)
                return (false, $"step did not converge: {s.StepFinalErrDeg:F2} deg residual > {SwivelMaxStepFinalErrDeg}");
            if (s.NoiseRejectRatio >= SwivelMaxNoiseRejectRatio)
                return (false, $"output not smoother than the noisy input (reject ratio {s.NoiseRejectRatio:F2} >= {SwivelMaxNoiseRejectRatio})");
            return (true, $"overshoot={s.MaxStepOvershootDeg:F2}° final={s.StepFinalErrDeg:F2}° noiseReject={s.NoiseRejectRatio:F2} rampLag={s.MaxRampLagDeg:F1}° glide={s.MaxGlideJitterDeg:F3}°");
        }

        // --- swing continuity (collision-gated elbow swing rate limiter) ---
        // ApplySwingContinuity must (1) never swing faster than rate*dt while easing a collision pop,
        // (2) converge to the target, (3) follow instantly in free air (no collision change), and
        // (4) re-seed on a target teleport. A rate violation or a stuck/laggy free-air swing is the live
        // "elbow pop / lag" regression this guards.
        public static (bool pass, string reason) GateSwingContinuity(in BasisSwingContinuitySweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite samples");
            if (s.RateLimitViolations > 0)
                return (false, $"{s.RateLimitViolations} frames swung faster than the rate limit (easing step up to {s.MaxEasingStepDeg:F1} deg)");
            if (!s.Converged)
                return (false, "easing never converged to the target (permanent lag after a collision pop)");
            if (s.FreeAirMaxLagDeg > 0.5f)
                return (false, $"free-air swing lags the target by {s.FreeAirMaxLagDeg:F2} deg > 0.5 (limiting non-collision motion)");
            if (!s.TeleportAccepted)
                return (false, "a target teleport did not re-seed instantly (limiter fights a re-pose)");
            return (true, $"rateOk easingStep={s.MaxEasingStepDeg:F2}° converged={s.ConvergeFrames}f freeAirLag={s.FreeAirMaxLagDeg:F2}° teleportOk");
        }

        // --- general-purpose One-Euro filter (Vector3 + Quaternion tracker/target smoothing) ---
        public const float OneEuroMaxStepOvershootM = 0.02f;    // first-order low-pass must not overshoot a step
        public const float OneEuroMaxStepFinalErrM = 0.02f;     // must converge to a held value
        public const float OneEuroMaxNoiseRejectRatio = 0.9f;   // output must be smoother than the noisy input
        public const float OneEuroMaxQuatNonUnit = 1e-3f;       // filtered quaternion must stay unit

        // The Vector3/Quaternion One-Euro filter the rig runs on tracker/target inputs before IK (the swivel
        // sweep only covers the scalar swivel filter). Same qualitative invariants (no calibration needed):
        // never NaN, no overshoot on a step, converges to a held value, the smoothed vec3 output is
        // rougher-than-input rejection ratio < 1, and the filtered quaternion stays unit. Ramp lag, glide
        // jitter, and the quaternion smoothing ratio are reported for tuning.
        public static (bool pass, string reason) GateOneEuro(in BasisOneEuroSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite samples (filter blew up)");
            if (s.MaxStepOvershootM > OneEuroMaxStepOvershootM)
                return (false, $"step overshoot {s.MaxStepOvershootM * 1000f:F0}mm > {OneEuroMaxStepOvershootM * 1000f:F0}mm (low-pass should not overshoot)");
            if (s.StepFinalErrM > OneEuroMaxStepFinalErrM)
                return (false, $"step did not converge: {s.StepFinalErrM * 1000f:F0}mm residual > {OneEuroMaxStepFinalErrM * 1000f:F0}mm");
            if (s.NoiseRejectRatio >= OneEuroMaxNoiseRejectRatio)
                return (false, $"vec3 output not smoother than the noisy input (reject ratio {s.NoiseRejectRatio:F2} >= {OneEuroMaxNoiseRejectRatio})");
            if (s.QuatMaxNonUnit > OneEuroMaxQuatNonUnit)
                return (false, $"filtered quaternion drifted off unit by {s.QuatMaxNonUnit:F4} > {OneEuroMaxQuatNonUnit} (re-normalization not holding)");
            return (true, $"stepOvershoot={s.MaxStepOvershootM * 1000f:F1}mm final={s.StepFinalErrM * 1000f:F1}mm noiseReject={s.NoiseRejectRatio:F2} rampLag={s.MaxRampLagM * 1000f:F0}mm glide={s.MaxGlideJitterM * 1000f:F2}mm quat(reject={s.QuatNoiseRejectRatio:F2} glide={s.QuatMaxGlideJitterDeg:F3}° nonUnit={s.QuatMaxNonUnit:F4})");
        }

        // --- eye gaze / saccade / vergence (BasisEyeState.Update) ---
        public const float EyeMaxClampOvershootDeg = 0.5f;  // gaze must stay under the maxAngle clamp
        public const float EyeMaxJumpOvershootDeg = 1.0f;   // eyes must not overshoot a fast target jump
        public const float EyeMaxSymmetryDeg = 2.0f;        // the two eyes must verge symmetrically about the focal

        // The per-eye gaze driver: gaze must never leave the maxAngle clamp, both eyes must verge TOWARD the
        // focal point (not away / not crossed), personality-driven idle saccades must hold within their bounds,
        // and the two eyes must stay symmetric. Repro guards (vergence sampled, saccades measured) ensure the
        // sweep actually exercised the state machine. Eye tracking is the maintainer's focus area.
        public static (bool pass, string reason) GateEye(in BasisEyeSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite eye samples (gaze math blew up)");
            if (s.ClampOvershootDeg > EyeMaxClampOvershootDeg)
                return (false, $"gaze exceeds the maxAngle clamp by {s.ClampOvershootDeg:F1} deg > {EyeMaxClampOvershootDeg} (clamp not holding; maxGaze {s.MaxGazeAngleDeg:F0} vs {s.ClampDeg:F0})");
            if (s.VergenceSamples <= 0) return (false, "vergence never sampled (sweep not exercising gaze)");
            if (s.VergenceWrongSign > 0)
                return (false, $"{s.VergenceWrongSign}/{s.VergenceSamples} eye-frames verged to the wrong side of the focal point (vergence sign inverted)");
            if (s.JumpMovedTowardOk != 1)
                return (false, "eyes moved AWAY from the jump target (diverged instead of converging)");
            if (s.JumpMaxOvershootDeg > EyeMaxJumpOvershootDeg)
                return (false, $"eyes overshoot the jump target by {s.JumpMaxOvershootDeg:F1} deg > {EyeMaxJumpOvershootDeg}");
            if (s.SaccadeIntervals <= 0) return (false, "no idle saccades measured (state machine not advancing)");
            if (s.SaccadeOutOfBounds > 0)
                return (false, $"{s.SaccadeOutOfBounds} saccade holds fell outside the personality [{s.PersonalityHoldMinSec:F2},{s.PersonalityHoldMaxSec:F2}]s bounds (measured {s.SaccadeMinHoldSec:F2}..{s.SaccadeMaxHoldSec:F2}s)");
            if (s.MaxSymmetryDeg > EyeMaxSymmetryDeg)
                return (false, $"left/right gaze asymmetric by {s.MaxSymmetryDeg:F1} deg > {EyeMaxSymmetryDeg} (eyes not verging symmetrically about the focal)");
            return (true, $"maxGaze={s.MaxGazeAngleDeg:F0}/{s.ClampDeg:F0}° vergenceWrong={s.VergenceWrongSign}/{s.VergenceSamples} jump(toward overshoot={s.JumpMaxOvershootDeg:F1}°) holds {s.SaccadeMinHoldSec:F2}..{s.SaccadeMaxHoldSec:F2}s in [{s.PersonalityHoldMinSec:F2},{s.PersonalityHoldMaxSec:F2}] sym={s.MaxSymmetryDeg:F1}°");
        }

        // --- secondary bone-chain sim stability (BasisBoneSimChainJob) ---
        public const float BoneSimMaxQuatNonUnit = 1e-3f;   // outgoing quaternion must stay unit
        public const float BoneSimMaxEnergyGain = 0.02f;    // output rotation must not grow past its drive
        public const float BoneSimMaxRootPosLagM = 0.05f;   // smoothed root must catch its target
        public const float BoneSimMaxChildPosLagM = 0.10f;  // target-follow child must catch its parent+offset
        public const float BoneSimMaxOscResidualDeg = 3f;   // sustained osc must not overshoot the drive envelope
        public const float BoneSimMaxOscResidualM = 0.02f;
        public const float BoneSimMaxSettleRatio = 1.1f;    // late-window output/drive range (>this = ringing)

        // The stateful per-frame smoother (lerp from LastRun toward destination). A feedback smoother fails by
        // blowing up (NaN), de-normalizing or GROWING its quaternion (energy gain), never catching the target
        // (unbounded lag), or ringing (output range exceeds the drive). This is a correctness/stability gate --
        // the chain Simulate path is intentionally serial; nothing here is about parallelism.
        public static (bool pass, string reason) GateBoneSim(in BasisBoneSimStabilitySweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0 || s.Scenarios <= 0) return (false, "no ticks");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} NaN/Inf in the bone-sim recurrence (the chain blew up)");
            if (s.MaxQuatNonUnit > BoneSimMaxQuatNonUnit)
                return (false, $"outgoing quaternion drifted {s.MaxQuatNonUnit:F4} off unit > {BoneSimMaxQuatNonUnit} (not renormalized)");
            if (s.MaxQuatEnergyGain > BoneSimMaxEnergyGain)
                return (false, $"output rotation grew {s.MaxQuatEnergyGain:F3}x past its drive > {BoneSimMaxEnergyGain} (energy gain / divergence)");
            if (s.MaxRootPosLagM > BoneSimMaxRootPosLagM || s.MaxChildPosLagM > BoneSimMaxChildPosLagM)
                return (false, $"chain never catches the target: lag root {s.MaxRootPosLagM * 1000f:F0}mm child {s.MaxChildPosLagM * 1000f:F0}mm (> {BoneSimMaxRootPosLagM * 1000f:F0}/{BoneSimMaxChildPosLagM * 1000f:F0}mm)");
            if (s.OscResidualDeg > BoneSimMaxOscResidualDeg || s.OscResidualM > BoneSimMaxOscResidualM || s.SettleRatio > BoneSimMaxSettleRatio)
                return (false, $"sustained self-oscillation/ringing: osc {s.OscResidualDeg:F1}° {s.OscResidualM * 1000f:F0}mm settleRatio {s.SettleRatio:F2} (> {BoneSimMaxOscResidualDeg}°/{BoneSimMaxOscResidualM * 1000f:F0}mm/{BoneSimMaxSettleRatio})");
            return (true, $"nonUnit={s.MaxQuatNonUnit:F4} energyGain={s.MaxQuatEnergyGain:F3} lag(root={s.MaxRootPosLagM * 1000f:F0}mm child={s.MaxChildPosLagM * 1000f:F0}mm) settle(deg={s.StepSettleErrDeg:F1} m={s.StepSettleErrM * 1000f:F0}mm ratio={s.SettleRatio:F2})");
        }

        // --- procedural blink timing (BasisLocalFacialBlinkDriver) ---
        public const float BlinkMaxIntervalTolS = 0.05f;  // slack on the random inter-blink band (quantization)
        public const float BlinkMaxCloseDurErrS = 0.05f;  // measured close duration vs blinkDuration
        public const float BlinkMaxRestWeight = 1f;       // eyelid must return to rest (0) between blinks (0..100 scale)

        // The blink scheduler: every random inter-blink wait must land in [min,max], each close must match its
        // configured duration, the eyelid weight must stay in range and return to rest between blinks, blinks
        // must never overlap, and nothing goes NaN. (Faithful re-implementation of the driver's timing math.)
        public static (bool pass, string reason) GateBlinkTiming(in BasisBlinkTimingSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.Blinks <= 0) return (false, "no blinks fired (scheduler not advancing)");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite eyelid samples (the blink sim blew up)");
            if (s.IntervalUnderMinS > BlinkMaxIntervalTolS)
                return (false, $"a blink fired {s.IntervalUnderMinS * 1000f:F0}ms below minBlinkInterval > {BlinkMaxIntervalTolS * 1000f:F0}ms");
            if (s.IntervalOverMaxS > BlinkMaxIntervalTolS)
                return (false, $"a blink wait ran {s.IntervalOverMaxS * 1000f:F0}ms over maxBlinkInterval > {BlinkMaxIntervalTolS * 1000f:F0}ms");
            if (s.MaxCloseDurationErrS > BlinkMaxCloseDurErrS)
                return (false, $"blink close duration off by {s.MaxCloseDurationErrS * 1000f:F0}ms from blinkDuration > {BlinkMaxCloseDurErrS * 1000f:F0}ms");
            if (s.MaxWeight > 101f || s.MinWeight < -1f)
                return (false, $"eyelid weight left [0,100]: min {s.MinWeight:F1} max {s.MaxWeight:F1}");
            if (s.MaxRestWeight > BlinkMaxRestWeight)
                return (false, $"eyelid not at rest between blinks ({s.MaxRestWeight:F1} weight) -- close/open didn't return to 0");
            if (s.OverlapCount > 0)
                return (false, $"{s.OverlapCount} blinks overlapped (a new blink fired mid-close)");
            return (true, $"blinks={s.Blinks} interval={s.MinIntervalS:F1}..{s.MaxIntervalS:F1}s (under {s.IntervalUnderMinS * 1000f:F0}ms over {s.IntervalOverMaxS * 1000f:F0}ms) closeErr={s.MaxCloseDurationErrS * 1000f:F0}ms weight[{s.MinWeight:F0},{s.MaxWeight:F0}] rest={s.MaxRestWeight:F1} overlaps={s.OverlapCount}");
        }

        // --- locomotion vertical model (BasisLocalCharacterDriver, pure-kinematic re-implementation) ---
        public const float LocomotionMaxApexErrM = 0.02f;          // jump apex must equal the configured jumpHeight
        public const float LocomotionMaxCoyoteErrS = 0.03f;        // coyote window vs configured (one dt + slack)
        public const float LocomotionMaxRecoveryReversalM = 1e-3f; // landing recovery must be monotone

        // The deterministic vertical math: a ballistic jump must apex at jumpHeight (validates the
        // gravity<->jumpHeight energy relation), the coyote window must equal its configured duration, and the
        // landing dip must recover monotonically back to rest. Out of scope: CharacterController.Move/colliders.
        public static (bool pass, string reason) GateLocomotion(in BasisLocomotionSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite samples (locomotion math blew up)");
            // Semi-implicit Euler's SAMPLED apex undershoots the true apex by ~v0*dt/2 (the half-step sampling
            // miss) -- the live FixedUpdate jump undershoots identically, so that's correct, not a broken
            // relation. Tolerate that dt-scaled bound (with margin); a genuinely broken gravity<->jumpHeight
            // relation is off by far more (e.g. a dropped *2 halves the apex).
            float apexTol = s.ApexDiscretizationBoundM * 1.5f;
            if (apexTol < LocomotionMaxApexErrM) apexTol = LocomotionMaxApexErrM;
            if (s.ApexErrM > apexTol)
                return (false, $"jump apex {s.SimApexHeightM:F3}m off the configured {s.ConfiguredJumpHeightM:F3}m by {s.ApexErrM * 1000f:F0}mm > {apexTol * 1000f:F0}mm tol (gravity<->jumpHeight relation broken; discretization bound {s.ApexDiscretizationBoundM * 1000f:F0}mm)");
            if (s.CoyoteErrS > LocomotionMaxCoyoteErrS)
                return (false, $"coyote window {s.MeasuredCoyoteS:F3}s off configured {s.ConfiguredCoyoteS:F3}s by {s.CoyoteErrS * 1000f:F0}ms > {LocomotionMaxCoyoteErrS * 1000f:F0}ms");
            if (s.LandingRecoveryReversalM > LocomotionMaxRecoveryReversalM)
                return (false, $"landing recovery re-deepened {s.LandingRecoveryReversalM * 1000f:F1}mm > {LocomotionMaxRecoveryReversalM * 1000f:F1}mm (non-monotone)");
            if (!s.LandingSettled)
                return (false, "landing dip never returned to rest within its recovery horizon");
            return (true, $"apex={s.SimApexHeightM:F3}/{s.ConfiguredJumpHeightM:F3}m (err {s.ApexErrM * 1000f:F0}mm) v0={s.LaunchVelocity:F2}m/s coyote={s.MeasuredCoyoteS:F3}/{s.ConfiguredCoyoteS:F3}s dip={s.LandingPeakDipM:F3}m settle={s.LandingSettleTimeS:F2}s");
        }

        // --- virtual spine temporal dynamics (BasisVirtualSpineSolveJob, runtime per-frame solve) ---
        public const float VSpineTemporalMaxStepDeg = 5f;       // torso yaw 2nd-diff on smooth head motion (stepping)
        public const float VSpineTemporalMaxAboveHeadM = 1e-3f; // hips must never sit above the head (the flip)
        public const float VSpineTemporalMaxCatchupDeg = 15f;   // torso must catch the head after it stops (relock)
        public const float VSpineTemporalMinEngageFollow = 0.9f; // repro guard: the deadzone must actually break

        // The runtime spine solve carries per-frame state the static spine sweeps can't reach: a yaw deadzone
        // that holds the torso heading, eases a follow weight in to catch up past the cone, then relocks when the
        // head stops; plus a hips counterbalance. The dynamics fail by SNAPPING as the deadzone relocks, STEPPING
        // on smooth head motion, driving the hips ABOVE the head (flip), or failing to CATCH UP after the head
        // stops. Reuses TrajMaxPopDeg for the snap threshold. Pop/step thresholds calibrate against a known-good run.
        public static (bool pass, string reason) GateVirtualSpineTemporal(in BasisVirtualSpineTemporalSweepSummary s)
        {
            if (!s.Ok) return (false, string.IsNullOrEmpty(s.Error) ? "did not run" : s.Error);
            if (s.Steps <= 0) return (false, "no steps");
            if (s.NaNCount > 0) return (false, $"{s.NaNCount} non-finite torso samples (the spine solve blew up)");
            if (s.MaxTorsoFollow < VSpineTemporalMinEngageFollow)
                return (false, $"torso never broke the yaw deadzone (follow peaked {s.MaxTorsoFollow:F2} < {VSpineTemporalMinEngageFollow}) -- sweep not exercising the catch-up");
            if (s.MaxOutputPopDeg > TrajMaxPopDeg)
                return (false, $"torso yaw snaps {s.MaxOutputPopDeg:F0}° in one frame > {TrajMaxPopDeg} (deadzone relock pop)");
            if (s.MaxHipsYawStepDeg > VSpineTemporalMaxStepDeg || s.MaxChestYawStepDeg > VSpineTemporalMaxStepDeg)
                return (false, $"torso yaw steps on smooth head motion (hips {s.MaxHipsYawStepDeg:F1}° chest {s.MaxChestYawStepDeg:F1}°/frame) > {VSpineTemporalMaxStepDeg} (stepping/jitter)");
            if (s.MaxHipsAboveHeadM > VSpineTemporalMaxAboveHeadM)
                return (false, $"hips driven {s.MaxHipsAboveHeadM * 100f:F1}cm above the head > {VSpineTemporalMaxAboveHeadM * 100f:F1}cm (torso flip)");
            if (s.FinalCatchupErrDeg > VSpineTemporalMaxCatchupDeg)
                return (false, $"torso {s.FinalCatchupErrDeg:F0}° off the head after it stopped > {VSpineTemporalMaxCatchupDeg} (relock didn't complete)");
            return (true, $"follow={s.MaxTorsoFollow:F2} pop={s.MaxOutputPopDeg:F0}° step(hips={s.MaxHipsYawStepDeg:F1} chest={s.MaxChestYawStepDeg:F1})° aboveHead={s.MaxHipsAboveHeadM * 100f:F1}cm catchUp={s.FinalCatchupErrDeg:F0}°");
        }
    }
}
