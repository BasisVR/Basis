using UnityEngine;
using Unity.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
namespace uLipSync
{
    [System.Serializable]
    public struct MfccCalibrationData
    {
        public float[] array;
        public float this[int i] => array[i];
        public int length => array.Length;
    }
    [System.Serializable]
    public class MfccData
    {
        public string name;
        public List<MfccCalibrationData> mfccCalibrationDataList = new List<MfccCalibrationData>();
        public NativeArray<float> mfccNativeArray;
        ~MfccData()
        {
            Deallocate();
        }
        public void Allocate()
        {
            if (IsAllocated()) return;

            mfccNativeArray = new NativeArray<float>(12, Allocator.Persistent);
        }
        public void Deallocate()
        {
            if (!IsAllocated()) return;

            mfccNativeArray.Dispose();
        }
        bool IsAllocated()
        {
            return mfccNativeArray.IsCreated;
        }
        public void RemoveOldCalibrationData(int dataCount)
        {
            while (mfccCalibrationDataList.Count > dataCount) mfccCalibrationDataList.RemoveAt(0);
        }
        public void UpdateNativeArray()
        {
            if (mfccCalibrationDataList.Count == 0) return;

            for (int i = 0; i < 12; ++i)
            {
                mfccNativeArray[i] = 0f;
                foreach (var mfcc in mfccCalibrationDataList)
                {
                    mfccNativeArray[i] += mfcc[i];
                }
                mfccNativeArray[i] /= mfccCalibrationDataList.Count;
            }
        }
    }
    [CreateAssetMenu(menuName = Common.AssetName + "/Profile")]
    public class Profile : ScriptableObject
    {
        [Tooltip("The number of MFCC")]
        public int mfccNum = 12;
        [Tooltip("The number of MFCC data to calculate the average MFCC values")]
        public int mfccDataCount = 16;
        [Tooltip("The number of Mel Filter Bank channels")]
        public int melFilterBankChannels = 30;
        [Tooltip("Target sampling rate to apply downsampling")]
        public int targetSampleRate = 16000;
        [Tooltip("Number of audio samples after downsampling is applied")]
        public int sampleCount = 1024;
        [Tooltip("Whether to perform standardization of each coefficient of MFCC")]
        public bool useStandardization = false;
        [Tooltip("The comparison method for MFCC")]
        public CompareMethod compareMethod = CompareMethod.L2Norm;
        public List<MfccData> mfccs = new List<MfccData>();
        float[] _means = new float[12];
        float[] _stdDevs = new float[12];
        public float[] means => _means;
        public float[] standardDeviation => _stdDevs;
        void OnEnable()
        {
            UpdateMeansAndStandardization();

            foreach (var data in mfccs)
            {
                data.Allocate();
                data.RemoveOldCalibrationData(mfccDataCount);
                data.UpdateNativeArray();
            }
        }
        void OnDisable()
        {
            foreach (var data in mfccs)
            {
                data.Deallocate();
            }
        }
        public string GetPhoneme(int index)
        {
            if (index < 0 || index >= mfccs.Count) return "";

            return mfccs[index].name;
        }
        public void UpdateMeansAndStandardization()
        {
            UpdateMeans();
            UpdateStandardizations();
        }
        void UpdateMeans()
        {
            for (int i = 0; i < _means.Length; ++i)
            {
                _means[i] = 0f;
            }

            if (!useStandardization) return;

            int n = 0;
            foreach (var mfccData in mfccs)
            {
                var list = mfccData.mfccCalibrationDataList;
                foreach (var mfcc in list)
                {
                    for (int i = 0; i < mfcc.length; ++i)
                    {
                        _means[i] += mfcc[i];
                    }
                    ++n;
                }
            }

            for (int i = 0; i < _means.Length; ++i)
            {
                _means[i] /= n;
            }
        }
        void UpdateStandardizations()
        {
            if (!useStandardization)
            {
                for (int i = 0; i < _stdDevs.Length; ++i)
                {
                    _stdDevs[i] = 1f;
                }
                return;
            }

            for (int i = 0; i < _stdDevs.Length; ++i)
            {
                _stdDevs[i] = 0f;
            }

            int n = 0;
            foreach (var mfccData in mfccs)
            {
                var list = mfccData.mfccCalibrationDataList;
                foreach (var mfcc in list)
                {
                    for (int i = 0; i < mfcc.length; ++i)
                    {
                        _stdDevs[i] += math.pow(mfcc[i] - _means[i], 2f);
                    }
                    ++n;
                }
            }

            for (int i = 0; i < _stdDevs.Length; ++i)
            {
                _stdDevs[i] = math.sqrt(_stdDevs[i] / n);
            }
        }
    }
}
