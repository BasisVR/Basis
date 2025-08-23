using Basis.Scripts.Networking;
using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public static partial class BasisRemoteNetworkDriver
{
    /// <summary>
    /// Fixed capacity for this driver instance.
    /// </summary>
    public const int FixedCapacity = 1024;

    /// <summary>
    /// Returns the number of active indices (max index that has been written + 1).
    /// This is advanced automatically when you call SetInputs for a higher index.
    /// </summary>
    public static int ActivePlayerCount => _activeCount;

    /// <summary>
    /// Muscle count used by the driver.
    /// </summary>
    public static int MuscleCount => _muscleCount;

    // Native buffers (size == FixedCapacity, except muscles which is FixedCapacity * _muscleCount)
    static NativeArray<float3> _prevPositions;
    static NativeArray<float3> _targetPositions;
    static NativeArray<float3> _prevScales;
    static NativeArray<float3> _targetScales;
    static NativeArray<quaternion> _prevRotations;
    static NativeArray<quaternion> _targetRotations;
    static NativeArray<float> _interpolationTimes; // per-index dt or interpolation t

    static NativeArray<float3> _outPositions;
    static NativeArray<float3> _outScales;
    static NativeArray<quaternion> _outRotations;

    // Muscles (flattened: players * muscles)
    static NativeArray<float> _prevMuscles;   // flattened
    static NativeArray<float> _targetMuscles; // flattened
    static NativeArray<float> _outMuscles;    // flattened (interpolated, before filter)

    // 1€ filter buffers (flattened: players * muscles)
    static NativeArray<float> euroValuesOutput;   // filtered output
    static NativeArray<float2> positionFilters;   // per-channel state
    static NativeArray<float2> derivativeFilters; // per-channel state

    // State
    static int _muscleCount;
    static bool _initialized;
    static int _activeCount; // highest index written + 1
    static Allocator _allocator = Allocator.Persistent;

    public static JobHandle oneEuroJob;

    static void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("BasisRemoteNetworkDriver.Initialize(...) must be called before use.");
    }

    /// <summary>
    /// Initialize the driver with a fixed capacity of 1024. Must be called before SetInputs/Compute/Apply/GetOutputs.
    /// </summary>
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
        for (int i = 0; i < FixedCapacity; i++)
        {
            _prevScales[i] = new float3(1, 1, 1);
            _targetScales[i] = new float3(1, 1, 1);
            _prevRotations[i] = quaternion.identity;
            _targetRotations[i] = quaternion.identity;
            _interpolationTimes[i] = 0f;
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

    /// <summary>
    /// Dispose all native allocations. Call on shutdown/domain unload.
    /// </summary>
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

    /// <summary>
    /// Write inputs for a given index (0..FixedCapacity-1) for this frame.
    /// You manage indices yourself; this driver never resizes or re-maps.
    /// </summary>
    public static void SetInputs(
        int index,
        float3 prevPos, float3 targetPos,
        float3 prevScale, float3 targetScale,
        quaternion prevRot, quaternion targetRot,
        float interpolationTime,
        float[] prevMuscles, float[] targetMuscles)
    {
        EnsureInitialized();
        if ((uint)index >= FixedCapacity)
            throw new IndexOutOfRangeException($"index {index} is out of range [0,{FixedCapacity - 1}]");

        if (prevMuscles == null || targetMuscles == null)
            throw new ArgumentNullException("prevMuscles/targetMuscles must be non-null arrays");

        if (prevMuscles.Length < _muscleCount || targetMuscles.Length < _muscleCount)
            throw new ArgumentException($"prevMuscles/targetMuscles must have length >= {_muscleCount}");

        _prevPositions[index] = prevPos;
        _targetPositions[index] = targetPos;
        _prevScales[index] = prevScale;
        _targetScales[index] = targetScale;
        _prevRotations[index] = prevRot;
        _targetRotations[index] = targetRot;
        _interpolationTimes[index] = math.clamp(interpolationTime, 0f, 1f);

        // Flattened write: [index * MuscleCount .. (index+1) * MuscleCount)
        int baseOffset = index * _muscleCount;

        NativeArray<float>.Copy(prevMuscles, 0, _prevMuscles, baseOffset, _muscleCount);
        NativeArray<float>.Copy(targetMuscles, 0, _targetMuscles, baseOffset, _muscleCount);

        // Advance active count if needed
        if (index + 1 > _activeCount) _activeCount = index + 1;
    }

    /// <summary>
    /// Optional: reset a given index back to defaults (zeros/identity). Useful if you stop updating an index.
    /// </summary>
    public static void ResetIndex(int index)
    {
        EnsureInitialized();
        if ((uint)index >= FixedCapacity)
            throw new IndexOutOfRangeException($"index {index} is out of range [0,{FixedCapacity - 1}]");

        // Make sure no jobs are still reading/writing these buffers
        if (!oneEuroJob.IsCompleted) oneEuroJob.Complete();

        _prevPositions[index] = default;
        _targetPositions[index] = default;
        _prevScales[index] = new float3(1, 1, 1);
        _targetScales[index] = new float3(1, 1, 1);
        _prevRotations[index] = quaternion.identity;
        _targetRotations[index] = quaternion.identity;
        _interpolationTimes[index] = 0f;

        int baseOffset = index * _muscleCount;
        for (int m = 0; m < _muscleCount; m++)
        {
            int flat = baseOffset + m;
            _prevMuscles[flat] = 0f;
            _targetMuscles[flat] = 0f;
            _outMuscles[flat] = 0f;

            euroValuesOutput[flat] = 0f;
            positionFilters[flat] = float2.zero;
            derivativeFilters[flat] = float2.zero;
        }

        // Optionally shrink active count if we cleared the tail
        if (index == _activeCount - 1)
        {
            int newCount = index;
            _activeCount = newCount;
        }
    }

    /// <summary>
    /// Run the batched jobs once for the current frame.
    /// </summary>
    public static void Compute()
    {
        EnsureInitialized();

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
        oneEuroJob = new BasisOneEuroFilterParallelJob
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
    }

    /// <summary>
    /// Completes all scheduled work for this frame.
    /// </summary>
    public static void Apply()
    {
        if (!_initialized) return;
        oneEuroJob.Complete();
    }

    /// <summary>
    /// Read back the computed outputs for an index after Apply().
    /// </summary>
    public static bool GetOutputs(
        int index,
        out float3 outPos, out float3 outScale, out quaternion outRot,
        ref float[] outMuscles) // length == MuscleCount
    {
        EnsureInitialized();
        outPos = default;
        outScale = default;
        outRot = default;

        if ((uint)index >= FixedCapacity) return false;
        if (outMuscles == null || outMuscles.Length < _muscleCount)
            outMuscles = new float[_muscleCount];

        outPos = _outPositions[index];
        outScale = _outScales[index];
        outRot = _outRotations[index];

        int baseOffset = index * _muscleCount;
        NativeArray<float>.Copy(euroValuesOutput, baseOffset, outMuscles, 0, _muscleCount);
        return true;
    }
    /// <summary>
    /// Update the One Euro filter parameters on the shared network singleton and (optionally)
    /// reset the filter internal state so it "forgets" previous history and re-converges
    /// from the current inputs.
    /// </summary>
    /// <param name="minCutoff">New MinCutoff (Hz).</param>
    /// <param name="beta">New Beta (cutoff slope vs speed).</param>
    /// <param name="derivativeCutoff">New DerivativeCutoff (Hz).</param>
    /// <param name="resetState">
    /// If true, clears PositionFilters, DerivativeFilters, and euroValuesOutput.
    /// Do this when you want motion to be recalculated fresh with the new settings.
    /// </param>
    public static void UpdateOneEuroParameters(float minCutoff, float beta, float derivativeCutoff, bool resetState = true)
    {
        EnsureInitialized();

        // Ensure no jobs are currently touching the buffers.
        if (!oneEuroJob.IsCompleted) oneEuroJob.Complete();

        // Push values to the source of truth used in Compute()
        MinCutoff = minCutoff;
        Beta = beta;
        DerivativeCutoff = derivativeCutoff;

        if (resetState)
        {
            ResetFilterStateAll();
        }
    }
    // Parameters for Euro filter
    public static float MinCutoff = 0.05f;
    public static float Beta = 0.01f;
    public static float DerivativeCutoff = 1.0f;
    /// <summary>
    /// Resets the filter state for ALL avatars/muscles.
    /// This clears the internal history so the next Compute() uses only fresh samples.
    /// </summary>
    public static void ResetFilterStateAll()
    {
        EnsureInitialized();

        if (!oneEuroJob.IsCompleted) oneEuroJob.Complete();

        int flat = FixedCapacity * _muscleCount;
        for (int i = 0; i < flat; i++)
        {
            positionFilters[i] = float2.zero;   // previous raw (x) and filtered (y)
            derivativeFilters[i] = float2.zero; // previous derivative raw (x) and filtered (y)
            euroValuesOutput[i] = 0f;           // clear last filtered output
        }
    }

    /// <summary>
    /// Resets the filter state for a single avatar index (0..FixedCapacity-1).
    /// Useful if you only want one rig to "reboot" its smoothing after a parameter tweak
    /// or a teleport/desync.
    /// </summary>
    public static void ResetFilterStateForIndex(int index)
    {
        EnsureInitialized();
        if ((uint)index >= FixedCapacity)
            throw new IndexOutOfRangeException($"index {index} is out of range [0,{FixedCapacity - 1}]");

        if (!oneEuroJob.IsCompleted) oneEuroJob.Complete();

        int baseOffset = index * _muscleCount;
        for (int m = 0; m < _muscleCount; m++)
        {
            int flat = baseOffset + m;
            positionFilters[flat] = float2.zero;
            derivativeFilters[flat] = float2.zero;
            euroValuesOutput[flat] = 0f;
        }
    }

    // ---------------- internal allocation helpers ----------------

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

        if (_prevMuscles.IsCreated) _prevMuscles.Dispose();
        if (_targetMuscles.IsCreated) _targetMuscles.Dispose();
        if (_outMuscles.IsCreated) _outMuscles.Dispose();

        if (euroValuesOutput.IsCreated) euroValuesOutput.Dispose();
        if (positionFilters.IsCreated) positionFilters.Dispose();
        if (derivativeFilters.IsCreated) derivativeFilters.Dispose();
    }
}
