using Basis.Scripts.Networking;
using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Remote network driver that:
/// 1) Interpolates prev->target pose (pos/scale/rot) per remote player
/// 2) 1€-filters pose position + rotation per player (packed state; job-safety friendly)
/// 3) Interpolates muscles and 1€-filters them (existing behavior)
/// 4) Computes scaled body position for HumanPose.bodyPosition
/// </summary>
public static class BasisRemoteNetworkDriver
{
    public const int FixedCapacity = 1024;

    // ---------------- INPUTS (prev/target) ----------------
    static NativeArray<float3> _prevPositions;
    static NativeArray<float3> _targetPositions;

    static NativeArray<float3> _prevScales;
    static NativeArray<float3> _targetScales;

    static NativeArray<quaternion> _prevRotations;
    static NativeArray<quaternion> _targetRotations;

    // 0..1 interpolation factor per player
    static NativeArray<double> _interpolationTimes;

    // EFFECTIVE dt seconds per player (IMPORTANT: should include playback rate when catching up)
    static NativeArray<double> _deltaTimes;

    // ---------------- RAW INTERPOLATED OUTPUTS ----------------
    static NativeArray<float3> _outPositions;
    static NativeArray<float3> _outScales;
    static NativeArray<quaternion> _outRotations;

    // ---------------- FILTERED POSE OUTPUTS ----------------
    static NativeArray<float3> _filteredPositions;
    static NativeArray<quaternion> _filteredRotations;

    // Seed flag per player to avoid "ease in from identity"
    static NativeArray<byte> _poseFilterSeeded;

    // Packed position 1€ state per player (ParallelFor-safe)
    static NativeArray<float3> _posPrevRaw;
    static NativeArray<float3> _posPrevFiltered;
    static NativeArray<float3> _posPrevDerivFiltered;

    // Packed rotation 1€ state per player
    static NativeArray<quaternion> _rotPrevRaw;
    static NativeArray<quaternion> _rotPrevFiltered;
    static NativeArray<float2> _rotDerivFilter; // x=prevDerivRaw, y=prevDerivFiltered (scalar omega)

    // ---------------- SCALED BODY ----------------
    static NativeArray<float> _humanScales;
    static NativeArray<float3> _scaledBodyPositions;

    // ---------------- SCALE CHANGE ----------------
    static NativeArray<bool> _HasScaleChange;

    // ---------------- MUSCLES ----------------
    static NativeArray<float> _prevMuscles;
    static NativeArray<float> _targetMuscles;
    static NativeArray<float> _outMuscles;

    // 1€ muscle filter buffers (flattened players * muscles)
    static NativeArray<float> euroValuesOutput;
    static NativeArray<float2> positionFilters;
    static NativeArray<float2> derivativeFilters;

    // State
    static int _muscleCount;
    static bool _initialized;
    static Allocator _allocator = Allocator.Persistent;

    public static JobHandle oneEuroJob;

    // ---------------- TUNING ----------------
    // Pose (position + rotation) smoothing: usually higher MinCutoff than muscles to reduce "floaty" lag.
    public static float PoseMinCutoff = 3.0f;
    public static float PoseBeta = 0.10f;
    public static float PoseDerivativeCutoff = 1.0f;

    /// <summary>Initialize the driver. Must be called before use.</summary>
    public static void Initialize(int muscleCount, Allocator allocator = Allocator.Persistent)
    {
        if (_initialized) return;
        if (muscleCount <= 0) throw new ArgumentOutOfRangeException(nameof(muscleCount));

        _allocator = allocator;
        _muscleCount = muscleCount;

        AllocateAll(FixedCapacity);

        // Seed defaults
        for (int i = 0; i < FixedCapacity; i++)
        {
            _prevPositions[i] = float3.zero;
            _targetPositions[i] = float3.zero;

            _prevScales[i] = new float3(1, 1, 1);
            _targetScales[i] = new float3(1, 1, 1);

            _prevRotations[i] = quaternion.identity;
            _targetRotations[i] = quaternion.identity;

            _interpolationTimes[i] = 0.0;
            _deltaTimes[i] = 1.0 / 60.0;

            _outPositions[i] = float3.zero;
            _outScales[i] = new float3(1, 1, 1);
            _outRotations[i] = quaternion.identity;

            _filteredPositions[i] = float3.zero;
            _filteredRotations[i] = quaternion.identity;

            _poseFilterSeeded[i] = 0;

            _posPrevRaw[i] = float3.zero;
            _posPrevFiltered[i] = float3.zero;
            _posPrevDerivFiltered[i] = float3.zero;

            _rotPrevRaw[i] = quaternion.identity;
            _rotPrevFiltered[i] = quaternion.identity;
            _rotDerivFilter[i] = float2.zero;

            _HasScaleChange[i] = false;

            _humanScales[i] = 1f;
            _scaledBodyPositions[i] = float3.zero;
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

    public static void Shutdown()
    {
        if (!_initialized) return;

        if (!oneEuroJob.IsCompleted)
            oneEuroJob.Complete();

        DisposeAll();
        _muscleCount = 0;
        _initialized = false;
    }

    /// <summary>
    /// Write timing inputs for a given index (0..FixedCapacity-1).
    /// interpolationTime is 0..1, deltaTimeSeconds is EFFECTIVE dt seconds (should include playback rate).
    /// </summary>
    public static void SetFrameTiming(int index, double interpolationTime, double deltaTimeSeconds)
    {
        if (!_initialized) return;
        _interpolationTimes[index] = interpolationTime;
        _deltaTimes[index] = deltaTimeSeconds;
    }

    public static void SetFrameInputs(
        int index,
        float humanScale,
        float3 prevPos, float3 targetPos,
        float3 prevScale, float3 targetScale,
        quaternion prevRot, quaternion targetRot,
        NativeArray<float> prevMuscles, NativeArray<float> targetMuscles)
    {
        if (!_initialized) return;

        _humanScales[index] = humanScale;

        _prevPositions[index] = prevPos;
        _targetPositions[index] = targetPos;

        _prevScales[index] = prevScale;
        _targetScales[index] = targetScale;

        _prevRotations[index] = prevRot;
        _targetRotations[index] = targetRot;

        int baseOffset = index * _muscleCount;
        FastCopyMuscles(prevMuscles, 0, _prevMuscles, baseOffset, _muscleCount);
        FastCopyMuscles(targetMuscles, 0, _targetMuscles, baseOffset, _muscleCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void FastCopyMuscles(NativeArray<float> src, int srcStart, NativeArray<float> dst, int dstStart, int count)
    {
        var bytes = (long)count * sizeof(float);
        var srcPtr = (byte*)src.GetUnsafeReadOnlyPtr() + (long)srcStart * sizeof(float);
        var dstPtr = (byte*)dst.GetUnsafePtr() + (long)dstStart * sizeof(float);
        UnsafeUtility.MemCpy(dstPtr, srcPtr, bytes);
    }

    /// <summary>Schedule jobs for the current frame (does not complete them).</summary>
    public static void Compute()
    {
        if (!_initialized) return;
        if (BasisNetworkPlayers.ReceiverCount == 0) return;

        int num = BasisNetworkPlayers.LargestNetworkReceiverID + 1;
        num = math.clamp(num, 0, FixedCapacity);

        // 1) Raw interpolation (pos/scale/rot)
        var avatarJob = new UpdateAllAvatarsJob
        {
            PreviousPositions = _prevPositions,
            TargetPositions = _targetPositions,

            PreviousScales = _prevScales,
            TargetScales = _targetScales,

            PreviousRotations = _prevRotations,
            TargetRotations = _targetRotations,

            InterpolationTimes = _interpolationTimes,

            HasScaleChange = _HasScaleChange,

            OutputPositions = _outPositions,
            OutputScales = _outScales,
            OutputRotations = _outRotations
        }.Schedule(num, 128);

        // 2) Pose filtering (position + rotation) per player (packed state => job-safety OK)
        JobHandle poseFilterJob = new FilterPoseOneEuroJob
        {
            InputPositions = _outPositions,
            InputRotations = _outRotations,

            OutputPositions = _filteredPositions,
            OutputRotations = _filteredRotations,

            DeltaTimeSeconds = _deltaTimes,

            PoseFilterSeeded = _poseFilterSeeded,

            PosPrevRaw = _posPrevRaw,
            PosPrevFiltered = _posPrevFiltered,
            PosPrevDerivFiltered = _posPrevDerivFiltered,

            RotPrevRaw = _rotPrevRaw,
            RotPrevFiltered = _rotPrevFiltered,
            RotDerivFilter = _rotDerivFilter,

            MinCutoff = PoseMinCutoff,
            Beta = PoseBeta,
            DerivativeCutoff = PoseDerivativeCutoff
        }.Schedule(num, 128, avatarJob);

        // 3) Scaled body position uses FILTERED position
        var scaledBodyJob = new ComputeScaledBodyJob
        {
            OutputPositions = _filteredPositions,
            OutputScales = _outScales,
            HumanScales = _humanScales,
            ScaledBodyPositions = _scaledBodyPositions
        }.Schedule(num, 128, poseFilterJob);

        // 4) Muscle interpolation (raw)
        JobHandle musclesJob = new UpdateAllAvatarMusclesJob
        {
            PreviousMuscles = _prevMuscles,
            TargetMuscles = _targetMuscles,
            InterpolationTimes = _interpolationTimes,
            OutputMuscles = _outMuscles,
            MuscleCountPerAvatar = _muscleCount
        }.Schedule(num * _muscleCount, 128, avatarJob);

        // 5) Muscle 1€ filter (uses BasisNetworkManagement knobs)
        JobHandle euroMusclesJob = new BasisOneEuroFilterParallelJob
        {
            InputValues = _outMuscles,
            OutputValues = euroValuesOutput,
            DeltaTimeSeconds = _deltaTimes,
            MinCutoff = BasisNetworkManagement.MinCutoff,
            Beta = BasisNetworkManagement.Beta,
            DerivativeCutoff = BasisNetworkManagement.DerivativeCutoff,
            PositionFilters = positionFilters,
            DerivativeFilters = derivativeFilters,
            MuscleCountPerAvatar = _muscleCount
        }.Schedule(num * _muscleCount, 128, musclesJob);

        oneEuroJob = JobHandle.CombineDependencies(euroMusclesJob, scaledBodyJob);
    }

    /// <summary>Complete scheduled jobs for the current frame.</summary>
    public static void Apply()
    {
        if (!_initialized) return;
        oneEuroJob.Complete();
    }

    // ---------------- OUTPUT GETTERS ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetPositionOutput(int index, out float3 outPos) => outPos = _filteredPositions[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetScaleOutput(int index, out float3 outScale) => outScale = _outScales[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GetMuscleArray(
        int index,
        out bool outScale,
        out quaternion outRot,
        out float3 BodyPosition,
        ref HumanPose poseData,
        float[] eyesAndMouth,
        int eyesAndMouthOffsetFloats,
        int eyesAndMouthCountBytes)
    {
        outScale = _HasScaleChange[index];
        outRot = _filteredRotations[index];
        BodyPosition = _scaledBodyPositions[index];

        int baseOffset = index * _muscleCount;
        unsafe
        {
            float* src = (float*)euroValuesOutput.GetUnsafeReadOnlyPtr() + baseOffset;
            fixed (float* dst = poseData.muscles)
            {
                UnsafeUtility.MemCpy(dst, src, _muscleCount * sizeof(float));
                fixed (float* em = eyesAndMouth)
                {
                    UnsafeUtility.MemCpy(dst + eyesAndMouthOffsetFloats, em, eyesAndMouthCountBytes);
                }
            }
        }
    }

    // ---------------- MEMORY ----------------

    static void AllocateAll(int capacity)
    {
        _prevPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        _prevScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        _prevRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        _interpolationTimes = new NativeArray<double>(capacity, _allocator, NativeArrayOptions.ClearMemory);
        _deltaTimes = new NativeArray<double>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        _outPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _outScales = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _outRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        _filteredPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _filteredRotations = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        _poseFilterSeeded = new NativeArray<byte>(capacity, _allocator, NativeArrayOptions.ClearMemory);

        _posPrevRaw = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _posPrevFiltered = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _posPrevDerivFiltered = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        _rotPrevRaw = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _rotPrevFiltered = new NativeArray<quaternion>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _rotDerivFilter = new NativeArray<float2>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        _humanScales = new NativeArray<float>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);
        _scaledBodyPositions = new NativeArray<float3>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        _HasScaleChange = new NativeArray<bool>(capacity, _allocator, NativeArrayOptions.UninitializedMemory);

        int flat = capacity * _muscleCount;
        _prevMuscles = new NativeArray<float>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _targetMuscles = new NativeArray<float>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        _outMuscles = new NativeArray<float>(flat, _allocator, NativeArrayOptions.UninitializedMemory);

        euroValuesOutput = new NativeArray<float>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        positionFilters = new NativeArray<float2>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
        derivativeFilters = new NativeArray<float2>(flat, _allocator, NativeArrayOptions.UninitializedMemory);
    }

    static void DisposeAll()
    {
        if (_prevPositions.IsCreated) _prevPositions.Dispose();
        if (_targetPositions.IsCreated) _targetPositions.Dispose();

        if (_prevScales.IsCreated) _prevScales.Dispose();
        if (_targetScales.IsCreated) _targetScales.Dispose();

        if (_prevRotations.IsCreated) _prevRotations.Dispose();
        if (_targetRotations.IsCreated) _targetRotations.Dispose();

        if (_interpolationTimes.IsCreated) _interpolationTimes.Dispose();
        if (_deltaTimes.IsCreated) _deltaTimes.Dispose();

        if (_outPositions.IsCreated) _outPositions.Dispose();
        if (_outScales.IsCreated) _outScales.Dispose();
        if (_outRotations.IsCreated) _outRotations.Dispose();

        if (_filteredPositions.IsCreated) _filteredPositions.Dispose();
        if (_filteredRotations.IsCreated) _filteredRotations.Dispose();

        if (_poseFilterSeeded.IsCreated) _poseFilterSeeded.Dispose();

        if (_posPrevRaw.IsCreated) _posPrevRaw.Dispose();
        if (_posPrevFiltered.IsCreated) _posPrevFiltered.Dispose();
        if (_posPrevDerivFiltered.IsCreated) _posPrevDerivFiltered.Dispose();

        if (_rotPrevRaw.IsCreated) _rotPrevRaw.Dispose();
        if (_rotPrevFiltered.IsCreated) _rotPrevFiltered.Dispose();
        if (_rotDerivFilter.IsCreated) _rotDerivFilter.Dispose();

        if (_humanScales.IsCreated) _humanScales.Dispose();
        if (_scaledBodyPositions.IsCreated) _scaledBodyPositions.Dispose();

        if (_prevMuscles.IsCreated) _prevMuscles.Dispose();
        if (_targetMuscles.IsCreated) _targetMuscles.Dispose();
        if (_outMuscles.IsCreated) _outMuscles.Dispose();

        if (euroValuesOutput.IsCreated) euroValuesOutput.Dispose();
        if (positionFilters.IsCreated) positionFilters.Dispose();
        if (derivativeFilters.IsCreated) derivativeFilters.Dispose();

        if (_HasScaleChange.IsCreated) _HasScaleChange.Dispose();
    }

    // ---------------- JOBS ----------------

    [BurstCompile]
    public struct UpdateAllAvatarsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> PreviousPositions;
        [ReadOnly] public NativeArray<float3> TargetPositions;

        [ReadOnly] public NativeArray<float3> PreviousScales;
        [ReadOnly] public NativeArray<float3> TargetScales;

        [ReadOnly] public NativeArray<quaternion> PreviousRotations;
        [ReadOnly] public NativeArray<quaternion> TargetRotations;

        [ReadOnly] public NativeArray<double> InterpolationTimes;

        [WriteOnly] public NativeArray<float3> OutputPositions;
        [WriteOnly] public NativeArray<float3> OutputScales;
        [WriteOnly] public NativeArray<quaternion> OutputRotations;

        [WriteOnly] public NativeArray<bool> HasScaleChange;

        public void Execute(int index)
        {
            float t = (float)InterpolationTimes[index];
            if (!math.isfinite(t)) t = 0f;
            t = math.clamp(t, 0f, 1f);

            OutputPositions[index] = math.lerp(PreviousPositions[index], TargetPositions[index], t);
            OutputScales[index] = math.lerp(PreviousScales[index], TargetScales[index], t);

            // nlerp is cheap and fine for small deltas
            OutputRotations[index] = math.normalize(math.nlerp(PreviousRotations[index], TargetRotations[index], t));

            const float scaleEpsSq = 1e-10f;
            float3 prevS = PreviousScales[index];
            float3 targS = TargetScales[index];
            HasScaleChange[index] = math.lengthsq(targS - prevS) > scaleEpsSq;
        }
    }

    /// <summary>
    /// 1€ filtering for pose position + rotation, using packed per-player state.
    /// Job-safety friendly: every RW array is indexed ONLY by playerIndex.
    /// </summary>
    [BurstCompile]
    public struct FilterPoseOneEuroJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> InputPositions;
        [ReadOnly] public NativeArray<quaternion> InputRotations;

        [WriteOnly] public NativeArray<float3> OutputPositions;
        [WriteOnly] public NativeArray<quaternion> OutputRotations;

        [ReadOnly] public NativeArray<double> DeltaTimeSeconds;

        public NativeArray<byte> PoseFilterSeeded;

        public NativeArray<float3> PosPrevRaw;
        public NativeArray<float3> PosPrevFiltered;
        public NativeArray<float3> PosPrevDerivFiltered;

        public NativeArray<quaternion> RotPrevRaw;
        public NativeArray<quaternion> RotPrevFiltered;
        public NativeArray<float2> RotDerivFilter; // scalar omega derivative filter state

        public float MinCutoff;
        public float Beta;
        public float DerivativeCutoff;

        public void Execute(int playerIndex)
        {
            double dt = math.max(DeltaTimeSeconds[playerIndex], 1e-3);
            double freq = math.rcp(dt);

            float3 rawPos = InputPositions[playerIndex];
            quaternion rawRot = math.normalize(InputRotations[playerIndex]);

            // Seed first sample to prevent "ease in from identity"
            if (PoseFilterSeeded[playerIndex] == 0)
            {
                PoseFilterSeeded[playerIndex] = 1;

                PosPrevRaw[playerIndex] = rawPos;
                PosPrevFiltered[playerIndex] = rawPos;
                PosPrevDerivFiltered[playerIndex] = float3.zero;

                RotPrevRaw[playerIndex] = rawRot;
                RotPrevFiltered[playerIndex] = rawRot;
                RotDerivFilter[playerIndex] = float2.zero;

                OutputPositions[playerIndex] = rawPos;
                OutputRotations[playerIndex] = rawRot;
                return;
            }

            // ---------------- POSITION 1€ (per-axis) ----------------
            float3 prevRaw = PosPrevRaw[playerIndex];
            float3 prevFiltered = PosPrevFiltered[playerIndex];
            float3 prevDerivFiltered = PosPrevDerivFiltered[playerIndex];

            float3 dValue = (rawPos - prevRaw) * (float)freq;

            double alphaD = Alpha(DerivativeCutoff, freq);
            float3 edValue = (float)alphaD * dValue + (1f - (float)alphaD) * prevDerivFiltered;

            float3 cutoff = MinCutoff + Beta * math.abs(edValue);

            float3 alphaX = new float3(
                (float)Alpha(cutoff.x, freq),
                (float)Alpha(cutoff.y, freq),
                (float)Alpha(cutoff.z, freq)
            );

            float3 filteredPos = alphaX * rawPos + (new float3(1f) - alphaX) * prevFiltered;

            PosPrevRaw[playerIndex] = rawPos;
            PosPrevFiltered[playerIndex] = filteredPos;
            PosPrevDerivFiltered[playerIndex] = edValue;

            OutputPositions[playerIndex] = filteredPos;

            // ---------------- ROTATION 1€ (alpha from angular speed) ----------------
            quaternion prevRawQ = RotPrevRaw[playerIndex];
            quaternion prevFiltQ = RotPrevFiltered[playerIndex];

            // qDelta = raw * inverse(prevRaw)
            quaternion qDelta = math.mul(rawRot, math.conjugate(prevRawQ));
            // shortest path
            if (qDelta.value.w < 0f) qDelta.value = -qDelta.value;

            float w = math.clamp(qDelta.value.w, -1f, 1f);
            float angle = 2f * math.acos(w);      // radians
            double omega = (double)angle * freq;  // rad/s

            float2 rdf = RotDerivFilter[playerIndex];
            double alphaDR = Alpha(DerivativeCutoff, freq);
            double edOmega = alphaDR * omega + (1.0 - alphaDR) * (double)rdf.y;

            rdf.x = (float)omega;
            rdf.y = (float)edOmega;
            RotDerivFilter[playerIndex] = rdf;

            double cutoffR = MinCutoff + Beta * math.abs(edOmega);
            double alphaQ = Alpha(cutoffR, freq);

            quaternion filtQ = math.normalize(math.nlerp(prevFiltQ, rawRot, (float)alphaQ));

            OutputRotations[playerIndex] = filtQ;

            RotPrevRaw[playerIndex] = rawRot;
            RotPrevFiltered[playerIndex] = filtQ;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Alpha(double cutoff, double frequency)
        {
            double te = math.rcp(frequency);
            double tau = math.rcp(2.0 * math.PI * math.max(cutoff, 1e-4));
            return math.rcp(1.0 + tau / te);
        }
    }

    [BurstCompile]
    public struct UpdateAllAvatarMusclesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> PreviousMuscles;
        [ReadOnly] public NativeArray<float> TargetMuscles;
        [ReadOnly] public NativeArray<double> InterpolationTimes;

        [WriteOnly] public NativeArray<float> OutputMuscles;

        public int MuscleCountPerAvatar;

        public void Execute(int index)
        {
            int playerIndex = index / MuscleCountPerAvatar;
            double t = InterpolationTimes[playerIndex];
            t = math.clamp(t, 0f, 1f);
            OutputMuscles[index] = (float)math.lerp(PreviousMuscles[index], TargetMuscles[index], t);
        }
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
        [ReadOnly] public NativeArray<float> InputValues;
        [WriteOnly] public NativeArray<float> OutputValues;

        // per-player dt
        [ReadOnly] public NativeArray<double> DeltaTimeSeconds;

        // per-value filter state
        public NativeArray<float2> PositionFilters;   // x = previous input, y = previous output
        public NativeArray<float2> DerivativeFilters; // x = previous derivative input, y = previous derivative output

        public float MinCutoff;
        public float Beta;
        public float DerivativeCutoff;

        [ReadOnly] public int MuscleCountPerAvatar;

        public void Execute(int index)
        {
            int playerIndex = MuscleCountPerAvatar > 0 ? (index / MuscleCountPerAvatar) : 0;

            double dt = math.max(DeltaTimeSeconds[playerIndex], 1e-3);
            double frequency = math.rcp(dt);

            float inputValue = InputValues[index];

            float prevFiltered = PositionFilters[index].y;
            float prevRaw = PositionFilters[index].x;

            double dValue = ((inputValue - prevRaw) * frequency);

            double alphaD = Alpha(DerivativeCutoff, frequency);
            float prevDerivFiltered = DerivativeFilters[index].y;
            double edValue = alphaD * dValue + (1.0 - alphaD) * (double)prevDerivFiltered;

            double cutoff = MinCutoff + Beta * math.abs(edValue);
            double alphaX = Alpha(cutoff, frequency);

            double filtered = alphaX * (double)inputValue + (1.0 - alphaX) * (double)prevFiltered;

            OutputValues[index] = (float)filtered;
            PositionFilters[index] = new float2(inputValue, (float)filtered);
            DerivativeFilters[index] = new float2((float)dValue, (float)edValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Alpha(double cutoff, double frequency)
        {
            double te = math.rcp(frequency);
            double tau = math.rcp(2.0 * math.PI * math.max(cutoff, 1e-4));
            return math.rcp(1.0 + tau / te);
        }
    }

    /// <summary>Guarded divide + scaled body position (Burst).</summary>
    [BurstCompile]
    public struct ComputeScaledBodyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> OutputPositions;
        [ReadOnly] public NativeArray<float3> OutputScales;
        [ReadOnly] public NativeArray<float> HumanScales;

        [WriteOnly] public NativeArray<float3> ScaledBodyPositions;

        public void Execute(int Index)
        {
            const float eps = 1e-6f;

            float3 applyScale = OutputScales[Index];
            float baseScale = HumanScales[Index];

            bool baseBad = !math.isfinite(baseScale) | (math.abs(baseScale) <= eps);
            float invBase = math.select(math.rcp(baseScale), 1f, baseBad);

            // Per-component guard for applyScale
            bool3 validApply = math.isfinite(applyScale) & (math.abs(applyScale) > eps);

            float3 safe = new float3(invBase);
            float3 safeDiv = math.select(safe, safe / applyScale, validApply);

            ScaledBodyPositions[Index] = OutputPositions[Index] * safeDiv;
        }
    }
}
