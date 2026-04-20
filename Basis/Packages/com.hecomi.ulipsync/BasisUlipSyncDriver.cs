using System;
using System.Collections.Generic;
using uLipSync;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

public static unsafe class BasisUlipSyncDriver
{
    public static Profile Profile;

    // Profile-derived constants
    public static int phonemeCount;
    public static int outputSampleRate;
    public static int PhonemesCount;
    public static int CachedInputSampleCount;
    public static int mfccLen;
    public static int Count;
    public static int targetRate;
    public static int melDiv;
    public static int Max;
    public static int targetSampleRate;

    // Derived DSP sizing (shared)
    public static int downLen;
    public static int fftN;
    public static int specLen;
    public static int firLen;

    // Tuning
    public const float BlendshapeWriteEps = 0.25f;
    public const float smoothness = 0.05f;
    public const float minVolume = -2.5f;
    public const float maxVolume = -1.5f;
    public const float Z_EPS = 1e-12f;

    // Shared phoneme/profile blobs
    public static NativeArray<float> SharedMeans;        // [mfccLen]
    public static NativeArray<float> SharedStd;          // [mfccLen]
    public static NativeArray<float> SharedInvStd;       // [mfccLen]
    public static NativeArray<float> SharedPhonemesRaw;  // [mfccLen * phonemeCount]
    public static NativeArray<float> SharedPhonemesZ;    // [mfccLen * phonemeCount]
    public static NativeArray<float> SharedPhonemeNorms; // [phonemeCount]

    // Shared DSP constants
    public static NativeArray<float> SharedFirTaps;       // [firLen]
    public static NativeArray<float> SharedHammingWindow; // [fftN]
    public static BasisFftPlan SharedFftPlan;             // built for fftN

    // Shared plans
    public static BasisMelFilterPlan SharedMelPlan; // needs fftN, sampleRate, melDiv
    public static BasisDctPlan SharedDctPlan;       // needs melDiv, mfccLen

    // Shared phoneme name map
    public static readonly Dictionary<string, int> PhonemeNameToIndex = new Dictionary<string, int>(64);
    public static CompareMethod compareMethod;
    static bool _initialized;
    public static bool IsInitialized => _initialized;
    public static MfccData[] mfccs;
    public static float[] standardDeviation;
    public static float[] means;
    public static void Initialize(Profile profile, float firRangeHz = 500f)
    {
        if (profile == null)
        {
            DisposeShared();
            Profile = null;
            _initialized = false;
            return;
        }

        // Single-profile assumption: if same profile already built, do nothing.
        if (_initialized && ReferenceEquals(Profile, profile))
        {
            return;
        }

        DisposeShared();
        Profile = profile;
        mfccs = profile.mfccs.ToArray();
        means = profile.means;
        standardDeviation = profile.standardDeviation;
        // --------- constants ----------
        outputSampleRate = AudioSettings.outputSampleRate;
        compareMethod = profile.compareMethod;
        float r = (float)outputSampleRate / math.max(profile.targetSampleRate, 1);
        CachedInputSampleCount = Mathf.CeilToInt(math.max(profile.sampleCount, 1) * r);
        targetSampleRate = profile.targetSampleRate;
        Count = profile.mfccs.Count;
        phonemeCount = math.max(Count, 1);
        mfccLen = math.max(profile.mfccNum, 1);
        PhonemesCount = mfccLen * phonemeCount;

        targetRate = math.max(profile.targetSampleRate, 1);
        melDiv = math.max(profile.melFilterBankChannels, 1);
        Max = math.min(Count, phonemeCount);

        // --------- derive DSP sizes (same logic as workspace Create) ----------
        downLen = BasisLipSyncWorkspaceShared.ComputeDownsampleLength(CachedInputSampleCount, outputSampleRate, targetRate);
        fftN = BasisLipSyncWorkspaceShared.NextPow2(downLen);
        if ((fftN & (fftN - 1)) != 0) fftN = BasisLipSyncWorkspaceShared.NextPow2(fftN);
        fftN = math.max(fftN, downLen);

        specLen = fftN / 2 + 1;

        float cutoffHz = targetRate * 0.5f;
        firLen = BasisLipSyncWorkspaceShared.ComputeLowPassFirLength(outputSampleRate, cutoffHz, firRangeHz);

        // --------- shared phoneme blobs ----------
        SharedMeans = new NativeArray<float>(mfccLen, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        SharedStd = new NativeArray<float>(mfccLen, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        SharedInvStd = new NativeArray<float>(mfccLen, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        SharedPhonemesRaw = new NativeArray<float>(PhonemesCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        SharedPhonemesZ = new NativeArray<float>(PhonemesCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        SharedPhonemeNorms = new NativeArray<float>(phonemeCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        // pack raw phonemes once
        int write = 0;
        for (int p = 0; p < Max && write < PhonemesCount; p++)
        {
            var src = profile.mfccs[p].mfccNativeArray; // expects mfccLen
            int remaining = PhonemesCount - write;
            int len = math.min(mfccLen, remaining);
            NativeArray<float>.Copy(src, 0, SharedPhonemesRaw, write, len);
            write += len;
        }
        for (int i = write; i < PhonemesCount; i++) SharedPhonemesRaw[i] = 0f;

        // means/std once
        var meansArr = profile.means;
        if (meansArr != null)
        {
            int len = math.min(meansArr.Length, mfccLen);
            NativeArray<float>.Copy(meansArr, 0, SharedMeans, 0, len);
            for (int i = len; i < mfccLen; i++)
            {
                SharedMeans[i] = 0f;
            }
        }
        else
        {
            for (int i = 0; i < mfccLen; i++)
            {
                SharedMeans[i] = 0f;
            }
        }

        var stdArr = profile.standardDeviation;
        if (stdArr != null)
        {
            int len = math.min(stdArr.Length, mfccLen);
            NativeArray<float>.Copy(stdArr, 0, SharedStd, 0, len);
            for (int i = len; i < mfccLen; i++)
            {
                SharedStd[i] = 1f;
            }
        }
        else
        {
            for (int i = 0; i < mfccLen; i++)
            {
                SharedStd[i] = 1f;
            }
        }

        // invStd + phonemesZ + norms once
        PrecomputeInvStd(SharedStd, SharedInvStd);
        PrecomputePhonemesZ(SharedPhonemesRaw, SharedPhonemesZ, SharedMeans, SharedInvStd, mfccLen, phonemeCount);
        PrecomputePhonemeNorms(SharedPhonemesZ, SharedPhonemeNorms, mfccLen, phonemeCount);

        // phoneme name map once
        PhonemeNameToIndex.Clear();
        for (int Index = 0; Index < phonemeCount; Index++)
        {
            var name = profile.GetPhoneme(Index);
            if (!string.IsNullOrEmpty(name))
            {
                PhonemeNameToIndex[name] = Index;
            }
        }

        // --------- shared DSP constants ----------
        SharedFirTaps = new NativeArray<float>(firLen, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        SharedHammingWindow = new NativeArray<float>(fftN, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        BasisLipSyncWorkspaceShared.PrecomputeLowPassTaps(SharedFirTaps, outputSampleRate, cutoffHz, firRangeHz);
        BasisLipSyncWorkspaceShared.PrecomputeHamming(SharedHammingWindow);

        SharedFftPlan = BasisFftPlan.Build(fftN, Allocator.Persistent);

        // --------- shared mel + dct plans ----------
        SharedMelPlan = BasisMelFilterPlan.Build(
            fftN: fftN,
            sampleRate: targetRate,
            melDiv: melDiv,
            alloc: Allocator.Persistent
        );

        SharedDctPlan = BasisDctPlan.Build(
            melDiv: melDiv,
            mfccLen: mfccLen,
            alloc: Allocator.Persistent
        );

        _initialized = true;
    }
    public static void DisposeShared()
    {
        _initialized = false;

        SafeDispose(ref SharedMeans);
        SafeDispose(ref SharedStd);
        SafeDispose(ref SharedInvStd);
        SafeDispose(ref SharedPhonemesRaw);
        SafeDispose(ref SharedPhonemesZ);
        SafeDispose(ref SharedPhonemeNorms);

        SafeDispose(ref SharedFirTaps);
        SafeDispose(ref SharedHammingWindow);

        if (SharedFftPlan.IsCreated) SharedFftPlan.Dispose();
        if (SharedMelPlan.IsCreated) SharedMelPlan.Dispose();
        if (SharedDctPlan.IsCreated) SharedDctPlan.Dispose();

        PhonemeNameToIndex.Clear();
        Profile = null;
    }

    static void SafeDispose<T>(ref NativeArray<T> a) where T : struct
    {
        if (a.IsCreated) a.Dispose();
        a = default;
    }

    // ---- one-time math ----
    static void PrecomputeInvStd(NativeArray<float> std, NativeArray<float> invStd)
    {
        int n = math.min(std.Length, invStd.Length);
        float* s = (float*)std.GetUnsafeReadOnlyPtr();
        float* inv = (float*)invStd.GetUnsafePtr();
        for (int i = 0; i < n; i++)
            inv[i] = math.rcp(s[i] + Z_EPS);
    }

    static void PrecomputePhonemesZ(
        NativeArray<float> phonemesRaw,
        NativeArray<float> phonemesZ,
        NativeArray<float> means,
        NativeArray<float> invStd,
        int mfccLen_,
        int phonemeCount_)
    {
        int total = mfccLen_ * phonemeCount_;
        if (phonemesRaw.Length < total || phonemesZ.Length < total) return;

        float* raw = (float*)phonemesRaw.GetUnsafeReadOnlyPtr();
        float* z = (float*)phonemesZ.GetUnsafePtr();
        float* mu = (float*)means.GetUnsafeReadOnlyPtr();
        float* inv = (float*)invStd.GetUnsafeReadOnlyPtr();

        int vecLimit = mfccLen_ & ~3;

        for (int p = 0; p < phonemeCount_; p++)
        {
            int baseOff = p * mfccLen_;
            int i = 0;

            for (; i < vecLimit; i += 4)
            {
                float4 r = *(float4*)(raw + baseOff + i);
                float4 m = *(float4*)(mu + i);
                float4 k = *(float4*)(inv + i);
                *(float4*)(z + baseOff + i) = (r - m) * k;
            }

            for (; i < mfccLen_; i++)
                z[baseOff + i] = (raw[baseOff + i] - mu[i]) * inv[i];
        }
    }

    static void PrecomputePhonemeNorms(NativeArray<float> phonemesZ, NativeArray<float> norms, int mfccLen_, int phonemeCount_)
    {
        int total = mfccLen_ * phonemeCount_;
        if (phonemesZ.Length < total || norms.Length < phonemeCount_) return;

        float* z = (float*)phonemesZ.GetUnsafeReadOnlyPtr();
        float* outN = (float*)norms.GetUnsafePtr();

        int vecLimit = mfccLen_ & ~3;

        for (int p = 0; p < phonemeCount_; p++)
        {
            int baseOff = p * mfccLen_;
            float sum = 0f;

            int i = 0;
            for (; i < vecLimit; i += 4)
            {
                float4 v = *(float4*)(z + baseOff + i);
                sum += math.dot(v, v);
            }
            for (; i < mfccLen_; i++)
            {
                float v = z[baseOff + i];
                sum += v * v;
            }

            outN[p] = math.sqrt(sum) + Z_EPS;
        }
    }
}
namespace uLipSync
{
    public struct BasisLipSyncWorkspaceScratch : IDisposable
    {
        // Scratch (mutable every frame/job)
        public NativeArray<float> buffer;      // inputLen
        public NativeArray<float> down;        // downLen
        public NativeArray<float> frame;       // fftN
        public NativeArray<float> powerHalf;   // specLen
        public NativeArray<float> melSpectrum; // melDiv

        public NativeArray<float> tmp;         // >= max(inputLen, downLen, fftN)
        public NativeArray<float> fftRe;       // fftN
        public NativeArray<float> fftIm;       // fftN

        public NativeArray<float> mfccZ;       // mfccLen (standardization scratch)

        public bool IsCreated =>
            buffer.IsCreated && down.IsCreated && frame.IsCreated &&
            powerHalf.IsCreated && melSpectrum.IsCreated &&
            tmp.IsCreated && fftRe.IsCreated && fftIm.IsCreated &&
            mfccZ.IsCreated;

        public void Dispose()
        {
            if (buffer.IsCreated) buffer.Dispose();
            if (down.IsCreated) down.Dispose();
            if (frame.IsCreated) frame.Dispose();
            if (powerHalf.IsCreated) powerHalf.Dispose();
            if (melSpectrum.IsCreated) melSpectrum.Dispose();
            if (tmp.IsCreated) tmp.Dispose();
            if (fftRe.IsCreated) fftRe.Dispose();
            if (fftIm.IsCreated) fftIm.Dispose();
            if (mfccZ.IsCreated) mfccZ.Dispose();
        }

        public static BasisLipSyncWorkspaceScratch Create(
            int inputLen, int downLen, int fftN, int specLen, int melDiv, int mfccLen, Allocator allocator)
        {
            return new BasisLipSyncWorkspaceScratch
            {
                buffer = new NativeArray<float>(inputLen, allocator),
                down = new NativeArray<float>(downLen, allocator),
                frame = new NativeArray<float>(fftN, allocator),
                powerHalf = new NativeArray<float>(specLen, allocator),
                melSpectrum = new NativeArray<float>(melDiv, allocator),

                tmp = new NativeArray<float>(math.max(math.max(inputLen, downLen), fftN), allocator),
                fftRe = new NativeArray<float>(fftN, allocator),
                fftIm = new NativeArray<float>(fftN, allocator),

                mfccZ = new NativeArray<float>(mfccLen, allocator),
            };
        }
    }

    public static class BasisLipSyncWorkspaceShared
    {
        public static int ComputeDownsampleLength(int inputLen, int outputSampleRate, int targetSampleRate)
        {
            if (outputSampleRate <= targetSampleRate) return inputLen;

            if (outputSampleRate % targetSampleRate == 0)
            {
                int skip = outputSampleRate / targetSampleRate;
                return inputLen / skip;
            }

            float df = (float)outputSampleRate / targetSampleRate;
            return (int)math.round(inputLen / df);
        }

        public static int ComputeLowPassFirLength(float sampleRate, float cutoffHz, float rangeHz)
        {
            float range = rangeHz / sampleRate;
            int n = (int)math.round(3.1f / range);
            if (((n + 1) & 1) == 0) n += 1;
            return n;
        }

        public static int NextPow2(int x)
        {
            x = math.max(1, x);
            x--;
            x |= x >> 1;
            x |= x >> 2;
            x |= x >> 4;
            x |= x >> 8;
            x |= x >> 16;
            return x + 1;
        }

        public static void PrecomputeHamming(NativeArray<float> window)
        {
            unsafe
            {
                float* w = (float*)window.GetUnsafePtr();
                int len = window.Length;
                float inv = 1f / (len - 1);
                for (int i = 0; i < len; i++)
                {
                    float x = i * inv;
                    w[i] = 0.54f - 0.46f * math.cos(2f * math.PI * x);
                }
            }
        }

        public static void PrecomputeLowPassTaps(NativeArray<float> taps, float sampleRate, float cutoffHz, float rangeHz)
        {
            float cutoff = (cutoffHz - rangeHz) / sampleRate;
            float range = rangeHz / sampleRate;

            int n = (int)math.round(3.1f / range);
            if (((n + 1) & 1) == 0) n += 1;
            n = math.min(n, taps.Length);

            unsafe
            {
                float* b = (float*)taps.GetUnsafePtr();
                float half = (n - 1) * 0.5f;

                for (int i = 0; i < n; i++)
                {
                    float x = i - half;
                    float ang = 2f * math.PI * cutoff * x;
                    if (math.abs(ang) < 1e-12f) b[i] = 2f * cutoff;
                    else b[i] = 2f * cutoff * math.sin(ang) / ang;
                }
                for (int i = n; i < taps.Length; i++) b[i] = 0f;
            }
        }
    }
}
