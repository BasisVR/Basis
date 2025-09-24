using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
public static class BasisRemoteNetworkDriver
{
    public const int FixedCapacity = 1024;
    static NativeArray<float3> _prevPositions;
    static NativeArray<float3> _targetPositions;
    static NativeArray<float3> _prevScales;
    static NativeArray<float3> _targetScales;
    static NativeArray<quaternion> _prevRotations;
    static NativeArray<quaternion> _targetRotations;
    static NativeArray<float> _interpolationTimes;
    static NativeArray<float3> _outPositions;
    static NativeArray<float3> _outScales;
    static NativeArray<quaternion> _outRotations;
    static NativeArray<float> _humanScales; 
    static NativeArray<float3> _scaledBodyPositions;
    // Muscles (flattened: players * muscles)
    static NativeArray<float> _prevMuscles;
    static NativeArray<float> _targetMuscles;
    static NativeArray<float> _outMuscles;
    // 1€ filter buffers (flattened: players * muscles)
    static NativeArray<float> euroValuesOutput;
    static NativeArray<float2> positionFilters;
    static NativeArray<float2> derivativeFilters;
    // State
    static int _muscleCount;
    static bool _initialized;
    static int _activeCount; // highest index written + 1
    static Allocator _allocator = Allocator.Persistent;
    public static JobHandle oneEuroJob;
    // Parameters for Euro filter
    public static float MinCutoff = 0.05f;
    public static float Beta = 0.01f;
    public static float DerivativeCutoff = 1.0f;
    /// <summary>Initialize the driver with a fixed capacity of 1024. Must be called before SetInputs/Compute/Apply/GetOutputs.</summary>
    public static void Initialize(int muscleCount, Allocator allocator = Allocator.Persistent)
    {
        if (_initialized) return;

        if (muscleCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(muscleCount));

        _allocator = allocator;
        _muscleCount = muscleCount;
        _activeCount = 0;

        AllocateAll(FixedCapacity);

        // Seed defaults
        for (int Index = 0; Index < FixedCapacity; Index++)
        {
            _prevScales[Index] = new float3(1, 1, 1);
            _targetScales[Index] = new float3(1, 1, 1);
            _prevRotations[Index] = quaternion.identity;
            _targetRotations[Index] = quaternion.identity;
            _interpolationTimes[Index] = 0f;

            // New: default human scale to 1
            _humanScales[Index] = 1;
            _scaledBodyPositions[Index] = float3.zero;
        }

        // Seed muscles/filter state
        int flat = FixedCapacity * _muscleCount;
        for (int c = 0; c < flat; c++)
        {
            _prevMuscles[c] = 0f;
            _targetMuscles[c] = 0f;
            _outMuscles[c] = 0f;
            euroValuesOutput[c] = 0f;
            positionFilters[c] = float2.zero;
            derivativeFilters[c] = float2.zero;
        }

        _initialized = true;
    }

    /// <summary>Dispose all native allocations. Call on shutdown/domain unload.</summary>
    public static void Shutdown()
    {
        if (!_initialized) return;

        // Make sure no jobs are still using our arrays
        if (!oneEuroJob.IsCompleted) oneEuroJob.Complete();

        DisposeAll();
        _activeCount = 0;
        _muscleCount = 0;
        _initialized = false;
    }

    /// <summary>Write inputs for a given index (0..FixedCapacity-1) for this frame.</summary>
    public static void SetInputs( int index, float humanScale,float3 prevPos, float3 targetPos, float3 prevScale, float3 targetScale,
        quaternion prevRot, quaternion targetRot, float interpolationTime, NativeArray<float> prevMuscles,NativeArray<float> targetMuscles)
    {
        if ((uint)index >= FixedCapacity)
            throw new IndexOutOfRangeException($"index {index} is out of range [0,{FixedCapacity - 1}]");

        _humanScales[index] = humanScale;
        _prevPositions[index] = prevPos;
        _targetPositions[index] = targetPos;
        _prevScales[index] = prevScale;
        _targetScales[index] = targetScale;
        _prevRotations[index] = prevRot;
        _targetRotations[index] = targetRot;
        _interpolationTimes[index] = interpolationTime;

        // Flattened write: [index * MuscleCount .. (index+1) * MuscleCount)
        int baseOffset = index * _muscleCount;
        FastCopyMuscles(prevMuscles, 0, _prevMuscles, baseOffset, _muscleCount);
        FastCopyMuscles(targetMuscles, 0, _targetMuscles, baseOffset, _muscleCount);

        // Advance active count if needed
        if (index + 1 > _activeCount) _activeCount = index + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void FastCopyMuscles(NativeArray<float> src, int srcStart, NativeArray<float> dst, int dstStart, int count)
    {
        var bytes = (long)count * sizeof(float);
        var srcPtr = (byte*)src.GetUnsafeReadOnlyPtr() + (long)srcStart * sizeof(float);
        var dstPtr = (byte*)dst.GetUnsafePtr() + (long)dstStart * sizeof(float);
        UnsafeUtility.MemCpy(dstPtr, srcPtr, bytes);
    }
    /// <summary>Run the batched jobs once for the current frame.</summary>
    public static void Compute()
    {
        int num = _activeCount;
        if (num <= 0) return;

        var avatarJob = new UpdateAllAvatarsJob
        {
            PreviousPositions = _prevPositions,
            TargetPositions = _targetPositions,
            PreviousScales = _prevScales,
            TargetScales = _targetScales,
            PreviousRotations = _prevRotations,
            TargetRotations = _targetRotations,
            InterpolationTimes = _interpolationTimes,
            OutputPositions = _outPositions,
            OutputScales = _outScales,
            OutputRotations = _outRotations
        }.Schedule(num, 128);

        // Precompute scaled body position with guarded divide (Burst)
        var scaledBodyJob = new ComputeScaledBodyJob
        {
            OutputPositions = _outPositions,
            OutputScales = _outScales,
            HumanScales = _humanScales,
            ScaledBodyPositions = _scaledBodyPositions
        }.Schedule(num, 128, avatarJob);

        // Interpolate muscles across players * muscles (flattened)
        JobHandle musclesJob = new UpdateAllAvatarMusclesJob
        {
            PreviousMuscles = _prevMuscles,
            TargetMuscles = _targetMuscles,
            InterpolationTimes = _interpolationTimes, // index-based per "player"
            OutputMuscles = _outMuscles,
            MuscleCountPerAvatar = _muscleCount
        }.Schedule(num * _muscleCount, 128, avatarJob);

        // 1€ filter: read interpolated muscles, write filtered output
        JobHandle euroJobHandle = new BasisOneEuroFilterParallelJob
        {
            InputValues = _outMuscles,          // raw/interpolated input per (index,muscle)
            OutputValues = euroValuesOutput,    // filtered output
            DeltaTime = _interpolationTimes,    // per-index dt / interpolation t
            MinCutoff = MinCutoff,
            Beta = Beta,
            DerivativeCutoff = DerivativeCutoff,
            PositionFilters = positionFilters,
            DerivativeFilters = derivativeFilters,
            MuscleCountPerAvatar = _muscleCount
        }.Schedule(num * _muscleCount, 128, musclesJob);

        // Combine all deps so Apply() has a single fence
        oneEuroJob = JobHandle.CombineDependencies(euroJobHandle, scaledBodyJob);
    }

    /*
     * BasicOneEuroFilterParallelJob.cs
     * Author: Dario Mazzanti (dario.mazzanti@iit.it), 2016
     *
     * This Unity C# utility is based on the C++ implementation of the OneEuroFilter algorithm by Nicolas Roussel (http://www.lifl.fr/~casiez/1euro/OneEuroFilter.cc)
     * More info on the 1€ filter by Géry Casiez at http://www.lifl.fr/~casiez/1euro/
     *
     */
    [BurstCompile]
    public struct BasisOneEuroFilterParallelJob : IJobParallelFor
    {
        // Input signal (flattened: players * muscles)
        [ReadOnly] public NativeArray<float> InputValues;

        // Output signal (flattened: players * muscles)
        public NativeArray<float> OutputValues;

        // Per-player deltaTime (or sampling period proxy). Length == numPlayers
        [ReadOnly] public NativeArray<float> DeltaTime;

        // Filter state per flattened channel (same length as OutputValues)
        public NativeArray<float2> PositionFilters;   // x = previous input, y = previous output
        public NativeArray<float2> DerivativeFilters; // x = previous derivative input, y = previous derivative output

        // Parameters
        public float MinCutoff;
        public float Beta;
        public float DerivativeCutoff;

        // Stride to recover the player index from the flattened channel index
        // i.e., the number of muscles per avatar
        [ReadOnly] public int MuscleCountPerAvatar;

        public void Execute(int index)
        {
            int playerIndex = MuscleCountPerAvatar > 0 ? (index / MuscleCountPerAvatar) : 0;

            if ((uint)playerIndex >= (uint)DeltaTime.Length) return;
            if ((uint)index >= (uint)InputValues.Length) return;
            if ((uint)index >= (uint)OutputValues.Length) return;
            if ((uint)index >= (uint)PositionFilters.Length) return;
            if ((uint)index >= (uint)DerivativeFilters.Length) return;

            float dt = DeltaTime[playerIndex];
            if (dt <= 0f) dt = 1e-3f; // guard

            float frequency = 1.0f / dt;
            float inputValue = InputValues[index];

            float prevFiltered = PositionFilters[index].y;
            float prevRaw = PositionFilters[index].x;

            float dValue = (inputValue - prevRaw) * frequency;

            float alphaD = Alpha(DerivativeCutoff, frequency);
            float prevDerivFiltered = DerivativeFilters[index].y;
            float edValue = alphaD * dValue + (1.0f - alphaD) * prevDerivFiltered;

            float cutoff = MinCutoff + Beta * Mathf.Abs(edValue);

            float alphaX = Alpha(cutoff, frequency);
            float filtered = alphaX * inputValue + (1.0f - alphaX) * prevFiltered;

            OutputValues[index] = filtered;

            PositionFilters[index] = new float2(inputValue, filtered);
            DerivativeFilters[index] = new float2(dValue, edValue);
        }

        private static float Alpha(float cutoff, float frequency)
        {
            float te = 1.0f / frequency;
            float tau = 1.0f / (2.0f * Mathf.PI * math.max(cutoff, 1e-4f));
            return 1.0f / (1.0f + tau / te);
        }
    }

    [BurstCompile]
    public struct UpdateAllAvatarsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> PreviousPositions;
        [ReadOnly] public NativeArray<float3> TargetPositions;

        [ReadOnly] public NativeArray<float3> PreviousScales;
        [ReadOnly] public NativeArray<float3> TargetScales;

        [ReadOnly] public NativeArray<quaternion> PreviousRotations;
        [ReadOnly] public NativeArray<quaternion> TargetRotations;

        [ReadOnly] public NativeArray<float> InterpolationTimes;

        [WriteOnly]
        public NativeArray<float3> OutputPositions;
        [WriteOnly]
        public NativeArray<float3> OutputScales;
        [WriteOnly]
        public NativeArray<quaternion> OutputRotations;

        public void Execute(int index)
        {
            float t = InterpolationTimes[index];

            OutputPositions[index] = math.lerp(PreviousPositions[index], TargetPositions[index], t);
            OutputScales[index] = math.lerp(PreviousScales[index], TargetScales[index], t);
            OutputRotations[index] = math.slerp(PreviousRotations[index], TargetRotations[index], t);
        }
    }

    [BurstCompile]
    public struct UpdateAllAvatarMusclesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> PreviousMuscles; // Flattened array
        [ReadOnly] public NativeArray<float> TargetMuscles;
        [ReadOnly] public NativeArray<float> InterpolationTimes;
        [WriteOnly]
        public NativeArray<float> OutputMuscles;
        public int MuscleCountPerAvatar;

        public void Execute(int index)
        {
            int playerIndex = index / MuscleCountPerAvatar;
            float t = InterpolationTimes[playerIndex];
            OutputMuscles[index] = math.lerp( PreviousMuscles[index], TargetMuscles[index],t);
        }
    }

    /// <summary>Guarded divide + scaled body position (Burst).</summary>
    [BurstCompile]
    public struct ComputeScaledBodyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> OutputPositions; // from UpdateAllAvatarsJob
        [ReadOnly] public NativeArray<float3> OutputScales;    // from UpdateAllAvatarsJob
        [ReadOnly] public NativeArray<float> HumanScales;     // per avatar

        [WriteOnly] public NativeArray<float3> ScaledBodyPositions;

        public void Execute(int i)
        {
            const float eps = 1e-6f;

            float3 applyScale = OutputScales[i];

            // Sanitize baseScale: avoid 0 / NaN / Inf before reciprocal
            float baseScale = HumanScales[i];
            bool baseBad = !math.isfinite(baseScale) | (math.abs(baseScale) <= eps);
            // If bad, fall back to 1.0; else use reciprocal
            float invBase = math.select(math.rcp(baseScale), 1f, baseBad);

            // Use float3 everywhere (avoid Vector3 in Burst jobs)
            float3 scale = new float3(invBase); // equivalent to 1 / baseScale if valid

            // Per-component guard for applyScale (also handle NaN/Inf there)
            bool3 validApply = math.isfinite(applyScale) & (math.abs(applyScale) > eps);

            // If valid, divide; otherwise just use the base scale
            float3 safeDiv = math.select(scale, scale / applyScale, validApply);

            // Optional: clamp to avoid exploding values if inputs are extreme
            // safeDiv = math.clamp(safeDiv, -1e6f, 1e6f);

            ScaledBodyPositions[i] = OutputPositions[i] * safeDiv;
        }
    }

    /// <summary>Completes all scheduled work for this frame.</summary>
    public static void Apply()
    {
        if (!_initialized) return;
        oneEuroJob.Complete(); // also fences scaledBody + transform jobs via combined deps
    }

    /// <summary>Read back the computed outputs for an index after Apply().</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GetOutputs_NoAlloc(int index,out float3 outPos,out float3 outScale,out quaternion outRot,out float3 BodyPosition,float[] outMuscles)
    {
        // minimal guards; no allocations
        if ((uint)index >= FixedCapacity)
        {
            outPos = Vector3.zero;
            outScale = Vector3.one;
            outRot = Quaternion.identity;
            BodyPosition = Vector3.zero;
            outMuscles = default;
            return false;
        }
        if (outMuscles == null || outMuscles.Length != _muscleCount)
        {
            outPos = Vector3.zero;
            outScale = Vector3.one;
            outRot = Quaternion.identity;
            BodyPosition = Vector3.zero;
            outMuscles = default;
            return false;
        }

        outPos = _outPositions[index];
        outScale = _outScales[index];
        outRot = _outRotations[index];

        int baseOffset = index * _muscleCount;

        unsafe
        {
            // source: NativeArray<float> (contiguous)
            float* src = (float*)euroValuesOutput.GetUnsafeReadOnlyPtr() + baseOffset;

            // dest: managed float[] pinned just for the copy
            fixed (float* dst = outMuscles)
            {
                UnsafeUtility.MemCpy(dst, src, _muscleCount * sizeof(float));
            }
        }
        BodyPosition = _scaledBodyPositions[index];
        return true;
    }

    static void AllocateAll(int capacity)
    {
        // Transform data
        _prevPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _prevScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _prevRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _interpolationTimes = new NativeArray<float>(capacity, _allocator, NativeArrayOptions.ClearMemory);

        _outPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _outScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _outRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        // New: human scale + scaled body
        _humanScales = new NativeArray<float>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _scaledBodyPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        // Muscles (flattened)
        int flat = capacity * _muscleCount;
        _prevMuscles = new NativeArray<float>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetMuscles = new NativeArray<float>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _outMuscles = new NativeArray<float>(flat, _allocator, NativeArrayOptions.UninitializedMemory);

        // Euro filter buffers (flattened)
        euroValuesOutput = new NativeArray<float>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        positionFilters = new NativeArray<float2>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        derivativeFilters = new NativeArray<float2>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
    }

    static void DisposeAll()
    {
        // Dispose safely
        if (_prevPositions.IsCreated) _prevPositions.Dispose();
        if (_targetPositions.IsCreated) _targetPositions.Dispose();
        if (_prevScales.IsCreated) _prevScales.Dispose();
        if (_targetScales.IsCreated) _targetScales.Dispose();
        if (_prevRotations.IsCreated) _prevRotations.Dispose();
        if (_targetRotations.IsCreated) _targetRotations.Dispose();
        if (_interpolationTimes.IsCreated) _interpolationTimes.Dispose();

        if (_outPositions.IsCreated) _outPositions.Dispose();
        if (_outScales.IsCreated) _outScales.Dispose();
        if (_outRotations.IsCreated) _outRotations.Dispose();

        if (_humanScales.IsCreated) _humanScales.Dispose();
        if (_scaledBodyPositions.IsCreated) _scaledBodyPositions.Dispose();

        if (_prevMuscles.IsCreated) _prevMuscles.Dispose();
        if (_targetMuscles.IsCreated) _targetMuscles.Dispose();
        if (_outMuscles.IsCreated) _outMuscles.Dispose();

        if (euroValuesOutput.IsCreated) euroValuesOutput.Dispose();
        if (positionFilters.IsCreated) positionFilters.Dispose();
        if (derivativeFilters.IsCreated) derivativeFilters.Dispose();
    }
}
