using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct BasisFootSimulateJob : IJob
{
    // ── Gait constants (biomechanics, not taste) ──
    // Scale-free by construction: every one is a FRACTION, so they hold for a chibi and a giant alike.

    // Double support (both feet planted) as a fraction of one step's duration, faded across the WALK range.
    //
    // Measured 2026-07-18 against real human walking (CMU corpus + 16 downloaded clean slow-walk clips, see
    // project_basis_foot_mocap_comparison_harness). Real double-support swings hugely with speed: ~0.50 of the
    // time both-feet-down at a slow deliberate walk, ~0.14 at a brisk one. The first attempt used a SINGLE
    // fraction faded by the RUN speed reference (fastSpeedRef ~2.9 m/s), so it barely moved across walking and
    // slow walking still mince-stepped (short, frequent, always-moving feet -- "walking slowly looks wrong").
    //
    // So fade Slow->Fast across the WALK range (speed / (k_WalkTopSpeedFrac * sqrt(g*L))) instead. A longer
    // dwell is the UNIFIED lever: it raises double-support, lowers the (too-high) cadence, AND lengthens the
    // stride, because the swing foot drifts further before it is allowed to step. Result vs real: slow
    // double-support 0.26 -> 0.46 (real 0.48), med 0.31 (real 0.25), brisk 0.21 (real 0.10).
    //   ⚠ CEILING: dwell x speed = drift, so an arbitrarily long dwell OVER-EXTENDS the leg -- the values are
    //   capped so worst-frame ext stays ~1.19-1.29 (safe). A real slow walk reaches a 1.0 L stride at cadence
    //   0.11 via pelvic rotation + toe-off; a rigid pelvis can't, so cadence stays above real at the slow end.
    //   Multiplied by (1 - yawFrac) so a SPIN still kills double support (the anti-cross fix depends on it).
    const float k_DoubleSupportSlow = 2.00f;   // both-feet fraction of one step at a slow crawl
    const float k_DoubleSupportFast = 0.05f;   // ...at the walk->run transition
    const float k_WalkTopSpeedFrac = 0.45f;    // v-hat (v/sqrt(gL)) at which the fade reaches k_DoubleSupportFast

    // ...and the SLOW end has to stop somewhere, because k_DoubleSupportSlow is a WALK number and below a
    // certain speed there is no walk to have a duty cycle of. Standing, shifting your weight and turning on
    // the spot are not slow gait -- they are isolated adjustment steps, and a human makes those promptly.
    //
    // Left unbounded the lerp ran to its 2.00 endpoint at speed 0: a 561 ms both-feet lockout between
    // adjustment steps on an adult, which is 67% of the cycle spent in double support against this codebase's
    // own "too much = a shuffle" gate (BasisMocapFootQuality.MaxDoubleSupport = 0.45). Nothing measured it,
    // because BasisFootWalkInvarianceTests only walks (v-hat 0.20/0.30/0.42, where the fade is already
    // saturated and these two constants are exactly inert) and the one standing test asserts FEWER steps is
    // better -- so a lockout reads as a pass. And per project_basis_foot_ik_rework the stick gate means
    // standing and turning are the ONLY regimes this stepper is ever visible in.
    // Set by CADENCE, not by duty cycle. Self-paced stepping in place measures 98-106 steps/min across three
    // independent samples (Rohling 2022 IJERPH 19:16989 n=121, 98.3 +/- 16.1; Grostern 2021 Physiother Can
    // 73:322 n=16, 100.2 +/- 12.6; Dalton 2016 Aging Clin Exp Res 28:909 n=29, 104-106). One cycle here is
    // 2*(stepDur + doubleSupportSec) and holds 2 steps, so a settle of one whole stepDur caps the standing
    // regime at ~107 steps/min -- the top of that band. The rule is "never out-step the fastest self-paced
    // human stepping on the spot", which is exactly the behaviour this regime is.
    //   ⚠ 0.25 was tried first and is WRONG: it permits 171 steps/min, ~1.6x human.
    // CORROBORATED INDEPENDENTLY by measured double support at very low speed. Raw-data regressions (Wu's
    // Dataverse + Fukuchi's Figshare, cross-validated to 2 ms at 0.5 m/s) put DS at 43-57% of cycle below
    // 0.50 m/s -- 57.3% at 0.11 m/s, still climbing, no plateau. k = 1.0 lands DS at exactly 50%, inside that
    // band. So cadence and duty fraction agree here; they are not in tension.
    //   ⚠ 50% IS above BasisMocapFootQuality.MaxDoubleSupport (0.45), but that gate only ever runs on WALK
    //     scenarios, and the measurements above show 0.45 is simply too tight for the sub-walk regime.
    //   ⚠ Do NOT "fix" this by lengthening stepDurSlow. An earlier pass here concluded it was ~55% short of a
    //     real swing; that was wrong -- it came from extrapolating a WALKING-derived swing model (Wendt 2010
    //     GUD WIP) to stepping-in-place cadence, which its own authors warn against. At the measured SIP
    //     cadence WITH the measured ~50% DS, the implied swing is ~0.29 s, which is what stepDurSlow already
    //     is (281 ms). It is correct as it stands.
    //   ⚠ Architectural note: below ~0.7 m/s the energetic optimum is a walk-REST mixture, not a slower
    //     continuous gait (Long & Srinivasan 2013, PMC3627106) -- humans make discrete adjustment steps
    //     separated by genuine standstill. That is exactly what this trigger-gated stepper does, so the
    //     burst-then-dwell shape is right; only the dwell length needed fixing.
    const float k_DoubleSupportStanding = 1.0f;    // both-feet fraction between isolated adjustment steps
    const float k_WalkOnsetFrac = 0.10f;           // v-hat below which the gait duty cycle no longer applies

    // REACTION DISTANCE while standing is its own quantity too. stepTriggerDist is 0.18*leg because that is
    // what makes a WALK's stride come out the right length (it was raised 0.08 -> 0.10 -> 0.18 against the
    // mocap harness for exactly that). Applied to standing it means the body must drift 15.7 cm -- 23.5 cm
    // with the idle boost -- before the foot so much as begins to move, which does not read as a slow walk,
    // it reads as the feet not responding. Same error class as k_DoubleSupportSlow: a walk constant evaluated
    // where there is no walk. Faded on the SAME walkOnset ramp, so the validated walk band is untouched.
    const float k_StandingTriggerFrac = 0.55f;     // of stepTriggerDist, at v-hat 0

    // EFFORT SHOULD BE PROPORTIONAL TO NEED -- this is what separates purposeful from mechanical.
    //
    // urgencyT is built from body speed and yaw rate ONLY, so it cannot see how far behind the foot actually
    // is. A foot 25 cm adrift and a foot 2 cm adrift therefore take the SAME lazy 281 ms swing. Humans do not
    // do that: a big recovery is fast and committed, a small adjustment is gentle. Uniform effort regardless
    // of need is precisely the "robotic" read, and no amount of retuning a single duration can fix it because
    // the duration is not a function of the thing that varies.
    //
    // So scale urgency by how far PAST its trigger the foot is: at the threshold the step stays deliberate
    // (0 extra), at twice the threshold it commits fully. Frozen at commit alongside stepDur/stepArcScale --
    // a live term would retime a swing mid-flight.
    const float k_DriftUrgencyRefMul = 1.0f;       // extra threshold-widths of drift for full urgency

    // ACCELERATION. Urgency was built from the smoothed SPEED, which is the one signal that is still ~0 at the
    // exact moment you start moving and is FALLING at the moment you stop -- so a hard start had to wait for
    // speed to accumulate before the feet took it seriously, and an emergency stop made the step LESS urgent
    // precisely as it became most necessary. The derivative carries both, and it carries them a filter-length
    // earlier than the magnitude does.
    //   ⭐ SCALE-INVARIANT BY CONSTRUCTION, unlike every other reference in this file: distances go as L and
    //   times as sqrt(L/g), so accelerations go as L/(L/g) = g. A fixed m/s^2 reference is therefore already
    //   correct for a chibi and a giant -- it must NOT be scaled, which is the opposite of the usual rule here.
    const float k_AccelUrgencyRef = 3.0f;          // m/s^2 at which acceleration alone commands a full-speed step
    const float k_AccelTriggerFrac = 0.60f;        // trigger distance multiplier at full acceleration urgency

    // LEG OVER-EXTENSION. The most urgent thing a foot can be is out of reach, and nothing was measuring it:
    // the trigger only knows plant->ideal drift, so a leg being torn straight registered exactly the same as a
    // lazy drift of equal size. This is also the sweep's worst metric by far (WorstExtensionRatio).
    //   ⚠ Measured against 1.0 (= hipToFoot, the STANDING straight-leg distance), NOT against some fraction of
    //   it: footHeightOffset is deliberately clamped so the legs fully extend when standing, so the leg already
    //   sits at ~1.0 doing nothing. Ramping from anything below 1.0 would pin urgency at maximum permanently.
    //   Only genuine OVER-extension is urgent.
    const float k_ExtUrgencyBand = 0.10f;          // over-extension beyond standing reach for full urgency

    // YAW REVERSAL (mouse-look snap-back). smoothedYawRateDeg is SIGNED and filtered at bodyFwdRateMoving
    // (tau ~167 ms), which is fine while a turn holds one direction and actively WRONG the instant it reverses:
    // for ~15 frames the filter still reports the OLD direction. Everything that only wants magnitude (pacing,
    // urgency, stance widening) survives that because it takes abs -- but ComputeStepPrediction does NOT. It
    // rotates the step target by `yawRateDeg * stepDur`, signed, so a foot committed during a snap-back is
    // aimed up to maxPlantedYawDegrees (20 deg) THE WRONG WAY, and then has to re-plant to undo it.
    //
    // A reversal is a deliberate input, not noise -- the same argument the root-yaw carry and k_YawTrackGain
    // already make elsewhere in this file. So detect the sign disagreement and open the filter while it lasts,
    // collapsing tau to ~42 ms (sign correct in ~3 frames). Costs nothing when the turn direction is steady,
    // because the product is then positive and the gain never applies.
    const float k_YawReversalGain = 4.0f;

    // MEDIAL SWING EXCURSION, as a fraction of HALF the stance width, at full stride. A real swing foot passes
    // close to the stance leg on its way through rather than tracking a straight line between its two ground
    // contacts, and that inward pass is one of the most recognisable things about a walk seen from the front.
    //
    // ⚠️ HARD BOUND, NOT A TASTE KNOB: it is coupled to k_MinFootSepFrac and must satisfy
    //       k_MedialSwingFrac < 2 * (1 - k_MinFootSepFrac)      [= 0.30 at the shipped 0.85]
    // The excursion PEAKS at mid-swing, which is precisely when the swing foot is passing the stance foot and
    // their fore-aft separation is ~0 -- so the euclidean gap the backstop measures collapses to the lateral
    // gap exactly there. At 0.35 the gap fell to 14.8 cm against a 15.3 cm backstop on an 18 cm stance: the
    // backstop would have fired every stride and shoved the feet apart, fighting this and popping the foot.
    // 0.20 leaves ~33% margin. Raising k_MinFootSepFrac lowers this ceiling; check both together.
    const float k_MedialSwingFrac = 0.20f;

    // Reach-ahead floor: minimum forward distance of a step target as a fraction of leg, so a WALK plants the
    // foot in front deliberately (see ComputeStepPrediction). Speed-gated there so a spin is untouched.
    const float k_ReachAheadFrac = 0.25f;

    // Step width at top speed, as a fraction of the walking stance. Walkers plant ~0.13 leg-lengths apart
    // laterally; runners land nearly on one line as the COM spends less time shifted over each stance leg.
    const float k_StanceNarrowAtSpeed = 0.35f;

    // Lateral COM sway toward the STANCE leg, as a fraction of leg length. Humans shift ~2-3 cm sideways per step
    // (~0.03 leg) at a normal walk -- you rock over the loaded leg. With zero sway the pelvis tracks a dead
    // straight line and the walk reads as a glide on rails, which is a large part of "unnatural".
    const float k_HipSwayFraction = 0.03f;

    // Ankle pitch through the swing. A real foot does not fly flat: it PLANTARFLEXES (toes down) as it pushes off,
    // passes ~neutral at mid-swing, then DORSIFLEXES (toes up) to present the heel for heel-strike. The foot then
    // rolls down flat under the plantedRot slerp -- which gives the foot-flat rocker for free.
    const float k_ToeOffDeg = 22f;       // plantarflexion at toe-off (end of pre-swing)
    const float k_HeelStrikeDeg = 16f;   // dorsiflexion at heel-strike (t=1)

    // PRE-SWING as a fraction of the whole step event. Perry's phases: pre-swing is 50-60% of the gait cycle
    // and swing is 60-100%, so against the 50% of the cycle this step event spans, pre-swing is 10/50 = 0.20.
    // During it the foot is still in contact through the TOE and only rotates; the translation and the step
    // arc both run on the remaining 0.80. Setting this to 0 restores the previous single-stage behaviour.
    const float k_PreSwingFrac = 0.20f;

    // HEEL STRIKE -> FOOT FLAT, as a fraction of one stepDur. The foot lands dorsiflexed (heel first) and
    // rolls down onto the sole; in real gait that takes ~7-8% of the cycle, which against this model's cycle
    // (2*(stepDur + doubleSupportSec), ~1.12 s standing) is ~85 ms, i.e. ~0.30 of a stepDur. Deliberately in
    // stepDur units so it scales with the avatar's own pendulum like every other duration here -- the fixed
    // rotationLerpSpeed it replaces did not. Overlaps k_LoadingResponseFrac (0.35) on purpose: the foot is
    // rolling flat while the knee is absorbing, which is what loading response IS.
    const float k_FootFlatFrac = 0.30f;

    // Pelvis, per Saunders' determinants of gait.
    // Axial: the swing-side hip is carried FORWARD ~4-5 deg -- this is what lengthens the stride without
    // lengthening the leg, and its absence is the classic "pelvis is one rigid block" robotic tell.
    // List: the swing-side hip DROPS ~5 deg in the frontal plane (Trendelenburg), because nothing is holding it up.
    const float k_PelvisAxialDeg = 4.5f;
    const float k_PelvisListDeg = 5f;

    // LOADING RESPONSE -- Saunders' stance-phase knee flexion. Just after heel strike the knee flexes 15-20 deg to
    // absorb the impact, and the COM dips to its lowest point of the whole cycle before re-extending.
    //
    // In a 2-bone IK the knee angle is ENTIRELY a function of hip->foot distance: flexion = 2*acos(d/L). And
    // footHeightOffset is deliberately clamped so "the legs fully extend when standing", which parks the standing
    // leg at d/L ~= 1.0 -- i.e. exactly ON the pole singularity, which is the whole reason the knee-swivel filter
    // has to exist. acos is near-vertical there, so a tiny dip buys a lot of bend:
    //     d/L 1.000 -> 0 deg      0.990 -> 16.2 deg      0.985 -> 19.9 deg
    // So ~1.2% of leg (about 1 cm on an adult) delivers the real 15-20 deg AND lifts the leg off the singularity.
    // Both a naturalness fix and an IK-conditioning fix, for one centimetre.
    const float k_LoadingDipFraction = 0.012f;   // of leg length
    const float k_LoadingResponseFrac = 0.35f;   // of one stepDur, measured from heel strike

    // ANTI-CROSSOVER during fast rotation. Measured 2026-07-18 (synthetic spin harness, see
    // project_basis_foot_mocap_comparison_harness): a fast PHYSICAL spin (the playspace fixed, the body/hips
    // physically yawing) drove the feet to step ON each other -- at 360 deg/s the feet crossed 85% of the time
    // and overlapped (gap < foot length) 70% of the time. TWO causes, one const each:
    //
    //  1. bodyFwd TRACKING LAG. A stick/character-controller turn is carried through the smoothedBodyFwd filter
    //     losslessly by the root-yaw carry, but a PHYSICAL spin does not rotate the root, so the filter alone
    //     must track it -- and at the shipped rate 6 (tau ~167 ms) a 360 deg/s spin lags ~60 deg, so the ideals,
    //     step targets and bodyRight are all built from a stale facing and the feet scissor. Open the filter rate
    //     in proportion to the (deliberate) yaw rate during a turn: a fast yaw is an input to follow, not noise.
    //
    //  2. The planted foot is orbited across the centre line during the other foot's swing. A spinning human
    //     keeps a WIDE base; widening the stance in proportion to yaw rate gives the scissor the margin it needs.
    //
    // Together they take collisions 70% -> 0% across chibi/adult/giant, with ZERO effect on straight walking
    // (both scale on yaw rate, which is ~0 in a straight walk) and a small over-extension improvement on turns.
    const float k_YawTrackGain = 5.0f;    // bodyFwd filter rate >= this * |yawRate(rad/s)| while turning
    const float k_RotWidenFrac = 0.6f;    // widen the stance by up to +60% at the full-fast yaw rate
    // Backstop for spins TOO fast for tracking+widening to keep up (~600-1200 deg/s): the feet may never be
    // closer than this fraction of the stance width, euclidean. Guarantees no interpenetration at any rate;
    // a no-op during walking (feet are fore-aft-offset there, so the euclidean gap never drops this low).
    const float k_MinFootSepFrac = 0.85f;

    // TURN URGENCY vs TURN PACING -- one yaw term was doing two different jobs, and it made every turn frantic.
    //
    // fastYawRef is DERIVED as "the yaw rate at which even fast steps cannot keep up" (0.5*maxPlantedYaw/stepDurFast
    // ~= 59 deg/s on an adult). As a PACING reference that is right: past it the feet must re-plant continuously or
    // they strand. But yawFrac was then reused as the URGENCY scale for step DURATION and DOUBLE SUPPORT, and those
    // are not the same question. A turn needs MORE steps; it does not need each individual step to be a 168 ms flick
    // with no both-feet-planted settle. Stepping often and stepping hurriedly are independent.
    //
    // The cost was severe because 59 deg/s is an ordinary turn. A VR smooth-turn runs 90-120 deg/s, so yawFrac sat
    // PINNED at 1.0 for the entire turn: stepDur locked to its minimum AND doubleSupportSec multiplied to exactly
    // zero (a foot could lift the tick the other landed). Both weight cues gone, together, whenever you turn.
    //
    // So: yawFrac keeps pacing the TRIGGER and widening the stance (measured, and the anti-crossover result above
    // depends on both). Urgency gets its own, much higher reference -- an ordinary turn stays deliberate, and only a
    // genuine fast spin compresses the step. At mul 5 urgency saturates ~297 deg/s, which is where the measured
    // scissor problems actually begin; a 110 deg/s smooth-turn reads 0.37 instead of 1.0.
    // Public because BasisLocalFootDriver.FinalizeStep commits the step and must derive the SAME values. Shared
    // rather than duplicated: fastYawRef is already copy-pasted across the job and the driver, and this file's
    // history is full of mirrors drifting apart (the sweep, the mocap harness, and DeriveStepParameters are three).
    public const float YawUrgencyRefMul = 5.0f;
    // Arc floor for a step taken while turning. A turn step travels ~4 cm, which the stride-scaled lift reads as a
    // drift correction (~0.46x height). Floor strideFrac so it clears the ground like the real step it is.
    public const float TurnStepArcFloor = 0.65f;

    public BasisFootSimParams p;
    public NativeArray<BasisFootNativeState> feet;   // length 2: [0]=left, [1]=right
    public NativeArray<BasisFootSimState> simState;   // length 1
    public NativeArray<BasisFootSimInput> input;      // length 1
    public NativeArray<BasisFootSimOutput> output;    // length 1

    public void Execute()
    {
        var inp = input[0];
        var sim = simState[0];
        var left = feet[0];
        var right = feet[1];
        float dt = inp.dt;

        if (dt <= 0f) return;

        float3 up = inp.playerUp;
        if (math.lengthsq(up) < 0.001f) up = new float3(0, 1, 0);

        // ── Velocity from the HIPS ──
        // Was taken from the HEAD. The head is the worst available proxy for body translation: it bobs with every
        // step, sways, and swings a long lever arm whenever you look around, so "speed" carried gait-frequency
        // noise and look-around motion that has nothing to do with travelling. The hips ARE the body's
        // translation -- the pelvis is the COM proxy the whole of gait biomechanics is written against -- so the
        // feet now pace off the thing that is actually moving.
        //
        // prevHeadPos keeps its name/slot in the state struct (it is the previous SAMPLE position) so the layout
        // and the sweep's mirrored state stay put; it now holds the hips.
        float3 velSample = inp.hipsPos;
        float3 rawVel = (velSample - sim.prevHeadPos) / dt;
        rawVel -= up * math.dot(rawVel, up); // strip vertical component
        sim.prevHeadPos = velSample;

        bool decelerating = math.lengthsq(rawVel) < math.lengthsq(sim.smoothedVelocity);
        float vAlpha = 1f - math.exp(-(decelerating ? p.velocitySmoothDecel : p.velocitySmoothAccel) * dt);
        float3 prevSmoothedVel = sim.smoothedVelocity;
        sim.smoothedVelocity = math.lerp(sim.smoothedVelocity, rawVel, vAlpha);

        float speed = math.length(sim.smoothedVelocity);

        // Magnitude, not signed: a hard STOP is as much a reason to commit a step as a hard start. Smoothed on
        // the accel rate so a single noisy tracker frame cannot spike a step.
        float accelMag = math.length(sim.smoothedVelocity - prevSmoothedVel) / dt;
        sim.smoothedAccelMag = math.lerp(sim.smoothedAccelMag, accelMag, 1f - math.exp(-p.velocitySmoothAccel * dt));
        float accelUrgency = math.saturate(sim.smoothedAccelMag / k_AccelUrgencyRef);

        // ── Body forward ──
        float3 rawFwd = ComputeBodyForward(inp, sim, p, up);

        // Body yaw rate (deg/s), smoothed — paces stepping so feet keep up when turning / spinning.
        // Deliberately measured BEFORE the root carry below, so a character-controller turn still counts as yaw
        // rate and still paces the steps faster.
        float bodyYawRate = math.lengthsq(sim.prevBodyFwd) > 0.001f ? SignedAngle(sim.prevBodyFwd, rawFwd, up) / dt : 0f;
        sim.prevBodyFwd = rawFwd;
        // Open the filter while the raw rate DISAGREES IN SIGN with the smoothed one -- see k_YawReversalGain.
        float yawFilterRate = bodyYawRate * sim.smoothedYawRateDeg < 0f
            ? p.bodyFwdRateMoving * k_YawReversalGain
            : p.bodyFwdRateMoving;
        sim.smoothedYawRateDeg = math.lerp(sim.smoothedYawRateDeg, bodyYawRate, 1f - math.exp(-yawFilterRate * dt));

        // ── Ride the root, THEN smooth ──
        // smoothedBodyFwd is a WORLD vector, but it is derived from the hips/chest/head bones, which are rigid
        // children of the player root. So a character-controller turn rotates all of them identically -- it is a
        // deliberate, known input, not noise -- yet the exponential filter treated it as something to chase, at
        // tau = 400 ms whenever translation speed was under 0.1 m/s. Turning in place IS speed ~0, so a turn got
        // the SLOWEST possible tracking: the step targets were built from a body forward that was ~0.4 s stale and
        // the feet floated after it. Same bug class as the world-referenced knee swivel.
        //
        // Rotating the filter's STATE by the root's own yaw delta first makes a root turn pass through with zero
        // filter error (instant), while the organic deviation -- hips/chest/head wobbling RELATIVE to the root --
        // is still damped exactly as before. The filter now smooths only what it was ever meant to smooth.
        float3 rootFwd = inp.avatarForward - up * math.dot(inp.avatarForward, up);
        if (math.lengthsq(rootFwd) > 1e-6f)
        {
            rootFwd = math.normalize(rootFwd);
            if (math.lengthsq(sim.prevRootFwd) > 1e-6f && math.lengthsq(sim.smoothedBodyFwd) > 1e-6f)
            {
                float rootYawDeg = SignedAngle(sim.prevRootFwd, rootFwd, up);
                quaternion rootDelta = quaternion.AxisAngle(up, math.radians(rootYawDeg));
                sim.smoothedBodyFwd = math.mul(rootDelta, sim.smoothedBodyFwd);
            }
            sim.prevRootFwd = rootFwd;
        }

        // Turning in place is speed ~0, so the speed test alone always picked the slow rate exactly when the body
        // was rotating fastest. Any real turn (physical or stick) gets the responsive rate.
        bool turning = math.abs(sim.smoothedYawRateDeg) > 20f;
        // Relative, not an absolute 0.1 m/s — same reasoning as the hip-bob gate in ComputeHipBob: a small
        // avatar's whole speed range scales as sqrt(g*L), so a fixed cutoff left it below the threshold and
        // building step targets from a stale facing.
        float fwdRate = (speed > 0.034f * p.fastSpeedRef || turning) ? p.bodyFwdRateMoving : p.bodyFwdRateStationary;
        // Track a fast PHYSICAL spin (which the root-yaw carry above cannot help, because the root does not
        // rotate) by opening the filter rate in proportion to the yaw rate -- see k_YawTrackGain.
        if (turning)
            fwdRate = math.max(fwdRate, k_YawTrackGain * math.abs(math.radians(sim.smoothedYawRateDeg)));
        float fwdAlpha = 1f - math.exp(-fwdRate * dt);
        // Degeneracy checks run BEFORE normalize: normalizing a near-zero vector yields NaN, and
        // a NaN never compares < epsilon — with smoothedBodyFwd self-feeding, one bad frame would
        // otherwise corrupt the sim permanently.
        float3 blendedFwd = Slerp3(sim.smoothedBodyFwd, rawFwd, fwdAlpha);
        sim.smoothedBodyFwd = math.lengthsq(blendedFwd) < 0.001f ? rawFwd : math.normalize(blendedFwd);

        float3 rightCross = math.cross(up, sim.smoothedBodyFwd);
        sim.smoothedBodyRight = math.lengthsq(rightCross) < 0.001f ? inp.avatarRight : math.normalize(rightCross);

        float3 bodyFwd = sim.smoothedBodyFwd;
        float3 bodyRight = sim.smoothedBodyRight;

        float3 rawRightCross = math.cross(up, rawFwd);
        float3 rawRight = math.lengthsq(rawRightCross) < 0.001f ? bodyRight : math.normalize(rawRightCross);

        // ── Ground ──
        // Project hips onto the horizontal plane (perpendicular to player up)
        float hipsUpComponent = math.dot(inp.hipsPos, up);
        float3 hipsFlat = inp.hipsPos - up * hipsUpComponent;
        float3 velDir = sim.smoothedVelocity - up * math.dot(sim.smoothedVelocity, up);

        // A ground HIT is not the same as being ON the ground. `airborne` used to mean "the ray missed",
        // which only happens on a fall taller than rayCastRange -- so during an ordinary jump the ray still
        // finds the floor under you, groundHit stays true, and the planted feet stay WELDED to it while the
        // hips rise. The leg simply stretches: 1.31x standing reach on an adult, 1.63x on a half-scale
        // avatar (a fixed jump impulse is a far bigger fraction of a short leg -- this is a large part of
        // "does not do well with small avatars"). Zero horizontal drift, zero steps, pure vertical tearing.
        //
        // You are airborne once the hips rise meaningfully ABOVE the height they stand at with straight legs.
        //
        // Do NOT compare against legLen (thigh+shin). legLen is the UPPER-LEG->foot chain, but hipToFoot is
        // HIPS->foot, and the hips bone sits ABOVE the hip sockets -- so hipToFoot > legLen on every rig, and a
        // legLen-based reach test reads "airborne" while the avatar is simply STANDING. The feet then stop using
        // the floor and ride the hips-relative fallback, which parks them slightly high: the whole avatar hovers.
        //
        // hipToFoot + ankleHeight IS the straight-leg standing height (that is how both are measured, off the
        // T-pose), so it is the correct reference by construction. Real standing sits a touch BELOW it (knees are
        // never perfectly locked), which gives free margin. The margin only has to clear the hip bob (a few cm)
        // while staying far below a real jump (>= ~0.35 leg), so 15% of leg separates them with room to spare.
        float standHipsAboveGround = p.hipToFoot + p.ankleHeight;
        float avgLegReach = (p.leftLegLen + p.rightLegLen) * 0.5f;
        bool groundInReach = inp.groundHit &&
            (hipsUpComponent - math.dot(inp.groundPoint, up)) <= standHipsAboveGround + avgLegReach * 0.15f;

        float groundUpComponent;
        bool airborne;
        if (groundInReach)
        {
            // Planted feet carry hit.point + footHeightOffset; the ideal/vertical-snap level must
            // share that convention or the drift snap buries a planted foot by the offset.
            groundUpComponent = math.dot(inp.groundPoint, up) + p.footHeightOffset;
            airborne = false;
        }
        else
        {
            // hipToFoot measures Hips→Foot bone, but the ground is ankleHeight
            // below the Foot bone.  Subtract the full distance so the fallback
            // ground level matches what a successful raycast would produce.
            // Then add footHeightOffset, just like raycasted positions do, so
            // the feet don't hover above the floor when the raycast misses.
            groundUpComponent = hipsUpComponent - p.hipToFoot - p.ankleHeight + p.footHeightOffset;
            airborne = true;
        }

        // ── Ideal positions ──
        // Relative move-direction gate (was an absolute (0.1 m/s)^2): below it the velocity bias and lead
        // offset collapse to bodyFwd, so a fixed cutoff robbed a small avatar of directional prediction.
        float moveDirGate = 0.034f * p.fastSpeedRef;
        float3 moveDir = math.lengthsq(velDir) > moveDirGate * moveDirGate ? math.normalize(velDir) : bodyFwd;
        float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float maxOffset = avgLeg * p.maxVelocityOffsetFraction;
        float baseBias = math.min(speed * p.velocityBiasFactor, maxOffset);
        float3 center = hipsFlat + up * groundUpComponent + moveDir * baseBias;

        // Step width NARROWS as you speed up. A walking human plants ~0.13 leg-lengths apart laterally; a runner
        // lands almost on a single line (the COM has less time to shift sideways over each stance leg, so the feet
        // converge under it). Holding one width at every speed is what makes a fast gait read as a WADDLE.
        //
        // But that is a FORWARD-gait effect, and applying it to a STRAFE is actively wrong -- side-stepping humans
        // WIDEN their base, they do not narrow it, because the COM is travelling toward the edge of the support
        // polygon rather than over it. Narrowing during a strafe drove the lateral velocity bias past the (now
        // smaller) half-stance, which collapsed both ideals onto the same side of the body: the sweep showed
        // strafe-left at "steps 18 (L0 R18)" -- the left foot never stepped at all, and the body strafed 11 metres
        // away from it. Scale the narrowing by how much of the motion is actually forward, so a pure strafe keeps
        // its full stance and a pure run gets the full convergence.
        float speedFrac = math.saturate(speed / p.fastSpeedRef);
        float fwdFrac = math.abs(math.dot(moveDir, bodyFwd));   // 1 = pure forward/back, 0 = pure sideways
        // fastYawRef: the yaw rate at which even fast steps can't keep up. Declared here (was below) so the
        // rotation stance-widening can use it too. yawFrac is 0 in a straight walk, so widening is a no-op there.
        float fastYawRef = math.max(1f, 0.5f * p.maxPlantedYawDegrees / math.max(0.01f, p.stepDurFast));
        float yawFrac = math.saturate(math.abs(sim.smoothedYawRateDeg) / fastYawRef);
        // Separate scale for "how HURRIED is this step" -- see k_YawUrgencyRefMul. Pacing (yawFrac) stays as it was.
        float yawUrgency = math.saturate(math.abs(sim.smoothedYawRateDeg) / (fastYawRef * YawUrgencyRefMul));
        float halfStance = p.stanceWidth * 0.5f * math.lerp(1f, k_StanceNarrowAtSpeed, speedFrac * fwdFrac)
                           * (1f + k_RotWidenFrac * yawFrac);   // widen the base during a fast turn (anti-scissor)

        float leadAmount = math.min(speed * p.velocityBiasFactor * p.leadOffsetFactor, maxOffset * 0.5f);
        float3 leadOffset = moveDir * leadAmount;

        float leftDist = HDist(left.plantedPos, center - bodyRight * halfStance, up);
        float rightDist = HDist(right.plantedPos, center + bodyRight * halfStance, up);

        if (leftDist >= rightDist)
        {
            left.idealPos = center - bodyRight * halfStance + leadOffset;
            right.idealPos = center + bodyRight * halfStance;
        }
        else
        {
            left.idealPos = center - bodyRight * halfStance;
            right.idealPos = center + bodyRight * halfStance + leadOffset;
        }

        // ── Enforce side on ideals ──
        float3 hipsGround = hipsFlat + up * groundUpComponent;
        EnforceSide(ref left.idealPos, hipsGround, rawRight, -1, halfStance * p.idealSideEnforceFraction);
        EnforceSide(ref right.idealPos, hipsGround, rawRight, +1, halfStance * p.idealSideEnforceFraction);

        // ── Step parameters ──
        // Pace steps by translation OR body rotation: a spin (high yaw rate, no translation) must step
        // as fast as a fast walk so the feet keep up and don't cross. fastYawRef/yawFrac were computed above
        // (the rotation stance-widening needs them too); yawFrac IS the yaw pacing term.
        // How HURRIED a single step is (its duration) -- NOT how often steps fire. Translation still saturates on
        // fastSpeedRef, but the yaw contribution uses the urgency reference so an ordinary turn stays deliberate.
        // Body-wide urgency: the WORST of translation speed, turn rate and acceleration. Max rather than a sum
        // because each one alone is sufficient reason to hurry, and summing would saturate on any two at once.
        // The per-foot drift and over-extension terms are added later, in UpdateFoot, where they are known.
        float urgencyT = math.max(math.max(math.saturate(speed / p.fastSpeedRef), yawUrgency), accelUrgency);
        // Idle boost only when genuinely stationary -- NOT spinning in place (which has speed ~0 but
        // a high yaw rate); boosting the trigger there steps late and the foot crosses.
        bool stationary = speed < p.idleSpeedThreshold && math.abs(sim.smoothedYawRateDeg) < 20f;
        float idleBoost = stationary ? p.stepTriggerDist * p.idleBoostFraction : 0f;

        // The trigger drift is CAPPED at what the leg can actually recover.
        //
        // It used to grow without bound as `stepTriggerDist + speed * strideScale` -- 59 cm of drag at 5 m/s,
        // 80 cm at 7 m/s. But the foot can only ever be PLANTED about 0.35*leg (~30 cm) ahead of the hips before
        // the leg over-extends, so past a point the foot is being asked to recover more ground than it can reach:
        // it stays glued while the body races past, the leg stretches, and the feet read as "too slow to keep up".
        // Beyond the cap the only way to hold speed is to step MORE OFTEN, which is exactly what a real human
        // does -- stride length saturates and cadence takes over. Capping the drift makes that happen.
        float avgLegT = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float dsSpeedRef = math.sqrt(9.81f * avgLegT);
        float walkOnset = math.saturate(speed / math.max(1e-3f, k_WalkOnsetFrac * dsSpeedRef));
        // Reaction DISTANCE also answers to acceleration, not just to the standing/walking fade: when the body
        // is changing velocity hard the foot should commit sooner, not wait out the same drift it would tolerate
        // at a constant amble. Multiplicative so it composes with the standing fade instead of fighting it.
        float triggerScale = math.lerp(k_StandingTriggerFrac, 1f, walkOnset)
                           * math.lerp(1f, k_AccelTriggerFrac, accelUrgency);
        float threshold = math.min(p.stepTriggerDist * triggerScale + speed * p.strideScale + idleBoost, avgLegT * 0.55f);
        float stepDur = math.lerp(p.stepDurSlow, p.stepDurFast, urgencyT);

        // DOUBLE SUPPORT -- both feet on the ground together, which is what makes a walk read as WEIGHTED, and
        // what a slow deliberate walk is MOSTLY made of. A hump, not a ramp (see the const block): it PEAKS at a
        // slow walk, falls to k_DoubleSupportFast at a brisk one, and falls away again below k_WalkOnsetFrac,
        // where there is no gait cycle left to have a duty fraction of. Zeroed during a spin (yawUrgency) so the
        // anti-cross fix holds. walkOnset only bites below v-hat 0.10, where the walk term is always > 1.57, so
        // it can only ever REDUCE the dwell -- the walk range is untouched to the bit.
        float dsWalkT = math.saturate(speed / math.max(1e-3f, k_WalkTopSpeedFrac * dsSpeedRef));
        float dsFraction = math.lerp(k_DoubleSupportStanding,
            math.lerp(k_DoubleSupportSlow, k_DoubleSupportFast, dsWalkT), walkOnset);
        float doubleSupportSec = stepDur * dsFraction * (1f - yawUrgency);

        // ...but NEVER hold the feet longer than the turn can afford. Double support blocks stepping outright, so
        // decaying it on the softer urgency scale risks re-creating the stranding bug it was zeroed to avoid: at
        // 110 deg/s an unbounded 0.30 s settle would forbid stepping through 33 deg of body rotation, well past the
        // 20 deg re-plant threshold, and the foot would sit there with its yaw trigger screaming and gated shut.
        //
        // Cap it at the time it takes to actually SPEND the planted-yaw budget. That is the exact quantity the
        // trigger is measuring, so the settle can never outlast the foot's permission to stay put -- self-limiting
        // at every rate, and it needs no tuning constant. Above ~300 deg/s urgency has already zeroed the term, so
        // the measured 360-1200 deg/s anti-crossover behaviour is bit-identical to before.
        float absYawRate = math.abs(sim.smoothedYawRateDeg);
        if (absYawRate > 1f)
        {
            doubleSupportSec = math.min(doubleSupportSec, p.maxPlantedYawDegrees / absYawRate);
        }

        // TOUCHDOWN, detected as an EDGE (airborne last tick, grounded this one).
        //
        // While airborne the planted feet ride a HIPS-RELATIVE height -- correct in the air, where there is no
        // floor to stand on. But nothing put them back on the actual floor when the jump ended: the residual sits
        // UNDER maxVD, so the symmetric drift snap below never fired, and the foot simply landed hovering (the
        // sweep measured 138 mm on an adult jump, 79 mm at half scale -- i.e. the airborne margin itself).
        //
        // It cannot be fixed by tightening that snap: a planted foot legitimately sits above the hips' ground ray
        // when standing uphill, because the sim casts ONE ray under the hips and never re-casts under a planted
        // foot. Tightening it would fight every slope. The landing is an event, so treat it as one.
        bool touchedDown = sim.wasAirborne && !airborne;
        sim.wasAirborne = airborne;

        // ── Vertical correction ──
        if (airborne)
        {
            float airUpComp = groundUpComponent;
            if (left.phase == 0) { SetUpComponent(ref left.currentPos, airUpComp, up); SetUpComponent(ref left.plantedPos, airUpComp, up); }
            if (right.phase == 0) { SetUpComponent(ref right.currentPos, airUpComp, up); SetUpComponent(ref right.plantedPos, airUpComp, up); }
        }
        else if (touchedDown)
        {
            if (left.phase == 0) { SetUpComponent(ref left.currentPos, groundUpComponent, up); SetUpComponent(ref left.plantedPos, groundUpComponent, up); }
            if (right.phase == 0) { SetUpComponent(ref right.currentPos, groundUpComponent, up); SetUpComponent(ref right.plantedPos, groundUpComponent, up); }
        }
        else
        {
            float maxVD = p.hipToFoot * p.maxVerticalDriftFraction;
            if (left.phase == 0 && math.abs(GetUpComponent(left.plantedPos, up) - GetUpComponent(left.idealPos, up)) > maxVD)
            {
                float idealUp = GetUpComponent(left.idealPos, up);
                SetUpComponent(ref left.currentPos, idealUp, up);
                SetUpComponent(ref left.plantedPos, idealUp, up);
            }
            if (right.phase == 0 && math.abs(GetUpComponent(right.plantedPos, up) - GetUpComponent(right.idealPos, up)) > maxVD)
            {
                float idealUp = GetUpComponent(right.idealPos, up);
                SetUpComponent(ref right.currentPos, idealUp, up);
                SetUpComponent(ref right.plantedPos, idealUp, up);
            }
        }

        // Idle leg stiffening: a world-locked foot + a head-driven hip force the knee to track ~half the body
        // sway (the leg tilts rigidly about the foot). When stationary, let each planted foot gently give toward
        // the body's drift so the leg tilts less and the knee stays stiller -- a direct trade (stiffer knee = more
        // idle foot give). Walking is untouched (idle-only). idleStiffenRate 0 = off (locked feet, knee tracks ~0.5x).
        const float idleStiffenRate = 0f;
        if (stationary && idleStiffenRate > 0f)
        {
            float g = 1f - math.exp(-idleStiffenRate * dt);
            if (left.phase == 0) left.plantedPos += ProjectOntoUpPlane(left.idealPos - left.plantedPos, up) * g;
            if (right.phase == 0) right.plantedPos += ProjectOntoUpPlane(right.idealPos - right.plantedPos, up) * g;
        }

        // ── Update feet ──
        // Both plantedTimes advance BEFORE either gate is evaluated, so the two feet read the same snapshot.
        // This used to live inside UpdateFoot, which incremented f.plantedTime and then compared it against an
        // other.plantedTime that had NOT yet advanced this tick. The foot updated SECOND therefore saw its own
        // timer one tick fresher than the first foot saw it, so on the tick the window opens the second foot's
        // gate is already true while the first foot's is still one dt short. It steps, resets its timer to 0,
        // and the first foot's gate never opens at all -- it is permanently one tick behind. The same-tick
        // resolver below cannot arbitrate a race it never sees, because the two wantsStep flags are never
        // true together. That is the strafe-left starvation: left is updated first, so during a continuous
        // lateral drift -- where BOTH feet sit above the step threshold every tick -- every step goes to the
        // right foot and the left is left behind (1 step in 6 s, 7.5 m of drift, 10.9x leg extension).
        if (left.phase == 0) left.plantedTime += dt;
        if (right.phase == 0) right.plantedTime += dt;

        UpdateFoot(ref left, ref right, rawFwd, sim.smoothedVelocity, speed, threshold, stepDur, dt, up, hipsGround, inp.hipsPos, sim.smoothedYawRateDeg, doubleSupportSec, urgencyT);
        UpdateFoot(ref right, ref left, rawFwd, sim.smoothedVelocity, speed, threshold, stepDur, dt, up, hipsGround, inp.hipsPos, sim.smoothedYawRateDeg, doubleSupportSec, urgencyT);

        // One foot stays grounded: if both planted feet want to step the same tick, keep the
        // more-urgent (farther-drifted) request and defer the other. Without this both can lift at
        // once (e.g. spinning in place), which reads as floating/moonwalking. The per-foot trigger
        // already requires the other foot planted, so this only catches the same-tick double-want.
        if (left.phase == 0 && right.phase == 0 && left.wantsStep && right.wantsStep)
        {
            float ld = HDist(left.plantedPos, left.idealPos, up);
            float rd = HDist(right.plantedPos, right.idealPos, up);
            if (ld >= rd) right.wantsStep = false; else left.wantsStep = false;
        }

        // ── Knee hints ──
        float avgThigh = (p.leftThighLen + p.rightThighLen) * 0.5f;
        float3 hp = inp.hipsPos;

        float hipFootUpDist = math.abs(GetUpComponent(hp, up) - GetUpComponent((left.currentPos + right.currentPos) * 0.5f, up));
        float avgLeg2 = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float bendRatio = 1f - math.saturate(hipFootUpDist / avgLeg2);

        float3 leftKneeTarget = (hp + left.currentPos) * 0.5f + bodyFwd * (avgThigh * p.kneeForwardPushFraction);
        float3 rightKneeTarget = (hp + right.currentPos) * 0.5f + bodyFwd * (avgThigh * p.kneeForwardPushFraction);

        float kneeSplay = halfStance * inp.splayWhenCrouched * bendRatio;
        leftKneeTarget -= rawRight * kneeSplay;
        rightKneeTarget += rawRight * kneeSplay;

        float leftKneeUp = GetUpComponent(leftKneeTarget, up);
        float3 hipsGround2 = ProjectOntoUpPlane(hp, up) + up * leftKneeUp;
        float kneeMinSide = halfStance * p.kneeMinSideFraction;
        EnforceSide(ref leftKneeTarget, hipsGround2, rawRight, -1, kneeMinSide);
        float rightKneeUp = GetUpComponent(rightKneeTarget, up);
        EnforceSide(ref rightKneeTarget, ProjectOntoUpPlane(hp, up) + up * rightKneeUp, rawRight, +1, kneeMinSide);

        // RIDE THE BODY'S YAW, THEN SMOOTH -- the same fix smoothedBodyFwd needed, for the same reason.
        //
        // kneeHint is a WORLD position, and its target orbits the hips whenever the body turns. Smoothing it in
        // the world at tau ~100 ms therefore treats a deliberate rotation as something to chase: at 450 deg/s
        // the hint trails the leg by ~45 deg, which is the knee/lower-leg looking wrong during a fast turn (and
        // it is worst on a mouse-look flick, where the error reverses before the filter has caught up).
        //
        // Rotating the STORED hint about the hips by the body's own yaw delta first makes a turn pass through
        // with zero filter error, while the organic deviation -- the knee target moving relative to the body --
        // is still damped exactly as before. Identical output when the body is not turning.
        float kneeCarryDeg = bodyYawRate * dt;
        if (math.abs(kneeCarryDeg) > 1e-4f)
        {
            quaternion kneeCarry = quaternion.AxisAngle(up, math.radians(kneeCarryDeg));
            left.kneeHint = hp + math.mul(kneeCarry, left.kneeHint - hp);
            right.kneeHint = hp + math.mul(kneeCarry, right.kneeHint - hp);
        }

        float kneeAlpha = 1f - math.exp(-p.kneeHintLerpSpeed * dt);
        left.kneeHint = math.lerp(left.kneeHint, leftKneeTarget, kneeAlpha);
        right.kneeHint = math.lerp(right.kneeHint, rightKneeTarget, kneeAlpha);

        // ── Final side enforcement ──
        // Only on stepping feet (anti-cross while in flight). A planted foot is world-locked;
        // enforcing side on it hard-snaps it sideways when the body drifts (a visible pop), and
        // the plant->ideal step trigger already re-steps a plant that has drifted too far.
        float leftFootUp = GetUpComponent(left.currentPos, up);
        float3 hipsGround3 = ProjectOntoUpPlane(hp, up) + up * leftFootUp;
        float footMinSide = halfStance * p.footSideEnforceFraction;
        if (left.phase == 1) EnforceSide(ref left.currentPos, hipsGround3, rawRight, -1, footMinSide);
        if (right.phase == 1) EnforceSide(ref right.currentPos, hipsGround3, rawRight, +1, footMinSide);

        // ── HARD anti-overlap backstop ──
        // The tracking + widening above hold the feet apart up to ~480 deg/s; past that (a fast physical whip
        // or snap turn, ~600-1200 deg/s) a planted foot is orbited into the other before either can step, and
        // the feet visibly interpenetrate. This GUARANTEES they never do, at ANY rate: enforce a minimum
        // EUCLIDEAN separation, pushing symmetrically along their current separation (no net drift). Euclidean,
        // not lateral, so it does NOT fight the narrow-stance fast walk -- feet passing close there are offset
        // fore-aft, so their euclidean gap is large and this never fires (measured byte-identical walk metrics).
        float3 sepV = ProjectOntoUpPlane(left.currentPos - right.currentPos, up);
        float sepLen = math.length(sepV);
        float minSep = p.stanceWidth * k_MinFootSepFrac;
        if (sepLen < minSep)
        {
            float3 sepAxis = sepLen > 1e-4f ? sepV / sepLen : -rawRight;   // degenerate: left to the -right side
            float push = 0.5f * (minSep - sepLen);
            left.currentPos += sepAxis * push;
            right.currentPos -= sepAxis * push;
        }

        // ── Hip bob + lateral sway + pelvis rotation ──
        var outp = output[0];
        outp.hipBob = ComputeHipBob(ref left, ref right, speed);
        outp.hipSway = bodyRight * ComputeHipSway(ref left, ref right, speed);
        outp.pelvisDelta = ComputePelvisDelta(ref left, ref right, speed, up, bodyFwd);
        outp.airborne = airborne;
        output[0] = outp;

        // ── Write back ──
        feet[0] = left;
        feet[1] = right;
        simState[0] = sim;
    }

    private void UpdateFoot(ref BasisFootNativeState f, ref BasisFootNativeState other,
        float3 rawFwd, float3 smoothedVelocity, float speed, float threshold, float stepDur, float dt, float3 up,
        float3 hipsGround, float3 hipsPos, float yawRateDeg, float doubleSupportSec, float urgencyT)
    {
        float3 bodyFlat = rawFwd - up * math.dot(rawFwd, up);
        bool bodyFlatValid = math.lengthsq(bodyFlat) > 1e-6f;
        if (bodyFlatValid) bodyFlat = math.normalize(bodyFlat);

        if (f.phase == 0) // Planted
        {
            float a = 1f - math.exp(-p.plantedLerpSpeed * dt);
            f.currentPos = math.lerp(f.currentPos, f.plantedPos, a);

            // Re-conform a PLANTED foot to the live surface normal. plantedRot is otherwise written only at
            // landing, so the per-frame surface probes would be inert for exactly the foot they describe.
            //
            // Rebuilt from the FROZEN plantedBodyFwd, never the live one. Holding a planted foot's YAW is
            // deliberate -- a foot that is down must not pivot as the body turns; that is the whole reason the
            // yaw trigger and maxPlantedYawDegrees exist to arbitrate when it may re-plant. Only the TILT
            // tracks. Because the forward is unchanged, this is exactly a no-op on flat ground (filteredNormal
            // stays up) and a no-op when the probes are disabled (filteredNormal keeps its plant-time value).
            if (math.lengthsq(f.plantedBodyFwd) > 1e-6f && math.lengthsq(f.filteredNormal) > 1e-6f)
            {
                quaternion plantedAlign = f.sideSign < 0 ? p.footAlignLeft : p.footAlignRight;
                f.plantedRot = FootRotation(f.plantedBodyFwd, f.filteredNormal, up, plantedAlign, 0f);
            }

            // HEEL STRIKE -> FOOT FLAT is a STAGE with a duration, not a smoothing rate. It used to be a bare
            // exponential (`slerp(currentRot, plantedRot, 1-exp(-rate*dt))`), which has three problems: it has
            // no defined end (tau 62 ms never actually arrives), it is a fixed rate so it does NOT scale with
            // the avatar the way every other gait time here does, and it is self-referential -- the same shape
            // this file already rejected for the swing. Real foot-flat takes ~7-8% of the gait cycle, which
            // against this model's cycle is ~0.30 of one stepDur.
            //
            // plantedTime is already the elapsed-since-landing clock, so drive the roll-down off it directly.
            // Past the window the exponential takes over again -- not for the landing, but to smooth ONGOING
            // changes in plantedRot as the surface probes re-conform the foot. That is what rotationLerpSpeed
            // is actually for.
            float flatDur = math.max(1e-4f, f.stepDur * k_FootFlatFrac);
            if (f.plantedTime < flatDur)
            {
                float fl = f.plantedTime / flatDur;
                f.currentRot = math.slerp(f.landRot, f.plantedRot, fl * fl * (3f - 2f * fl));
            }
            else
            {
                float ra = 1f - math.exp(-p.rotationLerpSpeed * dt);
                f.currentRot = math.slerp(f.currentRot, f.plantedRot, ra);
            }

            float dist = HDist(f.plantedPos, f.idealPos, up);

            // "How far has the BODY turned since this foot planted." The reference used to be
            // plantedRot * (0,0,1) -- i.e. the FOOT BONE's local +Z, because plantedRot is seeded from
            // the foot bone's world rotation on every re-engage. A humanoid foot bone's +Z is a
            // rig-dependent axis (typically down the shin or along the toes), NOT the body forward, so
            // that comparison was meaningless: flatten a shin-aligned +Z and you get ~zero, the length
            // guard rejects it, and the yaw trigger SILENTLY DISABLES ITSELF. Rotation could then only
            // ever be answered by the distance trigger -- and a pure turn only orbits the ideal foot at
            // radius halfStance (~12 cm), so it takes ~90 deg of turn to travel one trigger distance.
            // That is the "I can spin 180 before the legs even try to follow".
            //
            // Store the body forward itself. Rig-convention-free, and it survives foot rotation being
            // discarded downstream. Seed (never skip) when degenerate so it cannot deadlock again.
            bool yawTrigger = false;
            if (bodyFlatValid)
            {
                if (math.lengthsq(f.plantedBodyFwd) < 1e-6f) f.plantedBodyFwd = bodyFlat;

                float yawDiff = math.abs(SignedAngle(math.normalize(f.plantedBodyFwd), bodyFlat, up));
                yawTrigger = yawDiff > p.maxPlantedYawDegrees;
            }

            // Double support = BOTH feet down together for a while. That period began at the LATER of the two
            // landings, so its length so far is min(f.plantedTime, other.plantedTime) -- and that expression is
            // SYMMETRIC, which is the whole point.
            //
            // The obvious formulation, `other.plantedTime >= doubleSupportSec`, is asymmetric and STARVES A FOOT
            // OUTRIGHT. At a walk the swinging foot is airborne ~90% of the time, so the planted foot's only
            // opportunity is the instant the other one LANDS -- and at that exact instant other.plantedTime is 0,
            // so its gate fails. The foot that just landed, meanwhile, sees a partner that has been planted for
            // ages, so ITS gate always passes and it immediately steps again. Whichever foot moves first therefore
            // wins forever: the sweep showed strafe-left at "steps 23 (L0 R23)" -- the left foot took ZERO steps in
            // six seconds while the body walked 11 metres away from it (extension 23x standing reach).
            //
            // With min(), both feet read the same number, so neither can lock the other out. Both gates open on
            // the same tick and the existing same-tick resolver hands the step to whichever foot has drifted
            // further -- i.e. exactly the starved one.
            float doubleSupportSoFar = math.min(f.plantedTime, other.plantedTime);
            bool otherSettled = other.phase == 0 && doubleSupportSoFar >= doubleSupportSec;

            if ((dist > threshold || yawTrigger) && otherSettled)
            {
                f.wantsStep = true;
                // Effort proportional to need: how far PAST its trigger this foot is, on top of the body-wide
                // urgency. A foot barely over the line still takes a deliberate step; one that has been left
                // well behind commits. See k_DriftUrgencyRefMul.
                float driftOver = math.saturate((dist - threshold) / math.max(1e-4f, threshold * k_DriftUrgencyRefMul));
                // ...and how close this leg is to being torn straight. hipToFoot IS the standing straight-leg
                // distance, so ratio 1.0 is "standing" and only the excess counts -- see k_ExtUrgencyBand.
                float extRatio = math.length(hipsPos - f.plantedPos) / math.max(1e-4f, p.hipToFoot);
                float extOver = math.saturate((extRatio - 1f) / k_ExtUrgencyBand);
                f.stepUrgency = math.max(urgencyT, math.max(driftOver, extOver));
                f.predictedTargetXZ = ComputeStepPrediction(ref f, rawFwd, smoothedVelocity, speed, stepDur, up, hipsGround, yawRateDeg);
            }
            else
            {
                f.wantsStep = false;
            }
        }
        else // Stepping
        {
            f.wantsStep = false;
            f.stepTimer += dt;
            float t = math.saturate(f.stepTimer / f.stepDur);

            // Smoothstep, not a cubic ease-OUT. The old curve (1-(1-t)^3) has its MAXIMUM slope at t=0, so the
            // foot was flung off the ground at ~3.7 m/s on the first frame of the swing and then decelerated into
            // the landing -- backwards. A real swing leg is slow at toe-off, fastest at mid-swing, and slow again
            // at heel-strike. Smoothstep's derivative 6t(1-t) is exactly that: zero at both ends, peak in the
            // middle. This is the single most visible "the feet snap" artifact.
            // PRE-SWING comes FIRST, and it is a rotation, not a translation. Real gait spends 50-60% of the
            // cycle rolling over the forefoot before the foot goes anywhere: the heel lifts, the ankle
            // plantarflexes to ~20 deg, and the TOE stays on the ground as the pivot ("third rocker", Perry).
            // Only then does the foot leave. The step used to have no such stage -- toe-off pitch was applied
            // at t=0 SIMULTANEOUSLY with translation, so the foot slid away already pitched and you never saw
            // it get ready. That missing preparation is what reads as mechanical: there is no weight transfer,
            // the foot just goes.
            //
            // Pre-swing is 10% of the gait cycle against a 40% swing, and this step event spans both, so it is
            // 10/50 = 20% of it. During it the ankle ARCS ABOUT THE TOE: forward by arm*(1-cos), up by
            // arm*sin -- ~1.4 cm forward and ~7 cm up on an adult, which is the visible heel-off.
            float3 footFwd = math.lengthsq(f.plantedBodyFwd) > 1e-6f ? math.normalize(f.plantedBodyFwd) : bodyFlat;
            float pivotArm = math.max(p.footLength, 1e-4f);
            float toeOffRad = math.radians(k_ToeOffDeg);
            float3 liftOffPos = f.stepStartPos
                + footFwd * (pivotArm * (1f - math.cos(toeOffRad)))
                + up * (pivotArm * math.sin(toeOffRad));

            float3 pos;
            float pitchDeg;
            float swingU;
            if (t < k_PreSwingFrac)
            {
                float pr = t / k_PreSwingFrac;
                float roll = pr * pr * (3f - 2f * pr);
                pitchDeg = k_ToeOffDeg * roll;
                float th = math.radians(pitchDeg);
                pos = f.stepStartPos
                    + footFwd * (pivotArm * (1f - math.cos(th)))
                    + up * (pivotArm * math.sin(th));
                swingU = 0f;
            }
            else
            {
                swingU = (t - k_PreSwingFrac) / math.max(1e-4f, 1f - k_PreSwingFrac);
                pitchDeg = SwingAnklePitchDeg(swingU);
                pos = math.lerp(liftOffPos, f.stepTargetPos, swingU * swingU * (3f - 2f * swingU));
            }
            float ease = swingU * swingU * (3f - 2f * swingU);

            // Lift scales with stride length (a short shuffle barely lifts; a full stride lifts fully),
            // floored so a tiny step still clears the ground and capped at the calibrated step height.
            // Scales with the avatar automatically (avgLeg and stepHeightCalc both scale).
            float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
            float stepDist = HDist(f.stepStartPos, f.stepTargetPos, up);
            float strideFrac = math.saturate(stepDist / math.max(1e-3f, avgLeg * p.stepHeightStrideRefFraction));
            // Kept BEFORE the turn floor below: the medial swing path scales on how much ground this step
            // actually covers, and a turn step covers almost none. Using the floored value would give a
            // pivot-in-place step a full medial excursion, which is exactly where the feet can least afford it.
            float travelFrac = strideFrac;
            // A turn step covers almost no ground but is still a real step -- floor it (frozen at commit, so the
            // peak height cannot move mid-swing). stepArcScale is 0 for every non-turn step and in the sweep/mocap
            // mirrors, which makes this exactly a no-op there.
            strideFrac = math.max(strideFrac, f.stepArcScale);
            float dynamicHeight = p.stepHeightCalc * math.lerp(p.stepHeightMinFraction, 1.0f, strideFrac);

            // Normalise the arc by its OWN peak, derived from the exponents. The hardcoded 0.234 was wrong for the
            // default exponents (the true peak of t^0.6*(1-t)^1.4 is 0.2947 at t=0.3), so the expression peaked at
            // 1.26 and saturate() clipped it -- the foot sat at exactly max height, dead flat, for ~42% of the
            // swing. That plateau is the "robotic" read. Worse, the constant did not track the exponents: retuning
            // either one silently clipped harder or never reached the commanded height.
            //
            //   peak of t^a * (1-t)^b  is at  t* = a/(a+b),  value  t*^a * (1-t*)^b
            float a = p.stepArcLiftExp, b = p.stepArcDropExp;
            float tPeak = a / math.max(1e-4f, a + b);
            float arcPeak = math.max(1e-4f, math.pow(tPeak, a) * math.pow(1f - tPeak, b));

            // The arc rides the SWING parameter, not the whole step -- during pre-swing the foot is still on
            // its toe and must not be lifted by the arc as well, or the heel-off double-counts.
            float lift = math.pow(swingU, a) * math.pow(1f - swingU, b) / arcPeak;
            pos += up * (math.saturate(lift) * dynamicHeight);

            // MEDIAL SWING PATH. The foot was travelling a straight line from lift-off to landing, which no
            // leg does: the swing foot passes CLOSE TO THE STANCE LEG as it comes under the body and only
            // then swings back out to its landing spot. That inward pass is one of the most recognisable
            // things about a walk seen from the front, and a straight lateral line is a robotic tell of the
            // same family as the flat-topped arc that used to sit in the lift curve.
            //
            // sin(pi*u) is exactly zero at both ends, so lift-off and landing are untouched -- the step still
            // starts and finishes precisely where the solver put it, only the path between bends. Scaled by
            // TRAVEL (not the turn-floored strideFrac) so a pivot-in-place step keeps its full base, which
            // matters because the anti-crossover margin is narrowest exactly there.
            float3 swingRightCross = math.cross(up, rawFwd);
            if (math.lengthsq(swingRightCross) > 1e-6f)
            {
                float3 swingRight = math.normalize(swingRightCross);
                float medial = math.sin(swingU * math.PI) * (p.stanceWidth * 0.5f) * k_MedialSwingFrac * travelFrac;
                pos -= swingRight * (f.sideSign * medial);
            }
            f.currentPos = pos;

            quaternion footAlign = f.sideSign < 0 ? p.footAlignLeft : p.footAlignRight;

            // Pre-swing holds the PLANTED yaw: the toe is still on the ground, so the foot may pitch but must
            // not swivel. Only once it is airborne does the frame track the live body forward.
            quaternion liveRot = t < k_PreSwingFrac
                ? FootRotation(footFwd, f.filteredNormal, up, footAlign, pitchDeg)
                : FootRotation(rawFwd, f.filteredNormal, up, footAlign, pitchDeg);

            // Blend FROM the rotation the foot had at lift-off, parameterised by step progress -- exactly
            // what the line above does for position (lerp(stepStartPos, stepTargetPos, ease)).
            //
            // This used to be `slerp(f.currentRot, liveRot, ease)`: a SELF-REFERENTIAL slerp, re-applied
            // every frame, using a step-progress curve as if it were a per-frame smoothing alpha. That
            // converges by FRAME COUNT, not by elapsed time, so the foot's orientation part-way through an
            // identical 0.35 s swing depended on the framerate: 74% of the way there at 30 fps, 94% at
            // 72 Hz, 99.7% at 144 Hz. The foot was literally in a different pose mid-stride on a faster GPU.
            //
            // It hid because the ENDPOINT always converges (ease reaches 1, so the last frame snaps it home
            // regardless) -- the error only exists while the foot is moving, which is the only time anyone
            // is looking at it. And no rate constant could have fixed it: the FORM was wrong, not the tuning.
            //
            // Latent today (BasisLocalRigDriver.FootRotationFromDriver is false, so this rotation is computed
            // and discarded) -- but foot rotation is the next naturalness win, and this would have shipped
            // underneath it. Gated by BasisBlendSpeedTests.
            // Each stage blends from where the previous one ended, so the two are continuous by construction:
            // pre-swing runs stepStartRot -> the fully plantarflexed lift-off pose, and the swing starts from
            // exactly that pose. Blending the whole step on `ease` would leave the roll invisible, since ease
            // is 0 for the entire pre-swing.
            if (t < k_PreSwingFrac)
            {
                float pr = t / k_PreSwingFrac;
                f.currentRot = math.slerp(f.stepStartRot, liveRot, pr * pr * (3f - 2f * pr));
            }
            else
            {
                quaternion liftOffRot = FootRotation(footFwd, f.filteredNormal, up, footAlign, k_ToeOffDeg);
                f.currentRot = math.slerp(liftOffRot, liveRot, ease);
            }

            if (t >= 1f)
            {
                f.phase = 0; // Planted
                f.plantedPos = f.stepTargetPos;

                // The PLANTED rotation is flat (pitch 0), while currentRot is still sitting at the dorsiflexed
                // heel-strike pose. Freezing that pose as landRot gives the foot-flat roll-down an explicit
                // FROM, so it is a staged transition over k_FootFlatFrac rather than an open-ended smoothing
                // rate -- the same treatment stepStartRot gives the swing.
                //
                // From here plantedRot is FROZEN IN THE WORLD until this foot next steps. That is what stops a
                // planted foot pivoting in place as the body turns -- the rotation is held until we lift it and
                // put it back down.
                f.landRot = f.currentRot;
                f.plantedRot = FootRotation(rawFwd, f.filteredNormal, up, footAlign, 0f);
                f.plantedBodyFwd = bodyFlatValid ? bodyFlat : f.plantedBodyFwd;
                f.currentPos = f.stepTargetPos;
                f.plantedTime = 0f;   // starts the double-support window for the OTHER foot
            }
        }
    }

    private float3 ComputeStepPrediction(ref BasisFootNativeState f, float3 bodyFwd, float3 smoothedVelocity, float speed, float stepDur, float3 up,
        float3 hipsGround, float yawRateDeg)
    {
        // Uses the caller's live sim state — simState[0] still holds last frame's values here
        // (Execute writes back only at its end).
        float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float3 svFlat = smoothedVelocity - up * math.dot(smoothedVelocity, up);
        // Relative, matching the reachGate below which already uses sqrt(g*L) — was an absolute (0.1 m/s)^2.
        float predMoveGate = 0.034f * p.fastSpeedRef;
        float3 moveDir = math.lengthsq(svFlat) > predMoveGate * predMoveGate
            ? math.normalize(svFlat)
            : bodyFwd;
        float predAmount = math.min(speed * stepDur * p.predictionFactor, avgLeg * p.maxPredictionFraction);
        // Reach-ahead FLOOR: plant the foot well in FRONT even on a slow deliberate walk (real gait does; the
        // velocity term above is tiny at slow speed). Gated by translation speed so a turn-in-place (speed ~0,
        // high yaw) is untouched -- otherwise it would fling the foot forward on a spin. Measured to lengthen
        // the stride toward real at MEDIUM walk (0.70 -> 0.76 L, real 0.77) and slightly lower over-extension;
        // it does NOT help slow-walk stride (there stride == speed/cadence and cadence is dwell-capped).
        float dsRef = math.sqrt(9.81f * avgLeg);
        float reachGate = math.saturate((speed - 0.03f * dsRef) / (0.08f * dsRef));
        float reachFloor = k_ReachAheadFrac * avgLeg * reachGate;
        predAmount = math.min(math.max(predAmount, reachFloor), avgLeg * p.maxPredictionFraction);

        // ANGULAR prediction. The old code predicted TRANSLATION only, so a pure turn (speed ~0 => predAmount ~0)
        // aimed the foot at where the body is pointing NOW -- but the foot lands stepDur LATER, by which time the
        // body has turned further. The foot was therefore born already behind and could never catch up: at 90 deg/s
        // with a 0.24 s swing the body turns ~22 deg mid-flight, so the foot perpetually trailed by 22-35 deg
        // (the yaw trigger). That is the leg "lagging behind the motion" and the steps not landing where the
        // rotation is going -- and it is why the sweep had to TOLERATE crossovers during spins instead of
        // preventing them.
        //
        // Rotate the target about the body centre by the yaw the body will actually cover during the swing, so the
        // foot lands where the body WILL be pointing. Clamped so a violent spin can't fling the foot past what the
        // hip can rotate to.
        float predYawDeg = math.clamp(yawRateDeg * stepDur, -p.maxPlantedYawDegrees, p.maxPlantedYawDegrees);
        quaternion yawPredict = quaternion.AxisAngle(up, math.radians(predYawDeg));
        float3 fromCenter = f.idealPos - hipsGround;
        float3 predictedIdeal = hipsGround + math.mul(yawPredict, fromCenter);

        return predictedIdeal + moveDir * predAmount;
    }

    // Vertical pelvis motion over the gait cycle. The PHASE here was inverted, which is why the walk read wrong
    // even though the frequency was right.
    //
    // While one foot is swinging, the OTHER is in mid-stance -- and mid-stance is where a human's centre of mass
    // is at its HIGHEST: you vault over the straight stance leg like an inverted pendulum. The COM is at its
    // LOWEST at double support, where both legs are splayed and the hips sit between them. The old code returned
    // -sin(pi*t) at mid-swing, pushing the pelvis DOWN exactly where it should rise, and 0 at double support
    // exactly where it should be lowest. Right frequency, opposite sign.
    //
    // Speed gate is relative, not an absolute 0.05 m/s: a small avatar's whole speed range scales as sqrt(g*L),
    // so a fixed m/s cutoff silently disables the bob for anything short.
    private float ComputeHipBob(ref BasisFootNativeState left, ref BasisFootNativeState right, float speed)
    {
        if (speed < 0.02f * p.fastSpeedRef) return 0f;

        float maxBob = p.hipToFoot * p.hipBobFraction;
        float speedScale = math.saturate(speed / p.fastSpeedRef);
        float amplitude = maxBob * speedScale;

        float leftRise = 0f, rightRise = 0f;
        if (left.phase == 1)
        {
            float t = math.saturate(left.stepTimer / left.stepDur);
            leftRise = math.sin(t * math.PI);
        }
        if (right.phase == 1)
        {
            float t = math.saturate(right.stepTimer / right.stepDur);
            rightRise = math.sin(t * math.PI);
        }

        float rise = math.max(leftRise, rightRise) * amplitude;

        // Subtract the loading-response dip, which lands right after heel strike -- when nothing is swinging yet,
        // so `rise` is 0 and the COM reaches its true low point of the cycle. This is what bends the stance knee
        // (see k_LoadingDipFraction). Net travel stays within a couple of cm.
        float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float dipAmplitude = avgLeg * k_LoadingDipFraction * speedScale;
        float dip = math.max(LoadingResponse(ref left), LoadingResponse(ref right)) * dipAmplitude;

        return rise - dip;
    }

    /// <summary>0 -> 1 -> 0 over the loading response window that follows this foot's heel strike; 0 otherwise.</summary>
    private static float LoadingResponse(ref BasisFootNativeState f)
    {
        if (f.phase != 0) return 0f;
        float dur = f.stepDur * k_LoadingResponseFrac;
        if (dur <= 1e-4f) return 0f;
        float u = f.plantedTime / dur;
        if (u >= 1f) return 0f;
        return math.sin(u * math.PI);
    }

    // Lateral pelvis sway, signed along body-right, in metres.
    //
    // A human does not walk down a rail: each step rocks the COM sideways OVER THE LOADED LEG, because the body
    // has to balance on it. Whichever foot is SWINGING, the other is the stance leg, so the pelvis leans toward
    // the stance side. It peaks at mid-swing (mid-stance of the other leg) and passes through zero at double
    // support, where the weight is shared. Same phase as the vertical bob, one axis over.
    //
    // Sign: a swinging LEFT foot means the RIGHT leg is loaded, so the body sways to +right. And vice versa.
    // Amplitude scales with the avatar's own leg and fades out at a standstill, exactly like the bob.
    private float ComputeHipSway(ref BasisFootNativeState left, ref BasisFootNativeState right, float speed)
    {
        if (speed < 0.02f * p.fastSpeedRef) return 0f;

        float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float amplitude = avgLeg * k_HipSwayFraction * math.saturate(speed / p.fastSpeedRef);

        float sway = 0f;
        if (left.phase == 1)
        {
            float t = math.saturate(left.stepTimer / left.stepDur);
            sway += math.sin(t * math.PI);    // left swings => right leg loaded => lean +right
        }
        if (right.phase == 1)
        {
            float t = math.saturate(right.stepTimer / right.stepDur);
            sway -= math.sin(t * math.PI);    // right swings => left leg loaded => lean -right
        }

        return sway * amplitude;
    }

    // Gait-driven pelvis rotation, as a WORLD delta to pre-multiply onto the hips rotation.
    //
    // Two of Saunders' determinants of gait, both absent today, which is why the pelvis reads as one rigid block:
    //
    //  AXIAL (about up): the SWING-side hip is carried FORWARD. This is what lets a human lengthen the stride
    //  without lengthening the leg -- the pelvis rotates instead of the leg over-reaching -- and it is the single
    //  most recognisable thing about a human walk when seen from above.
    //
    //  LIST / Trendelenburg (about body-forward): the SWING-side hip DROPS, because there is no longer a leg under
    //  it holding it up. It is what makes the walk look loaded rather than floated.
    //
    // Both peak at mid-swing and pass through zero at double support, in phase with the bob and the sway. Sign is
    // driven off which foot is SWINGING: a swinging LEFT foot carries the LEFT hip forward and drops it.
    // Returns identity when standing still, and is only ever applied when there is NO hip tracker.
    private quaternion ComputePelvisDelta(ref BasisFootNativeState left, ref BasisFootNativeState right, float speed, float3 up, float3 bodyFwd)
    {
        if (speed < 0.02f * p.fastSpeedRef) return quaternion.identity;

        float scale = math.saturate(speed / p.fastSpeedRef);

        // +1 => LEFT foot is swinging, -1 => RIGHT foot is swinging.
        float swingSide = 0f;
        if (left.phase == 1)
        {
            float t = math.saturate(left.stepTimer / left.stepDur);
            swingSide += math.sin(t * math.PI);
        }
        if (right.phase == 1)
        {
            float t = math.saturate(right.stepTimer / right.stepDur);
            swingSide -= math.sin(t * math.PI);
        }
        if (math.abs(swingSide) < 1e-4f) return quaternion.identity;

        // Left-handed: a POSITIVE rotation about `up` turns +right toward -forward, i.e. it carries the LEFT side
        // forward. So a swinging left foot (swingSide > 0) wants a positive axial angle. Same reasoning for list:
        // a positive rotation about bodyFwd tilts +right upward, which drops the LEFT side.
        float axialDeg = swingSide * k_PelvisAxialDeg * scale;
        float listDeg = swingSide * k_PelvisListDeg * scale;

        quaternion axial = quaternion.AxisAngle(up, math.radians(axialDeg));
        quaternion list = quaternion.AxisAngle(bodyFwd, math.radians(listDeg));
        return math.mul(axial, list);
    }

    // ── Helpers ──

    /// <summary>Horizontal distance (in the plane perpendicular to up).</summary>
    private static float HDist(float3 a, float3 b, float3 up)
    {
        float3 diff = a - b;
        diff -= up * math.dot(diff, up); // remove vertical component
        return math.length(diff);
    }

    /// <summary>Get the component of a position along the up axis.</summary>
    private static float GetUpComponent(float3 pos, float3 up)
    {
        return math.dot(pos, up);
    }

    /// <summary>Set the component of a position along the up axis.</summary>
    private static void SetUpComponent(ref float3 pos, float value, float3 up)
    {
        float current = math.dot(pos, up);
        pos += up * (value - current);
    }

    /// <summary>Project a position onto the plane perpendicular to up (remove up component).</summary>
    private static float3 ProjectOntoUpPlane(float3 pos, float3 up)
    {
        return pos - up * math.dot(pos, up);
    }

    private static void EnforceSide(ref float3 pos, float3 center, float3 bodyRight, int sideSign, float minDist)
    {
        float3 toPos = pos - center;
        float lateral = math.dot(toPos, bodyRight);

        if (sideSign > 0 && lateral < sideSign * minDist)
            pos += bodyRight * (sideSign * minDist - lateral);
        else if (sideSign < 0 && lateral > -minDist)
            pos -= bodyRight * (lateral + minDist);
    }

    /// <summary>
    /// The horizontal yaw a bone is facing, and how much that answer is worth.
    ///
    /// Flattening a bone's forward onto the ground plane (fwd - up*dot(fwd,up)) is only meaningful while the
    /// forward has some horizontal length. Look STRAIGHT DOWN or STRAIGHT UP and the head's forward is parallel
    /// to up, so the projection collapses to ~zero -- and normalizing a near-zero vector turns float noise into a
    /// wildly swinging yaw. The old guards let this through: hips and chest only required lengthsq > 0.001, i.e.
    /// a mere 1.8 degrees off vertical, and then normalized a vector carrying almost no yaw information at all.
    ///
    /// But the yaw is not actually lost -- it has just moved axes. When a bone's FORWARD goes vertical, its UP
    /// axis becomes HORIZONTAL, and it carries the yaw exactly:
    ///     looking DOWN (fwd.up &lt; 0): the head's up axis points FORWARD   => yaw = +flat(boneUp)
    ///     looking UP   (fwd.up &gt; 0): the head's up axis points BACKWARD  => yaw = -flat(boneUp)
    /// So read the yaw off whichever axis is currently horizontal, and cross-fade between them by how horizontal
    /// the forward is. No cutoff, no pop, no noise amplification, and it stays exact at the poles.
    ///
    /// confidence = |flat(fwd)| = sin(angle from vertical), which is precisely how much horizontal information
    /// the forward axis holds. Returns false only if BOTH axes are degenerate, which cannot happen for a valid
    /// rotation (forward and up are orthogonal, so at most one can be parallel to `up`).
    /// </summary>
    public static bool BoneYaw(quaternion rot, float3 up, out float3 yawDir)
    {
        float3 fwd = math.mul(rot, new float3(0, 0, 1));
        float3 fwdFlat = fwd - up * math.dot(fwd, up);
        float fwdLen = math.length(fwdFlat);

        float3 boneUp = math.mul(rot, new float3(0, 1, 0));
        float3 upFlat = boneUp - up * math.dot(boneUp, up);
        float upLen = math.length(upFlat);

        // -sign(fwd . up): looking down (negative) flips the up-axis reading to +forward, looking up flips it to
        // -forward. At fwd.up == 0 the forward axis is fully horizontal, so this branch carries zero weight anyway.
        float upSign = -math.sign(math.dot(fwd, up));

        float3 fromFwd = fwdLen > 1e-5f ? fwdFlat / fwdLen : float3.zero;
        float3 fromUp = upLen > 1e-5f ? (upFlat / upLen) * upSign : float3.zero;

        // fwdLen IS the blend: 1 when the forward is horizontal (trust it entirely), 0 when it is vertical
        // (trust the up axis entirely). fwd and boneUp are orthogonal, so fwdLen^2 + upLen^2 == 1 -- as one
        // collapses the other is at full length. The handover is smooth and always well-conditioned.
        float3 blended = fromFwd * fwdLen + fromUp * (1f - fwdLen);
        if (math.lengthsq(blended) < 1e-8f)
        {
            yawDir = float3.zero;
            return false;
        }

        yawDir = math.normalize(blended);
        return true;
    }

    public static float3 ComputeBodyForward(BasisFootSimInput inp, BasisFootSimState sim, BasisFootSimParams p, float3 up)
    {
        float3 accumulated = float3.zero;
        float totalWeight = 0f;

        if (BoneYaw(inp.hipsRot, up, out float3 hipsYaw))
        {
            accumulated += hipsYaw * p.bodyFwdHipsWeight;
            totalWeight += p.bodyFwdHipsWeight;
        }

        if (inp.hasChest && BoneYaw(inp.chestRot, up, out float3 chestYaw))
        {
            accumulated += chestYaw * p.bodyFwdChestWeight;
            totalWeight += p.bodyFwdChestWeight;
        }

        if (BoneYaw(inp.headRot, up, out float3 headYaw))
        {
            accumulated += headYaw * p.bodyFwdHeadWeight;
            totalWeight += p.bodyFwdHeadWeight;
        }

        if (totalWeight > 0f)
        {
            accumulated /= totalWeight;
            if (math.lengthsq(accumulated) > 0.001f)
                return math.normalize(accumulated);
        }

        return inp.avatarForward;
    }

    // Desired WORLD rotation of the foot bone.
    //
    // Everything up to `result` builds a body-derived FRAME (+Z = body forward laid on the surface, +Y = surface
    // normal), tilt-clamped toward upright and yaw-clamped. That frame is NOT the foot bone's rotation, and the
    // old code returned it as if it were -- which is precisely why switching foot rotation on "came out toes-up",
    // and why it got switched back off. A humanoid foot bone's local axes are rig-dependent (its +Z often runs
    // down the shin or out along the toes); they are not the body's.
    //
    // `footAlign` is the bone's orientation measured IN this same frame at the calibration T-pose, so
    // `frame * footAlign` re-seats the bone into whatever frame we hand it. At rest the frame IS the rest frame,
    // so it reproduces the T-pose rotation exactly -- identity by construction -- and the avatar's natural toe-out
    // comes along for free instead of being clamped away.
    //
    // swingPitchDeg pitches the FRAME about its own lateral axis (local X) before the bone is seated, so the ankle
    // pitches about the correct axis on any rig. Unity is left-handed, so a POSITIVE rotation about +X carries +Z
    // (forward) downward => positive = toes down = plantarflexion.
    public quaternion FootRotation(float3 bodyFwd, float3 normal, float3 up, quaternion footAlign, float swingPitchDeg)
    {
        if (math.lengthsq(normal) < 0.001f) normal = up;

        float3 fwd = ProjectOnPlane(bodyFwd, normal);
        if (math.lengthsq(fwd) < 1e-6f)
            fwd = ProjectOnPlane(new float3(0, 0, 1), normal);
        fwd = math.normalize(fwd);

        quaternion surfaceRot = quaternion.LookRotation(fwd, normal);
        quaternion uprightRot = quaternion.LookRotation(fwd, up);
        float tiltAngle = AngleBetween(uprightRot, surfaceRot);
        quaternion result = tiltAngle > 0.01f
            ? math.slerp(uprightRot, surfaceRot, math.saturate(p.maxFootTiltDegrees / tiltAngle))
            : uprightRot;

        float3 footFwd = math.mul(result, new float3(0, 0, 1));
        float3 footFwdFlat = footFwd - up * math.dot(footFwd, up);
        float3 bodyFwdFlat = bodyFwd - up * math.dot(bodyFwd, up);

        if (math.lengthsq(footFwdFlat) > 1e-6f && math.lengthsq(bodyFwdFlat) > 1e-6f)
        {
            footFwdFlat = math.normalize(footFwdFlat);
            bodyFwdFlat = math.normalize(bodyFwdFlat);

            float yawAngle = SignedAngle(bodyFwdFlat, footFwdFlat, up);
            if (math.abs(yawAngle) > p.maxFootYawDegrees)
            {
                float correction = math.clamp(yawAngle, -p.maxFootYawDegrees, p.maxFootYawDegrees) - yawAngle;
                result = math.mul(quaternion.AxisAngle(up, math.radians(correction)), result);
            }
        }

        if (math.abs(swingPitchDeg) > 1e-4f)
        {
            result = math.mul(result, quaternion.AxisAngle(new float3(1, 0, 0), math.radians(swingPitchDeg)));
        }

        return math.mul(result, footAlign);
    }

    /// <summary>Ankle pitch across the swing: plantarflexed at toe-off, ~neutral mid-swing, dorsiflexed for heel-strike.</summary>
    public static float SwingAnklePitchDeg(float t)
    {
        float inv = 1f - t;
        return k_ToeOffDeg * inv * inv - k_HeelStrikeDeg * t * t;
    }

    private static float3 ProjectOnPlane(float3 v, float3 normal)
    {
        return v - math.dot(v, normal) * normal;
    }

    private static float AngleBetween(quaternion a, quaternion b)
    {
        float dot = math.abs(math.dot(a.value, b.value));
        return math.degrees(2f * math.acos(math.min(dot, 1f)));
    }

    private static float SignedAngle(float3 from, float3 to, float3 axis)
    {
        float angle = math.degrees(math.acos(math.clamp(math.dot(from, to), -1f, 1f)));
        float sign = math.sign(math.dot(axis, math.cross(from, to)));
        return angle * sign;
    }

    private static float3 Slerp3(float3 a, float3 b, float t)
    {
        float la = math.length(a);
        float lb = math.length(b);
        if (la < 0.001f || lb < 0.001f) return math.lerp(a, b, t);

        float3 na = a / la;
        float3 nb = b / lb;
        float dot = math.clamp(math.dot(na, nb), -1f, 1f);
        float theta = math.acos(dot);

        if (theta < 0.001f) return math.lerp(a, b, t);

        float sinTheta = math.sin(theta);
        float3 dir = (na * math.sin((1f - t) * theta) + nb * math.sin(t * theta)) / sinTheta;
        return dir * math.lerp(la, lb, t);
    }
}
