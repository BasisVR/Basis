using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;

/// <summary>
/// Burst-compiled job that bilinearly interpolates each finger joint's target rotation from the baked 2D pose
/// grid, slerps the current rotation toward it, and writes the result to transforms via TransformAccessArray.
/// Uses JointMapping to recover (fingerIdx, jointIdx) from the compact TAA layout.
///
/// The interpolation used to be a separate 10-item job feeding a 30-entry target array. Folding it in costs no
/// extra slerps -- that job produced 3 joints per finger and this one already runs per joint, so the same 3
/// grid slerps happen either way -- and it removes a job dispatch, a dependency fence and the array round-trip
/// from a chain whose total work is ~3 us but whose completion cost an order of magnitude more than that.
/// </summary>
[BurstCompile]
public struct BasisFingerSlerpJob : IJobParallelForTransform
{
    /// <summary>Baked grid: [fingerIdx * gridCount * 3 + gridIdx * 3 + jointIdx].
    /// Per-finger layout keeps the 4 bilinear sample cells on nearby cache lines.</summary>
    [ReadOnly] public NativeArray<quaternion> PoseGrid;

    /// <summary>Per-finger input percentages (10 entries, curl/spread in [-1,1]).</summary>
    [ReadOnly] public NativeArray<float2> Percentages;

    /// <summary>Current smoothed rotations (compact, _validJointCount elements).</summary>
    public NativeArray<quaternion> CurrentRotations;

    /// <summary>Maps compact TAA index → flat joint index (fingerIdx * 3 + jointIdx).</summary>
    [ReadOnly] public NativeArray<int> JointMapping;

    public int GridWidth;
    public int GridHeight;
    /// <summary>fingerIdx * gridCount * 3 (precomputed stride per finger).</summary>
    public int FingerStride;
    public float Increment;
    public float LerpFactor;

    public void Execute(int index, TransformAccess transform)
    {
        int flatIdx = JointMapping[index];
        int fingerIndex = flatIdx / 3;
        int jointIndex = flatIdx - fingerIndex * 3;

        float2 pct = Percentages[fingerIndex];
        float fx = (pct.x + 1f) / Increment;
        float fy = (pct.y + 1f) / Increment;
        int x0 = math.clamp((int)math.floor(fx), 0, GridWidth - 2);
        int y0 = math.clamp((int)math.floor(fy), 0, GridHeight - 2);
        float tx = math.clamp(fx - x0, 0f, 1f);
        float ty = math.clamp(fy - y0, 0f, 1f);

        int fingerBase = fingerIndex * FingerStride;
        int g00 = fingerBase + (x0 * GridHeight + y0) * 3 + jointIndex;
        int g10 = fingerBase + ((x0 + 1) * GridHeight + y0) * 3 + jointIndex;
        int g01 = fingerBase + (x0 * GridHeight + y0 + 1) * 3 + jointIndex;
        int g11 = fingerBase + ((x0 + 1) * GridHeight + y0 + 1) * 3 + jointIndex;

        quaternion bottom = math.slerp(PoseGrid[g00], PoseGrid[g10], tx);
        quaternion top = math.slerp(PoseGrid[g01], PoseGrid[g11], tx);
        quaternion target = math.slerp(bottom, top, ty);

        quaternion result = math.slerp(CurrentRotations[index], target, LerpFactor);
        CurrentRotations[index] = result;
        transform.localRotation = result;
    }
}
