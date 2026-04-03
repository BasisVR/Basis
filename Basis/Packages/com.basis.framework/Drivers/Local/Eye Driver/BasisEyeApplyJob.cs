using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;
[BurstCompile]
public struct BasisEyeApplyJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<BasisEyeState> state;
    public quaternion calLeftInitial;
    public quaternion calRightInitial;
    public void Execute(int index, TransformAccess transform)
    {
        BasisEyeState s = state[0];
        quaternion initial = index == 0 ? calLeftInitial : calRightInitial;
        quaternion offset = index == 0 ? s.leftOffset : s.rightOffset;
        transform.localRotation = math.mul(initial, offset);
    }
}
