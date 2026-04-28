using Unity.Mathematics;
public struct BasisEyeState
{


    // Physiology-based constants
    public const float VerticalDampening = 0.7f;
    public const float VergenceScale = 0.6f;
    public const float PeripheralHorizDeg = 50f;
    public const float PeripheralVertDeg = 20f;
    public const float PeripheralFadeStart = 0.8f;
    public const float PeripheralFadeRange = 0.2f;
    public const float MaxPursuitSpeedDeg = 35f;

    // RNG
    public Unity.Mathematics.Random rng;

    // Phase: 0=Hold, 1=Saccade
    public byte phase;
    public float phaseT;
    public float phaseDur;

    // Motion in canonical space as yaw/pitch (radians)
    public float2 startYawPitch;
    public float2 targetYawPitch;
    public float2 currentYawPitch;

    // Per-eye vergence offset (picked once per saccade)
    public float2 eyeVar;

    // Output rotations (rig-local offsets to multiply onto initial pose)
    public quaternion leftOffset;
    public quaternion rightOffset;

#if UNITY_EDITOR
    // Final gaze direction for the debug window
    public float2 finalYawPitch;
#endif

    // Gaze targeting blend (0 = idle saccades only, 1 = fully targeted)
    public float gazeBlend;

    // Social triangle sub-state (cycles between 3 focus points on a target)
    public byte socialPhase; // 0=left eye, 1=right eye, 2=mouth
    public float socialTimer;
    public float socialDur;

    // Saccade within social triangle
    public float2 socialStart;
    public float2 socialCurrent;
    public float socialSaccadeT;
    public float socialSaccadeDur;

    // Jitter amplitude scale (random per saccade).
    // Usually subtle but sometimes spikes to create brief natural drift.
    public float jitterScale;

    // For personality-driven periodic disengagement
    // gazeBreakTimer counts down during active gaze. When 0, eyes avert.
    // avertedTimer counts down during aversion. When 0, gaze can re-engage.
    public float gazeBreakTimer;
    public float avertedTimer;
    public byte isAverting; // 1 = in gaze/focus break

    public static BasisEyeState Create(uint seed)
    {
        var r = new Unity.Mathematics.Random(seed);
        return new BasisEyeState
        {
            rng = r,

            phase = 0,
            phaseT = 0f,
            phaseDur = 0.5f,

            startYawPitch = float2.zero,
            targetYawPitch = float2.zero,
            currentYawPitch = float2.zero,

            leftOffset = quaternion.identity,
            rightOffset = quaternion.identity,

            gazeBlend = 0f,

            socialPhase = 0,
            socialTimer = 0f,
            socialDur = r.NextFloat(0.5f, 2f),
            socialStart = float2.zero,
            socialCurrent = float2.zero,
            socialSaccadeT = 1f,
            socialSaccadeDur = 0.08f,

            jitterScale = 1f,

            gazeBreakTimer = 0f,
            avertedTimer = 0f,
            isAverting = 0,
        };
    }

    #region Update

    public void Update(
        float dt,
        float2 headDeltaYP,
        float maxAngleRad,
        float saccadeMin, float saccadeMax,
        float perEyeVarRad,
        in BasisEyePersonality p,
        in BasisEyeCalibration calLeft, in BasisEyeCalibration calRight,
        bool hasGazeTarget,
        float2 gazeLeftEye, float2 gazeRightEye, float2 gazeMouth,
        float gazeMouthScale,
        bool gazeTargetChanged
        )
    {

        // VOR compensation: cancel out head rotation so eyes hold their world-space target.
        currentYawPitch += headDeltaYP;
        startYawPitch   += headDeltaYP;
        targetYawPitch  += headDeltaYP;
        socialCurrent   += headDeltaYP;
        socialStart     += headDeltaYP;

        // === IDLE SACCADE STATE MACHINE ===
        phaseT += dt;

        if (phase == 0) // HOLD
        {
            // Soft drift toward target while holding
            currentYawPitch = math.lerp(currentYawPitch, targetYawPitch, 1f - math.exp(-dt * 8f));

            // End hold -> begin saccade
            if (phaseT >= phaseDur)
            {
                phase = 1;
                phaseT = 0f;

                startYawPitch = currentYawPitch;
                targetYawPitch = PickNewTarget(ref rng, maxAngleRad, p.centerBias, p.centerReturnChance);

                // Pick new per-eye vergence offset (held until next saccade)
                eyeVar = new float2(
                    rng.NextFloat(-perEyeVarRad, perEyeVarRad),
                    rng.NextFloat(-perEyeVarRad, perEyeVarRad)
                );

                // Randomize jitter amplitude with occasional spikes for brief drift.
                jitterScale = rng.NextFloat() < 0.10f
                    ? rng.NextFloat(1.5f, 2.5f)
                    : 1.0f;

                // Duration is amplitude-dependent: larger saccades take longer (physiology).
                // When jitterScale is elevated, we slow the saccade to match pacing.
                float distance = math.length(targetYawPitch - startYawPitch);
                float baseDur = saccadeMin + (distance / math.max(maxAngleRad, 1e-5f)) * (saccadeMax - saccadeMin);
                phaseDur = baseDur * rng.NextFloat(0.8f, 1.2f) * math.max(1f, jitterScale * 0.5f);
            }
        }
        else // SACCADE
        {
            float u = math.saturate(phaseT / math.max(phaseDur, 1e-5f));

            // Ease-out-ish: quick start, settle
            float eased = 1f - math.pow(1f - u, 3f);

            currentYawPitch = math.lerp(startYawPitch, targetYawPitch, eased);

            // End saccade -> hold
            // Hold range comes from Liveliness, scaled by Attentiveness when focused.
            if (phaseT >= phaseDur)
            {
                phase = 0;
                phaseT = 0f;
                float holdScale = gazeBlend > 0f ? p.holdScaleAtFullGaze : 1f;
                phaseDur = rng.NextFloat(p.holdMin, p.holdMax) * holdScale * math.max(1f, jitterScale * 0.4f);
            }
        }

        // Keep idle saccades near the gaze position so disengaging looks smooth, not a snap.
        currentYawPitch = math.lerp(currentYawPitch, socialCurrent, gazeBlend);
        float2 idleYP = currentYawPitch;

        // === GAZE TARGETING ===

        float2 gazeYP = socialCurrent; // socialCurrent keeps its last value even when we lose the target.

        if (hasGazeTarget)
        {
            // On target switch, reset social saccade to start from current position
            if (gazeTargetChanged)
            {
                socialStart = socialCurrent;
                socialPhase = (byte)(rng.NextInt(0, 2));

                // Larger gaze shifts take longer (we go 2-3.5x vs micro-saccades bcs IRL inter-person shifts are like that)
                float2 approxTarget = (gazeLeftEye + gazeRightEye) * 0.5f; // we don't know which eye will be first
                float angularDist = math.length(approxTarget - socialStart);
                float baseDur = saccadeMin + (angularDist / math.max(maxAngleRad, 1e-5f)) * (saccadeMax - saccadeMin);
                socialSaccadeDur = baseDur * rng.NextFloat(2.0f, 3.5f);

                // Reaction delay / voluntary shift latency (scaled by Attentiveness)
                // Negative socialSaccadeT means "before reaction", saccade starts when it crosses 0
                float reactionDelay = rng.NextFloat(p.reactionMin, p.reactionMax);
                socialSaccadeT = -(reactionDelay / math.max(socialSaccadeDur, 1e-5f));

                socialTimer = 0f;
                socialDur = rng.NextFloat(0.5f, 2.0f);
                jitterScale = 1f;

                // If not mid-aversion, reset the break timer.
                // If mid-aversion, let it finish naturally so eyes don't jerk.
                if (isAverting == 0)
                    gazeBreakTimer = 0f;
            }

            // === SOCIAL TRIANGLE SUB-STATE ===
            socialTimer += dt;
            if (socialTimer >= socialDur)
            {
                socialTimer = 0f;
                socialStart = socialCurrent;
                socialSaccadeT = 0f;
                socialSaccadeDur = rng.NextFloat(saccadeMin, saccadeMax);

                // Pick next social point
                float mouthProb = 0.20f * gazeMouthScale; // 20% (w/ mouth scaled toward 0 at close range)
                float eyeProb = (1f - mouthProb) * 0.5f; // 40% each eye
                float roll = rng.NextFloat();
                if (roll < eyeProb)
                    socialPhase = 0; // left eye
                else if (roll < eyeProb + eyeProb)
                    socialPhase = 1; // right eye
                else
                    socialPhase = 2; // mouth

                // Set hold duration based on target (this is scaled by 'Attentiveness')
                socialDur = socialPhase < 2
                    ? rng.NextFloat(0.5f, 2.0f) * p.socialHoldScale   // eyes: longer holds
                    : rng.NextFloat(0.3f, 0.8f) * p.socialHoldScale;  // mouth: brief glance
            }

            // target yaw/pitch for current social phase
            float2 socialTargetYP;
            if (socialPhase == 0)
                socialTargetYP = gazeLeftEye;
            else if (socialPhase == 1)
                socialTargetYP = gazeRightEye;
            else
                socialTargetYP = gazeMouth;

            // Saccade-speed jump to new social target
            if (socialSaccadeT < 1f)
            {
                socialSaccadeT += dt / math.max(socialSaccadeDur, 1e-5f);
                // Negative = natural reaction delay (eyes hold at socialStart until crossing 0)
                float t = math.clamp(socialSaccadeT, 0f, 1f);
                float eased = 1f - math.pow(1f - t, 3f);
                socialCurrent = math.lerp(socialStart, socialTargetYP, eased);
            }
            else
            {
                // Soft drift while holding, capped at smooth pursuit speed (about 35 deg per sec)
                // Fast-moving targets: eyes fall behind, elliptical limit disengages gaze
                float maxPursuitSpeed = math.radians(MaxPursuitSpeedDeg);
                float2 desired = math.lerp(socialCurrent, socialTargetYP, 1f - math.exp(-dt * 8f));
                float2 delta = desired - socialCurrent;
                float deltaLen = math.length(delta);
                float maxDelta = maxPursuitSpeed * dt;
                if (deltaLen > maxDelta)
                    socialCurrent += delta * (maxDelta / deltaLen);
                else
                    socialCurrent = desired;
            }

            gazeYP = socialCurrent;

            // Personality-driven periodic disengagement
            // While gazing, gazeBreakTimer counts down. On expiry, eyes avert.
            if (isAverting == 0)
            {
                if (gazeBreakTimer <= 0f && gazeBlend > 0.1f)
                    gazeBreakTimer = rng.NextFloat(p.gazeBreakMin, p.gazeBreakMax);

                if (gazeBreakTimer > 0f)
                {
                    gazeBreakTimer -= dt;
                    if (gazeBreakTimer <= 0f)
                    {
                        isAverting = 1;
                        avertedTimer = rng.NextFloat(p.avertedMin, p.avertedMax);
                    }
                }
            }
            else
            {
                avertedTimer -= dt;
                if (avertedTimer <= 0f)
                {
                    isAverting = 0;
                    gazeBreakTimer = 0f;
                }
            }

            // Eyes can track further than they idle-wander. Horizontal range is wider than vertical (physiology).
            float horizLimit = math.radians(PeripheralHorizDeg);
            float vertLimit = math.radians(PeripheralVertDeg);
            // peripheralExtent < FadeStart = full gaze. FadeStart to 1.0 = linear fade.
            // Past 1.0, gaze is fully disengaged and idle saccades take over.
            float peripheralExtent = math.length(new float2(gazeYP.x / horizLimit, gazeYP.y / vertLimit));
            float maxBlend = 1f - math.saturate((peripheralExtent - PeripheralFadeStart) / PeripheralFadeRange);

            // During disengagement, drive maxBlend to 0 so gazeBlendOutSpeed takes over
            if (isAverting == 1)
                maxBlend = 0f;

            if (gazeBlend < maxBlend)
                gazeBlend = math.min(gazeBlend + p.gazeBlendInSpeed * dt, maxBlend);
            else
                gazeBlend = math.max(gazeBlend - p.gazeBlendOutSpeed * dt, maxBlend);
        }
        else
        {
            gazeBlend = math.max(gazeBlend - p.gazeBlendOutSpeed * dt, 0f);

            // Reset disengagement on target loss
            isAverting = 0;
            gazeBreakTimer = 0f;

            // Keep socialCurrent in sync with idle so the next gaze-engage saccade starts from where the eyes actually are.
            if (gazeBlend < 0.05f)
                socialCurrent = currentYawPitch;
        }

        // === FINAL BLEND ===
        // Combines idle saccades, social gaze, jitter, vert dampening, and per-eye vergence into final output.

        // Reuse idle saccade position as micro-jitter when gazing (clamped to jitter radius).
        float2 clampedIdle = ClampYawPitchPlane(idleYP, p.maxFocusedJitterRad * jitterScale);
        float2 finalYP = math.lerp(idleYP, gazeYP + clampedIdle, gazeBlend);
        finalYP.y *= VerticalDampening; // eyes bias slightly more horizontal than vertical IRL
        float2 leftYP = ClampYawPitchPlane(finalYP + eyeVar * VergenceScale, maxAngleRad);
        float2 rightYP = ClampYawPitchPlane(finalYP - eyeVar * VergenceScale, maxAngleRad);

    #if UNITY_EDITOR
        finalYawPitch = ClampYawPitchPlane(finalYP, maxAngleRad); // for debug window
    #endif

        // Build canonical yaw/pitch -> rig-local offset via calibration basis
        leftOffset = CanonicalYawPitchToRigOffset(leftYP, calLeft.basis, calLeft.invBasis);
        rightOffset = CanonicalYawPitchToRigOffset(rightYP, calRight.basis, calRight.invBasis);
    }

    #endregion

    #region Utilities

    // Canonical yaw around +Y, pitch around +X, forward +Z
    private static quaternion CanonicalYawPitchToQuat(float2 yawPitch)
    {
        quaternion yaw = quaternion.AxisAngle(new float3(0, 1, 0), yawPitch.x);
        quaternion pitch = quaternion.AxisAngle(new float3(1, 0, 0), -yawPitch.y);
        return math.mul(yaw, pitch);
    }

    // Convert canonical offset to rig-local using: basis * q * basis^-1
    private static quaternion CanonicalYawPitchToRigOffset(float2 yawPitch, quaternion basis, quaternion invBasis)
    {
        quaternion qCan = CanonicalYawPitchToQuat(yawPitch);
        return math.mul(math.mul(basis, qCan), invBasis);
    }

    // Plane clamp: keeps sqrt(yaw^2 + pitch^2) <= maxAngle (good approximation of cone for small angles)
    private static float2 ClampYawPitchPlane(float2 yawPitch, float maxAngleRad)
    {
        float mag = math.length(yawPitch);
        if (mag > maxAngleRad)
            yawPitch *= (maxAngleRad / mag);
        return yawPitch;
    }

    private static float2 PickNewTarget(ref Unity.Mathematics.Random rng, float maxAngleRad, float centerBias, float centerReturnChance)
    {
        // Occasionally return toward center (frequency driven by Liveliness)
        if (centerReturnChance > 0f && rng.NextFloat() < centerReturnChance)
        {
            float small = maxAngleRad * 0.25f;
            return new float2(rng.NextFloat(-small, small), rng.NextFloat(-small, small));
        }

        // Occasional wide saccade (eyes scan the periphery)
        if (rng.NextFloat() < 0.12f)
        {
            float wr = maxAngleRad * rng.NextFloat(0.6f, 1.0f);
            float wa = rng.NextFloat(0f, math.PI * 2f);
            return new float2(math.cos(wa) * wr, math.sin(wa) * wr);
        }

        // Bias toward center: r = U^(bias) * max
        float u = rng.NextFloat(0f, 1f);
        float r = math.pow(u, centerBias) * maxAngleRad;

        float a = rng.NextFloat(0f, math.PI * 2f);
        float yaw = math.cos(a) * r;
        float pitch = math.sin(a) * r;

        return new float2(yaw, pitch);
    }

    #endregion
}
