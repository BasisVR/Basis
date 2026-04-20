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

        // Fully inlined FNV-1a of every spatializer-relevant field.
        //
        // Why not use local functions like the previous version did: capturing
        // `h` in a local function forces the C# compiler to emit a display
        // struct and per-mix method calls, so the JIT spills `h` out of a
        // register on every call. Straight-line arithmetic keeps `h` pinned in
        // a register for the whole mix and costs roughly 1/4 as much in
        // practice. This runs every frame per source, so it matters.
        //
        // Booleans are packed into a single uint so 11 mixes collapse to 1.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static ulong ComputeParamsHash(SteamAudioSource s, int handle)
        {
            const ulong FnvOffset = 1469598103934665603UL;
            const ulong FnvPrime = 1099511628211UL;

            uint bits = 0;
            if (s.distanceAttenuation)    bits |= 1u << 0;
            if (s.airAbsorption)          bits |= 1u << 1;
            if (s.directivity)            bits |= 1u << 2;
            if (s.occlusion)              bits |= 1u << 3;
            if (s.transmission)           bits |= 1u << 4;
            if (s.reflections)            bits |= 1u << 5;
            if (s.pathing)                bits |= 1u << 6;
            if (s.applyHRTFToReflections) bits |= 1u << 7;
            if (s.applyHRTFToPathing)     bits |= 1u << 8;
            if (s.directBinaural)         bits |= 1u << 9;
            if (s.perspectiveCorrection)  bits |= 1u << 10;

            ulong h = FnvOffset;
            h = (h ^ bits) * FnvPrime;
            h = (h ^ (uint)s.interpolation) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.distanceAttenuationValue))) * FnvPrime;
            h = (h ^ (uint)s.distanceAttenuationInput) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.airAbsorptionLow))) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.airAbsorptionMid))) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.airAbsorptionHigh))) * FnvPrime;
            h = (h ^ (uint)s.airAbsorptionInput) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.directivityValue))) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.dipoleWeight))) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.dipolePower))) * FnvPrime;
            h = (h ^ (uint)s.directivityInput) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.occlusionValue))) * FnvPrime;
            h = (h ^ (uint)s.transmissionType) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.transmissionLow))) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.transmissionMid))) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.transmissionHigh))) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.directMixLevel))) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.reflectionsMixLevel))) * FnvPrime;
            h = (h ^ unchecked((uint)BitConverter.SingleToInt32Bits(s.pathingMixLevel))) * FnvPrime;
            h = (h ^ unchecked((uint)handle)) * FnvPrime;

            return h;
        }
    }
}
#endif
