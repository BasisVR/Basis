using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;
namespace Basis.Capture
{
    [BurstCompile]
    public struct BasisCaptureLocalReadJob : IJobParallelForTransform
    {
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float> output;
        public void Execute(int index, TransformAccess transform)
        {
            if (!transform.isValid) return;
            int o = index * 10;
            UnityEngine.Vector3 p = transform.localPosition, s = transform.localScale;
            UnityEngine.Quaternion r = transform.localRotation;
            output[o] = p.x; output[o + 1] = p.y; output[o + 2] = p.z;
            output[o + 3] = r.x; output[o + 4] = r.y; output[o + 5] = r.z; output[o + 6] = r.w;
            output[o + 7] = s.x; output[o + 8] = s.y; output[o + 9] = s.z;
        }
    }
    [BurstCompile]
    public struct BasisCaptureWorldReadJob : IJobParallelForTransform
    {
        [WriteOnly] public NativeArray<float4x4> output;
        public void Execute(int index, TransformAccess transform)
        {
            if (!transform.isValid) return;
            output[index] = transform.localToWorldMatrix;
        }
    }
}
