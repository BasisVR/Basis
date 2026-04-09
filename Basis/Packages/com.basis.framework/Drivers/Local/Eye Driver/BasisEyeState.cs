using Unity.Mathematics;
public struct BasisEyeState
{
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

    // Output rotations (rig-local offsets to multiply onto base animation)
    public quaternion leftOffset;
    public quaternion rightOffset;
    public static BasisEyeState Create(uint seed)
    {
        return new BasisEyeState
        {
            rng = new Unity.Mathematics.Random(seed),

            phase = 0,
            phaseT = 0f,
            phaseDur = 0.5f,

            startYawPitch = float2.zero,
            targetYawPitch = float2.zero,
            currentYawPitch = float2.zero,

            leftOffset = quaternion.identity,
            rightOffset = quaternion.identity,
        };
    }

    public void Update(
        float dt, float maxAngleRad, float holdMin,
        float holdMax, float saccadeMin, float saccadeMax,
        float centerBias, float perEyeVarRad, bool occasionalCenterReturn,
        quaternion calLeftBasis, quaternion calLeftInvBasis,
        quaternion calRightBasis, quaternion calRightInvBasis)
    {
        // Advance timers
        phaseT += dt;

        if (phase == 0) // Hold
        {
            // Soft drift toward target while holding
            currentYawPitch = math.lerp(currentYawPitch, targetYawPitch, 1f - math.exp(-dt * 8f));

            // End hold -> begin saccade
            if (phaseT >= phaseDur)
            {
                phase = 1;
                phaseT = 0f;
                phaseDur = rng.NextFloat(saccadeMin, saccadeMax);

                startYawPitch = currentYawPitch;
                targetYawPitch = PickNewTarget(ref rng, maxAngleRad, centerBias, occasionalCenterReturn);
            }
        }
        else // Saccade
        {
            float u = math.saturate(phaseT / math.max(phaseDur, 1e-5f));

            // Ease-out-ish: quick start, settle
            float eased = 1f - math.pow(1f - u, 3f);

            currentYawPitch = math.lerp(startYawPitch, targetYawPitch, eased);

            // End saccade -> hold
            if (phaseT >= phaseDur)
            {
                phase = 0;
                phaseT = 0f;
                phaseDur = rng.NextFloat(holdMin, holdMax);
            }
        }

        // Slight per-eye variation (still highly correlated)
        float2 eyeVar = new float2(rng.NextFloat(-perEyeVarRad, perEyeVarRad), rng.NextFloat(-perEyeVarRad, perEyeVarRad));

        float2 leftYP = ClampYawPitchPlane(currentYawPitch + eyeVar * 0.6f, maxAngleRad);
        float2 rightYP = ClampYawPitchPlane(currentYawPitch - eyeVar * 0.6f, maxAngleRad);

        // Build canonical yaw/pitch -> rig-local offset via calibration basis
        leftOffset = CanonicalYawPitchToRigOffset(leftYP, calLeftBasis, calLeftInvBasis);
        rightOffset = CanonicalYawPitchToRigOffset(rightYP, calRightBasis, calRightInvBasis);
    }
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
        {
            yawPitch *= (maxAngleRad / mag);
        }

        return yawPitch;
    }

    private static float2 PickNewTarget(ref Random rng, float maxAngleRad, float centerBias, bool occasionalCenterReturn)
    {
        // Occasionally return toward center
        if (occasionalCenterReturn && rng.NextFloat() < 0.18f)
        {
            float small = maxAngleRad * 0.25f;
            return new float2(rng.NextFloat(-small, small), rng.NextFloat(-small, small));
        }

        // Bias toward center: r = U^(bias) * max
        float u = rng.NextFloat(0f, 1f);
        float r = math.pow(u, centerBias) * maxAngleRad;

        float a = rng.NextFloat(0f, math.PI * 2f);
        float yaw = math.cos(a) * r;
        float pitch = math.sin(a) * r;

        return new float2(yaw, pitch);
    }
}
