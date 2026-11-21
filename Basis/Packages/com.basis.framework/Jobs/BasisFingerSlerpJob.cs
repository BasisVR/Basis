using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;
public struct BasisFingerJointOverride
{
    public quaternion rotation;
    public float2 percentage;
    public float percentageWeight, rotationWeight;
}

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
    [ReadOnly] public NativeArray<BasisFingerJointOverride> Overrides;
    public uint OverrideMask;

    public void Execute(int index, TransformAccess transform)
    {
        int flatIdx = JointMapping[index];
        int fingerIndex = flatIdx / 3;
        int jointIndex = flatIdx - fingerIndex * 3;
        float2 percentage = Percentages[fingerIndex];
        quaternion target;
        if ((OverrideMask & (1u << flatIdx)) != 0)
        {
            BasisFingerJointOverride o = Overrides[flatIdx];
            percentage = math.lerp(percentage, o.percentage, o.percentageWeight);
            target = Basis.Scripts.Drivers.BasisHandPoseSampler.SampleJoint(PoseGrid, FingerStride, GridWidth, GridHeight, Increment, fingerIndex, jointIndex, percentage);
            if (o.rotationWeight > 0f)
            {
                target = math.slerp(target, o.rotation, o.rotationWeight);
            }
        }
        else
        {
            target = Basis.Scripts.Drivers.BasisHandPoseSampler.SampleJoint(PoseGrid, FingerStride, GridWidth, GridHeight, Increment, fingerIndex, jointIndex, percentage);
        }

        quaternion result = math.slerp(CurrentRotations[index], target, LerpFactor);
        CurrentRotations[index] = result;
        transform.localRotation = result;
    }
}
