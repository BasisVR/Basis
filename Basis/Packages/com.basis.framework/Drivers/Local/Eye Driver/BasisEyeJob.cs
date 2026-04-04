using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
[BurstCompile]
public struct BasisEyeJob : IJob
{
    public float dt;
    public float maxAngleDeg;
    public float saccadeMin, saccadeMax;
    public float perEyeVarDeg;

    public BasisEyePersonality personality;
    public BasisEyeCalibration calLeft, calRight;

    public float2 headDeltaYP;

    public bool hasGazeTarget;
    public float2 gazeLeftEye, gazeRightEye, gazeMouth;
    public float gazeMouthScale;
    public bool gazeTargetChanged;

    public NativeArray<BasisEyeState> state;

    public void Execute()
    {
        BasisEyeState s = state[0];

        s.Update(
            dt,
            headDeltaYP,
            math.radians(maxAngleDeg),
            saccadeMin, saccadeMax,
            math.radians(perEyeVarDeg),
            personality,
            calLeft, calRight,
            hasGazeTarget,
            gazeLeftEye, gazeRightEye, gazeMouth,
            gazeMouthScale,
            gazeTargetChanged
        );

        state[0] = s;
    }
}
