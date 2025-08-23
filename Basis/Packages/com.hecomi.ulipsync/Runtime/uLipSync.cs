using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using System.Collections.Generic;
using System.Threading;

namespace uLipSync
{
    public class uLipSync : MonoBehaviour
    {
        public Profile profile;

        JobHandle _jobHandle;

        readonly object _lockObject = new object();
        bool _allocated = false;

        // Audio ring-buffer write index
        int _writeIndex = 0;

        // flip-flop to indicate new data is ready
        volatile int _isDataReceived = 0; // 0 = false, 1 = true

        // Native buffers
        NativeArray<float> _inputData;             // ring buffer in native space
        NativeArray<float> _mfcc;                  // computed MFCCs (for our job)
        NativeArray<float> _mfccForOther;          // exposed copy for other systems
        NativeArray<float> _means;
        NativeArray<float> _standardDeviations;
        NativeArray<float> _phonemes;              // reference MFCCs packed [phonemeCount * mfccNum]
        NativeArray<float> _scores;                // output scores from job
        NativeArray<LipSyncJob.Info> _info;        // volume + main phoneme idx

        // calibration
        readonly List<int> _requestedCalibrationVowels = new List<int>(8);

        string[] _phonemeNames;

        // Exposed results / caches
        public int phonemeCount;
        public NativeArray<float> mfcc => _mfccForOther;
        public float[] Inputs;                     // managed mirror used for feeding audio only
        public int outputSampleRate;
        public int PhonemesCount;
        public int mfccsCount;

        // runtime mix values
        float[] _phonemeRatios;
        readonly Dictionary<string, int> _phonemeNameToIndex = new Dictionary<string, int>(32);

        public string mainPhoneme;
        public float NormalVolume;
        public float rawVolume;

        public bool RequestedCalibration = false;
        public int CachedInputSampleCount;

        public float globalMultiplier;
        public float MultipliedWeight;
        public float finalWeight;

        int mfccNum => profile ? profile.mfccNum : 12;

        public uLipSyncBlendShape uLipSyncBlendShape;

        // ---------- Unity lifecycle ----------

        public void LateUpdate()
        {
            // If last scheduled job is still running, skip this frame.
            if (!_jobHandle.Equals(default(JobHandle)) && !_jobHandle.IsCompleted)
                return;

            // If a job existed, complete it and consume results.
            if (!_jobHandle.Equals(default(JobHandle)))
            {
                _jobHandle.Complete();
                _jobHandle = default;

                // Copy MFCC for external access in one go (no managed allocs)
                if (_mfccForOther.IsCreated && _mfcc.IsCreated)
                    _mfccForOther.CopyFrom(_mfcc);

                // Pull main phoneme + volume
                if (_info.IsCreated && _info.Length > 0)
                {
                    int mainIndex = math.clamp(_info[0].mainPhonemeIndex, 0, phonemeCount - 1);
                    mainPhoneme = (_phonemeNames != null && mainIndex < _phonemeNames.Length) ? _phonemeNames[mainIndex] : string.Empty;

                    rawVolume = math.max(_info[0].volume, 0f);
                    // single log10, normalized to [0..1]
                    float logv = rawVolume > 0f ? math.log10(rawVolume) : 0f;
                    NormalVolume = math.clamp(
                        (logv - Common.DefaultMinVolume) / math.max(Common.DefaultMaxVolume - Common.DefaultMinVolume, 1e-6f),
                        0f, 1f);
                }

                // Compute phoneme ratios from scores (single pass, no managed copy)
                if (_scores.IsCreated && phonemeCount > 0)
                {
                    float sum = 0f;
                    for (int i = 0; i < phonemeCount; i++) sum += _scores[i];
                    float inv = sum > 0f ? 1f / sum : 0f;
                    for (int i = 0; i < phonemeCount; i++)
                        _phonemeRatios[i] = _scores[i] * inv;
                }

                // Apply to blendshapes
                OnLipSyncUpdate();

                // Handle deferred calibration (after results are stable)
                if (RequestedCalibration && _requestedCalibrationVowels.Count > 0 && profile != null)
                {
                    int count = _requestedCalibrationVowels.Count;
                    for (int i = 0; i < count; i++)
                    {
                        int idx = _requestedCalibrationVowels[i];
                        profile.UpdateMfcc(idx, mfcc, true);
                    }
                    _requestedCalibrationVowels.Clear();
                    RequestedCalibration = false;
                }

                // Keep a tightly packed copy of profile phoneme MFCCs in native array
                if (profile != null && _phonemes.IsCreated)
                {
                    int write = 0;
                    int max = math.min(mfccsCount, phonemeCount);
                    for (int p = 0; p < max && write < PhonemesCount; p++)
                    {
                        var src = profile.mfccs[p].mfccNativeArray; // assumed NativeArray<float> of length >= mfccNum
                        int remaining = PhonemesCount - write;
                        int len = math.min(mfccNum, remaining);
                        NativeArray<float>.Copy(src, 0, _phonemes, write, len);
                        write += len;
                    }
                }
            }

            // If new audio arrived, schedule a new job using the ring buffer snapshot.
            if (Interlocked.Exchange(ref _isDataReceived, 0) == 1)
            {
                if (!_allocated || profile == null) return;

                // Copy managed inputs -> native ringbuffer (fast; single copy)
                int startIndexSnapshot;
                float[] inputsSnapshot;
                lock (_lockObject)
                {
                    startIndexSnapshot = _writeIndex;
                    inputsSnapshot = Inputs; // reference copy; content is stable while we copy into native
                }

                // Copy the entire managed ring buffer into native (keeps job simple)
                if (_inputData.IsCreated && inputsSnapshot != null && inputsSnapshot.Length == _inputData.Length)
                    _inputData.CopyFrom(inputsSnapshot);

                // Build and dispatch the job
                var lipSyncJob = new LipSyncJob
                {
                    input = _inputData,
                    startIndex = startIndexSnapshot,
                    outputSampleRate = outputSampleRate,
                    targetSampleRate = profile.targetSampleRate,
                    melFilterBankChannels = profile.melFilterBankChannels,
                    means = _means,
                    standardDeviations = _standardDeviations,
                    mfcc = _mfcc,
                    phonemes = _phonemes,
                    compareMethod = profile.compareMethod,
                    scores = _scores,
                    info = _info,
                };

                _jobHandle = lipSyncJob.Schedule();
            }
        }

        public void OnLipSyncUpdate()
        {
            var smr = uLipSyncBlendShape != null ? uLipSyncBlendShape.skinnedMeshRenderer : null;
            if (smr == null || smr.sharedMesh == null) return;

            int blendShapeCount = smr.sharedMesh.blendShapeCount;

            // Volume smoothing (open/close)
            float normVol = 0f;
            if (rawVolume > 0f)
            {
                float logv = Mathf.Log10(rawVolume);
                float denom = Mathf.Max(uLipSyncBlendShape.maxVolume - uLipSyncBlendShape.minVolume, 1e-4f);
                normVol = Mathf.Clamp01((logv - uLipSyncBlendShape.minVolume) / denom);
            }

            uLipSyncBlendShape._volume = uLipSyncBlendShape.SmoothDamp(
                uLipSyncBlendShape._volume, normVol, ref uLipSyncBlendShape._openCloseVelocity);

            globalMultiplier = uLipSyncBlendShape._volume * uLipSyncBlendShape.maxBlendShapeValue;

            var infos = uLipSyncBlendShape.BlendShapeInfos;
            if (infos == null) return;

            int count = infos.Length;

            // First pass: compute target weights + sum (write back if struct)
            float totalWeight = 0f;
            for (int i = 0; i < count; i++)
            {
                var bs = infos[i];
                float targetWeight = 0f;

                if (uLipSyncBlendShape.usePhonemeBlend && !string.IsNullOrEmpty(bs.phoneme))
                {
                    if (_phonemeNameToIndex.TryGetValue(bs.phoneme, out int idx) &&
                        idx >= 0 && idx < _phonemeRatios.Length)
                    {
                        targetWeight = _phonemeRatios[idx];
                    }
                }
                else if (!string.IsNullOrEmpty(mainPhoneme) && bs.phoneme == mainPhoneme)
                {
                    targetWeight = 1f;
                }

                float vel = bs.weightVelocity;
                bs.weight = uLipSyncBlendShape.SmoothDamp(bs.weight, targetWeight, ref vel);
                bs.weightVelocity = vel;

                totalWeight += bs.weight;
                infos[i] = bs; // write back if struct
            }

            // Base multiply (normalize only when sum is not ~zero)
            const float epsilon = 1e-6f;
            float baseMultiply = (Mathf.Abs(totalWeight) > epsilon)
                ? (1f / totalWeight) * globalMultiplier
                : globalMultiplier;

            // Second pass: apply to renderer
            for (int i = 0; i < count; i++)
            {
                var bs = infos[i];
                if (bs.index < 0) continue;

                // guard against out-of-range (valid indices are 0..blendShapeCount-1)
                if (bs.index >= blendShapeCount) continue;

                MultipliedWeight = bs.weight * baseMultiply;
                finalWeight = math.clamp(MultipliedWeight, 0f, 100f);
                if (float.IsNaN(finalWeight)) finalWeight = 0f;

                smr.SetBlendShapeWeight(bs.index, finalWeight);
            }
        }

        public void Initalize() // keeping your original API name
        {
            AllocateBuffers();
        }

        void OnDisable()
        {
            if (!_jobHandle.Equals(default(JobHandle)))
            {
                _jobHandle.Complete();
                _jobHandle = default;
            }
            DisposeBuffers();
        }

        void AllocateBuffers()
        {
            if (profile == null)
            {
                DisposeBuffers();
                _allocated = false;
                return;
            }

            if (_allocated)
            {
                DisposeBuffers();
            }

            if (!_jobHandle.Equals(default(JobHandle)))
            {
                _jobHandle.Complete();
                _jobHandle = default;
            }

            _allocated = true;

            lock (_lockObject)
            {
                CachedInputSampleCount = inputSampleCount;

                phonemeCount = math.max(profile.mfccs.Count, 1);
                mfccsCount = profile.mfccs.Count;
                PhonemesCount = mfccNum * phonemeCount;
                outputSampleRate = AudioSettings.outputSampleRate;

                // Managed ring buffer for feeding audio
                Inputs = new float[CachedInputSampleCount];

                // Native buffers (create or recreate)
                SafeCreate(ref _inputData, CachedInputSampleCount);
                SafeCreate(ref _mfcc, mfccNum);
                SafeCreate(ref _mfccForOther, mfccNum);
                SafeCreate(ref _means, mfccNum);
                SafeCreate(ref _standardDeviations, mfccNum);
                SafeCreate(ref _scores, phonemeCount);
                SafeCreate(ref _phonemes, PhonemesCount);
                SafeCreate(ref _info, 1);
            }

            // Copy stats from profile (managed float[] → NativeArray<float>)
            var meansArr = profile.means; // float[]
            if (meansArr != null && _means.IsCreated)
            {
                int len = math.min(meansArr.Length, _means.Length);
                NativeArray<float>.Copy(meansArr, 0, _means, 0, len);
                // zero-fill any remainder if profile array is shorter
                for (int i = len; i < _means.Length; i++) _means[i] = 0f;
            }

            var stdArr = profile.standardDeviation; // float[]
            if (stdArr != null && _standardDeviations.IsCreated)
            {
                int len = math.min(stdArr.Length, _standardDeviations.Length);
                NativeArray<float>.Copy(stdArr, 0, _standardDeviations, 0, len);
                for (int i = len; i < _standardDeviations.Length; i++) _standardDeviations[i] = 1f; // sane default
            }

            // Phoneme names + map
            _phonemeNames = new string[phonemeCount];
            _phonemeRatios = new float[phonemeCount];
            _phonemeNameToIndex.Clear();
            for (int i = 0; i < phonemeCount; i++)
            {
                string name = profile.GetPhoneme(i);
                _phonemeNames[i] = name;
                if (!string.IsNullOrEmpty(name))
                    _phonemeNameToIndex[name] = i;
            }
        }

        void DisposeBuffers()
        {
            _allocated = false;

            if (!_jobHandle.Equals(default(JobHandle)))
            {
                _jobHandle.Complete();
                _jobHandle = default;
            }

            lock (_lockObject)
            {
                Inputs = null;

                SafeDispose(ref _inputData);
                SafeDispose(ref _mfcc);
                SafeDispose(ref _mfccForOther);
                SafeDispose(ref _means);
                SafeDispose(ref _standardDeviations);
                SafeDispose(ref _scores);
                SafeDispose(ref _phonemes);
                SafeDispose(ref _info);
            }
        }

        public void RequestCalibration(int index)
        {
            RequestedCalibration = true;
            _requestedCalibrationVowels.Add(index);
        }

        int inputSampleCount
        {
            get
            {
                int sr = AudioSettings.outputSampleRate;
                if (profile == null) return sr;

                float r = (float)sr / math.max(profile.targetSampleRate, 1);
                return Mathf.CeilToInt(math.max(profile.sampleCount, 1) * r);
            }
        }

        public void OnDataReceived(float[] input, int channels, int length)
        {
            if (!_allocated || input == null || length <= 0) return;

            // Write mono (left) samples into ring buffer
            lock (_lockObject)
            {
                int cap = CachedInputSampleCount;
                if (cap <= 0 || Inputs == null) return;

                int w = _writeIndex;
                int ch = math.max(channels, 1);
                for (int i = 0; i < length; i += ch)
                {
                    Inputs[w] = input[i];
                    w++;
                    if (w >= cap) w = 0;
                }
                _writeIndex = w;
            }

            // Signal new data for the next LateUpdate
            Interlocked.Exchange(ref _isDataReceived, 1);
        }

        // ---------- helpers ----------

        static void SafeCreate(ref NativeArray<float> array, int length)
        {
            if (array.IsCreated)
            {
                if (array.Length != length)
                {
                    array.Dispose();
                    array = new NativeArray<float>(length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                }
            }
            else
            {
                array = new NativeArray<float>(length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
        }

        static void SafeCreate(ref NativeArray<LipSyncJob.Info> array, int length)
        {
            if (array.IsCreated)
            {
                if (array.Length != length)
                {
                    array.Dispose();
                    array = new NativeArray<LipSyncJob.Info>(length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                }
            }
            else
            {
                array = new NativeArray<LipSyncJob.Info>(length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
        }

        static void SafeDispose<T>(ref NativeArray<T> array) where T : struct
        {
            if (array.IsCreated) array.Dispose();
            array = default;
        }
    }
}
