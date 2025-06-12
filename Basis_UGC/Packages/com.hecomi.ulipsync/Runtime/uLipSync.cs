using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using System.Collections.Generic;
namespace uLipSync
{
    public class uLipSync : MonoBehaviour
    {
        public Profile profile;
        JobHandle _jobHandle;
        object _lockObject = new object();
        bool _allocated = false;
        int _index = 0;
        bool _isDataReceived = false;
        NativeArray<float> _inputData;
        NativeArray<float> _mfcc;
        NativeArray<float> _mfccForOther;
        NativeArray<float> _means;
        NativeArray<float> _standardDeviations;
        NativeArray<float> _phonemes;
        NativeArray<float> _scores;
        NativeArray<LipSyncJob.Info> _info;
        List<int> _requestedCalibrationVowels = new List<int>();
        string[] _phonemeNames;
        public float[] UpdateResultsBuffer;
        public int phonemeCount;
        public NativeArray<float> mfcc => _mfccForOther;
        public float[] Inputs;
        int mfccNum => profile ? profile.mfccNum : 12;
        public uLipSyncBlendShape uLipSyncBlendShape;
        public int outputSampleRate;
        public int PhonemesCount;
        public int mfccsCount;
        float[] phonemeRatios;
        Dictionary<string, int> _phonemeNameToIndex = new Dictionary<string, int>();
        public string mainPhoneme;
        public float NormalVolume;
        public float rawVolume;
         public bool RequestedCalibration = false;
        public int CachedInputSampleCount;
        public void LateUpdate()
        {
            if (!_jobHandle.IsCompleted)
                return;

            _jobHandle.Complete();
            _mfccForOther.CopyFrom(_mfcc);

            int mainIndex = _info[0].mainPhonemeIndex;
            mainPhoneme = _phonemeNames[mainIndex];

            float sumScore = 0f;
            _scores.CopyTo(UpdateResultsBuffer);
            for (int Index = 0; Index < phonemeCount; ++Index)
            {
                sumScore += UpdateResultsBuffer[Index];
            }

            float invSum = sumScore > 0f ? 1f / sumScore : 0f;

            for (int i = 0; i < phonemeCount; ++i)
            {
                phonemeRatios[i] = UpdateResultsBuffer[i] * invSum;
            }

            rawVolume = _info[0].volume;
            NormalVolume = math.clamp((math.log10(rawVolume) - Common.DefaultMinVolume) / (Common.DefaultMaxVolume - Common.DefaultMinVolume), 0f, 1f);
            OnLipSyncUpdate();

            if (RequestedCalibration)
            {
                for (int i = 0; i < _requestedCalibrationVowels.Count; ++i)
                {
                    int idx = _requestedCalibrationVowels[i];
                    profile.UpdateMfcc(idx, mfcc, true);
                }
                _requestedCalibrationVowels.Clear();
                RequestedCalibration = false;
            }
            int index = 0;
            for (int i = 0; i < mfccsCount && index < PhonemesCount; i++)
            {
                var mfccNativeArray = profile.mfccs[i].mfccNativeArray;
                int remaining = PhonemesCount - index;
                int length = math.min(12, remaining);
                NativeArray<float>.Copy(mfccNativeArray, 0, _phonemes, index, length);
                index += length;
            }

            if (!_isDataReceived)
            {
                return;
            }
            _isDataReceived = false;

            CachedInputSampleCount = inputSampleCount;
            lock (_lockObject)
            {
                _inputData.CopyFrom(Inputs);
                index = _index;
            }

            LipSyncJob lipSyncJob = new LipSyncJob()
            {
                input = _inputData,
                startIndex = index,
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
        public float globalMultiplier;
        public void OnLipSyncUpdate()
        {
            if (uLipSyncBlendShape.skinnedMeshRenderer != null)
            {


                float normVol = 0f;
                if (rawVolume > 0f)
                {
                    normVol = Mathf.Log10(rawVolume);
                    normVol = Mathf.Clamp01((normVol - uLipSyncBlendShape.minVolume) / Mathf.Max(uLipSyncBlendShape.maxVolume - uLipSyncBlendShape.minVolume, 1e-4f));
                }

                uLipSyncBlendShape._volume = uLipSyncBlendShape.SmoothDamp(uLipSyncBlendShape._volume, normVol, ref uLipSyncBlendShape._openCloseVelocity);
                globalMultiplier = uLipSyncBlendShape._volume * uLipSyncBlendShape.maxBlendShapeValue;

                float totalWeight = 0f;
                int count = uLipSyncBlendShape.BlendShapeInfos.Length;

                // First pass: compute weights and total sum
                for (int Index = 0; Index < count; Index++)
                {
                    var bs = uLipSyncBlendShape.BlendShapeInfos[Index];
                    float targetWeight = 0f;

                    if (uLipSyncBlendShape.usePhonemeBlend && !string.IsNullOrEmpty(bs.phoneme))
                    {
                        if (_phonemeNameToIndex.TryGetValue(bs.phoneme, out int idx) && idx < phonemeRatios.Length)
                        {
                            targetWeight = phonemeRatios[idx];
                        }
                    }
                    //mainPhoneme, NormalVolume, rawVolume, phonemeRatios
                    else if (bs.phoneme == mainPhoneme)
                    {
                        targetWeight = 1f;
                    }

                    float weightVelocity = bs.weightVelocity;
                    bs.weight = uLipSyncBlendShape.SmoothDamp(bs.weight, targetWeight, ref weightVelocity);
                    bs.weightVelocity = weightVelocity;
                    totalWeight += bs.weight;
                }

                float BaseMultiply;
                const float epsilon = 1e-6f;
                if (Mathf.Abs(totalWeight) > epsilon)
                {
                    BaseMultiply = (1f / totalWeight) * globalMultiplier;
                }
                else
                {
                    BaseMultiply = globalMultiplier;
                }
                // Second pass: normalize + apply
                for (int i = 0; i < count; i++)
                {
                    var bs = uLipSyncBlendShape.BlendShapeInfos[i];

                    if (bs.index < 0) continue;

                    MultipliedWeight = bs.weight * BaseMultiply;
                    finalWeight = math.clamp(MultipliedWeight, 0f, 100);
                    if (float.IsNaN(finalWeight))
                    {
                        finalWeight = 0f;
                    }
                    uLipSyncBlendShape.skinnedMeshRenderer.SetBlendShapeWeight(bs.index, finalWeight);
                }
            }
        }
        public float MultipliedWeight;
        public float finalWeight;
        public void Initalize()
        {
            AllocateBuffers();
        }

        void OnDisable()
        {
            _jobHandle.Complete();
            DisposeBuffers();
        }
        void AllocateBuffers()
        {
            if (_allocated)
            {
                DisposeBuffers();
            }
            _allocated = true;

            _jobHandle.Complete();

            lock (_lockObject)
            {
                CachedInputSampleCount = inputSampleCount;
                phonemeCount = profile ? profile.mfccs.Count : 1;
                Inputs = new float[CachedInputSampleCount];
                _inputData = new NativeArray<float>(CachedInputSampleCount, Allocator.Persistent);
                _mfcc = new NativeArray<float>(mfccNum, Allocator.Persistent);
                _mfccForOther = new NativeArray<float>(mfccNum, Allocator.Persistent);
                _means = new NativeArray<float>(mfccNum, Allocator.Persistent);
                _standardDeviations = new NativeArray<float>(mfccNum, Allocator.Persistent);
                _scores = new NativeArray<float>(phonemeCount, Allocator.Persistent);
                UpdateResultsBuffer = new float[phonemeCount];
                _phonemes = new NativeArray<float>(mfccNum * phonemeCount, Allocator.Persistent);
                _info = new NativeArray<LipSyncJob.Info>(1, Allocator.Persistent);
                _means.CopyFrom(profile.means);
                _standardDeviations.CopyFrom(profile.standardDeviation);
                PhonemesCount = mfccNum * phonemeCount;
                mfccsCount = profile.mfccs.Count;
                outputSampleRate = AudioSettings.outputSampleRate;

                _phonemeNames = new string[phonemeCount];
                phonemeRatios = new float[phonemeCount];
                _phonemeNameToIndex.Clear();
                for (int i = 0; i < phonemeCount; ++i)
                {
                    string name = profile.GetPhoneme(i);
                    _phonemeNames[i] = name;
                    _phonemeNameToIndex[name] = i;
                }
            }
        }
        void DisposeBuffers()
        {
            if (!_allocated) return;
            _allocated = false;

            _jobHandle.Complete();

            lock (_lockObject)
            {
                Inputs = null;
                _inputData.Dispose();
                _mfcc.Dispose();
                _mfccForOther.Dispose();
                _means.Dispose();
                _standardDeviations.Dispose();
                _scores.Dispose();
                _phonemes.Dispose();
                _info.Dispose();
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
                if (!profile) return AudioSettings.outputSampleRate;
                float r = (float)AudioSettings.outputSampleRate / profile.targetSampleRate;
                return Mathf.CeilToInt(profile.sampleCount * r);
            }
        }
        public void OnDataReceived(float[] input, int channels, int length)
        {
            lock (_lockObject)
            {
                _index = _index % CachedInputSampleCount;
                for (int i = 0; i < length; i += channels)
                {
                    Inputs[_index++ % CachedInputSampleCount] = input[i];
                }
            }

            _isDataReceived = true;
        }
    }
}
