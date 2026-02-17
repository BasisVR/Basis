using Basis.Scripts.Drivers;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Jobified, rig-agnostic "natural eye look around" with:
/// - Humanoid LeftEye/RightEye bones
/// - One-time auto-calibration per eye so it works across weird rigs (axis-agnostic)
/// - Realistic timing: holds + fast saccades + micro-saccades
/// - Hard clamp so eyes never exceed a max cone angle (approximated via yaw/pitch plane clamp)
/// - Optional head micro-follow near the eye limit (prevents "pinned at edge" look)
///
/// NOTE: We jobify the math/state. Transform reads/writes stay on main thread (LateUpdate).
/// </summary>
[System.Serializable]
public class BasisLocalEyeDriver
{
    [Header("Limits")]
    [Tooltip("Max eye rotation away from forward, in degrees.")]
    [Range(1f, 30f)] public float maxAngleDeg = 25f;

    [Header("Timing (realistic ranges)")]
    [Tooltip("How long the eyes hold a direction.")]
    public Vector2 holdTimeRange = new Vector2(0.55f, 4f);

    [Tooltip("How long a saccade takes (fast).")]
    public Vector2 saccadeTimeRange = new Vector2(0.05f, 0.15f);


    [Header("Style")]
    [Tooltip("Bias toward looking near center. Higher = more centered.")]
    [Range(0f, 6f)] public float centerBias = 2.5f;

    [Tooltip("Small divergence between eyes (degrees).")]
    [Range(0f, 2f)] public float perEyeVarianceDeg = 0.4f;

    [Tooltip("If true, eyes return to center occasionally.")]
    public bool occasionalCenterReturn = true;

    public static Transform leftEyeTransform, rightEyeTransform;
    private static Transform _headRef;     // used for calibration reference
    private static EyeCalibration _calLeft;
    private static EyeCalibration _calRight;

    private static NativeArray<EyeState> _state;


    public static bool Override = false;
    public static bool IsEnabled = false;

    public static Quaternion leftEyeInitialRotation;
    public static quaternion rightEyeInitialRotation;
    public static void Initalize()
    {
        Dispose();

        var References = BasisLocalAvatarDriver.Mapping;
        if (References.HasLeftEye == false || References.HasRightEye == false)
        {
            IsEnabled = false;
            return;
        }

        leftEyeTransform = References.LeftEye;
        rightEyeTransform = References.RightEye;
        _headRef = References.head;

        _state = new NativeArray<EyeState>(1, Allocator.Persistent);
        _state[0] = EyeState.Create((uint)UnityEngine.Random.Range(1, int.MaxValue));
        leftEyeInitialRotation = leftEyeTransform.rotation;
        rightEyeInitialRotation = rightEyeTransform.rotation;
        CalibrateEyes();
        IsEnabled = true;

    }

    public static JobHandle handle;
    public static void Dispose()
    {
        if (_state.IsCreated)
        {
            handle.Complete();
            _state.Dispose();
        }
    }
    public bool HasEyeSchedule = false;
    public void Simulate(float dt)
    {
        if (IsEnabled && Override == false && HasEyeSchedule == false)
        {
            BasisEyeJob job = new BasisEyeJob
            {
                dt = dt,

                maxAngleRad = maxAngleDeg,

                holdMin = holdTimeRange.x,
                holdMax = holdTimeRange.y,

                saccadeMin = saccadeTimeRange.x,
                saccadeMax = saccadeTimeRange.y,

                centerBias = centerBias,
                perEyeVarRad = perEyeVarianceDeg,
                occasionalCenterReturn = occasionalCenterReturn,

                calLeftBasis = _calLeft.basis,
                calLeftInvBasis = _calLeft.invBasis,
                calRightBasis = _calRight.basis,
                calRightInvBasis = _calRight.invBasis,
                rightBase = rightEyeTransform.localRotation,
                leftBase = leftEyeTransform.localRotation,

                state = _state
            };
            HasEyeSchedule = true;
            handle = job.Schedule();
        }
    }
    public void Apply()
    {
        if (HasEyeSchedule)
        {
            HasEyeSchedule = false;
            handle.Complete();

            EyeState s = _state[0];

            // Apply results (still respecting base animation)
            leftEyeTransform.localRotation = s.leftoutput;
            rightEyeTransform.localRotation = s.rightoutput;
        }
    }

    private static void CalibrateEyes()
    {
        // Per-eye calibration against head reference directions
        _calLeft = CalibrateOneEye(leftEyeTransform, _headRef);
        _calRight = CalibrateOneEye(rightEyeTransform, _headRef);
    }

    [System.Serializable]
    public struct EyeCalibration
    {
        // basis maps canonical eye-space -> rig eye local-space
        // canonical: +Z forward, +Y up, +X right
        public quaternion basis;
        public quaternion invBasis;
    }
    private static float3[] axes = new float3[]
{
            new float3( 1, 0, 0), new float3(-1, 0, 0),
            new float3( 0, 1, 0), new float3( 0,-1, 0),
            new float3( 0, 0, 1), new float3( 0, 0,-1)
};
    /// <summary>
    /// Auto-detect the eye bone's local forward/up axes by comparing its transformed local axes
    /// to the head reference forward/up in world space.
    /// </summary>
    private static EyeCalibration CalibrateOneEye(Transform eye, Transform refHead)
    {

        float3 headF = refHead.forward;
        float3 headU = refHead.up;

        // Pick local axis that best matches head forward
        int bestF = 0;
        float bestFDot = -1e9f;
        for (int i = 0; i < axes.Length; i++)
        {
            float3 w = eye.TransformDirection((Vector3)axes[i]);
            float d = math.dot(math.normalizesafe(w), math.normalizesafe(headF));
            if (d > bestFDot) { bestFDot = d; bestF = i; }
        }
        float3 fLocal = axes[bestF];

        // Pick local axis (not colinear with forward) that best matches head up
        int bestU = 0;
        float bestUDot = -1e9f;
        for (int Index = 0; Index < axes.Length; Index++)
        {
            if (Index == bestF)
            {
                continue;
            }

            if (math.abs(math.dot(axes[Index], fLocal)) > 0.9f)
            {
                continue; // reject colinear
            }

            float3 w = eye.TransformDirection((Vector3)axes[Index]);
            float d = math.dot(math.normalizesafe(w), math.normalizesafe(headU));
            if (d > bestUDot) { bestUDot = d; bestU = Index; }
        }
        float3 uLocal = axes[bestU];

        // Orthonormalize basis
        fLocal = math.normalize(fLocal);
        uLocal = uLocal - fLocal * math.dot(uLocal, fLocal);
        uLocal = math.normalizesafe(uLocal, new float3(0, 1, 0));

        float3 rLocal = math.normalizesafe(math.cross(uLocal, fLocal), new float3(1, 0, 0));
        uLocal = math.normalizesafe(math.cross(fLocal, rLocal), new float3(0, 1, 0));

        // Build basis rotation: canonical (R,U,F) -> rig local (rLocal,uLocal,fLocal)
        float3x3 m = new float3x3(rLocal, uLocal, fLocal);
        quaternion basis = new quaternion(m);
        quaternion inv = math.inverse(basis);

        return new EyeCalibration { basis = basis, invBasis = inv };
    }

    [BurstCompile]
    public struct BasisEyeJob : IJob
    {
        public float dt;

        public float maxAngleRad;

        public float holdMin, holdMax;
        public float saccadeMin, saccadeMax;

        public float centerBias;
        public float perEyeVarRad;

        public bool occasionalCenterReturn;

        public quaternion calLeftBasis, calLeftInvBasis;
        public quaternion calRightBasis, calRightInvBasis;

        public NativeArray<EyeState> state;
        public Quaternion rightBase;
        public Quaternion leftBase;

        public void Execute()
        {
            EyeState s = state[0];

            s.Update(
                dt,
                math.radians(maxAngleRad),
                holdMin, holdMax,
                saccadeMin, saccadeMax,
                centerBias,
               math.radians(perEyeVarRad),
                occasionalCenterReturn,
                calLeftBasis, calLeftInvBasis,
                calRightBasis, calRightInvBasis,
                leftBase, rightBase
            );


            state[0] = s;
        }
    }

    public struct EyeState
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

        public quaternion leftoutput;
        public quaternion rightoutput;
        public static EyeState Create(uint seed)
        {
            return new EyeState
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
            float dt,
            float maxAngleRad,
            float holdMin, float holdMax,
            float saccadeMin, float saccadeMax,
            float centerBias,
            float perEyeVarRad,
            bool occasionalCenterReturn,
            quaternion calLeftBasis, quaternion calLeftInvBasis,
            quaternion calRightBasis, quaternion calRightInvBasis,
            quaternion leftinput,quaternion rightinput
            )
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
            float2 eyeVar = new float2(
                rng.NextFloat(-perEyeVarRad, perEyeVarRad),
                rng.NextFloat(-perEyeVarRad, perEyeVarRad)
            );

            float2 leftYP = ClampYawPitchPlane(currentYawPitch + eyeVar * 0.6f, maxAngleRad);
            float2 rightYP = ClampYawPitchPlane(currentYawPitch - eyeVar * 0.6f, maxAngleRad);

            // Build canonical yaw/pitch -> rig-local offset via calibration basis
            leftOffset = CanonicalYawPitchToRigOffset(leftYP, calLeftBasis, calLeftInvBasis);
            rightOffset = CanonicalYawPitchToRigOffset(rightYP, calRightBasis, calRightInvBasis);

            leftoutput = math.mul(leftinput, leftOffset);
            rightoutput = math.mul(rightinput, rightOffset);
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
                yawPitch *= (maxAngleRad / mag);
            return yawPitch;
        }

        private static float2 PickNewTarget(ref Unity.Mathematics.Random rng, float maxAngleRad, float centerBias, bool occasionalCenterReturn)
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
}
