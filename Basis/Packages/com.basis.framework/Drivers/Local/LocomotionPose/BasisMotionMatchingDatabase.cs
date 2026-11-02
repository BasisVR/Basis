using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Searchable motion database derived from the per-avatar locomotion bake. The stock clips are sparse cycles
    /// (walk 1.2 m/s, run 3.6 m/s) while the default move speed is 2.5 m/s, so each entry is a (blended velocity,
    /// cycle phase) pair rather than a raw clip frame. Poses are evaluated from the shared bake, so the database
    /// only adds the blend table and the feature vectors. Everything after the allocations runs on worker threads.
    /// </summary>
    public sealed class BasisMotionMatchingDatabase : IDisposable
    {
        public const int PhasesPerCycle = 24;
        public const int FeatureVectorsPerEntry = 4;
        public const int FeatureFloatCount = FeatureVectorsPerEntry * 4;
        public const int WalkingDirections = 16, WalkingRings = 6;
        public const int CrouchingDirections = 8, CrouchingRings = 2;
        public const int ProneDirections = 8, ProneRings = 2;
        public const float WalkingRingStep = 0.6f, CrouchingRingStep = 0.6f, ProneRingStep = 0.2f;
        public const int WalkingGridStart = 0;
        public const int WalkingGridCount = 1 + WalkingDirections * WalkingRings;
        public const int CrouchingGridStart = WalkingGridStart + WalkingGridCount;
        public const int CrouchingGridCount = 1 + CrouchingDirections * CrouchingRings;
        public const int ProneGridStart = CrouchingGridStart + CrouchingGridCount;
        public const int ProneGridCount = 1 + ProneDirections * ProneRings;
        public const int GridCount = ProneGridStart + ProneGridCount;
        public const int EntryCount = GridCount * PhasesPerCycle;
        public const int MaxGridContributions = BasisMotionMatchingBlend.MaxContributions;
        public const float FootPositionWeight = 0.75f, FootVelocityWeight = 1f, HipsVelocityWeight = 1f;
        public const float StanceHeightBand = 0.45f;
        public const float StanceDirectionShare = 0.25f, StanceDirectionMinSpeedRatio = 0.02f;
        public const byte LeftContactBit = 1, RightContactBit = 2;

        public NativeArray<float2> GridVelocity;
        public NativeArray<float> GridDuration;
        public NativeArray<int> GridContributionOffset, GridContributionCount;
        public NativeArray<BasisLocoContribution> GridContributions;
        public NativeArray<float4> Features;
        public NativeArray<float> FeatureScale;
        public NativeArray<byte> ContactFlags;
        public NativeArray<float> GridStanceSpeed;
        public NativeArray<float> ClipPhaseOffset;
        public NativeArray<float> ClipGroundSpeed;
        public bool Ready { get; private set; }
        NativeArray<int> parents;
        NativeArray<float3> restPositions;
        JobHandle buildHandle;
        bool buildPending;

        /// <summary>
        /// Allocates and schedules the build; nothing here blocks. The bake must stay alive until Tick reports Ready.
        /// Disposing the database joins any running build first.
        /// </summary>
        public static BasisMotionMatchingDatabase ScheduleBuild(BasisLocomotionPoseBake bake, NativeArray<int> sourceParents, NativeArray<float3> sourceRestPositions, int leftFootNode, int rightFootNode, float3 skeletonUp, float unitsPerMeter, NativeArray<float2> childPositions, NativeArray<float> childTimeScales)
        {
            var database = new BasisMotionMatchingDatabase
            {
                GridVelocity = new NativeArray<float2>(GridCount, Allocator.Persistent),
                GridDuration = new NativeArray<float>(GridCount, Allocator.Persistent),
                GridContributionOffset = new NativeArray<int>(GridCount, Allocator.Persistent),
                GridContributionCount = new NativeArray<int>(GridCount, Allocator.Persistent),
                GridContributions = new NativeArray<BasisLocoContribution>(GridCount * MaxGridContributions, Allocator.Persistent),
                Features = new NativeArray<float4>(EntryCount * FeatureVectorsPerEntry, Allocator.Persistent),
                FeatureScale = new NativeArray<float>(3, Allocator.Persistent),
                ContactFlags = new NativeArray<byte>(EntryCount, Allocator.Persistent),
                GridStanceSpeed = new NativeArray<float>(GridCount, Allocator.Persistent),
                ClipPhaseOffset = new NativeArray<float>(BasisLocomotionGraph.ClipCount, Allocator.Persistent),
                ClipGroundSpeed = new NativeArray<float>(BasisLocomotionGraph.ClipCount, Allocator.Persistent),
                parents = new NativeArray<int>(sourceParents, Allocator.Persistent),
                restPositions = new NativeArray<float3>(sourceRestPositions, Allocator.Persistent),
            };
            JobHandle clipPhases = new BasisMotionMatchingClipPhaseJob
            {
                Sampler = bake.CreateSampler(database.restPositions),
                Parents = database.parents,
                SnapshotScales = bake.SnapshotScales,
                LeftFootNode = leftFootNode,
                RightFootNode = rightFootNode,
                SkeletonUp = skeletonUp,
                UnitsPerMeter = unitsPerMeter,
                ChildPositions = childPositions,
                ChildTimeScales = childTimeScales,
                ClipPhaseOffset = database.ClipPhaseOffset,
                ClipGroundSpeed = database.ClipGroundSpeed,
            }.Schedule(BasisLocomotionGraph.JumpClip, 4);
            clipPhases = new BasisMotionMatchingClipAlignJob
            {
                Sampler = bake.CreateSampler(database.restPositions),
                Parents = database.parents,
                SnapshotScales = bake.SnapshotScales,
                LeftFootNode = leftFootNode,
                RightFootNode = rightFootNode,
                ClipPhaseOffset = database.ClipPhaseOffset,
            }.Schedule(clipPhases);
            JobHandle grid = new BasisMotionMatchingGridJob
            {
                ClipLengths = bake.ClipLength,
                ClipGroundSpeed = database.ClipGroundSpeed,
                ChildTimeScales = childTimeScales,
                GridVelocity = database.GridVelocity,
                GridDuration = database.GridDuration,
                GridContributionOffset = database.GridContributionOffset,
                GridContributionCount = database.GridContributionCount,
                GridContributions = database.GridContributions,
            }.Schedule(clipPhases);
            JobHandle features = new BasisMotionMatchingFeatureJob
            {
                Sampler = bake.CreateSampler(database.restPositions),
                ClipPhaseOffset = database.ClipPhaseOffset,
                Parents = database.parents,
                SnapshotScales = bake.SnapshotScales,
                LeftFootNode = leftFootNode,
                RightFootNode = rightFootNode,
                GridDuration = database.GridDuration,
                GridContributionOffset = database.GridContributionOffset,
                GridContributionCount = database.GridContributionCount,
                GridContributions = database.GridContributions,
                Features = database.Features,
            }.Schedule(EntryCount, 32, grid);
            JobHandle contacts = new BasisMotionMatchingContactJob
            {
                Features = database.Features,
                SkeletonUp = skeletonUp,
                ContactFlags = database.ContactFlags,
                GridStanceSpeed = database.GridStanceSpeed,
            }.Schedule(features);
            database.buildHandle = new BasisMotionMatchingNormalizeJob
            {
                Features = database.Features,
                FeatureScale = database.FeatureScale,
            }.Schedule(contacts);
            database.buildPending = true;
            JobHandle.ScheduleBatchedJobs();
            return database;
        }
        static void GetStateShape(BasisLocoState state, out int start, out int directions, out int rings, out float ringStep)
        {
            switch (state)
            {
                case BasisLocoState.Crouching:
                    start = CrouchingGridStart;
                    directions = CrouchingDirections;
                    rings = CrouchingRings;
                    ringStep = CrouchingRingStep;
                    break;
                case BasisLocoState.Prone:
                    start = ProneGridStart;
                    directions = ProneDirections;
                    rings = ProneRings;
                    ringStep = ProneRingStep;
                    break;
                default:
                    start = WalkingGridStart;
                    directions = WalkingDirections;
                    rings = WalkingRings;
                    ringStep = WalkingRingStep;
                    break;
            }
        }
        static int GridAt(int start, int directions, int ring, int direction) => ring <= 0 ? start : start + 1 + (ring - 1) * directions + direction % directions;
        static float DirectionCoordinate(float2 velocity, int directions)
        {
            float angle = math.atan2(velocity.x, velocity.y);
            return (angle < 0f ? angle + 2f * math.PI : angle) / (2f * math.PI) * directions;
        }
        public static int NearestGrid(BasisLocoState state, float2 velocity)
        {
            GetStateShape(state, out int start, out int directions, out int rings, out float ringStep);
            int ring = math.min((int)(math.length(velocity) / ringStep + 0.5f), rings);
            return GridAt(start, directions, ring, (int)(DirectionCoordinate(velocity, directions) + 0.5f));
        }
        /// <summary>The planted foot's travel speed at an arbitrary velocity, bilinear over the ring and direction table.</summary>
        public static float StanceSpeedAt(BasisLocoState state, float2 velocity, in NativeArray<float> gridStanceSpeed)
        {
            GetStateShape(state, out int start, out int directions, out int rings, out float ringStep);
            float ringCoordinate = math.min(math.length(velocity) / ringStep, rings);
            int ring = math.min((int)ringCoordinate, rings - 1);
            float ringAlpha = ringCoordinate - ring;
            float directionCoordinate = DirectionCoordinate(velocity, directions);
            int direction = (int)directionCoordinate;
            float directionAlpha = directionCoordinate - direction;
            float inner = math.lerp(gridStanceSpeed[GridAt(start, directions, ring, direction)], gridStanceSpeed[GridAt(start, directions, ring, direction + 1)], directionAlpha);
            float outer = math.lerp(gridStanceSpeed[GridAt(start, directions, ring + 1, direction)], gridStanceSpeed[GridAt(start, directions, ring + 1, direction + 1)], directionAlpha);
            return math.lerp(inner, outer, ringAlpha);
        }
        public void Tick()
        {
            if (buildPending && buildHandle.IsCompleted)
            {
                Complete();
            }
        }
        public void Complete()
        {
            if (buildPending)
            {
                buildHandle.Complete();
                buildPending = false;
                Ready = true;
            }
        }
        public void Dispose()
        {
            if (buildPending)
            {
                buildHandle.Complete();
                buildPending = false;
            }
            Ready = false;
            if (GridVelocity.IsCreated) GridVelocity.Dispose();
            if (GridDuration.IsCreated) GridDuration.Dispose();
            if (GridContributionOffset.IsCreated) GridContributionOffset.Dispose();
            if (GridContributionCount.IsCreated) GridContributionCount.Dispose();
            if (GridContributions.IsCreated) GridContributions.Dispose();
            if (Features.IsCreated) Features.Dispose();
            if (FeatureScale.IsCreated) FeatureScale.Dispose();
            if (ContactFlags.IsCreated) ContactFlags.Dispose();
            if (GridStanceSpeed.IsCreated) GridStanceSpeed.Dispose();
            if (ClipPhaseOffset.IsCreated) ClipPhaseOffset.Dispose();
            if (ClipGroundSpeed.IsCreated) ClipGroundSpeed.Dispose();
            if (parents.IsCreated) parents.Dispose();
            if (restPositions.IsCreated) restPositions.Dispose();
        }
    }

    /// <summary>
    /// Direction-and-speed interpolation over the clip rings: two neighbouring directions, and within each the speeds
    /// either side. Freeform cartesian weights also mix in clips on the far side of the circle, averaging strides that
    /// have nothing in common, and cost a 17-by-17 scan. Speeds are the ground speed each clip's feet really animate,
    /// not its authored blend position, so a requested velocity selects a blend that actually moves at that velocity.
    /// </summary>
    public static class BasisMotionMatchingBlend
    {
        public const int MaxContributions = 6;
        const int Directions = 8;
        public static int Evaluate(float2 velocity, BasisLocoState state, in NativeArray<float> clipGroundSpeed, in NativeArray<float> childTimeScales, in NativeArray<float> clipLengths, NativeArray<BasisLocoContribution> output, int outputStart, out float duration)
        {
            GetRings(state, out int walkStart, out int runStart, out int idleClip);
            float speed = math.length(velocity);
            int written = outputStart;
            float idleWeight = 0f;
            if (speed < 1e-4f)
            {
                idleWeight = 1f;
            }
            else
            {
                float angle = math.atan2(velocity.x, velocity.y);
                float octant = (angle < 0f ? angle + 2f * math.PI : angle) / (math.PI * 0.25f);
                int first = (int)math.floor(octant) % Directions;
                int second = (first + 1) % Directions;
                float alpha = octant - math.floor(octant);
                written = AddDirection(first, 1f - alpha, speed, walkStart, runStart, clipGroundSpeed, output, written, ref idleWeight);
                written = AddDirection(second, alpha, speed, walkStart, runStart, clipGroundSpeed, output, written, ref idleWeight);
            }
            if (idleWeight > 1e-5f)
            {
                output[written++] = new BasisLocoContribution { Clip = idleClip, Weight = idleWeight };
            }
            // The stride has to come from the moving clips alone. Averaging in the idle's long length would slow the
            // cadence far below what the walk's reduced amplitude needs, and the planted foot would slide.
            float moving = 0f, movingDuration = 0f, idleDuration = 0f;
            for (int i = outputStart; i < written; i++)
            {
                int clip = output[i].Clip;
                float clipDuration = clipLengths[clip] / childTimeScales[clip];
                if (clip == idleClip)
                {
                    idleDuration = clipDuration;
                }
                else
                {
                    moving += output[i].Weight;
                    movingDuration += output[i].Weight * clipDuration;
                }
            }
            duration = moving > 1e-5f ? movingDuration / moving : idleDuration;
            return written - outputStart;
        }
        public static int IdleClip(BasisLocoState state)
        {
            GetRings(state, out int _, out int _, out int idleClip);
            return idleClip;
        }
        static int AddDirection(int direction, float weight, float speed, int walkStart, int runStart, in NativeArray<float> clipGroundSpeed, NativeArray<BasisLocoContribution> output, int written, ref float idleWeight)
        {
            if (weight <= 1e-5f)
            {
                return written;
            }
            int walkClip = walkStart + direction;
            float walkRadius = clipGroundSpeed[walkClip];
            if (speed <= walkRadius || runStart < 0)
            {
                float toWalk = math.saturate(speed / walkRadius);
                idleWeight += weight * (1f - toWalk);
                output[written++] = new BasisLocoContribution { Clip = walkClip, Weight = weight * toWalk };
                return written;
            }
            int runClip = direction == 0 ? runStart : runStart + 1 + direction;
            float toRun = math.saturate((speed - walkRadius) / math.max(clipGroundSpeed[runClip] - walkRadius, 1e-3f));
            if (toRun < 1f)
            {
                output[written++] = new BasisLocoContribution { Clip = walkClip, Weight = weight * (1f - toRun) };
            }
            output[written++] = new BasisLocoContribution { Clip = runClip, Weight = weight * toRun };
            return written;
        }
        static void GetRings(BasisLocoState state, out int walkStart, out int runStart, out int idleClip)
        {
            switch (state)
            {
                case BasisLocoState.Crouching:
                    walkStart = BasisLocomotionGraph.CrouchingClipStart;
                    runStart = -1;
                    idleClip = BasisLocomotionGraph.CrouchingClipStart + 8;
                    break;
                case BasisLocoState.Prone:
                    walkStart = BasisLocomotionGraph.ProneClipStart;
                    runStart = -1;
                    idleClip = BasisLocomotionGraph.ProneClipStart + 8;
                    break;
                default:
                    // Stock child order: 8 walk directions, Run, Idle, then the remaining 7 run directions.
                    walkStart = BasisLocomotionGraph.WalkingClipStart;
                    runStart = BasisLocomotionGraph.WalkingClipStart + 8;
                    idleClip = BasisLocomotionGraph.WalkingClipStart + 9;
                    break;
            }
        }
    }

    public static class BasisMotionMatchingStride
    {
        /// <summary>
        /// The median horizontal speed of a foot's low samples. The height band also catches fast late-swing frames,
        /// which would inflate a mean. Sorts <paramref name="speeds"/> in place.
        /// </summary>
        public static float Median(NativeArray<float> speeds, int count)
        {
            if (count <= 0)
            {
                return 0f;
            }
            for (int i = 1; i < count; i++)
            {
                float value = speeds[i];
                int j = i - 1;
                while (j >= 0 && speeds[j] > value)
                {
                    speeds[j + 1] = speeds[j];
                    j--;
                }
                speeds[j + 1] = value;
            }
            return speeds[count / 2];
        }
    }

    public static class BasisMotionMatchingKinematics
    {
        /// <summary>Forward kinematics in the hips' parent frame; nodes are stored parent-first, ancestors of the hips stay invalid.</summary>
        public static void Forward(int hipsNode, int nodes, NativeArray<int> parents, NativeArray<float3> localPosition, NativeArray<quaternion> localRotation, NativeArray<float3> localScale, NativeArray<float3> worldPosition, NativeArray<quaternion> worldRotation, NativeArray<float3> worldScale, NativeArray<bool> valid)
        {
            for (int node = 0; node < nodes; node++)
            {
                if (node == hipsNode)
                {
                    worldPosition[node] = localPosition[node];
                    worldRotation[node] = localRotation[node];
                    worldScale[node] = localScale[node];
                    valid[node] = true;
                    continue;
                }
                int parent = parents[node];
                if (parent < 0 || !valid[parent])
                {
                    valid[node] = false;
                    continue;
                }
                worldPosition[node] = worldPosition[parent] + math.mul(worldRotation[parent], worldScale[parent] * localPosition[node]);
                worldRotation[node] = math.mul(worldRotation[parent], localRotation[node]);
                worldScale[node] = worldScale[parent] * localScale[node];
                valid[node] = true;
            }
        }
    }

    /// <summary>
    /// The blend tree assumes every clip plants its feet at the same normalized time. Where they do not, blending
    /// averages mismatched strides and the feet slide, so this finds where each cycle's left foot is planted and
    /// the runtime offsets clip time so those moments coincide. Clips with no real foot motion stay at zero.
    /// </summary>
    [BurstCompile]
    public struct BasisMotionMatchingClipPhaseJob : IJobParallelFor
    {
        const int Samples = 48;
        public const float MinFootTravelRatio = 0.02f;
        public const float MinCredibleSpeedRatio = 0.5f, MaxCredibleSpeedRatio = 1.6f;
        // Calibrated against StockClips_BeatTheGraphOnFidelity. The stock character's per-clip reading runs about
        // 0.76 to 0.89 m, so its fallback speeds land slightly above authored, which measured best.
        const float AuthoredHipsHeight = 0.76f;
        public BasisLocoPoseSampler Sampler;
        [ReadOnly] public NativeArray<int> Parents;
        [ReadOnly] public NativeArray<float3> SnapshotScales;
        public int LeftFootNode, RightFootNode;
        public float3 SkeletonUp;
        public float UnitsPerMeter;
        [ReadOnly] public NativeArray<float2> ChildPositions;
        [ReadOnly] public NativeArray<float> ChildTimeScales;
        [NativeDisableParallelForRestriction] public NativeArray<float> ClipPhaseOffset;
        [NativeDisableParallelForRestriction] public NativeArray<float> ClipGroundSpeed;
        public void Execute(int clip)
        {
            int nodes = Sampler.NodeCount;
            var contributions = new NativeArray<BasisLocoContribution>(1, Allocator.Temp);
            var localPosition = new NativeArray<float3>(nodes, Allocator.Temp);
            var localRotation = new NativeArray<quaternion>(nodes, Allocator.Temp);
            var worldPosition = new NativeArray<float3>(nodes, Allocator.Temp);
            var worldRotation = new NativeArray<quaternion>(nodes, Allocator.Temp);
            var worldScale = new NativeArray<float3>(nodes, Allocator.Temp);
            var valid = new NativeArray<bool>(nodes, Allocator.Temp);
            var left = new NativeArray<float3>(Samples, Allocator.Temp);
            var right = new NativeArray<float3>(Samples, Allocator.Temp);
            float hipsHeight = 0f;
            for (int sample = 0; sample < Samples; sample++)
            {
                contributions[0] = new BasisLocoContribution { Clip = clip, Weight = 1f, Time = sample / (float)Samples * Sampler.ClipLength[clip] };
                Sampler.Sample(contributions, 1, nodes, localPosition, localRotation);
                BasisMotionMatchingKinematics.Forward(Sampler.HipsNode, nodes, Parents, localPosition, localRotation, SnapshotScales, worldPosition, worldRotation, worldScale, valid);
                left[sample] = worldPosition[LeftFootNode];
                right[sample] = worldPosition[RightFootNode];
                hipsHeight = math.max(hipsHeight, math.dot(worldPosition[Sampler.HipsNode], SkeletonUp) - math.dot(left[sample], SkeletonUp));
            }

            // A cycle's speed scales with leg length, so a small avatar's authored figures must scale down with it.
            float hipsHeightMeters = hipsHeight / UnitsPerMeter;
            float authoredSpeed = math.max(math.length(ChildPositions[clip]) * hipsHeightMeters / AuthoredHipsHeight, 1e-3f);
            float cycleSeconds = Sampler.ClipLength[clip] / ChildTimeScales[clip];
            bool leftMoves = MeasureStance(left, hipsHeight, out float leftPhase);
            bool rightMoves = MeasureStance(right, hipsHeight, out float _);
            ClipPhaseOffset[clip] = leftMoves ? leftPhase : 0f;
            if (!leftMoves || !rightMoves)
            {
                ClipGroundSpeed[clip] = authoredSpeed;
                return;
            }
            var speeds = new NativeArray<float>(Samples * 2, Allocator.Temp);
            int count = CollectStanceSpeeds(left, cycleSeconds, speeds, 0);
            count = CollectStanceSpeeds(right, cycleSeconds, speeds, count);
            float measured = BasisMotionMatchingStride.Median(speeds, count) / UnitsPerMeter;
            // Sidesteps barely travel while low, so their reading is meaningless; trust the author when it is far out.
            bool credible = measured >= authoredSpeed * MinCredibleSpeedRatio && measured <= authoredSpeed * MaxCredibleSpeedRatio;
            ClipGroundSpeed[clip] = credible ? measured : authoredSpeed;
        }
        int CollectStanceSpeeds(NativeArray<float3> foot, float cycleSeconds, NativeArray<float> speeds, int count)
        {
            float minimum = float.MaxValue, maximum = float.MinValue;
            for (int sample = 0; sample < Samples; sample++)
            {
                float height = math.dot(foot[sample], SkeletonUp);
                minimum = math.min(minimum, height);
                maximum = math.max(maximum, height);
            }
            float limit = minimum + (maximum - minimum) * BasisMotionMatchingDatabase.StanceHeightBand;
            float inverseSpan = Samples / (2f * cycleSeconds);
            for (int sample = 0; sample < Samples; sample++)
            {
                if (math.dot(foot[sample], SkeletonUp) <= limit)
                {
                    float3 velocity = (foot[(sample + 1) % Samples] - foot[(sample + Samples - 1) % Samples]) * inverseSpan;
                    speeds[count++] = math.length(velocity - SkeletonUp * math.dot(velocity, SkeletonUp));
                }
            }
            return count;
        }
        /// <summary>The middle of the foot's lowest stretch of the cycle, as a stance is a plateau and its single lowest point is noise.</summary>
        bool MeasureStance(NativeArray<float3> foot, float hipsHeight, out float phase)
        {
            phase = 0f;
            float minimum = float.MaxValue, maximum = float.MinValue;
            int lowest = 0;
            for (int sample = 0; sample < Samples; sample++)
            {
                float height = math.dot(foot[sample], SkeletonUp);
                if (height < minimum)
                {
                    minimum = height;
                    lowest = sample;
                }
                maximum = math.max(maximum, height);
            }
            if (maximum - minimum < hipsHeight * MinFootTravelRatio)
            {
                return false;
            }
            float limit = minimum + (maximum - minimum) * BasisMotionMatchingDatabase.StanceHeightBand;
            int before = 0, after = 0;
            while (before < Samples / 2 && math.dot(foot[(lowest - before - 1 + Samples) % Samples], SkeletonUp) <= limit)
            {
                before++;
            }
            while (after < Samples / 2 && math.dot(foot[(lowest + after + 1) % Samples], SkeletonUp) <= limit)
            {
                after++;
            }
            phase = math.frac((lowest + (after - before) * 0.5f) / Samples);
            return true;
        }
    }

    /// <summary>
    /// A foot's lowest point says little about a sidestep, and a blend of two strafes half a cycle apart cancels its own stride.
    /// So each cycle is slid along its neighbour until both feet's whole paths agree: around the walk ring, then each
    /// run onto the walk going the same way, then crouch and prone onto the stance before them.
    /// </summary>
    [BurstCompile]
    public struct BasisMotionMatchingClipAlignJob : IJob
    {
        const int Samples = 48, Directions = 8;
        public BasisLocoPoseSampler Sampler;
        [ReadOnly] public NativeArray<int> Parents;
        [ReadOnly] public NativeArray<float3> SnapshotScales;
        public int LeftFootNode, RightFootNode;
        public NativeArray<float> ClipPhaseOffset;
        public void Execute()
        {
            int nodes = Sampler.NodeCount;
            var tracks = new NativeArray<float3>(BasisLocomotionGraph.JumpClip * Samples * 2, Allocator.Temp);
            var contributions = new NativeArray<BasisLocoContribution>(1, Allocator.Temp);
            var localPosition = new NativeArray<float3>(nodes, Allocator.Temp);
            var localRotation = new NativeArray<quaternion>(nodes, Allocator.Temp);
            var worldPosition = new NativeArray<float3>(nodes, Allocator.Temp);
            var worldRotation = new NativeArray<quaternion>(nodes, Allocator.Temp);
            var worldScale = new NativeArray<float3>(nodes, Allocator.Temp);
            var valid = new NativeArray<bool>(nodes, Allocator.Temp);
            for (int clip = 0; clip < BasisLocomotionGraph.JumpClip; clip++)
            {
                for (int sample = 0; sample < Samples; sample++)
                {
                    contributions[0] = new BasisLocoContribution { Clip = clip, Weight = 1f, Time = sample / (float)Samples * Sampler.ClipLength[clip] };
                    Sampler.Sample(contributions, 1, nodes, localPosition, localRotation);
                    BasisMotionMatchingKinematics.Forward(Sampler.HipsNode, nodes, Parents, localPosition, localRotation, SnapshotScales, worldPosition, worldRotation, worldScale, valid);
                    float3 hips = worldPosition[Sampler.HipsNode];
                    tracks[(clip * Samples + sample) * 2] = worldPosition[LeftFootNode] - hips;
                    tracks[(clip * Samples + sample) * 2 + 1] = worldPosition[RightFootNode] - hips;
                }
            }
            int walk = BasisLocomotionGraph.WalkingClipStart, run = walk + 8, crouch = BasisLocomotionGraph.CrouchingClipStart, prone = BasisLocomotionGraph.ProneClipStart;
            for (int direction = 1; direction < Directions; direction++)
            {
                Align(tracks, walk + direction, walk + direction - 1);
            }
            for (int direction = 0; direction < Directions; direction++)
            {
                Align(tracks, direction == 0 ? run : run + 1 + direction, walk + direction);
                Align(tracks, crouch + direction, direction == 0 ? walk : crouch + direction - 1);
                Align(tracks, prone + direction, direction == 0 ? crouch : prone + direction - 1);
            }
        }
        void Align(NativeArray<float3> tracks, int clip, int reference)
        {
            float referenceOffset = ClipPhaseOffset[reference];
            float bestCost = float.MaxValue, bestOffset = ClipPhaseOffset[clip];
            for (int shift = 0; shift < Samples; shift++)
            {
                float offset = shift / (float)Samples;
                float cost = 0f;
                for (int sample = 0; sample < Samples; sample++)
                {
                    float phase = sample / (float)Samples;
                    int a = (reference * Samples + (int)(math.frac(phase + referenceOffset) * Samples + 0.5f) % Samples) * 2;
                    int b = (clip * Samples + (int)(math.frac(phase + offset) * Samples + 0.5f) % Samples) * 2;
                    cost += math.distancesq(tracks[a], tracks[b]) + math.distancesq(tracks[a + 1], tracks[b + 1]);
                }
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestOffset = offset;
                }
            }
            ClipPhaseOffset[clip] = bestOffset;
        }
    }

    [BurstCompile]
    public struct BasisMotionMatchingGridJob : IJob
    {
        [ReadOnly] public NativeArray<float> ClipLengths;
        [ReadOnly] public NativeArray<float> ClipGroundSpeed;
        [ReadOnly] public NativeArray<float> ChildTimeScales;
        public NativeArray<float2> GridVelocity;
        public NativeArray<float> GridDuration;
        public NativeArray<int> GridContributionOffset, GridContributionCount;
        public NativeArray<BasisLocoContribution> GridContributions;
        public void Execute()
        {
            int written = 0;
            written = BuildState(BasisLocoState.Walking, BasisMotionMatchingDatabase.WalkingGridStart, BasisMotionMatchingDatabase.WalkingDirections, BasisMotionMatchingDatabase.WalkingRings, BasisMotionMatchingDatabase.WalkingRingStep, written);
            written = BuildState(BasisLocoState.Crouching, BasisMotionMatchingDatabase.CrouchingGridStart, BasisMotionMatchingDatabase.CrouchingDirections, BasisMotionMatchingDatabase.CrouchingRings, BasisMotionMatchingDatabase.CrouchingRingStep, written);
            BuildState(BasisLocoState.Prone, BasisMotionMatchingDatabase.ProneGridStart, BasisMotionMatchingDatabase.ProneDirections, BasisMotionMatchingDatabase.ProneRings, BasisMotionMatchingDatabase.ProneRingStep, written);
        }
        int BuildState(BasisLocoState state, int gridStart, int directions, int rings, float ringStep, int written)
        {
            int grid = gridStart;
            written = BuildPoint(state, grid++, float2.zero, written);
            for (int ring = 1; ring <= rings; ring++)
            {
                for (int direction = 0; direction < directions; direction++)
                {
                    float angle = 2f * math.PI * direction / directions;
                    float2 velocity = new float2(math.sin(angle), math.cos(angle)) * (ring * ringStep);
                    written = BuildPoint(state, grid++, velocity, written);
                }
            }
            return written;
        }
        int BuildPoint(BasisLocoState state, int grid, float2 velocity, int written)
        {
            int first = written;
            written += BasisMotionMatchingBlend.Evaluate(velocity, state, ClipGroundSpeed, ChildTimeScales, ClipLengths, GridContributions, first, out float duration);

            GridVelocity[grid] = velocity;
            GridDuration[grid] = math.max(duration, 1e-3f);
            GridContributionOffset[grid] = first;
            GridContributionCount[grid] = written - first;
            return written;
        }
    }

    [BurstCompile]
    public struct BasisMotionMatchingFeatureJob : IJobParallelFor
    {
        public BasisLocoPoseSampler Sampler;
        [ReadOnly] public NativeArray<float> ClipPhaseOffset;
        [ReadOnly] public NativeArray<int> Parents;
        [ReadOnly] public NativeArray<float3> SnapshotScales;
        public int LeftFootNode, RightFootNode;
        [ReadOnly] public NativeArray<float> GridDuration;
        [ReadOnly] public NativeArray<int> GridContributionOffset, GridContributionCount;
        [ReadOnly] public NativeArray<BasisLocoContribution> GridContributions;
        [NativeDisableParallelForRestriction] public NativeArray<float4> Features;
        public void Execute(int entry)
        {
            int grid = entry / BasisMotionMatchingDatabase.PhasesPerCycle;
            float phaseStep = 1f / BasisMotionMatchingDatabase.PhasesPerCycle;
            float phase = (entry % BasisMotionMatchingDatabase.PhasesPerCycle) * phaseStep;
            int nodes = Sampler.NodeCount;

            var contributions = new NativeArray<BasisLocoContribution>(BasisMotionMatchingDatabase.MaxGridContributions, Allocator.Temp);
            var localPosition = new NativeArray<float3>(nodes, Allocator.Temp);
            var localRotation = new NativeArray<quaternion>(nodes, Allocator.Temp);
            var worldPosition = new NativeArray<float3>(nodes, Allocator.Temp);
            var worldRotation = new NativeArray<quaternion>(nodes, Allocator.Temp);
            var worldScale = new NativeArray<float3>(nodes, Allocator.Temp);
            var valid = new NativeArray<bool>(nodes, Allocator.Temp);

            Evaluate(grid, phase, contributions, localPosition, localRotation, worldPosition, worldRotation, worldScale, valid, out float3 left, out float3 right, out float3 hips);
            Evaluate(grid, phase - phaseStep, contributions, localPosition, localRotation, worldPosition, worldRotation, worldScale, valid, out float3 leftBefore, out float3 rightBefore, out float3 hipsBefore);
            Evaluate(grid, phase + phaseStep, contributions, localPosition, localRotation, worldPosition, worldRotation, worldScale, valid, out float3 leftAfter, out float3 rightAfter, out float3 hipsAfter);

            float inverseSpan = math.rcp(2f * phaseStep * GridDuration[grid]);
            float3 leftVelocity = (leftAfter - leftBefore) * inverseSpan;
            float3 rightVelocity = (rightAfter - rightBefore) * inverseSpan;
            float3 hipsVelocity = (hipsAfter - hipsBefore) * inverseSpan;
            int offset = entry * BasisMotionMatchingDatabase.FeatureVectorsPerEntry;
            Features[offset + 0] = new float4(left, right.x);
            Features[offset + 1] = new float4(right.yz, leftVelocity.xy);
            Features[offset + 2] = new float4(leftVelocity.z, rightVelocity);
            Features[offset + 3] = new float4(hipsVelocity, 0f);
        }
        void Evaluate(int grid, float phase, NativeArray<BasisLocoContribution> contributions, NativeArray<float3> localPosition, NativeArray<quaternion> localRotation, NativeArray<float3> worldPosition, NativeArray<quaternion> worldRotation, NativeArray<float3> worldScale, NativeArray<bool> valid, out float3 leftFoot, out float3 rightFoot, out float3 hips)
        {
            float wrapped = phase - math.floor(phase);
            int count = GridContributionCount[grid];
            int offset = GridContributionOffset[grid];
            for (int c = 0; c < count; c++)
            {
                BasisLocoContribution contribution = GridContributions[offset + c];
                contribution.Time = math.frac(wrapped + ClipPhaseOffset[contribution.Clip]) * Sampler.ClipLength[contribution.Clip];
                contributions[c] = contribution;
            }
            int nodes = Sampler.NodeCount;
            Sampler.Sample(contributions, count, nodes, localPosition, localRotation);

            BasisMotionMatchingKinematics.Forward(Sampler.HipsNode, nodes, Parents, localPosition, localRotation, SnapshotScales, worldPosition, worldRotation, worldScale, valid);
            leftFoot = worldPosition[LeftFootNode];
            rightFoot = worldPosition[RightFootNode];
            hips = worldPosition[Sampler.HipsNode];
        }
    }

    /// <summary>
    /// Runs on the raw features, before normalization. The stock clips have no root motion, so a foot's speed says
    /// nothing about whether it is planted; stance is the low part of each foot's height range over its cycle.
    /// </summary>
    [BurstCompile]
    public struct BasisMotionMatchingContactJob : IJob
    {
        [ReadOnly] public NativeArray<float4> Features;
        public float3 SkeletonUp;
        public NativeArray<byte> ContactFlags;
        public NativeArray<float> GridStanceSpeed;
        public void Execute()
        {
            int phases = BasisMotionMatchingDatabase.PhasesPerCycle;
            for (int grid = 0; grid < BasisMotionMatchingDatabase.GridCount; grid++)
            {
                float leftMin = float.MaxValue, leftMax = float.MinValue, rightMin = float.MaxValue, rightMax = float.MinValue;
                for (int phase = 0; phase < phases; phase++)
                {
                    Read(grid * phases + phase, out float3 left, out float3 right, out float3 leftVelocity, out float3 rightVelocity);
                    float leftHeight = math.dot(left, SkeletonUp), rightHeight = math.dot(right, SkeletonUp);
                    leftMin = math.min(leftMin, leftHeight);
                    leftMax = math.max(leftMax, leftHeight);
                    rightMin = math.min(rightMin, rightHeight);
                    rightMax = math.max(rightMax, rightHeight);
                }
                float leftLimit = leftMin + (leftMax - leftMin) * BasisMotionMatchingDatabase.StanceHeightBand;
                float rightLimit = rightMin + (rightMax - rightMin) * BasisMotionMatchingDatabase.StanceHeightBand;
                var speeds = new NativeArray<float>(phases * 2, Allocator.Temp);
                var axis = new NativeArray<float>(phases, Allocator.Temp);
                float3 leftStance = StanceVelocity(grid, true, leftLimit, axis), rightStance = StanceVelocity(grid, false, rightLimit, axis);
                int speedCount = 0;
                for (int phase = 0; phase < phases; phase++)
                {
                    int entry = grid * phases + phase;
                    Read(entry, out float3 left, out float3 right, out float3 leftVelocity, out float3 rightVelocity);
                    byte flags = 0;
                    if (math.dot(left, SkeletonUp) <= leftLimit && MovesWithStance(leftVelocity, leftStance))
                    {
                        flags |= BasisMotionMatchingDatabase.LeftContactBit;
                        speeds[speedCount++] = math.length(Horizontal(leftVelocity));
                    }
                    if (math.dot(right, SkeletonUp) <= rightLimit && MovesWithStance(rightVelocity, rightStance))
                    {
                        flags |= BasisMotionMatchingDatabase.RightContactBit;
                        speeds[speedCount++] = math.length(Horizontal(rightVelocity));
                    }
                    ContactFlags[entry] = flags;
                }
                GridStanceSpeed[grid] = BasisMotionMatchingStride.Median(speeds, speedCount);
            }
        }
        float3 Horizontal(float3 velocity) => velocity - SkeletonUp * math.dot(velocity, SkeletonUp);
        // The low part of a cycle also holds the start and end of the swing, where the foot travels the wrong way.
        // Taking the median per axis finds the stance direction without knowing how this rig's frame is oriented.
        float3 StanceVelocity(int grid, bool isLeft, float limit, NativeArray<float> values)
        {
            float3 result = float3.zero;
            for (int component = 0; component < 3; component++)
            {
                int count = 0;
                for (int phase = 0; phase < BasisMotionMatchingDatabase.PhasesPerCycle; phase++)
                {
                    Read(grid * BasisMotionMatchingDatabase.PhasesPerCycle + phase, out float3 left, out float3 right, out float3 leftVelocity, out float3 rightVelocity);
                    if (math.dot(isLeft ? left : right, SkeletonUp) <= limit)
                    {
                        values[count++] = Horizontal(isLeft ? leftVelocity : rightVelocity)[component];
                    }
                }
                result[component] = BasisMotionMatchingStride.Median(values, count);
            }
            return result;
        }
        static bool MovesWithStance(float3 velocity, float3 stance)
        {
            float stanceSq = math.lengthsq(stance);
            return stanceSq < BasisMotionMatchingDatabase.StanceDirectionMinSpeedRatio * BasisMotionMatchingDatabase.StanceDirectionMinSpeedRatio || math.dot(velocity, stance) >= stanceSq * BasisMotionMatchingDatabase.StanceDirectionShare;
        }
        void Read(int entry, out float3 left, out float3 right, out float3 leftVelocity, out float3 rightVelocity)
        {
            int offset = entry * BasisMotionMatchingDatabase.FeatureVectorsPerEntry;
            float4 f0 = Features[offset], f1 = Features[offset + 1], f2 = Features[offset + 2];
            left = f0.xyz;
            right = new float3(f0.w, f1.xy);
            leftVelocity = new float3(f1.zw, f2.x);
            rightVelocity = f2.yzw;
        }
    }

    [BurstCompile]
    public struct BasisMotionMatchingNormalizeJob : IJob
    {
        public NativeArray<float4> Features;
        public NativeArray<float> FeatureScale;
        public void Execute()
        {
            FeatureScale[0] = NormalizeGroup(0, 6, BasisMotionMatchingDatabase.FootPositionWeight);
            FeatureScale[1] = NormalizeGroup(6, 12, BasisMotionMatchingDatabase.FootVelocityWeight);
            FeatureScale[2] = NormalizeGroup(12, 15, BasisMotionMatchingDatabase.HipsVelocityWeight);
        }
        float NormalizeGroup(int firstFloat, int endFloat, float weight)
        {
            // Precomputing weight / deviation into the data leaves a plain squared distance for the search to run.
            int entries = BasisMotionMatchingDatabase.EntryCount;
            float variance = 0f;
            for (int dimension = firstFloat; dimension < endFloat; dimension++)
            {
                float mean = 0f;
                for (int entry = 0; entry < entries; entry++)
                {
                    mean += Read(entry, dimension);
                }
                mean /= entries;
                float sum = 0f;
                for (int entry = 0; entry < entries; entry++)
                {
                    float difference = Read(entry, dimension) - mean;
                    sum += difference * difference;
                }
                variance += sum / entries;
            }
            variance /= endFloat - firstFloat;
            float scale = math.sqrt(weight) / math.max(math.sqrt(variance), 1e-4f);
            for (int dimension = firstFloat; dimension < endFloat; dimension++)
            {
                for (int entry = 0; entry < entries; entry++)
                {
                    int index = entry * BasisMotionMatchingDatabase.FeatureVectorsPerEntry + (dimension >> 2);
                    float4 vector = Features[index];
                    vector[dimension & 3] *= scale;
                    Features[index] = vector;
                }
            }
            return scale;
        }
        float Read(int entry, int dimension)
        {
            return Features[entry * BasisMotionMatchingDatabase.FeatureVectorsPerEntry + (dimension >> 2)][dimension & 3];
        }
    }
}
