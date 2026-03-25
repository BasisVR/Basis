#if STEAMAUDIO_ENABLED
using System;
using UnityEngine;

namespace SteamAudio
{
    //
    // Copyright 2017-2023 Valve Corporation.
    //
    // Licensed under the Apache License, Version 2.0 (the "License");
    // you may not use this file except in compliance with the License.
    // You may obtain a copy of the License at
    //
    //     http://www.apache.org/licenses/LICENSE-2.0
    //
    // Unless required by applicable law or agreed to in writing, software
    // distributed under the License is distributed on an "AS IS" BASIS,
    // WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    // See the License for the specific language governing permissions and
    // limitations under the License.
    //
    public sealed class UnityAudioEngineSource : AudioEngineSource
    {
        const int kMaxParamIndex = 32;
        const int kDeprecatedParamIndex0 = 28;
        const float kEpsilon = 1e-4f;
        AudioSource mAudioSource = null;
        SteamAudioSource mSteamAudioSource = null;
        int mHandle = -1;

        readonly float[] mParamCache = new float[kMaxParamIndex + 1];
        ulong mLastParamsHash = 0;
        bool mHasLastHash = false;
        public override void Initialize(GameObject gameObject)
        {
            mAudioSource = gameObject.GetComponent<AudioSource>();

            for (int i = 0; i < mParamCache.Length; ++i)
                mParamCache[i] = float.NaN;

            mSteamAudioSource = gameObject.GetComponent<SteamAudioSource>();
            if (mSteamAudioSource)
                mHandle = API.iplUnityAddSource(mSteamAudioSource.GetSource().Get());

            // Force first push even in optimized mode.
            mHasLastHash = false;
        }

        public override void Destroy()
        {
            if (mAudioSource != null)
            {
                SetParam(kDeprecatedParamIndex0, -1f);
            }

            if (mSteamAudioSource)
            {
                API.iplUnityRemoveSource(mHandle);
            }
        }

        public override void UpdateParameters(SteamAudioSource source)
        {
            if (!mAudioSource)
            {
                return;
            }

            // If updates are allowed, we can use the hash optimization.
            ulong h = ComputeParamsHash(source, mHandle);
            if (mHasLastHash && h == mLastParamsHash)
            {
                return;
            }

            mLastParamsHash = h;
            mHasLastHash = true;


            int index = 0;
            SetParam(index++, source.distanceAttenuation ? 1f : 0f); // 0
            SetParam(index++, source.airAbsorption ? 1f : 0f); // 1
            SetParam(index++, source.directivity ? 1f : 0f); // 2
            SetParam(index++, source.occlusion ? 1f : 0f); // 3
            SetParam(index++, source.transmission ? 1f : 0f); // 4
            SetParam(index++, source.reflections ? 1f : 0f); // 5
            SetParam(index++, source.pathing ? 1f : 0f); // 6
            SetParam(index++, (float)source.interpolation); // 7
            SetParam(index++, source.distanceAttenuationValue); // 8
            SetParam(index++, (source.distanceAttenuationInput == DistanceAttenuationInput.CurveDriven) ? 1f : 0f); // 9
            SetParam(index++, source.airAbsorptionLow); // 10
            SetParam(index++, source.airAbsorptionMid); // 11
            SetParam(index++, source.airAbsorptionHigh); // 12
            SetParam(index++, (source.airAbsorptionInput == AirAbsorptionInput.UserDefined) ? 1f : 0f); // 13
            SetParam(index++, source.directivityValue); // 14
            SetParam(index++, source.dipoleWeight); // 15
            SetParam(index++, source.dipolePower); // 16
            SetParam(index++, (source.directivityInput == DirectivityInput.UserDefined) ? 1f : 0f); // 17
            SetParam(index++, source.occlusionValue); // 18
            SetParam(index++, (float)source.transmissionType); // 19
            SetParam(index++, source.transmissionLow); // 20
            SetParam(index++, source.transmissionMid); // 21
            SetParam(index++, source.transmissionHigh); // 22
            SetParam(index++, source.directMixLevel); // 23
            SetParam(index++, source.applyHRTFToReflections ? 1f : 0f); // 24
            SetParam(index++, source.reflectionsMixLevel); // 25
            SetParam(index++, source.applyHRTFToPathing ? 1f : 0f); // 26
            SetParam(index++, source.pathingMixLevel); // 27

            index++; // 28 (deprecated)
            index++; // 29 (deprecated)

            SetParam(index++, source.directBinaural ? 1f : 0f); // 30
            SetParam(index++, mHandle); // 31
            SetParam(index++, source.perspectiveCorrection ? 1f : 0f); // 32
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        void SetParam(int index, float value)
        {
            float cached = mParamCache[index];
            if (!(cached == value) && !Approximately(cached, value))
            {
                mAudioSource.SetSpatializerFloat(index, value);
                mParamCache[index] = value;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool Approximately(float a, float b) => Mathf.Abs(a - b) <= kEpsilon;

        // NEW: fast-ish hash of all relevant fields.
        // Uses raw float bits so tiny changes count as changes (which is good if the plugin needs it).
        // If you want to ignore tiny jitter, quantize floats here.
        static ulong ComputeParamsHash(SteamAudioSource s, int handle)
        {
            // 64-bit FNV-1a style mix
            ulong h = 1469598103934665603UL;

            void MixU32(uint v)
            {
                h ^= v;
                h *= 1099511628211UL;
            }

            void MixBool(bool v) => MixU32(v ? 1u : 0u);
            void MixInt(int v) => MixU32(unchecked((uint)v));
            void MixFloat(float v) => MixU32(unchecked((uint)BitConverter.SingleToInt32Bits(v)));

            MixBool(s.distanceAttenuation);
            MixBool(s.airAbsorption);
            MixBool(s.directivity);
            MixBool(s.occlusion);
            MixBool(s.transmission);
            MixBool(s.reflections);
            MixBool(s.pathing);

            MixInt((int)s.interpolation);

            MixFloat(s.distanceAttenuationValue);
            MixInt((int)s.distanceAttenuationInput);

            MixFloat(s.airAbsorptionLow);
            MixFloat(s.airAbsorptionMid);
            MixFloat(s.airAbsorptionHigh);
            MixInt((int)s.airAbsorptionInput);

            MixFloat(s.directivityValue);
            MixFloat(s.dipoleWeight);
            MixFloat(s.dipolePower);
            MixInt((int)s.directivityInput);

            MixFloat(s.occlusionValue);

            MixInt((int)s.transmissionType);
            MixFloat(s.transmissionLow);
            MixFloat(s.transmissionMid);
            MixFloat(s.transmissionHigh);

            MixFloat(s.directMixLevel);
            MixBool(s.applyHRTFToReflections);
            MixFloat(s.reflectionsMixLevel);

            MixBool(s.applyHRTFToPathing);
            MixFloat(s.pathingMixLevel);

            MixBool(s.directBinaural);
            MixInt(handle);
            MixBool(s.perspectiveCorrection);

            return h;
        }
    }
}
#endif
