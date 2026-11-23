using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
[BurstCompile]
public struct BasisFootSimulateJob : IJob
{
    public const int PhasePlanted = 0, PhaseStepping = 1, PhaseHanging = 2;
    const float velocitySmoothing = 10f, stillSpeedFrac = 0.1f, hangLeeway = 1.1f;
    const float hipSwayFraction = 0.03f, pelvisAxialDeg = 4.5f, pelvisListDeg = 5f, loadingDipFraction = 0.012f, loadingResponseFrac = 0.35f;
    public BasisFootSimParams p;
    public NativeArray<BasisFootNativeState> feet;
    public NativeArray<BasisFootSimState> simState;
    public NativeArray<BasisFootSimInput> input;
    public NativeArray<BasisFootSimOutput> output;
    public unsafe void Execute()
    {
        if (input.Length < 1 || simState.Length < 1 || output.Length < 1 || feet.Length < 2) return;

        ref BasisFootSimInput inp = ref UnsafeUtility.ArrayElementAsRef<BasisFootSimInput>(input.GetUnsafePtr(), 0);
        ref BasisFootSimState sim = ref UnsafeUtility.ArrayElementAsRef<BasisFootSimState>(simState.GetUnsafePtr(), 0);
        ref BasisFootNativeState left = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(feet.GetUnsafePtr(), 0);
        ref BasisFootNativeState right = ref UnsafeUtility.ArrayElementAsRef<BasisFootNativeState>(feet.GetUnsafePtr(), 1);
        float dt = inp.dt;
        if (dt <= 0f) return;

        float3 up = math.lengthsq(inp.playerUp) < 0.001f ? new float3(0, 1, 0) : math.normalize(inp.playerUp);
        float3 facing = Flatten(inp.facingFwd, up);
        facing = math.lengthsq(facing) < 1e-6f ? Flatten(inp.avatarForward, up) : facing;
        facing = math.lengthsq(facing) < 1e-6f ? math.cross(new float3(1, 0, 0), up) : facing;
        facing = math.normalize(facing);
        float3 sideAxis = math.normalize(math.cross(up, facing));

        float3 hipsMove = inp.hipsPos - sim.prevHipsPos;
        bool teleported = !sim.hasPrevHipsPos || math.length(hipsMove) > p.teleportDistance;
        if (teleported)
        {
            sim.smoothedVelocity = float3.zero;
        }
        else
        {
            float3 velocity = Flatten(hipsMove / dt, up);
            sim.smoothedVelocity = math.lerp(sim.smoothedVelocity, velocity, 1f - math.exp(-velocitySmoothing * dt));
        }
        sim.prevHipsPos = inp.hipsPos;
        sim.hasPrevHipsPos = true;

        float speed = math.length(sim.smoothedVelocity), stillSpeed = stillSpeedFrac * p.legLength;
        bool still = !teleported && speed < stillSpeed;
        sim.stillTime = still ? sim.stillTime + dt : 0f;
        if (teleported)
        {
            left.placed = false;
            right.placed = false;
        }

        float duration = DurationAt(speed);
        bool settling = sim.stillTime > p.settleDelay || sim.settlePending;
        float leftError = UpdateFoot(ref left, in right, inp.leftHit, inp.leftPoint, inp.leftNormal, inp.leftOrigin, inp.hipsPos, facing, sideAxis, up, duration, settling, dt, out bool leftOverstretched);
        float rightError = UpdateFoot(ref right, in left, inp.rightHit, inp.rightPoint, inp.rightNormal, inp.rightOrigin, inp.hipsPos, facing, sideAxis, up, duration, settling, dt, out bool rightOverstretched);

        if (!CanStart(in right, leftOverstretched)) leftError = 0f;
        if (!CanStart(in left, rightOverstretched)) rightError = 0f;

        if (leftError > 1f && rightError > 1f)
        {
            float sideways = math.dot(sim.smoothedVelocity, sideAxis);
            if (sideways > stillSpeed) leftError = 0f;
            else if (sideways < -stillSpeed) rightError = 0f;
        }

        if (leftError > 1f && leftError >= rightError) BeginStep(ref left);
        else if (rightError > 1f) BeginStep(ref right);

        if (still && left.phase != PhaseStepping && right.phase != PhaseStepping) sim.settlePending = false;

        var outp = output[0];
        outp.hipBob = ComputeHipBob(in left, in right, speed);
        outp.hipSway = sideAxis * ComputeHipSway(in left, in right, speed);
        outp.pelvisDelta = ComputePelvisDelta(in left, in right, speed, up, facing);
        outp.airborne = left.phase == PhaseHanging && right.phase == PhaseHanging;
        output[0] = outp;
    }
    public float DurationAt(float speed)
    {
        return DurationAt(speed, p.stepDuration, p.minStepDuration, p.legLength);
    }
    public static float DurationAt(float speed, float stepDuration, float minStepDuration, float legLength)
    {
        return math.max(minStepDuration, stepDuration / (1f + speed / math.max(1e-3f, legLength)));
    }
    private float UpdateFoot(ref BasisFootNativeState f, in BasisFootNativeState other, bool hit, float3 hitPoint, float3 hitNormal, float3 origin, float3 hipsPos, float3 facing, float3 sideAxis, float3 up, float duration, bool settling, float dt, out bool overstretched)
    {
        overstretched = false;
        float legLen = f.sideSign < 0 ? p.leftLegLen : p.rightLegLen;
        float3 hipJoint = hipsPos - up * p.hipJointDrop + sideAxis * (f.sideSign * 0.5f * p.stanceWidth);

        float3 normal = up, desired;
        if (hit)
        {
            normal = ClampTilt(hitNormal, up, math.radians(p.maxFootTiltDegrees));
            desired = hitPoint + normal * p.footHeightOffset;
        }
        else
        {
            desired = origin - up * p.rayLength;
        }
        desired = KeepOwnSide(desired, f.sideSign, in other, sideAxis);

        float3 toDesired = desired - hipJoint;
        float reach = f.phase == PhaseHanging ? legLen : legLen * hangLeeway;
        bool reachable = math.length(toDesired) <= reach;
        if (!reachable)
        {
            desired = hipJoint + math.normalizesafe(toDesired, -up) * legLen;
            normal = up;
        }
        quaternion desiredRot = FootRotation(facing, normal, f.sideSign < 0 ? p.footAlignLeft : p.footAlignRight);
        int restingPhase = reachable ? PhasePlanted : PhaseHanging;
        f.idealPos = desired;

        float error = 0f;
        if (!f.placed || (f.phase == PhaseHanging && !reachable))
        {
            f.placed = true;
            f.phase = restingPhase;
            f.currentPos = f.plantedPos = f.stepTargetPos = desired;
            f.currentRot = desiredRot;
            f.plantedBodyFwd = facing;
            f.filteredNormal = normal;
            f.plantedTime = BasisFootNativeState.SettledPlantedTime;
        }
        else if (f.phase == PhaseHanging)
        {
            BeginStep(ref f);
        }
        else if (f.phase == PhaseStepping)
        {
            AdvanceStep(ref f, desired, desiredRot, facing, normal, restingPhase, duration, up, dt);
        }
        else
        {
            f.plantedTime += dt;
            f.currentPos = f.plantedPos;
            if (math.lengthsq(f.plantedBodyFwd) < 1e-6f) f.plantedBodyFwd = facing;
            f.currentRot = FootRotation(f.plantedBodyFwd, ClampTilt(f.filteredNormal, up, math.radians(p.maxFootTiltDegrees)), f.sideSign < 0 ? p.footAlignLeft : p.footAlignRight);

            float distance = math.distance(f.plantedPos, desired);
            float yaw = math.abs(YawBetween(f.plantedBodyFwd, facing, up));
            float distanceError = distance / p.stepDistance;
            float yawError = yaw / p.stepAngle;
            if (settling) distanceError = math.max(distanceError, math.max(distance / p.settleDistance, yaw / p.settleAngle));

            float maxReach = math.max(legLen * p.overstretch, math.distance(hipJoint, desired) + p.stepDistance);
            float reachError = math.distance(hipJoint, f.plantedPos) / maxReach;
            overstretched = reachError > 1f;
            error = math.max(distanceError, math.max(yawError, reachError));
        }
        return error;
    }
    private bool CanStart(in BasisFootNativeState other, bool overstretched)
    {
        return overstretched || other.phase != PhaseStepping || other.stepProgress >= p.stepOverlap;
    }
    private static void BeginStep(ref BasisFootNativeState f)
    {
        f.phase = PhaseStepping;
        f.stepProgress = 0f;
        f.stepTimer = 0f;
        f.stepStartPos = f.currentPos;
        f.stepStartRot = f.currentRot;
    }
    private void AdvanceStep(ref BasisFootNativeState f, float3 end, quaternion endRot, float3 endFwd, float3 endNormal, int endPhase, float duration, float3 up, float dt)
    {
        f.stepDur = duration;
        f.stepTimer += dt;
        f.stepProgress = math.min(1f, f.stepProgress + dt / duration);
        f.stepTargetPos = end;
        if (f.stepProgress >= 1f)
        {
            f.phase = endPhase;
            f.currentPos = f.plantedPos = end;
            f.currentRot = endRot;
            f.plantedBodyFwd = endFwd;
            f.filteredNormal = endNormal;
            f.plantedTime = 0f;
            return;
        }

        float t = math.smoothstep(0f, 1f, f.stepProgress);
        float lift = p.stepHeight * math.saturate(math.distance(f.stepStartPos, end) / p.stepDistance);
        f.currentPos = math.lerp(f.stepStartPos, end, t) + up * (math.sin(math.PI * f.stepProgress) * lift);
        f.currentRot = math.slerp(f.stepStartRot, endRot, t);
    }
    private float3 KeepOwnSide(float3 position, int sideSign, in BasisFootNativeState other, float3 sideAxis)
    {
        if (!other.placed) return position;
        float3 outward = sideAxis * sideSign;
        float gap = math.dot(position - other.currentPos, outward);
        return gap < p.minFootGap ? position + outward * (p.minFootGap - gap) : position;
    }
    private float ComputeHipBob(in BasisFootNativeState left, in BasisFootNativeState right, float speed)
    {
        if (speed < 0.02f * p.fastSpeedRef) return 0f;

        float amplitude = p.hipToFoot * p.hipBobFraction * math.saturate(speed / p.fastSpeedRef);
        float rise = math.max(SwingArc(in left), SwingArc(in right)) * amplitude;
        float dip = math.max(LoadingResponse(in left), LoadingResponse(in right)) * p.legLength * loadingDipFraction * math.saturate(speed / p.fastSpeedRef);
        return rise - dip;
    }
    private static float SwingArc(in BasisFootNativeState f)
    {
        return f.phase == PhaseStepping ? math.sin(f.stepProgress * math.PI) : 0f;
    }
    private static float LoadingResponse(in BasisFootNativeState f)
    {
        if (f.phase != PhasePlanted) return 0f;
        float dur = f.stepDur * loadingResponseFrac;
        if (dur <= 1e-4f) return 0f;
        float u = f.plantedTime / dur;
        return u >= 1f ? 0f : math.sin(u * math.PI);
    }
    private float ComputeHipSway(in BasisFootNativeState left, in BasisFootNativeState right, float speed)
    {
        if (speed < 0.02f * p.fastSpeedRef) return 0f;
        return (SwingArc(in left) - SwingArc(in right)) * p.legLength * hipSwayFraction * math.saturate(speed / p.fastSpeedRef);
    }
    private quaternion ComputePelvisDelta(in BasisFootNativeState left, in BasisFootNativeState right, float speed, float3 up, float3 facing)
    {
        if (speed < 0.02f * p.fastSpeedRef) return quaternion.identity;

        float swingSide = SwingArc(in left) - SwingArc(in right);
        if (math.abs(swingSide) < 1e-4f) return quaternion.identity;

        float scale = math.saturate(speed / p.fastSpeedRef);
        quaternion axial = quaternion.AxisAngle(up, math.radians(swingSide * pelvisAxialDeg * scale));
        quaternion list = quaternion.AxisAngle(facing, math.radians(swingSide * pelvisListDeg * scale));
        return math.mul(axial, list);
    }
    public static float3 ClampTilt(float3 normal, float3 up, float maxTiltRad)
    {
        if (math.lengthsq(normal) < 1e-6f) return up;
        normal = math.normalize(normal);
        float cos = math.clamp(math.dot(normal, up), -1f, 1f);
        if (math.acos(cos) <= maxTiltRad) return normal;
        float3 across = normal - up * cos;
        if (math.lengthsq(across) < 1e-8f) return up;
        return math.normalize(up * math.cos(maxTiltRad) + math.normalize(across) * math.sin(maxTiltRad));
    }
    public static quaternion FootRotation(float3 fwd, float3 normal, quaternion footAlign)
    {
        float3 onSurface = fwd - normal * math.dot(fwd, normal);
        if (math.lengthsq(onSurface) < 1e-6f) onSurface = math.cross(normal, new float3(1, 0, 0));
        return math.mul(quaternion.LookRotation(math.normalize(onSurface), normal), footAlign);
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
    private static float3 Flatten(float3 v, float3 up)
    {
        return v - up * math.dot(v, up);
    }
    private static float YawBetween(float3 from, float3 to, float3 up)
    {
        float3 a = Flatten(from, up), b = Flatten(to, up);
        if (math.lengthsq(a) < 1e-6f || math.lengthsq(b) < 1e-6f) return 0f;
        a = math.normalize(a);
        b = math.normalize(b);
        return math.atan2(math.dot(up, math.cross(a, b)), math.clamp(math.dot(a, b), -1f, 1f));
    }
}
