using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
[BurstCompile]
public struct BasisFootSimulateJob : IJob
{
    const float doubleSupportSlow = 2.00f, doubleSupportFast = 0.05f, walkTopSpeedFrac = 0.45f;
    const float doubleSupportStanding = 1.0f, walkOnsetFrac = 0.10f, standingTriggerFrac = 0.38f;
    const float driftDwellWaiver = 0.5f, driftUrgencyRefMul = 1.0f, accelUrgencyRef = 3.0f, accelTriggerFrac = 0.60f;
    const float extUrgencyBand = 0.10f, yawReversalGain = 4.0f, medialSwingFrac = 0.20f, reachAheadFrac = 0.25f;
    const float stanceNarrowAtSpeed = 0.35f, hipSwayFraction = 0.03f, toeOffDeg = 22f, heelStrikeDeg = 16f;
    const float preSwingFrac = 0.20f, footFlatFrac = 0.30f, pelvisAxialDeg = 4.5f, pelvisListDeg = 5f;
    const float loadingDipFraction = 0.012f, loadingResponseFrac = 0.35f, yawTrackGain = 5.0f, rotWidenFrac = 0.6f;
    const float minFootSepFrac = 0.85f;
    public const float YawUrgencyRefMul = 5.0f, TurnStepArcFloor = 0.65f;
    public BasisFootSimParams p;
    public NativeArray<BasisFootNativeState> feet;
    public NativeArray<BasisFootSimState> simState;
    public NativeArray<BasisFootSimInput> input;
    public NativeArray<BasisFootSimOutput> output;
    public unsafe void Execute()
    {
        // ArrayElementAsRef takes the raw buffer pointer, so an uncreated array hands back a ref to
        // address 0 and every field write below faults; a short feet array walks past the end. The
        // driver always allocates 1/1/1/2, but this is a public job struct and Length is 0 on an
        // uncreated NativeArray, so one prologue test covers both shapes for the whole solve.
        if (input.Length < 1 || simState.Length < 1 || output.Length < 1 || feet.Length < 2) return;

        ref BasisFootSimInput inp = ref UnsafeUtility.ArrayElementAsRef<BasisFootSimInput>(input.GetUnsafePtr(), 0);
        ref BasisFootSimState sim = ref UnsafeUtility.ArrayElementAsRef<BasisFootSimState>(simState.GetUnsafePtr(), 0);
        ref BasisFootNativeState left = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(feet.GetUnsafePtr(), 0);
        ref BasisFootNativeState right = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(feet.GetUnsafePtr(), 1);
        float dt = inp.dt;

        if (dt <= 0f) return;

        float3 up = inp.playerUp;
        if (math.lengthsq(up) < 0.001f) up = new float3(0, 1, 0);

        float3 velSample = inp.hipsPos, rawVel = (velSample - sim.prevHeadPos) / dt;
        rawVel -= up * math.dot(rawVel, up);
        sim.prevHeadPos = velSample;

        bool decelerating = math.lengthsq(rawVel) < math.lengthsq(sim.smoothedVelocity);
        float vAlpha = 1f - math.exp(-(decelerating ? p.velocitySmoothDecel : p.velocitySmoothAccel) * dt);
        float3 prevSmoothedVel = sim.smoothedVelocity;
        sim.smoothedVelocity = math.lerp(sim.smoothedVelocity, rawVel, vAlpha);

        float speed = math.length(sim.smoothedVelocity);
        float accelMag = math.length(sim.smoothedVelocity - prevSmoothedVel) / dt;
        sim.smoothedAccelMag = math.lerp(sim.smoothedAccelMag, accelMag, 1f - math.exp(-p.velocitySmoothAccel * dt));
        float accelUrgency = math.saturate(sim.smoothedAccelMag / accelUrgencyRef);
        float3 rawFwd = ComputeBodyForward(inp, sim, p, up);
        float bodyYawRate = math.lengthsq(sim.prevBodyFwd) > 0.001f ? SignedAngle(sim.prevBodyFwd, rawFwd, up) / dt : 0f;
        sim.prevBodyFwd = rawFwd;

        float yawFilterRate = bodyYawRate * sim.smoothedYawRateDeg < 0f ? p.bodyFwdRateMoving * yawReversalGain : p.bodyFwdRateMoving;
        sim.smoothedYawRateDeg = math.lerp(sim.smoothedYawRateDeg, bodyYawRate, 1f - math.exp(-yawFilterRate * dt));

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

        bool turning = math.abs(sim.smoothedYawRateDeg) > 20f;
        float fwdRate = (speed > 0.034f * p.fastSpeedRef || turning) ? p.bodyFwdRateMoving : p.bodyFwdRateStationary;

        if (turning)
            fwdRate = math.max(fwdRate, yawTrackGain * math.abs(math.radians(sim.smoothedYawRateDeg)));
        float fwdAlpha = 1f - math.exp(-fwdRate * dt);
        float3 blendedFwd = Slerp3(sim.smoothedBodyFwd, rawFwd, fwdAlpha);
        sim.smoothedBodyFwd = math.lengthsq(blendedFwd) < 0.001f ? rawFwd : math.normalize(blendedFwd);

        float3 rightCross = math.cross(up, sim.smoothedBodyFwd);
        sim.smoothedBodyRight = math.lengthsq(rightCross) < 0.001f ? inp.avatarRight : math.normalize(rightCross);

        float3 bodyFwd = sim.smoothedBodyFwd, bodyRight = sim.smoothedBodyRight, rawRightCross = math.cross(up, rawFwd);
        float3 rawRight = math.lengthsq(rawRightCross) < 0.001f ? bodyRight : math.normalize(rawRightCross);
        float hipsUpComponent = math.dot(inp.hipsPos, up);
        float3 hipsFlat = inp.hipsPos - up * hipsUpComponent;
        float3 velDir = sim.smoothedVelocity - up * math.dot(sim.smoothedVelocity, up);
        float standHipsAboveGround = p.hipToFoot + p.ankleHeight, avgLegReach = (p.leftLegLen + p.rightLegLen) * 0.5f;
        bool groundInReach = inp.groundHit && (hipsUpComponent - math.dot(inp.groundPoint, up)) <= standHipsAboveGround + avgLegReach * 0.15f;
        float groundUpComponent;
        bool airborne;
        if (groundInReach)
        {
            groundUpComponent = math.dot(inp.groundPoint, up) + p.footHeightOffset;
            airborne = false;
        }
        else
        {
            groundUpComponent = hipsUpComponent - p.hipToFoot - p.ankleHeight + p.footHeightOffset;
            airborne = true;
        }

        float reachLimit = standHipsAboveGround + avgLegReach * 0.15f;
        float leftGroundUp = FootGroundUp(inp.leftGroundValid, inp.leftGroundUp, left.filteredNormal, up, hipsUpComponent, reachLimit, groundUpComponent);
        float rightGroundUp = FootGroundUp(inp.rightGroundValid, inp.rightGroundUp, right.filteredNormal, up, hipsUpComponent, reachLimit, groundUpComponent);
        float moveDirGate = 0.034f * p.fastSpeedRef;
        float3 moveDir = math.lengthsq(velDir) > moveDirGate * moveDirGate ? math.normalize(velDir) : bodyFwd;
        float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f, maxOffset = avgLeg * p.maxVelocityOffsetFraction;
        float baseBias = math.min(speed * p.velocityBiasFactor, maxOffset);
        float3 center = hipsFlat + up * groundUpComponent + moveDir * baseBias;
        float speedFrac = math.saturate(speed / p.fastSpeedRef), fwdFrac = math.abs(math.dot(moveDir, bodyFwd));
        float fastYawRef = math.max(1f, 0.5f * p.maxPlantedYawDegrees / math.max(0.01f, p.stepDurFast));
        float yawFrac = math.saturate(math.abs(sim.smoothedYawRateDeg) / fastYawRef);
        float yawUrgency = math.saturate(math.abs(sim.smoothedYawRateDeg) / (fastYawRef * YawUrgencyRefMul));
        float halfStance = p.stanceWidth * 0.5f * math.lerp(1f, stanceNarrowAtSpeed, speedFrac * fwdFrac) * (1f + rotWidenFrac * yawFrac);
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

        float3 hipsGround = hipsFlat + up * groundUpComponent;
        EnforceSide(ref left.idealPos, hipsGround, rawRight, -1, halfStance * p.idealSideEnforceFraction);
        EnforceSide(ref right.idealPos, hipsGround, rawRight, +1, halfStance * p.idealSideEnforceFraction);

        SetUpComponent(ref left.idealPos, leftGroundUp, up);
        SetUpComponent(ref right.idealPos, rightGroundUp, up);

        float urgencyT = math.max(math.max(math.saturate(speed / p.fastSpeedRef), yawUrgency), accelUrgency);
        bool stationary = speed < p.idleSpeedThreshold && math.abs(sim.smoothedYawRateDeg) < 20f;
        float idleBoost = stationary ? p.stepTriggerDist * p.idleBoostFraction : 0f;
        float avgLegT = (p.leftLegLen + p.rightLegLen) * 0.5f, dsSpeedRef = math.sqrt(9.81f * avgLegT);
        float walkOnset = math.saturate(speed / math.max(1e-3f, walkOnsetFrac * dsSpeedRef));
        float triggerScale = math.lerp(standingTriggerFrac, 1f, walkOnset) * math.lerp(1f, accelTriggerFrac, accelUrgency);
        float threshold = math.min((p.stepTriggerDist + idleBoost) * triggerScale + speed * p.strideScale, avgLegT * 0.55f);
        float stepDur = math.lerp(p.stepDurSlow, p.stepDurFast, urgencyT);
        float dsWalkT = math.saturate(speed / math.max(1e-3f, walkTopSpeedFrac * dsSpeedRef));
        float dsFraction = math.lerp(doubleSupportStanding, math.lerp(doubleSupportSlow, doubleSupportFast, dsWalkT), walkOnset);
        float doubleSupportSec = stepDur * dsFraction * (1f - yawUrgency);
        float absYawRate = math.abs(sim.smoothedYawRateDeg);
        if (absYawRate > 1f)
        {
            doubleSupportSec = math.min(doubleSupportSec, p.maxPlantedYawDegrees / absYawRate);
        }

        bool touchedDown = sim.wasAirborne && !airborne;
        sim.wasAirborne = airborne;

        if (airborne || touchedDown)
        {
            if (left.phase == 0) { SetUpComponent(ref left.currentPos, leftGroundUp, up); SetUpComponent(ref left.plantedPos, leftGroundUp, up); }
            if (right.phase == 0) { SetUpComponent(ref right.currentPos, rightGroundUp, up); SetUpComponent(ref right.plantedPos, rightGroundUp, up); }
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

        const float idleStiffenRate = 0f;
        if (stationary && idleStiffenRate > 0f)
        {
            float g = 1f - math.exp(-idleStiffenRate * dt);
            if (left.phase == 0) left.plantedPos += ProjectOntoUpPlane(left.idealPos - left.plantedPos, up) * g;
            if (right.phase == 0) right.plantedPos += ProjectOntoUpPlane(right.idealPos - right.plantedPos, up) * g;
        }

        if (left.phase == 0) left.plantedTime += dt;
        if (right.phase == 0) right.plantedTime += dt;

        UpdateFoot(ref left, ref right, rawFwd, sim.smoothedVelocity, speed, threshold, stepDur, dt, up, hipsGround, inp.hipsPos, sim.smoothedYawRateDeg, doubleSupportSec, urgencyT);
        UpdateFoot(ref right, ref left, rawFwd, sim.smoothedVelocity, speed, threshold, stepDur, dt, up, hipsGround, inp.hipsPos, sim.smoothedYawRateDeg, doubleSupportSec, urgencyT);

        if (left.phase == 0 && right.phase == 0 && left.wantsStep && right.wantsStep)
        {
            float ld = HDist(left.plantedPos, left.idealPos, up), rd = HDist(right.plantedPos, right.idealPos, up);
            if (ld >= rd) right.wantsStep = false; else left.wantsStep = false;
        }

        float avgThigh = (p.leftThighLen + p.rightThighLen) * 0.5f;
        float3 hp = inp.hipsPos;
        float hipFootUpDist = math.abs(GetUpComponent(hp, up) - GetUpComponent((left.currentPos + right.currentPos) * 0.5f, up));
        float avgLeg2 = (p.leftLegLen + p.rightLegLen) * 0.5f, bendRatio = 1f - math.saturate(hipFootUpDist / avgLeg2);
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

        float leftFootUp = GetUpComponent(left.currentPos, up);
        float3 hipsGround3 = ProjectOntoUpPlane(hp, up) + up * leftFootUp;
        float footMinSide = halfStance * p.footSideEnforceFraction;
        if (left.phase == 1) EnforceSide(ref left.currentPos, hipsGround3, rawRight, -1, footMinSide);
        if (right.phase == 1) EnforceSide(ref right.currentPos, hipsGround3, rawRight, +1, footMinSide);

        float3 sepV = ProjectOntoUpPlane(left.currentPos - right.currentPos, up);
        float sepLen = math.length(sepV), minSep = p.stanceWidth * minFootSepFrac;
        if (sepLen < minSep)
        {
            float3 sepAxis = sepLen > 1e-4f ? sepV / sepLen : -rawRight;
            float push = 0.5f * (minSep - sepLen);
            left.currentPos += sepAxis * push;
            right.currentPos -= sepAxis * push;
        }

        var outp = output[0];
        outp.hipBob = ComputeHipBob(ref left, ref right, speed);
        outp.hipSway = bodyRight * ComputeHipSway(ref left, ref right, speed);
        outp.pelvisDelta = ComputePelvisDelta(ref left, ref right, speed, up, bodyFwd);
        outp.airborne = airborne;
        output[0] = outp;
    }
    private void UpdateFoot(ref BasisFootNativeState f, ref BasisFootNativeState other, float3 rawFwd, float3 smoothedVelocity, float speed, float threshold, float stepDur, float dt, float3 up, float3 hipsGround, float3 hipsPos, float yawRateDeg, float doubleSupportSec, float urgencyT)
    {
        float3 bodyFlat = rawFwd - up * math.dot(rawFwd, up);
        bool bodyFlatValid = math.lengthsq(bodyFlat) > 1e-6f;
        if (bodyFlatValid) bodyFlat = math.normalize(bodyFlat);

        if (f.phase == 0)
        {
            float a = 1f - math.exp(-p.plantedLerpSpeed * dt);
            f.currentPos = math.lerp(f.currentPos, f.plantedPos, a);

            if (math.lengthsq(f.plantedBodyFwd) > 1e-6f && math.lengthsq(f.filteredNormal) > 1e-6f)
            {
                quaternion plantedAlign = f.sideSign < 0 ? p.footAlignLeft : p.footAlignRight;
                f.plantedRot = FootRotation(f.plantedBodyFwd, f.filteredNormal, up, plantedAlign, 0f);
            }

            float flatDur = math.max(1e-4f, f.stepDur * footFlatFrac);
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
            bool yawTrigger = false;
            if (bodyFlatValid)
            {
                if (math.lengthsq(f.plantedBodyFwd) < 1e-6f) f.plantedBodyFwd = bodyFlat;

                float yawDiff = math.abs(SignedAngle(math.normalize(f.plantedBodyFwd), bodyFlat, up));
                float predSwingYaw = math.min(math.abs(yawRateDeg) * stepDur, p.maxPlantedYawDegrees);
                yawTrigger = yawDiff + predSwingYaw > p.maxPlantedYawDegrees;
            }

            float dwellRequired = doubleSupportSec;
            if (dist > threshold && threshold > 1e-4f)
            {
                float driftUrgency = math.saturate((dist - threshold) / threshold);
                dwellRequired *= 1f - driftDwellWaiver * driftUrgency;
            }

            float doubleSupportSoFar = math.min(f.plantedTime, other.plantedTime);
            bool otherSettled = other.phase == 0 && doubleSupportSoFar >= dwellRequired;

            if ((dist > threshold || yawTrigger) && otherSettled)
            {
                f.wantsStep = true;

                float driftOver = math.saturate((dist - threshold) / math.max(1e-4f, threshold * driftUrgencyRefMul));
                float extRatio = math.length(hipsPos - f.plantedPos) / math.max(1e-4f, p.hipToFoot);
                float extOver = math.saturate((extRatio - 1f) / extUrgencyBand);
                f.stepUrgency = math.max(urgencyT, math.max(driftOver, extOver));
                f.predictedTargetXZ = ComputeStepPrediction(ref f, rawFwd, smoothedVelocity, speed, stepDur, up, hipsGround, yawRateDeg);
            }
            else
            {
                f.wantsStep = false;
            }
        }
        else
        {
            f.wantsStep = false;
            f.stepTimer += dt;
            float t = math.saturate(f.stepTimer / f.stepDur);
            float3 footFwd = math.lengthsq(f.plantedBodyFwd) > 1e-6f ? math.normalize(f.plantedBodyFwd) : bodyFlat;
            float pivotArm = math.max(p.footLength, 1e-4f), toeOffRad = math.radians(toeOffDeg);
            float3 liftOffPos = f.stepStartPos + footFwd * (pivotArm * (1f - math.cos(toeOffRad))) + up * (pivotArm * math.sin(toeOffRad));
            float3 pos;
            float pitchDeg, swingU;
            if (t < preSwingFrac)
            {
                float pr = t / preSwingFrac, roll = pr * pr * (3f - 2f * pr);
                pitchDeg = toeOffDeg * roll;
                float th = math.radians(pitchDeg);
                pos = f.stepStartPos + footFwd * (pivotArm * (1f - math.cos(th))) + up * (pivotArm * math.sin(th));
                swingU = 0f;

                float3 toeAxis = math.cross(up, footFwd);
                if (math.lengthsq(toeAxis) > 1e-6f)
                {
                    f.toeBendAxis = math.normalize(toeAxis);
                    f.toeBendDeg = pitchDeg;
                }
            }
            else
            {
                swingU = (t - preSwingFrac) / math.max(1e-4f, 1f - preSwingFrac);
                pitchDeg = SwingAnklePitchDeg(swingU);
                pos = math.lerp(liftOffPos, f.stepTargetPos, swingU * swingU * (3f - 2f * swingU));
            }
            float ease = swingU * swingU * (3f - 2f * swingU), avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
            float stepDist = HDist(f.stepStartPos, f.stepTargetPos, up);
            float strideFrac = math.saturate(stepDist / math.max(1e-3f, avgLeg * p.stepHeightStrideRefFraction));
            float travelFrac = strideFrac;

            strideFrac = math.max(strideFrac, f.stepArcScale);
            float dynamicHeight = p.stepHeightCalc * math.lerp(p.stepHeightMinFraction, 1.0f, strideFrac);
            float a = p.stepArcLiftExp, b = p.stepArcDropExp;
            float tPeak = a / math.max(1e-4f, a + b);
            float arcPeak = math.max(1e-4f, math.pow(tPeak, a) * math.pow(1f - tPeak, b));
            float lift = math.pow(swingU, a) * math.pow(1f - swingU, b) / arcPeak;
            pos += up * (math.saturate(lift) * dynamicHeight);

            float3 swingRightCross = math.cross(up, rawFwd);
            if (math.lengthsq(swingRightCross) > 1e-6f)
            {
                float3 swingRight = math.normalize(swingRightCross);
                float medial = math.sin(swingU * math.PI) * (p.stanceWidth * 0.5f) * medialSwingFrac * travelFrac;
                pos -= swingRight * (f.sideSign * medial);
            }
            f.currentPos = pos;

            quaternion footAlign = f.sideSign < 0 ? p.footAlignLeft : p.footAlignRight;
            quaternion liveRot = t < preSwingFrac ? FootRotation(footFwd, f.filteredNormal, up, footAlign, pitchDeg) : FootRotation(rawFwd, f.filteredNormal, up, footAlign, pitchDeg);

            if (t < preSwingFrac)
            {
                float pr = t / preSwingFrac;
                f.currentRot = math.slerp(f.stepStartRot, liveRot, pr * pr * (3f - 2f * pr));
            }
            else
            {
                quaternion liftOffRot = FootRotation(footFwd, f.filteredNormal, up, footAlign, toeOffDeg);
                f.currentRot = math.slerp(liftOffRot, liveRot, ease);
            }

            if (t >= 1f)
            {
                f.phase = 0;
                f.plantedPos = f.stepTargetPos;

                f.landRot = f.currentRot;
                f.plantedRot = FootRotation(rawFwd, f.filteredNormal, up, footAlign, 0f);
                f.plantedBodyFwd = bodyFlatValid ? bodyFlat : f.plantedBodyFwd;
                f.currentPos = f.stepTargetPos;
                f.plantedTime = 0f;
            }
        }
    }
    private float FootGroundUp(bool valid, float measuredUp, float3 normal, float3 up, float hipsUpComponent, float reachLimit, float shared)
    {
        if (!valid) return shared;
        if (measuredUp > hipsUpComponent) return shared;
        if (hipsUpComponent - measuredUp > reachLimit) return shared;

        float lift = p.footHeightOffset;
        if (math.lengthsq(normal) > 1e-6f) lift *= math.abs(math.dot(math.normalize(normal), up));

        return measuredUp + lift;
    }
    private float3 ComputeStepPrediction(ref BasisFootNativeState f, float3 bodyFwd, float3 smoothedVelocity, float speed, float stepDur, float3 up, float3 hipsGround, float yawRateDeg)
    {
        float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float3 svFlat = smoothedVelocity - up * math.dot(smoothedVelocity, up);
        float predMoveGate = 0.034f * p.fastSpeedRef;
        float3 moveDir = math.lengthsq(svFlat) > predMoveGate * predMoveGate ? math.normalize(svFlat) : bodyFwd;
        float predAmount = math.min(speed * stepDur * p.predictionFactor, avgLeg * p.maxPredictionFraction);
        float dsRef = math.sqrt(9.81f * avgLeg), reachGate = math.saturate((speed - 0.03f * dsRef) / (0.08f * dsRef));
        float reachFloor = reachAheadFrac * avgLeg * reachGate;
        predAmount = math.min(math.max(predAmount, reachFloor), avgLeg * p.maxPredictionFraction);

        float predYawDeg = math.clamp(yawRateDeg * stepDur, -p.maxPlantedYawDegrees, p.maxPlantedYawDegrees);
        quaternion yawPredict = quaternion.AxisAngle(up, math.radians(predYawDeg));
        float3 fromCenter = f.idealPos - hipsGround;
        fromCenter -= up * math.dot(fromCenter, up);
        float3 predictedIdeal = hipsGround + math.mul(yawPredict, fromCenter);

        return predictedIdeal + moveDir * predAmount;
    }
    private float ComputeHipBob(ref BasisFootNativeState left, ref BasisFootNativeState right, float speed)
    {
        if (speed < 0.02f * p.fastSpeedRef) return 0f;

        float maxBob = p.hipToFoot * p.hipBobFraction, speedScale = math.saturate(speed / p.fastSpeedRef);
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

        float rise = math.max(leftRise, rightRise) * amplitude, avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float dipAmplitude = avgLeg * loadingDipFraction * speedScale;
        float dip = math.max(LoadingResponse(ref left), LoadingResponse(ref right)) * dipAmplitude;

        return rise - dip;
    }
    private static float LoadingResponse(ref BasisFootNativeState f)
    {
        if (f.phase != 0) return 0f;
        float dur = f.stepDur * loadingResponseFrac;
        if (dur <= 1e-4f) return 0f;
        float u = f.plantedTime / dur;
        if (u >= 1f) return 0f;
        return math.sin(u * math.PI);
    }
    private float ComputeHipSway(ref BasisFootNativeState left, ref BasisFootNativeState right, float speed)
    {
        if (speed < 0.02f * p.fastSpeedRef) return 0f;

        float avgLeg = (p.leftLegLen + p.rightLegLen) * 0.5f;
        float amplitude = avgLeg * hipSwayFraction * math.saturate(speed / p.fastSpeedRef), sway = 0f;
        if (left.phase == 1)
        {
            float t = math.saturate(left.stepTimer / left.stepDur);
            sway += math.sin(t * math.PI);
        }
        if (right.phase == 1)
        {
            float t = math.saturate(right.stepTimer / right.stepDur);
            sway -= math.sin(t * math.PI);
        }

        return sway * amplitude;
    }
    private quaternion ComputePelvisDelta(ref BasisFootNativeState left, ref BasisFootNativeState right, float speed, float3 up, float3 bodyFwd)
    {
        if (speed < 0.02f * p.fastSpeedRef) return quaternion.identity;

        float scale = math.saturate(speed / p.fastSpeedRef), swingSide = 0f;
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

        float axialDeg = swingSide * pelvisAxialDeg * scale, listDeg = swingSide * pelvisListDeg * scale;
        quaternion axial = quaternion.AxisAngle(up, math.radians(axialDeg));
        quaternion list = quaternion.AxisAngle(bodyFwd, math.radians(listDeg));
        return math.mul(axial, list);
    }
    private static float HDist(float3 a, float3 b, float3 up)
    {
        float3 diff = a - b;
        diff -= up * math.dot(diff, up);
        return math.length(diff);
    }
    private static float GetUpComponent(float3 pos, float3 up)
    {
        return math.dot(pos, up);
    }
    private static void SetUpComponent(ref float3 pos, float value, float3 up)
    {
        float current = math.dot(pos, up);
        pos += up * (value - current);
    }
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
    public static bool BoneYaw(quaternion rot, float3 up, out float3 yawDir)
    {
        float3 fwd = math.mul(rot, new float3(0, 0, 1)), fwdFlat = fwd - up * math.dot(fwd, up);
        float fwdLen = math.length(fwdFlat);
        float3 boneUp = math.mul(rot, new float3(0, 1, 0)), upFlat = boneUp - up * math.dot(boneUp, up);
        float upLen = math.length(upFlat), upSign = -math.sign(math.dot(fwd, up));
        float3 fromFwd = fwdLen > 1e-5f ? fwdFlat / fwdLen : float3.zero;
        float3 fromUp = upLen > 1e-5f ? (upFlat / upLen) * upSign : float3.zero;
        float3 blended = fromFwd * fwdLen + fromUp * (1f - fwdLen);
        if (math.lengthsq(blended) < 1e-8f)
        {
            yawDir = float3.zero;
            return false;
        }

        yawDir = math.normalize(blended);
        return true;
    }
    public static float3 ComputeBodyForward(in BasisFootSimInput inp, in BasisFootSimState sim, in BasisFootSimParams p, float3 up)
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
    public quaternion FootRotation(float3 bodyFwd, float3 normal, float3 up, quaternion footAlign, float swingPitchDeg)
    {
        if (math.lengthsq(normal) < 0.001f) normal = up;

        float3 fwd = ProjectOnPlane(bodyFwd, normal);
        if (math.lengthsq(fwd) < 1e-6f)
            fwd = ProjectOnPlane(new float3(0, 0, 1), normal);
        fwd = math.normalize(fwd);

        quaternion surfaceRot = quaternion.LookRotation(fwd, normal), uprightRot = quaternion.LookRotation(fwd, up);
        float tiltAngle = AngleBetween(uprightRot, surfaceRot);
        quaternion result = tiltAngle > 0.01f ? math.slerp(uprightRot, surfaceRot, math.saturate(p.maxFootTiltDegrees / tiltAngle)) : uprightRot;
        float3 footFwd = math.mul(result, new float3(0, 0, 1)), footFwdFlat = footFwd - up * math.dot(footFwd, up);
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
    public static float SwingAnklePitchDeg(float t)
    {
        float inv = 1f - t;
        return toeOffDeg * inv * inv - heelStrikeDeg * t * t;
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
        float la = math.length(a), lb = math.length(b);
        if (la < 0.001f || lb < 0.001f) return math.lerp(a, b, t);

        float3 na = a / la, nb = b / lb;
        float dot = math.clamp(math.dot(na, nb), -1f, 1f), theta = math.acos(dot);

        if (theta < 0.001f) return math.lerp(a, b, t);

        float sinTheta = math.sin(theta);
        float3 dir = (na * math.sin((1f - t) * theta) + nb * math.sin(t * theta)) / sinTheta;
        return dir * math.lerp(la, lb, t);
    }
}
