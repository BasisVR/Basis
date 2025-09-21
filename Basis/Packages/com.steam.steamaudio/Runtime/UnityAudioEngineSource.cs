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
#if STEAMAUDIO_ENABLED

using System;
using UnityEngine;

namespace SteamAudio
{
    public sealed class UnityAudioEngineSource : AudioEngineSource
    {
        // Highest param index used below is 32. Indices are 0..32 inclusive.
        const int kMaxParamIndex = 32;
        const int kDeprecatedParamIndex0 = 28; // left intentionally unused
        const int kDeprecatedParamIndex1 = 29; // left intentionally unused

        // Tiny tolerance to avoid thrashing on insignificant float jitter.
        const float kEpsilon = 1e-4f;

        AudioSource mAudioSource = null;
        SteamAudioSource mSteamAudioSource = null;
        int mHandle = -1;

        // Cache of last-sent values. Initialized to NaN so first write always sends.
        // Using a flat array is the fastest general-purpose structure here.
        readonly float[] mParamCache = new float[kMaxParamIndex + 1];

        public override void Initialize(GameObject gameObject)
        {
            mAudioSource = gameObject.GetComponent<AudioSource>();

            // Prime cache with NaNs so first UpdateParameters does a full push.
            for (int i = 0; i < mParamCache.Length; ++i)
                mParamCache[i] = float.NaN;

            mSteamAudioSource = gameObject.GetComponent<SteamAudioSource>();
            if (mSteamAudioSource)
            {
                mHandle = API.iplUnityAddSource(mSteamAudioSource.GetSource().Get());
            }

            // Optionally mark deprecated slot 28 as "owned" by this source.
            // (Not strictly necessary here; Destroy() will set it to -1.)
        }

        public override void Destroy()
        {
            if (mAudioSource != null)
            {
                // Unity plugin convention in this file: index 28 used as a sentinel.
                // Use cached setter to avoid redundant calls.
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
                return;

            int index = 0;

            SetParam(index++, source.distanceAttenuation ? 1f : 0f);                          // 0
            SetParam(index++, source.airAbsorption ? 1f : 0f);                                 // 1
            SetParam(index++, source.directivity ? 1f : 0f);                                   // 2
            SetParam(index++, source.occlusion ? 1f : 0f);                                     // 3
            SetParam(index++, source.transmission ? 1f : 0f);                                  // 4
            SetParam(index++, source.reflections ? 1f : 0f);                                   // 5
            SetParam(index++, source.pathing ? 1f : 0f);                                       // 6
            SetParam(index++, (float)source.interpolation);                                     // 7
            SetParam(index++, source.distanceAttenuationValue);                                // 8
            SetParam(index++, (source.distanceAttenuationInput == DistanceAttenuationInput.CurveDriven) ? 1f : 0f); // 9
            SetParam(index++, source.airAbsorptionLow);                                        // 10
            SetParam(index++, source.airAbsorptionMid);                                        // 11
            SetParam(index++, source.airAbsorptionHigh);                                       // 12
            SetParam(index++, (source.airAbsorptionInput == AirAbsorptionInput.UserDefined) ? 1f : 0f); // 13
            SetParam(index++, source.directivityValue);                                        // 14
            SetParam(index++, source.dipoleWeight);                                            // 15
            SetParam(index++, source.dipolePower);                                             // 16
            SetParam(index++, (source.directivityInput == DirectivityInput.UserDefined) ? 1f : 0f); // 17
            SetParam(index++, source.occlusionValue);                                          // 18
            SetParam(index++, (float)source.transmissionType);                                  // 19
            SetParam(index++, source.transmissionLow);                                         // 20
            SetParam(index++, source.transmissionMid);                                         // 21
            SetParam(index++, source.transmissionHigh);                                        // 22
            SetParam(index++, source.directMixLevel);                                          // 23
            SetParam(index++, source.applyHRTFToReflections ? 1f : 0f);                        // 24
            SetParam(index++, source.reflectionsMixLevel);                                     // 25
            SetParam(index++, source.applyHRTFToPathing ? 1f : 0f);                            // 26
            SetParam(index++, source.pathingMixLevel);                                         // 27

            // 28, 29 are deprecated; advance the index without writing.
            index++; // 28 (deprecated)
            index++; // 29 (deprecated)

            SetParam(index++, source.directBinaural ? 1f : 0f);                                // 30
            SetParam(index++, mHandle);                                                        // 31
            SetParam(index++, source.perspectiveCorrection ? 1f : 0f);                         // 32
        }

        // --- fast path helpers ---

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        void SetParam(int index, float value)
        {
            // If cache is NaN or meaningfully different, push and cache.
            float cached = mParamCache[index];
            if (!(cached == value) && !Approximately(cached, value))
            {
                mAudioSource.SetSpatializerFloat(index, value);
                mParamCache[index] = value;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static bool Approximately(float a, float b)
        {
            // Treat NaNs as "different".
            if (float.IsNaN(a) || float.IsNaN(b))
                return false;
            return Mathf.Abs(a - b) <= kEpsilon;
        }
    }
}

#endif
