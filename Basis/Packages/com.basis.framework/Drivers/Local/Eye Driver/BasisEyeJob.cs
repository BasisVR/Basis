using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
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
    public NativeArray<BasisEyeState> state;

    public void Execute()
    {
        BasisEyeState s = state[0];

        s.Update(
            dt,
            math.radians(maxAngleRad),
            holdMin, holdMax,
            saccadeMin, saccadeMax,
            centerBias,
           math.radians(perEyeVarRad),
            occasionalCenterReturn,
            calLeftBasis, calLeftInvBasis,
            calRightBasis, calRightInvBasis
        );

        state[0] = s;
    }
}
