using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Blends the baked locomotion samples into the pose skeleton stream — the exact arrays GatherNow
    /// would have filled from transforms after an Animator evaluate. Pure NativeArray math: while this
    /// runs on a worker the main thread may do anything except touch the stream.
    /// </summary>
    [BurstCompile]
    public struct BasisLocomotionPoseJob : IJob
    {
        [ReadOnly] public NativeArray<BasisLocoContribution> Contributions;
        public int ContributionCount;

        [ReadOnly] public NativeArray<quaternion> Rotations;
        [ReadOnly] public NativeArray<float3> HipsPositions;
        [ReadOnly] public NativeArray<float3> SnapshotScales;
        [ReadOnly] public NativeArray<int> ClipRotationOffset;
        [ReadOnly] public NativeArray<int> ClipHipsOffset;
        [ReadOnly] public NativeArray<int> ClipSampleCount;
        [ReadOnly] public NativeArray<float> ClipLength;
        [ReadOnly] public NativeArray<float3> RestPositions;

        public int NodeCount;
        public int HipsNode;

        public NativeArray<float3> OutLocalPosition;
        public NativeArray<quaternion> OutLocalRotation;
        public NativeArray<float3> OutLocalScale;

        void SampleIndices(int clip, float time, out int a, out int b, out float t)
        {
            int samples = ClipSampleCount[clip];
            float length = ClipLength[clip];
            float u = time / length;
            u = math.select(math.clamp(u, 0f, 1f), 0f, math.isnan(u));
            float x = u * (samples - 1);
            a = (int)math.floor(x);
            b = math.min(a + 1, samples - 1);
            t = x - a;
        }

        public void Execute()
        {
            if (ContributionCount <= 0)
            {
                return;
            }

            int nodes = NodeCount;
            nodes = math.min(nodes, RestPositions.Length);
            nodes = math.min(nodes, SnapshotScales.Length);
            nodes = math.min(nodes, OutLocalPosition.Length);
            nodes = math.min(nodes, OutLocalRotation.Length);
            nodes = math.min(nodes, OutLocalScale.Length);

            for (int node = 0; node < nodes; node++)
            {
                float4 accumulated = float4.zero;
                float4 reference = float4.zero;
                for (int c = 0; c < ContributionCount; c++)
                {
                    BasisLocoContribution contribution = Contributions[c];
                    SampleIndices(contribution.Clip, contribution.Time, out int a, out int b, out float t);
                    int rotationBase = ClipRotationOffset[contribution.Clip];
                    quaternion sampled = math.slerp(
                        Rotations[rotationBase + a * NodeCount + node],
                        Rotations[rotationBase + b * NodeCount + node],
                        t);
                    float4 value = sampled.value;
                    if (c == 0)
                    {
                        reference = value;
                    }
                    else if (math.dot(value, reference) < 0f)
                    {
                        value = -value;
                    }
                    accumulated += contribution.Weight * value;
                }

                float lengthSq = math.lengthsq(accumulated);
                OutLocalRotation[node] = lengthSq > 1e-10f
                    ? new quaternion(accumulated * math.rsqrt(lengthSq))
                    : new quaternion(reference);
                OutLocalScale[node] = SnapshotScales[node];
                OutLocalPosition[node] = RestPositions[node];
            }

            float3 hips = float3.zero;
            float totalWeight = 0f;
            for (int c = 0; c < ContributionCount; c++)
            {
                BasisLocoContribution contribution = Contributions[c];
                SampleIndices(contribution.Clip, contribution.Time, out int a, out int b, out float t);
                int hipsBase = ClipHipsOffset[contribution.Clip];
                hips += contribution.Weight * math.lerp(HipsPositions[hipsBase + a], HipsPositions[hipsBase + b], t);
                totalWeight += contribution.Weight;
            }
            if (totalWeight > 1e-6f && (uint)HipsNode < (uint)OutLocalPosition.Length)
            {
                OutLocalPosition[HipsNode] = hips / totalWeight;
            }
        }
    }
}
