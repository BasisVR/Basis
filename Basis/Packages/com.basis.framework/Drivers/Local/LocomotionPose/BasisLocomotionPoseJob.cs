using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
namespace Basis.Scripts.Drivers
{
    public struct BasisLocoPoseSampler
    {
        [ReadOnly] public NativeArray<quaternion> Rotations;
        [ReadOnly] public NativeArray<float3> HipsPositions;
        [ReadOnly] public NativeArray<int> ClipRotationOffset;
        [ReadOnly] public NativeArray<int> ClipHipsOffset;
        [ReadOnly] public NativeArray<int> ClipSampleCount;
        [ReadOnly] public NativeArray<float> ClipLength;
        [ReadOnly] public NativeArray<float3> RestPositions;
        public int NodeCount, HipsNode;
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
        public void Sample(NativeArray<BasisLocoContribution> contributions, int contributionCount, int nodes, NativeArray<float3> outLocalPosition, NativeArray<quaternion> outLocalRotation)
        {
            for (int node = 0; node < nodes; node++)
            {
                float4 accumulated = float4.zero;
                float4 reference = float4.zero;
                for (int c = 0; c < contributionCount; c++)
                {
                    BasisLocoContribution contribution = contributions[c];
                    SampleIndices(contribution.Clip, contribution.Time, out int a, out int b, out float t);
                    int rotationBase = ClipRotationOffset[contribution.Clip];
                    quaternion sampled = math.slerp(Rotations[rotationBase + a * NodeCount + node], Rotations[rotationBase + b * NodeCount + node], t);
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
                outLocalRotation[node] = lengthSq > 1e-10f ? new quaternion(accumulated * math.rsqrt(lengthSq)) : new quaternion(reference);
                outLocalPosition[node] = RestPositions[node];
            }

            float3 hips = float3.zero;
            float totalWeight = 0f;
            for (int c = 0; c < contributionCount; c++)
            {
                BasisLocoContribution contribution = contributions[c];
                SampleIndices(contribution.Clip, contribution.Time, out int a, out int b, out float t);
                int hipsBase = ClipHipsOffset[contribution.Clip];
                hips += contribution.Weight * math.lerp(HipsPositions[hipsBase + a], HipsPositions[hipsBase + b], t);
                totalWeight += contribution.Weight;
            }
            if (totalWeight > 1e-6f && (uint)HipsNode < (uint)outLocalPosition.Length)
            {
                outLocalPosition[HipsNode] = hips / totalWeight;
            }
        }
        public int ClampNodes(int snapshotScales, int outPositions, int outRotations, int outScales)
        {
            int nodes = math.min(NodeCount, RestPositions.Length);
            nodes = math.min(nodes, snapshotScales);
            nodes = math.min(nodes, outPositions);
            nodes = math.min(nodes, outRotations);
            return math.min(nodes, outScales);
        }
    }
    [BurstCompile]
    public struct BasisLocomotionPoseJob : IJob
    {
        [ReadOnly] public NativeArray<BasisLocoContribution> Contributions;
        [ReadOnly] public NativeArray<int> ContributionCount;
        [ReadOnly] public NativeArray<float3> SnapshotScales;
        public BasisLocoPoseSampler Sampler;
        public NativeArray<float3> OutLocalPosition;
        public NativeArray<quaternion> OutLocalRotation;
        public NativeArray<float3> OutLocalScale;
        public void Execute()
        {
            int contributionCount = ContributionCount[0];
            if (contributionCount <= 0)
            {
                return;
            }

            int nodes = Sampler.ClampNodes(SnapshotScales.Length, OutLocalPosition.Length, OutLocalRotation.Length, OutLocalScale.Length);
            Sampler.Sample(Contributions, contributionCount, nodes, OutLocalPosition, OutLocalRotation);
            for (int node = 0; node < nodes; node++)
            {
                OutLocalScale[node] = SnapshotScales[node];
            }
        }
    }
}
