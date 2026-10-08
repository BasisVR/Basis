using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Drivers;
using Unity.Collections;
using Unity.Mathematics;

/// <summary>
/// Measures how far the local player's live finger bones sit from the pose a receiver would
/// reconstruct out of the twenty curl/splay scalars, WITHOUT changing the wire.
///
/// This exists to answer one question before the finger block commits to a format: can the grid
/// reproduce what is actually on the bones? Every finger backend in the codebase writes only
/// BasisFingerPose, and BasisFingerSlerpJob then drives all thirty joints from it, so the answer
/// should be "yes, to float noise". Anything else is a writer the survey missed, and finding it
/// here costs a log line instead of a wire revision.
///
/// Two different numbers, and the difference matters:
///
///   Lag       angle(grid at the CURRENT percentages, live bone). Non-zero by design — the local
///             driver slerps toward the grid target at LerpSpeed, so the bones trail it. This
///             bounds how far a receiver sampling the raw percentages lands from what the sender
///             is looking at; it is a smoothing question, not a representability one.
///
///   Manifold  angle to the BEST-FIT grid pose, searched over every node. This is the real
///             question: is the live pose reachable by some curl/splay at all? Lag cannot make
///             this large; only an off-grid writer can.
///
/// Undriven joints are counted separately. RebuildTransformAccess only drives joints whose
/// mapping Has flag is set, while the compressor sends every bone it can resolve — so a joint
/// that exists but is not driven carries a real bind/animator rotation the grid never produced,
/// and a grid-only format would overwrite it.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisFingerReconstructionDiagnostics
{
    public static bool Enabled;

    /// <summary>Sends between full best-fit searches. The search is ~13k quaternion compares.</summary>
    public static int ManifoldSearchEverySends = 10;

    public static float LagMaxDegrees;
    public static float LagLastMaxDegrees;
    public static float ManifoldMaxDegrees;
    public static float ManifoldLastMaxDegrees;
    public static int ManifoldWorstFinger = -1;
    public static int ManifoldWorstJoint = -1;
    public static int SendsMeasured;
    public static int ManifoldSearches;

    /// <summary>Joints (finger*3+joint) that exist on the rig but no finger job drives.</summary>
    public static int UndrivenJointCount;
    public static uint UndrivenJointMask;

    static int _sendCounter;

    public static void Reset()
    {
        LagMaxDegrees = 0f;
        LagLastMaxDegrees = 0f;
        ManifoldMaxDegrees = 0f;
        ManifoldLastMaxDegrees = 0f;
        ManifoldWorstFinger = -1;
        ManifoldWorstJoint = -1;
        SendsMeasured = 0;
        ManifoldSearches = 0;
        UndrivenJointCount = 0;
        UndrivenJointMask = 0;
        _sendCounter = 0;
    }

    public static string Summary() =>
        $"finger reconstruction: lag max {LagMaxDegrees:F3}° (last {LagLastMaxDegrees:F3}°), " +
        $"manifold max {ManifoldMaxDegrees:F3}° (last {ManifoldLastMaxDegrees:F3}°" +
        (ManifoldWorstFinger >= 0 ? $" at finger {ManifoldWorstFinger} joint {ManifoldWorstJoint}" : "") +
        $"), undriven joints {UndrivenJointCount}, sends {SendsMeasured}, searches {ManifoldSearches}";

    /// <summary>
    /// Geodesic angle via atan2 on the vector/scalar parts. 2*acos(|dot|) loses half its
    /// significant digits exactly where these numbers live (dot near 1) and would report
    /// conditioning noise as though it were reconstruction error.
    /// </summary>
    public static float AngleDegrees(quaternion a, quaternion b)
    {
        quaternion d = math.mul(math.normalize(a), math.conjugate(math.normalize(b)));
        float4 v = d.value;
        return math.degrees(2f * math.atan2(math.length(v.xyz), math.abs(v.w)));
    }

    /// <summary>
    /// <paramref name="liveByJoint"/> is the thirty live finger local rotations, flat-indexed
    /// finger*3+joint. <paramref name="percentages"/> is what the sender would put on the wire.
    /// <paramref name="driven"/> marks joints the finger job actually writes.
    /// </summary>
    public static void Measure(
        BasisHandPoseGrid grid,
        in NativeArray<quaternion> liveByJoint,
        in NativeArray<float2> percentages,
        bool[] driven)
    {
        if (!Enabled || grid == null || !grid.IsCreated) return;
        if (!liveByJoint.IsCreated || !percentages.IsCreated) return;

        SendsMeasured++;

        float lagMax = 0f;
        uint undrivenMask = 0;
        int undriven = 0;

        for (int finger = 0; finger < BasisHandPoseGrid.FingerCount; finger++)
        {
            float2 pct = percentages[finger];
            for (int joint = 0; joint < BasisHandPoseGrid.JointsPerFinger; joint++)
            {
                int flat = finger * BasisHandPoseGrid.JointsPerFinger + joint;
                if (driven != null && flat < driven.Length && !driven[flat])
                {
                    undrivenMask |= 1u << flat;
                    undriven++;
                    continue;
                }
                float angle = AngleDegrees(grid.SampleJoint(finger, joint, pct), liveByJoint[flat]);
                if (angle > lagMax) lagMax = angle;
            }
        }

        LagLastMaxDegrees = lagMax;
        if (lagMax > LagMaxDegrees) LagMaxDegrees = lagMax;
        UndrivenJointMask = undrivenMask;
        UndrivenJointCount = undriven;

        _sendCounter++;
        if (ManifoldSearchEverySends > 0 && _sendCounter < ManifoldSearchEverySends) return;
        _sendCounter = 0;

        ManifoldSearches++;
        float manifoldMax = 0f;
        int worstFinger = -1;
        int worstJoint = -1;

        for (int finger = 0; finger < BasisHandPoseGrid.FingerCount; finger++)
        {
            float bestForFinger = float.MaxValue;
            int bestJoint = -1;

            for (int xi = 0; xi < grid.GridWidth; xi++)
            {
                for (int yi = 0; yi < grid.GridHeight; yi++)
                {
                    int cell = finger * grid.FingerStride + (xi * grid.GridHeight + yi) * BasisHandPoseGrid.JointsPerFinger;
                    float worstHere = 0f;
                    int worstHereJoint = -1;

                    for (int joint = 0; joint < BasisHandPoseGrid.JointsPerFinger; joint++)
                    {
                        int flat = finger * BasisHandPoseGrid.JointsPerFinger + joint;
                        if (driven != null && flat < driven.Length && !driven[flat]) continue;
                        float angle = AngleDegrees(grid.Cells[cell + joint], liveByJoint[flat]);
                        if (angle > worstHere) { worstHere = angle; worstHereJoint = joint; }
                    }

                    if (worstHere < bestForFinger)
                    {
                        bestForFinger = worstHere;
                        bestJoint = worstHereJoint;
                    }
                }
            }

            if (bestForFinger != float.MaxValue && bestForFinger > manifoldMax)
            {
                manifoldMax = bestForFinger;
                worstFinger = finger;
                worstJoint = bestJoint;
            }
        }

        ManifoldLastMaxDegrees = manifoldMax;
        if (manifoldMax > ManifoldMaxDegrees)
        {
            ManifoldMaxDegrees = manifoldMax;
            ManifoldWorstFinger = worstFinger;
            ManifoldWorstJoint = worstJoint;
        }
    }
}
