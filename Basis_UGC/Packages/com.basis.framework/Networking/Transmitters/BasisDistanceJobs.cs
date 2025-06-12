using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;


namespace Basis.Scripts.Networking.Transmitters
{
    [BurstCompile]
    public struct BasisDistanceJobs : IJobParallelFor
    {
        public float VoiceDistance;
        public float HearingDistance;
        public float AvatarDistance;
        [ReadOnly]
        public float3 referencePosition;
        [ReadOnly]
        public NativeArray<float3> targetPositions;

        [WriteOnly]
        public NativeArray<float> distances;
        //HysteresisDistance stops this from working  [WriteOnly]
        public NativeArray<bool> DistanceResults;
        //HysteresisDistance stops this from working [WriteOnly]
        public NativeArray<bool> HearingResults;
        //HysteresisDistance stops this from working [WriteOnly]
        public NativeArray<bool> AvatarResults;

        // Shared result for the smallest distance
        [NativeDisableParallelForRestriction]
        public NativeArray<float> smallestDistance;

        // Returns whether the distance is smaller or greater than the boundaryDistance but adds a margin to prevent flapping (hysteresis).
        // We increase margin before exiting and we decrease margin before entering (the margin is a ratio).
        private bool HysteresisDistance(bool wasInside, float sqrDistance, float boundaryDistance, float margin = 0.05f)
        {
            float hysteresis = wasInside ? +margin : -margin;
            return sqrDistance < boundaryDistance * (1f + hysteresis);
        }

        public void Execute(int index)
        {
            // Calculate distance
            Vector3 diff = targetPositions[index] - referencePosition;
            float sqrDistance = diff.sqrMagnitude;
            distances[index] = sqrDistance;

            // Determine boolean results
            DistanceResults[index] = HysteresisDistance(DistanceResults[index], sqrDistance, VoiceDistance);
            HearingResults[index] = HysteresisDistance(HearingResults[index], sqrDistance, HearingDistance);
            AvatarResults[index] = HysteresisDistance(AvatarResults[index], sqrDistance, AvatarDistance);

            // Update the smallest distance (atomic operation to avoid race conditions)
            float currentSmallest = smallestDistance[0];
            if (sqrDistance < currentSmallest)
            {
                smallestDistance[0] = sqrDistance;
            }
        }
    }
}
