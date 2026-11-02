using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
namespace Basis.Scripts.Drivers
{
    public struct BasisMotionMatchingLegNodes
    {
        public int Upper, Lower, Foot;
    }
    public struct BasisMotionMatchingFootLock
    {
        public bool Locked, HasLastInput, HasBendAxis, WaitForLift;
        public float3 LockWorld, LastInputWorld, OffsetWorld, OffsetVelocity, BendAxis, TargetWorld;
        public float TargetWeight, TargetRamp;
    }
    public struct BasisMotionMatchingTuning
    {
        public float MaxStrideWarp, InertializeHalfLife, BlendHalfLife, BlendHalfLifeSudden, BlendLookahead, FootBlendHalfLife, FootLockMaxSpeed, FootUnlockDistance, MaxJointDegreesPerSecond, StopHoldSeconds;
        public static BasisMotionMatchingTuning Default => new BasisMotionMatchingTuning
        {
            // Chosen by sweeping StockClips_BeatTheGraphOnFidelity.
            InertializeHalfLife = 0.15f,
            BlendHalfLife = 0.1f,
            BlendHalfLifeSudden = 0.16f,
            BlendLookahead = 0.1f,
            FootBlendHalfLife = 0.15f,
            FootLockMaxSpeed = 2f,
            FootUnlockDistance = 0.3f,
            // Above natural motion on purpose: capping the stock run at 2000 deg/s raised its foot jitter by 25%.
            // This only catches one-frame pops.
            MaxJointDegreesPerSecond = 3000f,
            StopHoldSeconds = 0.3f,
            MaxStrideWarp = 2.5f,
        };
    }
    public struct BasisMotionMatchingState
    {
        public BasisLocoState LocoState;
        public int Grid;
        public float Norm, IdleTime, StateTime, StallTimer, PlaybackRate, StopTimer, StrideWarp;
        public float2 BlendVelocity, BlendAcceleration;
        public bool Seeded, NeedsPhase, StopLanded;
        public float3 PreviousHips, PreviousHipsVelocity, OffsetHips, OffsetHipsVelocity;
        public float3 LastLeftFoot, LastRightFoot, LastHips, LeftFootVelocity, RightFootVelocity, HipsVelocity;
        public float InertializeTime;
        public int TransitionCount, PhaseSearchCount;
        public BasisMotionMatchingFootLock LeftFoot, RightFoot;
        public static BasisMotionMatchingState Default => new BasisMotionMatchingState
        {
            LocoState = BasisLocoState.Walking,
            StateTime = BasisMotionMatchingJob.MinStateSeconds,
            InertializeTime = float.MaxValue,
            PlaybackRate = 1f,
            StrideWarp = 1f,
            NeedsPhase = true,
        };
    }

    /// <summary>
    /// One worker-thread hop per frame: state machine, velocity prediction, blend evaluation, pose sampling and
    /// inertialization. The stock clips are cycles, so every pose the database could offer is a blend of them at some
    /// phase. Blending at the exact velocity each frame is therefore always at least as close as the nearest entry and
    /// never steps; the search only runs when the phase is free, which is when leaving rest or a one-shot.
    /// Search and inertialization are adapted from JLPM22/MotionMatching; see THIRD_PARTY_NOTICES.md.
    /// </summary>
    [BurstCompile]
    public struct BasisMotionMatchingJob : IJob
    {
        public const float TrajectoryHalfLife = 0.15f;
        // The graph spends this long crossfading into a state before it evaluates the next transition. Without a
        // matching dwell, an instant switch would leave the landing clip after a single frame.
        public const float MinStateSeconds = 0.25f;
        public const float StallSpeedRatio = 0.25f, StallSeconds = 0.3f, StallMinDesiredSpeed = 0.1f;
        public const float RestBlendSpeed = 0.1f, PlaybackRateHalfLife = 0.1f, StrideWarpHalfLife = 0.15f, StrideWarpMinSpeed = 0.3f, WarpReachRatio = 0.97f;
        public const float SuddenInputSpeed = 3f, StopHoldSpeed = 0.5f, FootTargetBlendSeconds = 0.15f, MinSwingHalfLife = 0.08f;
        public const float MinPlaybackRate = 0.75f, MaxPlaybackRate = 1.35f;
        public const float FootTeleportLegRatio = 1.5f, FootLockMinBlendSpeed = 0.1f, FootSideFraction = 0.3f, OffsetCatchUpLegsPerSecond = 2.5f;
        public const float KneePlaneMinSine = 0.1f;
        // Near full extension a tiny foot movement swings the knee by a large angle, so stop just short of it.
        public const float MaxLegExtension = 0.995f;
        const float TargetVelocitySeconds = 1f / 60f;
        const float Ln2 = 0.69314718056f;

        public BasisLocoParams Params;
        public float DeltaTime;
        public BasisLocoPoseSampler Sampler;
        public float SkeletonUnitsPerMeter;
        public bool FootLockEnabled, InertializationEnabled;
        public quaternion FacingToSkeleton;
        public BasisMotionMatchingTuning Tuning;
        public BasisMotionMatchingLegNodes LeftLeg, RightLeg;
        public float4x4 SkeletonToWorld, WorldToSkeleton;
        [ReadOnly] public NativeArray<int> Parents;
        [ReadOnly] public NativeArray<byte> ContactFlags;
        [ReadOnly] public NativeArray<float> GridStanceSpeed;
        [ReadOnly] public NativeArray<float> ClipPhaseOffset;
        [ReadOnly] public NativeArray<byte> ClipLooping;
        [ReadOnly] public NativeArray<float3> SnapshotScales;
        [ReadOnly] public NativeArray<float> ClipGroundSpeed;
        [ReadOnly] public NativeArray<float> ChildTimeScales;
        [ReadOnly] public NativeArray<float> FeatureScale;
        [ReadOnly] public NativeArray<float4> Features;
        public NativeArray<BasisMotionMatchingState> State;
        public NativeArray<byte> LandingLatch;
        public NativeArray<BasisLocoContribution> Contributions;
        public NativeArray<float3> ScratchPosition;
        public NativeArray<quaternion> ScratchRotation;
        public NativeArray<quaternion> PreviousRotation, OffsetRotation, FinalRotation;
        public NativeArray<float3> PreviousAngularVelocity, OffsetAngularVelocity;
        public NativeArray<float3> OutLocalPosition;
        public NativeArray<quaternion> OutLocalRotation;
        public NativeArray<float3> OutLocalScale;

        public void Execute()
        {
            BasisMotionMatchingState state = State[0];
            BasisLocoParams parameters = Params;
            parameters.LandingTrigger = LandingLatch[0] != 0;
            float deltaTime = DeltaTime;
            bool wasSeeded = state.Seeded;
            bool transition = false;
            if (deltaTime > 0f)
            {
                transition = Advance(ref state, ref parameters, deltaTime);
            }

            int nodes = Sampler.ClampNodes(SnapshotScales.Length, OutLocalPosition.Length, OutLocalRotation.Length, OutLocalScale.Length);
            int count = Emit(in state, state.Norm, state.IdleTime, out float _);
            Sampler.Sample(Contributions, count, nodes, OutLocalPosition, OutLocalRotation);
            for (int node = 0; node < nodes; node++)
            {
                OutLocalScale[node] = SnapshotScales[node];
            }
            Inertialize(ref state, in parameters, transition, deltaTime, nodes);

            var worldPosition = new NativeArray<float3>(nodes, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var worldRotation = new NativeArray<quaternion>(nodes, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var worldScale = new NativeArray<float3>(nodes, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var valid = new NativeArray<bool>(nodes, Allocator.Temp);
            BasisMotionMatchingKinematics.Forward(Sampler.HipsNode, nodes, Parents, OutLocalPosition, OutLocalRotation, SnapshotScales, worldPosition, worldRotation, worldScale, valid);
            // Before the foot lock, so the phase search compares against what the animation is doing.
            TrackFeatures(ref state, worldPosition, deltaTime, wasSeeded);
            if (FootLockEnabled)
            {
                ApplyFootLock(ref state, deltaTime, worldPosition, worldRotation);
            }
            else
            {
                state.LeftFoot.TargetWeight = 0f;
                state.RightFoot.TargetWeight = 0f;
                state.LeftFoot.TargetRamp = 0f;
                state.RightFoot.TargetRamp = 0f;
            }
            LimitJointRates(deltaTime, nodes, !wasSeeded);

            State[0] = state;
            LandingLatch[0] = (byte)(parameters.LandingTrigger ? 1 : 0);
        }
        bool Advance(ref BasisMotionMatchingState state, ref BasisLocoParams parameters, float deltaTime)
        {
            float2 current = new float2(parameters.VelocityX, parameters.VelocityZ);
            float2 desired = new float2(parameters.DesiredVelocityX, parameters.DesiredVelocityZ);

            // Pushing into a wall: the stick says run while the body stands still. Believe the body.
            float desiredSpeed = math.length(desired);
            bool stalled = desiredSpeed > StallMinDesiredSpeed && math.length(current) < desiredSpeed * StallSpeedRatio;
            state.StallTimer = stalled ? state.StallTimer + deltaTime : 0f;
            if (state.StallTimer >= StallSeconds)
            {
                desired = current;
            }
            float2 target = PredictVelocity(current, desired, Tuning.BlendLookahead);
            target = HoldForSwingingFoot(ref state, target, deltaTime);
            // A big jump in the wanted velocity swings every clip weight at once, so give it longer than an ordinary stick move.
            float blendHalfLife = math.lerp(Tuning.BlendHalfLife, Tuning.BlendHalfLifeSudden, math.saturate(math.distance(target, state.BlendVelocity) / SuddenInputSpeed));
            DampVelocity(ref state.BlendVelocity, ref state.BlendAcceleration, target, blendHalfLife, deltaTime);

            bool transition = false;
            UpdatePlaybackRate(ref state, current, deltaTime);
            UpdateStrideWarp(ref state, current, deltaTime);
            float previousNorm = state.Norm;
            state.Norm += NormDelta(in state, in parameters, deltaTime);
            state.IdleTime += deltaTime;
            state.StateTime += deltaTime;
            if (state.StateTime >= MinStateSeconds && BasisLocomotionGraph.NativeTransition(state.LocoState, previousNorm, state.Norm, in parameters, out BasisLocoTransition next))
            {
                if (next.Condition == BasisLocoCondition.LandingTrigger)
                {
                    parameters.LandingTrigger = false;
                }
                // The cycles plant their feet at the same phase, so walk to crouch keeps its step. A one-shot has no phase to keep.
                bool wasTree = BasisLocomotionGraph.IsTree(state.LocoState);
                state.LocoState = next.To;
                if (!wasTree || !BasisLocomotionGraph.IsTree(next.To))
                {
                    state.Norm = 0f;
                    state.NeedsPhase = true;
                }
                state.StateTime = 0f;
                transition = true;
                state.TransitionCount++;
            }
            if (BasisLocomotionGraph.IsTree(state.LocoState))
            {
                state.Grid = BasisMotionMatchingDatabase.NearestGrid(state.LocoState, state.BlendVelocity);
                float targetSpeed = math.length(target);
                if (targetSpeed < RestBlendSpeed && math.length(state.BlendVelocity) < RestBlendSpeed)
                {
                    state.NeedsPhase = true;
                }
                else if (state.NeedsPhase && state.Seeded && targetSpeed >= RestBlendSpeed)
                {
                    SearchPhase(ref state, target);
                    state.NeedsPhase = false;
                    transition = true;
                }
            }
            return transition;
        }
        // Stopping shrinks the stride to idle, which would glide a raised foot along the floor. Keep enough stride for it to land.
        float2 HoldForSwingingFoot(ref BasisMotionMatchingState state, float2 target, float deltaTime)
        {
            float blendSpeed = math.length(state.BlendVelocity);
            bool stopping = BasisLocomotionGraph.IsTree(state.LocoState) && math.length(target) < RestBlendSpeed && blendSpeed > RestBlendSpeed;
            if (!stopping)
            {
                state.StopTimer = 0f;
                state.StopLanded = false;
                return target;
            }
            state.StopTimer += deltaTime;
            // Both feet down ends the walk: the standing foot driver takes over from here instead of the cycle shuffling down to idle.
            if (state.StopLanded || state.StopTimer >= Tuning.StopHoldSeconds || ContactAt(state.Grid, state.Norm) == (BasisMotionMatchingDatabase.LeftContactBit | BasisMotionMatchingDatabase.RightContactBit))
            {
                state.StopLanded = true;
                return target;
            }
            return state.BlendVelocity / blendSpeed * math.min(blendSpeed, StopHoldSpeed);
        }
        byte ContactAt(int grid, float norm)
        {
            int phases = BasisMotionMatchingDatabase.PhasesPerCycle;
            float phase = norm - math.floor(norm);
            return ContactFlags[grid * phases + (int)(phase * phases + 0.5f) % phases];
        }
        void UpdatePlaybackRate(ref BasisMotionMatchingState state, float2 current, float deltaTime)
        {
            float rate = 1f;
            if (BasisLocomotionGraph.IsTree(state.LocoState))
            {
                // Match the feet to the real ground speed, using the stride the planted foot actually travels.
                float blendSpeed = math.length(state.BlendVelocity);
                if (blendSpeed > RestBlendSpeed)
                {
                    float stance = BasisMotionMatchingDatabase.StanceSpeedAt(state.LocoState, state.BlendVelocity, GridStanceSpeed);
                    float groundSpeed = math.length(current);
                    rate = stance > 1e-3f ? groundSpeed * SkeletonUnitsPerMeter / stance : groundSpeed / blendSpeed;
                    rate = math.clamp(rate, MinPlaybackRate, MaxPlaybackRate);
                }
            }
            state.PlaybackRate = math.lerp(rate, state.PlaybackRate, math.exp(-2f * Ln2 / PlaybackRateHalfLife * deltaTime));
        }
        // What cadence cannot make up is made up in stride length. The strafe clips step about 1.5 m/s even at a full run.
        void UpdateStrideWarp(ref BasisMotionMatchingState state, float2 current, float deltaTime)
        {
            float wanted = 1f;
            float groundSpeed = math.length(current);
            if (BasisLocomotionGraph.IsTree(state.LocoState) && groundSpeed > StrideWarpMinSpeed && math.length(state.BlendVelocity) > StrideWarpMinSpeed)
            {
                float stance = BasisMotionMatchingDatabase.StanceSpeedAt(state.LocoState, state.BlendVelocity, GridStanceSpeed) * state.PlaybackRate;
                wanted = stance > 1e-3f ? math.clamp(groundSpeed * SkeletonUnitsPerMeter / stance, 1f, Tuning.MaxStrideWarp) : 1f;
            }
            state.StrideWarp = math.lerp(wanted, state.StrideWarp, math.exp(-2f * Ln2 / StrideWarpHalfLife * deltaTime));
        }
        float NormDelta(in BasisMotionMatchingState state, in BasisLocoParams parameters, float deltaTime)
        {
            if (!BasisLocomotionGraph.IsTree(state.LocoState))
            {
                float length = Sampler.ClipLength[BasisLocomotionGraph.OneShotClip(state.LocoState)];
                return length > 1e-4f ? deltaTime / length : 0f;
            }
            BasisMotionMatchingBlend.Evaluate(state.BlendVelocity, state.LocoState, ClipGroundSpeed, ChildTimeScales, Sampler.ClipLength, Contributions, 0, out float duration);
            return deltaTime * parameters.CurrentSpeed * state.PlaybackRate / math.max(duration, 1e-3f);
        }
        /// <summary>Picks the phase of the destination blend whose feet and hips best continue the pose on screen.</summary>
        void SearchPhase(ref BasisMotionMatchingState state, float2 target)
        {
            int grid = BasisMotionMatchingDatabase.NearestGrid(state.LocoState, target);
            int phases = BasisMotionMatchingDatabase.PhasesPerCycle;
            int vectors = BasisMotionMatchingDatabase.FeatureVectorsPerEntry;
            float position = FeatureScale[0], footVelocity = FeatureScale[1], hipsVelocity = FeatureScale[2];
            float3 left = state.LastLeftFoot * position, right = state.LastRightFoot * position;
            float3 leftVelocity = state.LeftFootVelocity * footVelocity, rightVelocity = state.RightFootVelocity * footVelocity, hips = state.HipsVelocity * hipsVelocity;
            float4 q0 = new float4(left, right.x), q1 = new float4(right.yz, leftVelocity.xy), q2 = new float4(leftVelocity.z, rightVelocity), q3 = new float4(hips, 0f);
            int best = 0;
            float bestCost = float.MaxValue;
            for (int phase = 0; phase < phases; phase++)
            {
                int offset = (grid * phases + phase) * vectors;
                float4 e0 = Features[offset] - q0, e1 = Features[offset + 1] - q1, e2 = Features[offset + 2] - q2, e3 = Features[offset + 3] - q3;
                float cost = math.dot(e0, e0) + math.dot(e1, e1) + math.dot(e2, e2) + math.dot(e3, e3);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = phase;
                }
            }
            state.Norm = best / (float)phases;
            state.PhaseSearchCount++;
        }
        void TrackFeatures(ref BasisMotionMatchingState state, NativeArray<float3> worldPosition, float deltaTime, bool wasSeeded)
        {
            float3 left = worldPosition[LeftLeg.Foot], right = worldPosition[RightLeg.Foot], hips = worldPosition[Sampler.HipsNode];
            if (wasSeeded && deltaTime > 0f)
            {
                float inverse = 1f / deltaTime;
                state.LeftFootVelocity = (left - state.LastLeftFoot) * inverse;
                state.RightFootVelocity = (right - state.LastRightFoot) * inverse;
                state.HipsVelocity = (hips - state.LastHips) * inverse;
            }
            state.LastLeftFoot = left;
            state.LastRightFoot = right;
            state.LastHips = hips;
        }
        /// <summary>The velocity after <paramref name="time"/> seconds of a critically damped spring toward the stick.</summary>
        public static float2 PredictVelocity(float2 velocity, float2 desired, float time)
        {
            float y = 2f * Ln2 / TrajectoryHalfLife;
            float2 j0 = velocity - desired;
            return math.exp(-y * time) * (j0 + j0 * y * time) + desired;
        }
        public static void DampVelocity(ref float2 velocity, ref float2 acceleration, float2 target, float halfLife, float deltaTime)
        {
            float y = 2f * Ln2 / halfLife;
            float2 j0 = velocity - target;
            float2 j1 = acceleration + j0 * y;
            float decay = math.exp(-y * deltaTime);
            velocity = decay * (j0 + j1 * deltaTime) + target;
            acceleration = decay * (acceleration - j1 * y * deltaTime);
        }
        int Emit(in BasisMotionMatchingState state, float norm, float idleTime, out float duration)
        {
            if (!BasisLocomotionGraph.IsTree(state.LocoState))
            {
                int clip = BasisLocomotionGraph.OneShotClip(state.LocoState);
                float oneShotPhase = ClipLooping[clip] != 0 ? norm - math.floor(norm) : math.min(norm, 1f);
                Contributions[0] = new BasisLocoContribution { Clip = clip, Weight = 1f, Time = oneShotPhase * Sampler.ClipLength[clip] };
                duration = Sampler.ClipLength[clip];
                return 1;
            }
            int count = BasisMotionMatchingBlend.Evaluate(state.BlendVelocity, state.LocoState, ClipGroundSpeed, ChildTimeScales, Sampler.ClipLength, Contributions, 0, out duration);
            int idleClip = BasisMotionMatchingBlend.IdleClip(state.LocoState);
            float phase = norm - math.floor(norm);
            for (int c = 0; c < count; c++)
            {
                BasisLocoContribution contribution = Contributions[c];
                float length = Sampler.ClipLength[contribution.Clip];
                // The idle has no step to stay in time with, and tying it to the stride would race it while walking.
                contribution.Time = contribution.Clip == idleClip ? math.frac(idleTime / length) * length : math.frac(phase + ClipPhaseOffset[contribution.Clip]) * length;
                Contributions[c] = contribution;
            }
            return count;
        }
        void Inertialize(ref BasisMotionMatchingState state, in BasisLocoParams parameters, bool transition, float deltaTime, int nodes)
        {
            int hipsNode = Sampler.HipsNode;
            if (!state.Seeded)
            {
                for (int node = 0; node < nodes; node++)
                {
                    PreviousRotation[node] = OutLocalRotation[node];
                    PreviousAngularVelocity[node] = float3.zero;
                    OffsetRotation[node] = quaternion.identity;
                    OffsetAngularVelocity[node] = float3.zero;
                }
                state.PreviousHips = OutLocalPosition[hipsNode];
                state.PreviousHipsVelocity = float3.zero;
                state.OffsetHips = float3.zero;
                state.OffsetHipsVelocity = float3.zero;
                state.InertializeTime = float.MaxValue;
                state.Seeded = true;
                return;
            }

            if (transition && !InertializationEnabled)
            {
                state.InertializeTime = float.MaxValue;
            }
            else if (transition)
            {
                // Sample a moment ahead for the destination's velocity so the transition keeps momentum continuous.
                float nextNorm = state.Norm + NormDelta(in state, in parameters, TargetVelocitySeconds);
                int nextCount = Emit(in state, nextNorm, state.IdleTime + TargetVelocitySeconds, out float _);
                Sampler.Sample(Contributions, nextCount, nodes, ScratchPosition, ScratchRotation);
                float inverseStep = 1f / TargetVelocitySeconds;
                for (int node = 0; node < nodes; node++)
                {
                    quaternion target = OutLocalRotation[node];
                    float3 targetVelocity = ToScaledAngleAxis(math.mul(math.inverse(target), ScratchRotation[node])) * inverseStep;
                    OffsetRotation[node] = math.normalizesafe(Abs(math.mul(math.inverse(target), PreviousRotation[node])));
                    OffsetAngularVelocity[node] = PreviousAngularVelocity[node] - targetVelocity;
                }
                float3 targetHipsVelocity = (ScratchPosition[hipsNode] - OutLocalPosition[hipsNode]) * inverseStep;
                state.OffsetHips = state.PreviousHips - OutLocalPosition[hipsNode];
                state.OffsetHipsVelocity = state.PreviousHipsVelocity - targetHipsVelocity;
                state.InertializeTime = 0f;
            }

            // After eight half-lives the offsets are under 0.4% of their start value; stop paying for them.
            bool active = state.InertializeTime < 8f * Tuning.InertializeHalfLife;
            if (active)
            {
                float y = 2f * Ln2 / Tuning.InertializeHalfLife;
                float decay = math.exp(-y * deltaTime);
                for (int node = 0; node < nodes; node++)
                {
                    float3 offset = ToScaledAngleAxis(OffsetRotation[node]);
                    float3 velocity = OffsetAngularVelocity[node];
                    float3 j1 = velocity + offset * y;
                    OffsetRotation[node] = FromScaledAngleAxis(decay * (offset + j1 * deltaTime));
                    OffsetAngularVelocity[node] = decay * (velocity - j1 * y * deltaTime);
                    OutLocalRotation[node] = math.normalizesafe(math.mul(OutLocalRotation[node], OffsetRotation[node]));
                }
                float3 hipsJ1 = state.OffsetHipsVelocity + state.OffsetHips * y;
                state.OffsetHips = decay * (state.OffsetHips + hipsJ1 * deltaTime);
                state.OffsetHipsVelocity = decay * (state.OffsetHipsVelocity - hipsJ1 * y * deltaTime);
                OutLocalPosition[hipsNode] += state.OffsetHips;
                state.InertializeTime += deltaTime;
            }

            if (deltaTime > 0f)
            {
                float inverseDelta = 1f / deltaTime;
                for (int node = 0; node < nodes; node++)
                {
                    quaternion output = OutLocalRotation[node];
                    PreviousAngularVelocity[node] = ToScaledAngleAxis(math.mul(math.inverse(PreviousRotation[node]), output)) * inverseDelta;
                    PreviousRotation[node] = output;
                }
                state.PreviousHipsVelocity = (OutLocalPosition[hipsNode] - state.PreviousHips) * inverseDelta;
                state.PreviousHips = OutLocalPosition[hipsNode];
            }
        }
        /// <summary>The last line of defense against a one-frame pop, whatever caused it.</summary>
        void LimitJointRates(float deltaTime, int nodes, bool firstFrame)
        {
            float limit = math.radians(Tuning.MaxJointDegreesPerSecond) * deltaTime;
            for (int node = 0; node < nodes; node++)
            {
                quaternion output = OutLocalRotation[node];
                if (!firstFrame && limit > 0f && deltaTime > 0f)
                {
                    quaternion previous = FinalRotation[node];
                    float dot = math.abs(math.dot(previous.value, output.value));
                    float angle = 2f * math.acos(math.min(dot, 1f));
                    if (angle > limit)
                    {
                        output = math.slerp(previous, output, limit / angle);
                        OutLocalRotation[node] = output;
                    }
                }
                FinalRotation[node] = output;
            }
        }
        void ApplyFootLock(ref BasisMotionMatchingState state, float deltaTime, NativeArray<float3> worldPosition, NativeArray<quaternion> worldRotation)
        {
            byte flags = 0;
            bool lockableState = state.LocoState == BasisLocoState.Walking || state.LocoState == BasisLocoState.Crouching;
            // Standing still belongs to the existing foot driver, which steps the feet when the body turns.
            if (lockableState && math.lengthsq(state.BlendVelocity) > FootLockMinBlendSpeed * FootLockMinBlendSpeed)
            {
                int phases = BasisMotionMatchingDatabase.PhasesPerCycle;
                float phase = state.Norm - math.floor(state.Norm);
                flags = ContactFlags[state.Grid * phases + (int)(phase * phases + 0.5f) % phases];
            }
            float blendSpeed = math.length(state.BlendVelocity);
            float3 travel = blendSpeed > 1e-3f ? math.mul(FacingToSkeleton, new float3(state.BlendVelocity.x, 0f, state.BlendVelocity.y) / blendSpeed) : float3.zero;
            // The strafe clips cross one leg over the other. Stop there and the feet stand tangled, so each foot keeps to its own side of the body.
            float3 hips = worldPosition[Sampler.HipsNode], right = math.mul(FacingToSkeleton, new float3(1f, 0f, 0f));
            float minSide = math.distance(worldPosition[LeftLeg.Upper], worldPosition[RightLeg.Upper]) * 0.5f * FootSideFraction;
            ProcessFoot(ref state.LeftFoot, LeftLeg, (flags & BasisMotionMatchingDatabase.LeftContactBit) != 0, SwingHalfLife(in state, BasisMotionMatchingDatabase.LeftContactBit), deltaTime, worldPosition, worldRotation, KeepSide(Warp(worldPosition[LeftLeg.Foot], worldPosition[LeftLeg.Upper], travel, state.StrideWarp), hips, right, -1f, minSide));
            ProcessFoot(ref state.RightFoot, RightLeg, (flags & BasisMotionMatchingDatabase.RightContactBit) != 0, SwingHalfLife(in state, BasisMotionMatchingDatabase.RightContactBit), deltaTime, worldPosition, worldRotation, KeepSide(Warp(worldPosition[RightLeg.Foot], worldPosition[RightLeg.Upper], travel, state.StrideWarp), hips, right, 1f, minSide));
        }
        // Eases in over one more minSide, so a foot brushing the line bends away instead of hitting a wall.
        static float3 KeepSide(float3 foot, float3 hips, float3 right, float side, float minSide)
        {
            float over = math.dot(foot - hips, right) * side - minSide;
            if (minSide <= 1e-5f || over >= minSide)
            {
                return foot;
            }
            return foot + right * (side * (minSide * math.exp(over / minSide - 1f) - over));
        }
        // Stretch each stride from its own hip joint. From the pelvis centre a strafe would also stretch the stance width, throwing the crossing foot further over the other.
        static float3 Warp(float3 foot, float3 hip, float3 travel, float warp) => foot + travel * (math.dot(foot - hip, travel) * (warp - 1f));
        // A correction left over from the last stance should be gone by the time the foot lands, or it lands sliding.
        float SwingHalfLife(in BasisMotionMatchingState state, byte contactBit)
        {
            int phases = BasisMotionMatchingDatabase.PhasesPerCycle;
            float phase = state.Norm - math.floor(state.Norm);
            int current = (int)(phase * phases + 0.5f) % phases;
            int ahead = 0;
            while (ahead < phases && (ContactFlags[state.Grid * phases + (current + ahead) % phases] & contactBit) == 0)
            {
                ahead++;
            }
            BasisMotionMatchingBlend.Evaluate(state.BlendVelocity, state.LocoState, ClipGroundSpeed, ChildTimeScales, Sampler.ClipLength, Contributions, 0, out float duration);
            float seconds = ahead / (float)phases * duration / math.max(state.PlaybackRate, 0.1f);
            return math.clamp(seconds / 3f, MinSwingHalfLife, Tuning.FootBlendHalfLife);
        }
        void ProcessFoot(ref BasisMotionMatchingFootLock foot, in BasisMotionMatchingLegNodes leg, bool contact, float swingHalfLife, float deltaTime, NativeArray<float3> skeletonPosition, NativeArray<quaternion> skeletonRotation, float3 warpedFoot)
        {
            float3 a = skeletonPosition[leg.Upper], b = skeletonPosition[leg.Lower], c = skeletonPosition[leg.Foot];
            // A warped stride the leg cannot reach would be solved at full stretch, where the knee flips and the foot pops.
            float reach = math.max((math.distance(a, b) + math.distance(b, c)) * WarpReachRatio, math.distance(a, c));
            float3 fromHip = warpedFoot - a;
            float fromHipLength = math.length(fromHip);
            if (fromHipLength > reach)
            {
                warpedFoot = a + fromHip * (reach / fromHipLength);
            }
            float3 input = math.transform(SkeletonToWorld, warpedFoot);
            float legLength = math.distance(math.transform(SkeletonToWorld, a), math.transform(SkeletonToWorld, b)) + math.distance(math.transform(SkeletonToWorld, b), math.transform(SkeletonToWorld, c));
            float3 inputVelocity = foot.HasLastInput && deltaTime > 0f ? (input - foot.LastInputWorld) / deltaTime : float3.zero;

            // A jump this far is a teleport, not a stride. Blending it would drag the foot across the world.
            if (foot.HasLastInput && math.distancesq(input, foot.LastInputWorld) > legLength * legLength * FootTeleportLegRatio * FootTeleportLegRatio)
            {
                foot.Locked = false;
                foot.OffsetWorld = float3.zero;
                foot.OffsetVelocity = float3.zero;
                foot.TargetWeight = 0f;
                foot.TargetRamp = 0f;
                inputVelocity = float3.zero;
            }
            foot.LastInputWorld = input;
            foot.HasLastInput = true;

            if (foot.Locked)
            {
                // Also let go before the hip outruns the lock: held to full stretch the leg snaps straight, then flicks forward when it lifts.
                if (!contact || math.distance(foot.LockWorld, input) > legLength * Tuning.FootUnlockDistance || math.distance(foot.LockWorld, math.transform(SkeletonToWorld, a)) > legLength * WarpReachRatio)
                {
                    foot.OffsetWorld = foot.LockWorld + foot.OffsetWorld - input;
                    foot.OffsetVelocity -= inputVelocity;
                    foot.Locked = false;
                    // Stretched past its limit mid-stance: stay free until this step ends, or it relocks every frame.
                    foot.WaitForLift = contact;
                }
            }
            else if (!contact)
            {
                foot.WaitForLift = false;
            }
            // The contact flag also covers the end of the swing, so wait until the foot has really slowed down.
            else if (!foot.WaitForLift && deltaTime > 0f && math.length(inputVelocity + foot.OffsetVelocity) < legLength * Tuning.FootLockMaxSpeed)
            {
                foot.LockWorld = input + foot.OffsetWorld;
                foot.OffsetWorld = float3.zero;
                foot.OffsetVelocity += inputVelocity;
                foot.Locked = true;
            }

            float y = 2f * Ln2 / (foot.Locked ? Tuning.FootBlendHalfLife : swingHalfLife);
            float decay = math.exp(-y * deltaTime);
            float3 j1 = foot.OffsetVelocity + foot.OffsetWorld * y, previousOffset = foot.OffsetWorld;
            foot.OffsetWorld = decay * (foot.OffsetWorld + j1 * deltaTime);
            foot.OffsetVelocity = decay * (foot.OffsetVelocity - j1 * y * deltaTime);
            // A lock released far from the animation (a long stance, a hitch) would close the gap in a flick. Cap how fast it closes.
            float3 offsetStep = foot.OffsetWorld - previousOffset;
            float maxOffsetStep = legLength * OffsetCatchUpLegsPerSecond * deltaTime, offsetStepLength = math.length(offsetStep);
            if (offsetStepLength > maxOffsetStep && deltaTime > 0f)
            {
                foot.OffsetWorld = previousOffset + offsetStep * (maxOffsetStep / offsetStepLength);
                foot.OffsetVelocity = (foot.OffsetWorld - previousOffset) / deltaTime;
            }

            float3 targetWorld = (foot.Locked ? foot.LockWorld : input) + foot.OffsetWorld;
            foot.TargetWorld = targetWorld;
            float weightStep = deltaTime / FootTargetBlendSeconds;
            foot.TargetRamp = foot.Locked ? math.min(foot.TargetRamp + weightStep, 1f) : math.max(foot.TargetRamp - weightStep, 0f);
            // The leg solve blends between two different knee solutions, so a ramp that starts abruptly kicks the foot.
            foot.TargetWeight = foot.TargetRamp * foot.TargetRamp * (3f - 2f * foot.TargetRamp);
            if (!foot.Locked && math.lengthsq(foot.OffsetWorld) < 1e-8f && math.distancesq(warpedFoot, c) < 1e-8f)
            {
                return;
            }
            float3 target = math.transform(WorldToSkeleton, targetWorld);
            quaternion upper = OutLocalRotation[leg.Upper];
            quaternion lower = OutLocalRotation[leg.Lower];
            SolveLeg(a, b, c, target, skeletonRotation[leg.Upper], skeletonRotation[leg.Lower], ref upper, ref lower, ref foot.BendAxis, ref foot.HasBendAxis);
            OutLocalRotation[leg.Upper] = upper;
            OutLocalRotation[leg.Lower] = lower;
        }
        /// <summary>
        /// Two-bone IK that keeps the bend plane: <paramref name="a"/> hip, <paramref name="b"/> knee, <paramref name="c"/> ankle, all in one frame.
        /// A straight leg has no plane of its own, so the last good axis stands in for it.
        /// </summary>
        public static void SolveLeg(float3 a, float3 b, float3 c, float3 target, quaternion upperWorld, quaternion lowerWorld, ref quaternion upperLocal, ref quaternion lowerLocal, ref float3 bendAxis, ref bool hasBendAxis)
        {
            const float Epsilon = 1e-4f;
            float lengthAB = math.distance(a, b);
            float lengthBC = math.distance(b, c);
            if (lengthAB < Epsilon || lengthBC < Epsilon || math.distancesq(target, c) < 1e-8f)
            {
                return;
            }
            float3 toTarget = target - a;
            float distance = math.length(toTarget);
            if (distance < Epsilon)
            {
                return;
            }
            float3 axisAT = toTarget / distance;
            float lengthAT = math.clamp(distance, math.abs(lengthAB - lengthBC) + Epsilon, (lengthAB + lengthBC) * MaxLegExtension);
            float3 axisAC = math.normalize(c - a);
            float3 axisAB = (b - a) / lengthAB;
            float3 axisBC = (c - b) / lengthBC;

            float3 plane = math.cross(axisAC, axisAB);
            float planeLength = math.length(plane);
            // A knee is a hinge: near straight this cross product is noise, and following it would reverse the knee.
            if (planeLength > KneePlaneMinSine)
            {
                float3 measured = plane / planeLength;
                if (!hasBendAxis || math.dot(measured, bendAxis) > 0f)
                {
                    bendAxis = measured;
                    hasBendAxis = true;
                }
            }

            quaternion upperDelta = quaternion.identity;
            quaternion lowerDelta = quaternion.identity;
            if (hasBendAxis)
            {
                float angleA = math.acos(math.clamp(math.dot(axisAC, axisAB), -1f, 1f));
                float angleB = math.acos(math.clamp(math.dot(-axisAB, axisBC), -1f, 1f));
                float desiredA = math.acos(math.clamp((lengthBC * lengthBC - lengthAB * lengthAB - lengthAT * lengthAT) / (-2f * lengthAB * lengthAT), -1f, 1f));
                float desiredB = math.acos(math.clamp((lengthAT * lengthAT - lengthAB * lengthAB - lengthBC * lengthBC) / (-2f * lengthAB * lengthBC), -1f, 1f));
                upperDelta = quaternion.AxisAngle(math.mul(math.inverse(upperWorld), bendAxis), desiredA - angleA);
                lowerDelta = quaternion.AxisAngle(math.mul(math.inverse(lowerWorld), bendAxis), desiredB - angleB);
            }

            float3 swingAxis = math.cross(axisAC, axisAT);
            float swingLength = math.length(swingAxis);
            quaternion swing = quaternion.identity;
            if (swingLength > 1e-5f)
            {
                float swingAngle = math.atan2(swingLength, math.dot(axisAC, axisAT));
                swing = quaternion.AxisAngle(math.mul(math.inverse(upperWorld), swingAxis / swingLength), swingAngle);
            }
            upperLocal = math.normalize(math.mul(upperLocal, math.mul(swing, upperDelta)));
            lowerLocal = math.normalize(math.mul(lowerLocal, lowerDelta));
        }
        static quaternion Abs(quaternion q)
        {
            return q.value.w < 0f ? new quaternion(-q.value) : q;
        }
        public static float3 ToScaledAngleAxis(quaternion q)
        {
            float4 v = Abs(q).value;
            float length = math.length(v.xyz);
            if (length < 1e-6f)
            {
                return v.xyz * 2f;
            }
            return v.xyz * (2f * math.atan2(length, v.w) / length);
        }
        public static quaternion FromScaledAngleAxis(float3 scaledAngleAxis)
        {
            float3 half = scaledAngleAxis * 0.5f;
            float angle = math.length(half);
            if (angle < 1e-6f)
            {
                return math.normalize(new quaternion(new float4(half, 1f)));
            }
            math.sincos(angle, out float sine, out float cosine);
            return new quaternion(new float4(half * (sine / angle), cosine));
        }
    }
}
