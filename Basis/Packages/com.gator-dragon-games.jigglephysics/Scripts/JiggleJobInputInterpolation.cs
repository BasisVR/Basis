using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics {

[BurstCompile]
public struct JiggleJobInputInterpolation : IJobFor {
    [ReadOnly] public NativeArray<JiggleTransform> previousInputs;
    [ReadOnly] public NativeArray<JiggleTransform> currentInputs;

    public double timeStamp;
    public double previousTimeStamp;
    
    public double currentTime;

    public NativeArray<JiggleTransform> outputInterpolatedPoses;

    public JiggleJobInputInterpolation(JiggleMemoryBus bus, double time, float fixedDeltaTime) {
        timeStamp = time - fixedDeltaTime;
        previousTimeStamp = timeStamp - fixedDeltaTime;
        currentTime = timeStamp;
        outputInterpolatedPoses = bus.simulateInputPoses;
        previousInputs = bus.inputPosesPrevious;
        currentInputs = bus.inputPosesCurrent;
    }

    public void UpdateArrays(JiggleMemoryBus bus) {
        previousInputs = bus.inputPosesPrevious;
        currentInputs = bus.inputPosesCurrent;
        outputInterpolatedPoses = bus.simulateInputPoses;
    }

    public void Execute(int index) {
        var prevPose = previousInputs[index];
        var newPose = currentInputs[index];

        var diff = timeStamp - previousTimeStamp;
        if (diff == 0) {
            outputInterpolatedPoses[index] = newPose;
            return;
        }

        var t = math.saturate((currentTime - previousTimeStamp) / diff);
        var inter= JiggleTransform.Lerp(prevPose, newPose, (float)t);

        outputInterpolatedPoses[index] = inter;
    }
}

}