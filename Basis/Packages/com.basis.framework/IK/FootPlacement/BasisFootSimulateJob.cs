using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct BasisFootSimulateJob : IJob
{
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

        // ── Velocity from head ──
        float3 headPos = inp.headPos;
        float3 rawVel = (headPos - sim.prevHeadPos) / dt;
        rawVel -= up * math.dot(rawVel, up); // strip vertical component
        sim.prevHeadPos = headPos;

        bool decelerating = math.lengthsq(rawVel) < math.lengthsq(sim.smoothedVelocity);
        float vAlpha = 1f - math.exp(-(decelerating ? p.velocitySmoothDecel : p.velocitySmoothAccel) * dt);
        sim.smoothedVelocity = math.lerp(sim.smoothedVelocity, rawVel, vAlpha);

        // HeadYaw
        float3 headFwdFull = math.mul(inp.headRot, new float3(0, 0, 1));
        float3 headFwdFlat = headFwdFull - up * math.dot(headFwdFull, up);
        sim.prevHeadYaw = math.lengthsq(headFwdFlat) < 0.001f
            ? sim.prevHeadYaw
            : math.atan2(headFwdFlat.x, headFwdFlat.z) * math.TODEGREES;

        float speed = math.length(sim.smoothedVelocity);

        // ── Body forward ──
        float3 rawFwd = ComputeBodyForward(inp, sim, p, up);
        float fwdRate = speed > 0.1f ? p.bodyFwdRateMoving : p.bodyFwdRateStationary;
        float fwdAlpha = 1f - math.exp(-fwdRate * dt);
        sim.smoothedBodyFwd = math.normalize(Slerp3(sim.smoothedBodyFwd, rawFwd, fwdAlpha));
        if (math.lengthsq(sim.smoothedBodyFwd) < 0.001f) sim.smoothedBodyFwd = rawFwd;

        sim.smoothedBodyRight = math.normalize(math.cross(up, sim.smoothedBodyFwd));
        if (math.lengthsq(sim.smoothedBodyRight) < 0.001f) sim.smoothedBodyRight = inp.avatarRight;

        float3 bodyFwd = sim.smoothedBodyFwd;
        float3 bodyRight = sim.smoothedBodyRight;

        float3 rawRight = math.normalize(math.cross(up, rawFwd));
        if (math.lengthsq(rawRight) < 0.001f) rawRight = bodyRight;

        // ── Ground ──
        // Project hips onto the horizontal plane (perpendicular to player up)
        float hipsUpComponent = math.dot(inp.hipsPos, up);
        float3 hipsFlat = inp.hipsPos - up * hipsUpComponent;
        float3 velDir = sim.smoothedVelocity - up * math.dot(sim.smoothedVelocity, up);

        float groundUpComponent;
        bool airborne;
        if (inp.groundHit)
        {
            groundUpComponent = math.dot(inp.groundPoint, up);
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
        float3 moveDir = math.lengthsq(velDir) > 0.01f ? math.normalize(velDir) : bodyFwd;
        float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float maxOffset = avgLeg * p.maxVelocityOffsetFraction;
        float baseBias = math.min(speed * p.velocityBiasFactor, maxOffset);
        float3 center = hipsFlat + up * groundUpComponent + moveDir * baseBias;
        float halfStance = p.stanceWidth * 0.5f;

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
        float speedT = math.saturate(speed / p.fastSpeedRef);
        float idleBoost = speed < p.idleSpeedThreshold ? p.stepTriggerDist * p.idleBoostFraction : 0f;
        float threshold = p.stepTriggerDist + speed * p.strideScale + idleBoost;
        float stepDur = math.lerp(p.stepDurSlow, p.stepDurFast, speedT);

        // ── Vertical correction ──
        if (airborne)
        {
            float airUpComp = groundUpComponent;
            if (left.phase == 0) { SetUpComponent(ref left.currentPos, airUpComp, up); SetUpComponent(ref left.plantedPos, airUpComp, up); }
            if (right.phase == 0) { SetUpComponent(ref right.currentPos, airUpComp, up); SetUpComponent(ref right.plantedPos, airUpComp, up); }
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

        // ── Update feet ──
        UpdateFoot(ref left, ref right, rawFwd, speed, threshold, stepDur, dt, up);
        UpdateFoot(ref right, ref left, rawFwd, speed, threshold, stepDur, dt, up);

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

        float kneeAlpha = 1f - math.exp(-p.kneeHintLerpSpeed * dt);
        left.kneeHint = math.lerp(left.kneeHint, leftKneeTarget, kneeAlpha);
        right.kneeHint = math.lerp(right.kneeHint, rightKneeTarget, kneeAlpha);

        // ── Final side enforcement ──
        float leftFootUp = GetUpComponent(left.currentPos, up);
        float3 hipsGround3 = ProjectOntoUpPlane(hp, up) + up * leftFootUp;
        float footMinSide = halfStance * p.footSideEnforceFraction;
        EnforceSide(ref left.currentPos, hipsGround3, rawRight, -1, footMinSide);
        EnforceSide(ref right.currentPos, hipsGround3, rawRight, +1, footMinSide);

        // ── Hip bob ──
        var outp = output[0];
        outp.hipBob = ComputeHipBob(ref left, ref right, speed);
        output[0] = outp;

        // ── Write back ──
        feet[0] = left;
        feet[1] = right;
        simState[0] = sim;
    }

    private void UpdateFoot(ref BasisFootNativeState f, ref BasisFootNativeState other,
        float3 rawFwd, float speed, float threshold, float stepDur, float dt, float3 up)
    {
        if (f.phase == 0) // Planted
        {
            float a = 1f - math.exp(-p.plantedLerpSpeed * dt);
            f.currentPos = math.lerp(f.currentPos, f.plantedPos, a);

            float ra = 1f - math.exp(-p.rotationLerpSpeed * dt);
            f.currentRot = math.slerp(f.currentRot, f.plantedRot, ra);

            float dist = HDist(f.plantedPos, f.idealPos, up);

            // Also check yaw: if body has turned significantly from planted rotation, step
            float3 plantedFwd = math.mul(f.plantedRot, new float3(0, 0, 1));
            float3 pff = plantedFwd - up * math.dot(plantedFwd, up);
            float3 bff = rawFwd - up * math.dot(rawFwd, up);
            bool yawTrigger = false;
            if (math.lengthsq(pff) > 0.001f && math.lengthsq(bff) > 0.001f)
            {
                float yawDiff = math.abs(SignedAngle(math.normalize(pff), math.normalize(bff), up));
                yawTrigger = yawDiff > p.maxPlantedYawDegrees;
            }

            if ((dist > threshold || yawTrigger) && other.phase == 0)
            {
                f.wantsStep = true;
                f.predictedTargetXZ = ComputeStepPrediction(ref f, rawFwd, speed, stepDur, up);
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
            float ease = 1f - (1f - t) * (1f - t) * (1f - t);

            float3 pos = math.lerp(f.stepStartPos, f.stepTargetPos, ease);

            float speedT2 = math.saturate(speed / p.fastSpeedRef);
            float dynamicHeight = p.stepHeightCalc * math.lerp(p.stepHeightMinFraction, 1.0f, speedT2);

            float lift = math.pow(t, p.stepArcLiftExp) * math.pow(1f - t, p.stepArcDropExp) / 0.234f;
            pos += up * (math.saturate(lift) * dynamicHeight);
            f.currentPos = pos;

            quaternion liveRot = FootRotation(rawFwd, f.filteredNormal, up);
            f.currentRot = math.slerp(f.currentRot, liveRot, ease);

            if (t >= 1f)
            {
                f.phase = 0; // Planted
                f.plantedPos = f.stepTargetPos;
                f.plantedRot = FootRotation(rawFwd, f.filteredNormal, up);
                f.currentPos = f.stepTargetPos;
            }
        }
    }

    private float3 ComputeStepPrediction(ref BasisFootNativeState f, float3 bodyFwd, float speed, float stepDur, float3 up)
    {
        var sim = simState[0];
        float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float3 sv = sim.smoothedVelocity;
        float3 svFlat = sv - up * math.dot(sv, up);
        float3 moveDir = math.lengthsq(svFlat) > 0.01f
            ? math.normalize(svFlat)
            : bodyFwd;
        float predAmount = math.min(speed * stepDur * p.predictionFactor, avgLeg * p.maxPredictionFraction);
        return f.idealPos + moveDir * predAmount;
    }

    private float ComputeHipBob(ref BasisFootNativeState left, ref BasisFootNativeState right, float speed)
    {
        if (speed < 0.05f) return 0f;

        float maxBob = p.hipToFoot * p.hipBobFraction;
        float speedScale = math.saturate(speed / p.fastSpeedRef);
        float amplitude = maxBob * speedScale;

        float leftDip = 0f, rightDip = 0f;
        if (left.phase == 1)
        {
            float t = math.saturate(left.stepTimer / left.stepDur);
            leftDip = math.sin(t * math.PI);
        }
        if (right.phase == 1)
        {
            float t = math.saturate(right.stepTimer / right.stepDur);
            rightDip = math.sin(t * math.PI);
        }

        return -math.max(leftDip, rightDip) * amplitude;
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

    private static float3 ComputeBodyForward(BasisFootSimInput inp, BasisFootSimState sim, BasisFootSimParams p, float3 up)
    {
        float3 accumulated = float3.zero;
        float totalWeight = 0f;

        float3 hipsFwd = math.mul(inp.hipsRot, new float3(0, 0, 1));
        hipsFwd -= up * math.dot(hipsFwd, up);
        if (math.lengthsq(hipsFwd) > 0.001f)
        {
            accumulated += math.normalize(hipsFwd) * p.bodyFwdHipsWeight;
            totalWeight += p.bodyFwdHipsWeight;
        }

        if (inp.hasChest)
        {
            float3 chestFwd = math.mul(inp.chestRot, new float3(0, 0, 1));
            chestFwd -= up * math.dot(chestFwd, up);
            if (math.lengthsq(chestFwd) > 0.001f)
            {
                accumulated += math.normalize(chestFwd) * p.bodyFwdChestWeight;
                totalWeight += p.bodyFwdChestWeight;
            }
        }

        float3 headFwd = math.mul(inp.headRot, new float3(0, 0, 1));
        float3 headFlat = headFwd - up * math.dot(headFwd, up);
        if (math.lengthsq(headFlat) > 0.1f)
        {
            accumulated += math.normalize(headFlat) * p.bodyFwdHeadWeight;
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

    private quaternion FootRotation(float3 bodyFwd, float3 normal, float3 up)
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

        return result;
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
