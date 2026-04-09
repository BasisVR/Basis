using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;

/// <summary>
/// Burst-compiled job that slerps finger joint rotations toward targets
/// and writes the result to transforms via TransformAccessArray.
/// Uses JointMapping to index from compact TAA layout into the flat 30-element target array.
/// </summary>
[BurstCompile]
public struct BasisFingerSlerpJob : IJobParallelForTransform
{
    /// <summary>Interpolated target rotations (30 elements: fingerIdx * 3 + jointIdx).</summary>
    [ReadOnly] public NativeArray<quaternion> TargetRotations;

    /// <summary>Current smoothed rotations (compact, _validJointCount elements).</summary>
    public NativeArray<quaternion> CurrentRotations;

    /// <summary>Maps compact TAA index → flat joint index (fingerIdx * 3 + jointIdx).</summary>
    [ReadOnly] public NativeArray<int> JointMapping;

    public float LerpFactor;

    public void Execute(int index, TransformAccess transform)
    {
        int flatIdx = JointMapping[index];
        quaternion current = CurrentRotations[index];
        quaternion target = TargetRotations[flatIdx];
        quaternion result = math.slerp(current, target, LerpFactor);
        CurrentRotations[index] = result;
        transform.localRotation = result;
    }
}
