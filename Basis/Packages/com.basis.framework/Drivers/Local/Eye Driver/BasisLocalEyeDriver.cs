using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
/// <summary>
/// - Humanoid LeftEye/RightEye bones
/// - One-time auto-calibration per eye so it works across weird rigs (axis-agnostic)
/// - Realistic timing: holds + fast saccades + micro-saccades
/// - Hard clamp so eyes never exceed a max cone angle (approximated via yaw/pitch plane clamp)
/// - Optional head micro-follow near the eye limit (prevents "pinned at edge" look)
/// </summary>
[System.Serializable]
public class BasisLocalEyeDriver
{
    [Header("Limits")]
    [Tooltip("Max eye rotation away from forward, in degrees.")]
    [Range(1f, 30f)]
    public float maxAngleDeg = 25f;

    [Header("Timing (realistic ranges)")]
    [Tooltip("How long the eyes hold a direction.")]
    public Vector2 holdTimeRange = new Vector2(0.55f, 4f);

    [Tooltip("How long a saccade takes (fast).")]
    public Vector2 saccadeTimeRange = new Vector2(0.05f, 0.15f);

    [Header("Style")]
    [Tooltip("Bias toward looking near center. Higher = more centered.")]
    [Range(0f, 6f)]
    public float centerBias = 2.5f;

    [Tooltip("Small divergence between eyes (degrees).")]
    [Range(0f, 2f)]
    public float perEyeVarianceDeg = 0.4f;

    [Tooltip("If true, eyes return to center occasionally.")]
    public bool occasionalCenterReturn = true;

    public static Transform leftEyeTransform;
    public static Transform rightEyeTransform;
    private static Transform _headRef;
    public static BasisEyeCalibration calLeft;
    public static BasisEyeCalibration calRight;
    private static NativeArray<BasisEyeState> _state;
    private static TransformAccessArray _eyeTransforms;
    public static bool Override = false;
    public static bool IsEnabled = false;
    public static JobHandle handle;
    public static bool HasEyeSchedule = false;
    public static void Initalize()
    {
        Dispose();

        BasisTransformMapping References = BasisLocalAvatarDriver.Mapping;
        if (References.HasLeftEye == false || References.HasRightEye == false)
        {
            IsEnabled = false;
            return;
        }

        leftEyeTransform = References.LeftEye;
        rightEyeTransform = References.RightEye;
        _headRef = References.head;

        _state = new NativeArray<BasisEyeState>(1, Allocator.Persistent);
        _state[0] = BasisEyeState.Create((uint)UnityEngine.Random.Range(1, int.MaxValue));

        _eyeTransforms = new TransformAccessArray(2);
        _eyeTransforms.Add(leftEyeTransform);
        _eyeTransforms.Add(rightEyeTransform);

        // Per-eye calibration against head reference directions
        calLeft = CalibrateOneEye(leftEyeTransform, _headRef);
        calRight = CalibrateOneEye(rightEyeTransform, _headRef);

        IsEnabled = true;

    }
    public static void Dispose()
    {
        if (_state.IsCreated)
        {
            handle.Complete();
            _state.Dispose();
        }
        if (_eyeTransforms.isCreated)
        {
            _eyeTransforms.Dispose();
        }
    }
    public void Simulate(float dt)
    {
        if (!IsEnabled || Override != false || HasEyeSchedule != false)
        {
            //   BasisDebug.Log("Not RUnning EYes");
            return;
        }
        BasisEyeJob computeJob = new BasisEyeJob
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

            calLeftBasis = calLeft.basis,
            calLeftInvBasis = calLeft.invBasis,
            calRightBasis = calRight.basis,
            calRightInvBasis = calRight.invBasis,

            state = _state
        };

        JobHandle computeHandle = computeJob.Schedule();

        BasisEyeApplyJob applyJob = new BasisEyeApplyJob
        {
            state = _state,
            calLeftInitial = calLeft.initialRotation,
            calRightInitial = calRight.initialRotation
        };
        handle = applyJob.Schedule(_eyeTransforms, computeHandle);

        HasEyeSchedule = true;
    }
    public static BasisEyeState LastKnownState;
    public void Apply()
    {
        if (HasEyeSchedule)
        {
            HasEyeSchedule = false;
            handle.Complete();
            LastKnownState = _state[0];
        }
    }

    private static readonly float3[] axes = new float3[]
{
            new float3( 1, 0, 0), new float3(-1, 0, 0),
            new float3( 0, 1, 0), new float3( 0,-1, 0),
            new float3( 0, 0, 1), new float3( 0, 0,-1)
};
    /// <summary>
    /// Auto-detect the eye bone's local forward/up axes by comparing its transformed local axes
    /// to the head reference forward/up in world space.
    /// </summary>
    private static BasisEyeCalibration CalibrateOneEye(Transform eye, Transform refHead)
    {

        float3 headF = refHead.forward;
        float3 headU = refHead.up;

        // Pick local axis that best matches head forward
        int bestF = 0;
        float bestFDot = -1e9f;
        for (int Index = 0; Index < axes.Length; Index++)
        {
            float3 w = eye.TransformDirection((Vector3)axes[Index]);
            float d = math.dot(math.normalizesafe(w), math.normalizesafe(headF));
            if (d <= bestFDot)
            {
                continue;
            }
            bestFDot = d; bestF = Index;
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
            if (d <= bestUDot)
            {
                continue;
            }
            bestUDot = d; bestU = Index;
        }
        float3 uLocal = axes[bestU];

        // Orthonormalize basis
        fLocal = math.normalize(fLocal);
        uLocal -= fLocal * math.dot(uLocal, fLocal);
        uLocal = math.normalizesafe(uLocal, new float3(0, 1, 0));

        float3 rLocal = math.normalizesafe(math.cross(uLocal, fLocal), new float3(1, 0, 0));
        uLocal = math.normalizesafe(math.cross(fLocal, rLocal), new float3(0, 1, 0));

        // Build basis rotation: canonical (R,U,F) -> rig local (rLocal,uLocal,fLocal)
        float3x3 m = new float3x3(rLocal, uLocal, fLocal);
        quaternion basis = new quaternion(m);
        quaternion inv = math.inverse(basis);

        return new BasisEyeCalibration { basis = basis, invBasis = inv, initialRotation = eye.localRotation };
    }
}
