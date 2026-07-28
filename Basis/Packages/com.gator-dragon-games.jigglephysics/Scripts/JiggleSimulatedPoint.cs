using System;
using Unity.Mathematics;

namespace GatorDragonGames.JigglePhysics {

public unsafe struct JiggleSimulatedPoint {
    public const int MAX_CHILDREN = 32;

    // Generated at runtime
    public float3 lastPosition;
    public float3 position;
    public float3 workingPosition;
    public float3 parentPose;
    public float3 pose;
    public float desiredLengthToParent;
    public bool animated;
    public float worldRadius;
    //public float3 debug;

    // Set at initialization
    public float distanceFromRoot;
    public int parentIndex;
    public fixed int childrenIndices[MAX_CHILDREN];
    public int childrenCount;
    public bool hasTransform;

    private static bool GetIsValid(float3 vector) {
        return math.all(math.isfinite(vector));
    }
    private static bool GetIsValid(float value) {
        return math.isfinite(value);
    }

    public bool GetIsValid(int pointCount, out string failReason) {
        if (!GetIsValid(lastPosition)) {
            failReason = "lastPosition is NaN";
            return false;
        }
        if (!GetIsValid(position)) {
            failReason = "position is NaN";
            return false;
        }
        if (!GetIsValid(workingPosition)) {
            failReason = "workingPosition is NaN";
            return false;
        }
        if (!GetIsValid(pose)) {
            failReason = "pose is NaN";
            return false;
        }
        if (!GetIsValid(parentPose)) {
            failReason = "parentPose is NaN";
            return false;
        }
        if (!GetIsValid(desiredLengthToParent)) {
            failReason = "desiredLengthToParent is NaN";
            return false;
        }
        if (!GetIsValid(worldRadius)) {
            failReason = "worldRadius is NaN";
            return false;
        }
        if (!GetIsValid(distanceFromRoot)) {
            failReason = "distanceFromRoot is NaN";
            return false;
        }
        if (childrenCount < 0 || childrenCount > MAX_CHILDREN) {
            failReason = "childrenCount is outside range";
            return false;
        }
        if (parentIndex < -1 || parentIndex >= pointCount) {
            failReason = "parentIndex is outside range";
            return false;
        }
        for (int i = 0; i < childrenCount; i++) {
            int childIndex = childrenIndices[i];
            if (childIndex < 0 || childIndex >= pointCount) {
                failReason = "childrenIndices is outside range";
                return false;
            }
        }
        failReason = "All good!";
        return true;
    }

    public override string ToString() {
        return $"(position: {position},\nlastPosition: {lastPosition},\n" +
               $"workingPosition: {workingPosition},\n" +
               $"parentPose: {parentPose},\npose: {pose},\ndesiredLengthToParent:{desiredLengthToParent},\n" +
               $"animated: {animated},\n parentIndex: {parentIndex},\n " +
               $"children: [{childrenIndices[0]}, ...],\n childrenCount: {childrenCount},\n hasTransform: {hasTransform})";
    }

    public void Sanitize() {
        if (!GetIsValid(distanceFromRoot)) {
            distanceFromRoot = 0.1f;
        }
        if (!GetIsValid(pose)) {
            pose = float3.zero;
        }
        if (!GetIsValid(parentPose)) {
            parentPose = pose;
        }
        // Recover to the pose rather than world origin, and contain corrupt-but-finite state:
        // no legitimate simulation carries a point beyond roughly its own chain length from
        // its animated pose, but garbage reads can — and Inf/1e38 floats survive NaN checks.
        var maxDeviation = math.max(10f, distanceFromRoot * 4f);
        var maxDeviationSq = maxDeviation * maxDeviation;
        if (!GetIsValid(position) || math.distancesq(position, pose) > maxDeviationSq) {
            position = pose;
        }
        if (!GetIsValid(lastPosition) || math.distancesq(lastPosition, pose) > maxDeviationSq) {
            lastPosition = position;
        }
        if (!GetIsValid(workingPosition) || math.distancesq(workingPosition, pose) > maxDeviationSq) {
            workingPosition = position;
        }
        if (!GetIsValid(desiredLengthToParent)) {
            desiredLengthToParent = 0.1f;
        }
        if (!GetIsValid(worldRadius)) {
            worldRadius = 0.1f;
        }
    }
}

}
