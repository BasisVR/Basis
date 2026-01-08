using System;
using System.Collections.Generic;
using System.Threading;
using uLipSync;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[System.Serializable]
public unsafe class BasisUlipSync
{
    JobHandle _jobHandle;
    bool _allocated;

    // Audio ring buffers (per instance)
    NativeArray<float> _inputA, _inputB;
    volatile int _activeInputBuffer;
    volatile int _writeIndexA, _writeIndexB;
    volatile int _isDataReceived;

    // Outputs / scratch (per instance)
    NativeArray<float> _mfcc;
    NativeArray<float> _mfccForOther;

    NativeArray<float> _scores;              // phoneme scores
    NativeArray<BasisLipSyncJob.Info> _info; // volume etc

    public float globalMultiplier;
    public float MultipliedWeight;
    public float finalWeight;

    public SkinnedMeshRenderer skinnedMeshRenderer;
    public Mesh sharedMesh;

    public List<BlendShapeInfo> CachedblendShapes = new List<BlendShapeInfo>();
    public BlendShapeInfo[] BlendShapeInfos;

    public bool HasJob;
    public int blendShapeCount;

    // Workspace scratch MUST remain per-instance (mutable buffers)
    public BasisLipSyncWorkspace ws;

    [ReadOnly] public NativeArray<float> firTaps;
    [ReadOnly] public NativeArray<float> hammingWindow;
    [ReadOnly] public BasisFftPlan fftPlan;
    // These are shared plans now (do NOT Build/Dispose per instance)
    BasisMelFilterPlan _melPlan;
    BasisDctPlan _dctPlan;

    float[] _lastApplied;

    public struct BlendMap
    {
        public int blendShapeIndex;
        public int phonemeIndex;
    }

    // Blendshape mapping/state/output (per instance)
    NativeArray<BlendMap> _blendMap;
    NativeArray<float> _bsWeight;
    NativeArray<float> _bsVelocity;
    NativeArray<float> _finalByBlendShape;
    NativeArray<float> _volState;
    NativeArray<int> _drivenBlendShapes;

    [BurstCompile]
    public struct BasisBlendshapeApplyJob : IJob
    {
        [ReadOnly] public NativeArray<float> scores;
        [ReadOnly] public NativeArray<BlendMap> map;
        [ReadOnly] public NativeArray<BasisLipSyncJob.Info> info;

        public NativeArray<float> bsWeight;
        public NativeArray<float> bsVelocity;
        public NativeArray<float> volState;
        public NativeArray<float> finalByBlendShape;

        public int phonemeCount;
        public float dt;
        public float smoothness;
        public float minVolume;
        public float maxVolume;

        public void Execute()
        {
            for (int i = 0; i < finalByBlendShape.Length; i++)
                finalByBlendShape[i] = 0f;

            float rawVolume = 0f;
            if (info.IsCreated && info.Length > 0)
                rawVolume = math.max(info[0].volume, 0f);

            float normVol = 0f;
            if (rawVolume > 0f)
            {
                float logv = math.log10(rawVolume);
                float denom = math.max((maxVolume - minVolume), 1e-4f);
                normVol = math.saturate((logv - minVolume) / denom);
            }

            float volume = volState[0];

            float tau = math.max(smoothness, 1e-4f);
            float a = 1f - math.exp(-dt / tau);
            volume = math.lerp(volume, normVol, a);
            volState[0] = volume;

            float globalMultiplier = volume * 100f;

            float total = 0f;
            for (int i = 0; i < map.Length; i++)
            {
                int p = map[i].phonemeIndex;
                float target = ((uint)p < (uint)phonemeCount) ? scores[p] : 0f;

                float w = bsWeight[i];
                w = math.lerp(w, target, a);
                bsWeight[i] = w;

                total += w;
            }

            float baseMultiply = (math.abs(total) > 1e-6f) ? (globalMultiplier / total) : globalMultiplier;

            for (int i = 0; i < map.Length; i++)
            {
                int bsIndex = map[i].blendShapeIndex;
                if ((uint)bsIndex >= (uint)finalByBlendShape.Length) continue;

                float fw = bsWeight[i] * baseMultiply;
                fw = math.clamp(fw, 0f, 100f);
                if (!math.isfinite(fw)) fw = 0f;

                finalByBlendShape[bsIndex] = fw;
            }
        }
    }

    public void Simulate(float DeltaTime)
    {
        if (Interlocked.Exchange(ref _isDataReceived, 0) != 1) return;
        if (!_allocated) return;

        // Must have shared cache ready
        if (!BasisUlipSyncDriver.IsInitialized)
            return;

        int oldActive = _activeInputBuffer;
        int newActive = oldActive ^ 1;

        Volatile.Write(ref _activeInputBuffer, newActive);

        int frozenStartIndex = oldActive == 0
            ? Volatile.Read(ref _writeIndexA)
            : Volatile.Read(ref _writeIndexB);

        NativeArray<float> frozenInput = oldActive == 0 ? _inputA : _inputB;

        byte normalizeScores = (byte)0;

        // Use SHARED blobs + SHARED plans
        var scoreJob = new BasisLipSyncJob
        {
            input = frozenInput,
            startIndex = frozenStartIndex,

            outputSampleRate = BasisUlipSyncDriver.outputSampleRate,
            targetSampleRate = BasisUlipSyncDriver.targetSampleRate,

            means = BasisUlipSyncDriver.SharedMeans,
            standardDeviations = BasisUlipSyncDriver.SharedStd,
            invStd = BasisUlipSyncDriver.SharedInvStd,
            phonemesZ = BasisUlipSyncDriver.SharedPhonemesZ,
            phonemeNorms = BasisUlipSyncDriver.SharedPhonemeNorms,
            compareMethod = BasisUlipSyncDriver.compareMethod,

            mfcc = _mfcc,
            scores = _scores,
            info = _info,

            restPhonemeIndex = 0,

            ws = ws,
            melPlan = _melPlan,
            dctPlan = _dctPlan,
             firTaps = firTaps,
              hammingWindow = hammingWindow,

            normalizeScores = normalizeScores,
        };

        JobHandle h0 = scoreJob.Schedule();

        if (_finalByBlendShape.IsCreated && _blendMap.IsCreated && _blendMap.Length > 0 &&
            _bsWeight.IsCreated && _volState.IsCreated && _info.IsCreated)
        {
            var applyJob = new BasisBlendshapeApplyJob
            {
                scores = _scores,
                map = _blendMap,
                info = _info,

                bsWeight = _bsWeight,
                bsVelocity = _bsVelocity,
                volState = _volState,

                finalByBlendShape = _finalByBlendShape,

                phonemeCount = BasisUlipSyncDriver.phonemeCount,
                dt = DeltaTime,
                smoothness = BasisUlipSyncDriver.smoothness,
                minVolume = BasisUlipSyncDriver.minVolume,
                maxVolume = BasisUlipSyncDriver.maxVolume,
            };

            _jobHandle = applyJob.Schedule(h0);
        }
        else
        {
            _jobHandle = h0;
        }

        HasJob = true;
    }

    public void Apply()
    {
        if (!HasJob)
        {
            return;
        }

        HasJob = false;
        _jobHandle.Complete();

        if (_mfccForOther.IsCreated && _mfcc.IsCreated)
        {
            _mfccForOther.CopyFrom(_mfcc);
        }

        if (!_finalByBlendShape.IsCreated || _finalByBlendShape.Length != blendShapeCount)
        {
            return;
        }

        if (_lastApplied == null || _lastApplied.Length != blendShapeCount)
        {
            _lastApplied = new float[blendShapeCount];
        }

        if (!_drivenBlendShapes.IsCreated || _drivenBlendShapes.Length == 0)
        {
            return;
        }

        int length = _drivenBlendShapes.Length;
        for (int i = 0; i < length; i++)
        {
            int bsIndex = _drivenBlendShapes[i];
            if ((uint)bsIndex >= (uint)blendShapeCount)
            {
                continue;
            }

            float fw = _finalByBlendShape[bsIndex];
            float prev = _lastApplied[bsIndex];
            float d = fw - prev;

            if (d == 0f)
            {
                continue;
            }

            if (d * d > BasisUlipSyncDriver.BlendshapeWriteEps * BasisUlipSyncDriver.BlendshapeWriteEps)
            {
                skinnedMeshRenderer.SetBlendShapeWeight(bsIndex, fw);
                _lastApplied[bsIndex] = fw;
            }
        }
    }

    public void Initalize()
    {
        if (_allocated) DisposeBuffers();

        if (!_jobHandle.Equals(default(JobHandle)))
        {
            _jobHandle.Complete();
            _jobHandle = default;
        }

        // Must be initialized externally: BasisUlipSyncDriver.Initialize(profile)
        if (!BasisUlipSyncDriver.IsInitialized)
        {
            _allocated = false;
            return;
        }

        _allocated = true;

        SafeCreate(ref _inputA, BasisUlipSyncDriver.CachedInputSampleCount, NativeArrayOptions.UninitializedMemory);
        SafeCreate(ref _inputB, BasisUlipSyncDriver.CachedInputSampleCount, NativeArrayOptions.UninitializedMemory);
        _activeInputBuffer = 0;
        _writeIndexA = 0;
        _writeIndexB = 0;
        _isDataReceived = 0;

        SafeCreate(ref _mfcc, BasisUlipSyncDriver.mfccLen);
        SafeCreate(ref _mfccForOther, BasisUlipSyncDriver.mfccLen);

        SafeCreate(ref _scores, BasisUlipSyncDriver.phonemeCount);
        SafeCreate(ref _info, 1);

        // Shared plans (no per-instance Build)
        _melPlan = BasisUlipSyncDriver.SharedMelPlan;
        _dctPlan = BasisUlipSyncDriver.SharedDctPlan;

        firTaps = BasisUlipSyncDriver.SharedFirTaps;
        hammingWindow = BasisUlipSyncDriver.SharedHammingWindow;
        // Per-instance workspace scratch (mutable)
        ws = BasisLipSyncWorkspace.Create(
            inputLen: BasisUlipSyncDriver.CachedInputSampleCount,
            outputSampleRate: BasisUlipSyncDriver.outputSampleRate,
            targetSampleRate: BasisUlipSyncDriver.targetRate,
            melDiv: BasisUlipSyncDriver.melDiv,
            mfccLen: BasisUlipSyncDriver.mfccLen,
            fftN: BasisUlipSyncDriver.fftN,
            allocator: Allocator.Persistent
        );

        // Map phoneme -> index using SHARED dictionary
        if (BlendShapeInfos != null)
        {
            for (int Index = 0; Index < BlendShapeInfos.Length; Index++)
            {
                var bs = BlendShapeInfos[Index];
                bs.phonemeIndex = (!string.IsNullOrEmpty(bs.phoneme) && BasisUlipSyncDriver.PhonemeNameToIndex.TryGetValue(bs.phoneme, out int idx)) ? idx : -1;

                BlendShapeInfos[Index] = bs;
            }
        }
        SafeDispose(ref _blendMap);
        SafeDispose(ref _bsWeight);
        SafeDispose(ref _bsVelocity);
        SafeDispose(ref _finalByBlendShape);
        SafeDispose(ref _volState);
        SafeDispose(ref _drivenBlendShapes);

        int blendInfoCount = (BlendShapeInfos != null) ? BlendShapeInfos.Length : 0;
        if (blendShapeCount <= 0 || blendInfoCount <= 0)
        {
            return;
        }

        _blendMap = new NativeArray<BlendMap>(blendInfoCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _bsWeight = new NativeArray<float>(blendInfoCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        _bsVelocity = new NativeArray<float>(blendInfoCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        _finalByBlendShape = new NativeArray<float>(blendShapeCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        _volState = new NativeArray<float>(2, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        for (int Index = 0; Index < blendInfoCount; Index++)
        {
            var bs = BlendShapeInfos[Index];
            _blendMap[Index] = new BlendMap
            {
                blendShapeIndex = bs.index,
                phonemeIndex = bs.phonemeIndex
            };
        }

        HashSet<int> driven = new HashSet<int>();
        for (int Index = 0; Index < blendInfoCount; Index++)
        {
            int idx = BlendShapeInfos[Index].index;
            if ((uint)idx < (uint)blendShapeCount)
            {
                driven.Add(idx);
            }
        }

        int drivenCount = driven.Count;
        if (drivenCount > 0)
        {
            _drivenBlendShapes = new NativeArray<int>(drivenCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int w = 0;
            foreach (var idx in driven)
            {
                _drivenBlendShapes[w++] = idx;
            }
        }

        _lastApplied = null;
    }
    public void DisposeBuffers()
    {
        _allocated = false;

        if (!_jobHandle.Equals(default(JobHandle)))
        {
            _jobHandle.Complete();
            _jobHandle = default;
        }

        SafeDispose(ref _inputA);
        SafeDispose(ref _inputB);

        SafeDispose(ref _mfcc);
        SafeDispose(ref _mfccForOther);

        SafeDispose(ref _scores);
        SafeDispose(ref _info);

        SafeDispose(ref _blendMap);
        SafeDispose(ref _bsWeight);
        SafeDispose(ref _bsVelocity);
        SafeDispose(ref _finalByBlendShape);
        SafeDispose(ref _volState);
        SafeDispose(ref _drivenBlendShapes);

        // Do NOT dispose _melPlan/_dctPlan (shared)
        _melPlan = default;
        _dctPlan = default;

        if (ws.IsCreated) ws.Dispose();

        _lastApplied = null;
    }

    // Audio thread -> ring buffer
    public void OnDataReceived(float[] input, int channels, int length)
    {
        if (!_allocated || input == null || length <= 0) return;

        int cap = BasisUlipSyncDriver.CachedInputSampleCount;
        if (cap <= 0) return;

        int ch = math.max(channels, 1);

        int buf = Volatile.Read(ref _activeInputBuffer);
        NativeArray<float> dstArr = (buf == 0) ? _inputA : _inputB;

        float* dst = (float*)NativeArrayUnsafeUtility.GetUnsafePtr(dstArr);

        fixed (float* src = input)
        {
            int w = (buf == 0) ? Volatile.Read(ref _writeIndexA) : Volatile.Read(ref _writeIndexB);

            for (int s = 0; s < length; s += ch)
            {
                dst[w] = src[s];
                w++;
                if (w == cap) w = 0;
            }

            if (buf == 0) Volatile.Write(ref _writeIndexA, w);
            else Volatile.Write(ref _writeIndexB, w);
        }

        Interlocked.Exchange(ref _isDataReceived, 1);
    }

    static void SafeCreate<T>(ref NativeArray<T> array, int length,
        NativeArrayOptions options = NativeArrayOptions.ClearMemory) where T : struct
    {
        if (array.IsCreated)
        {
            if (array.Length == length) return;
            array.Dispose();
        }
        array = new NativeArray<T>(length, Allocator.Persistent, options);
    }

    static void SafeDispose<T>(ref NativeArray<T> array) where T : struct
    {
        if (array.IsCreated) array.Dispose();
        array = default;
    }

    public void AddBlendShape(string phoneme, int blendShape)
    {
        var bs = CachedblendShapes.Find(info => info.phoneme == phoneme);
        if (bs == null)
        {
            bs = new BlendShapeInfo { phoneme = phoneme };
            CachedblendShapes.Add(bs);
        }
        if (skinnedMeshRenderer != null) bs.index = blendShape;
    }
}
