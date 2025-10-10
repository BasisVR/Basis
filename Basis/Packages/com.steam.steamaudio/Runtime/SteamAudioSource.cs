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

using AOT;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SteamAudio
{
    public enum DistanceAttenuationInput
    {
        CurveDriven,
        PhysicsBased
    }

    public enum AirAbsorptionInput
    {
        SimulationDefined,
        UserDefined
    }

    public enum DirectivityInput
    {
        SimulationDefined,
        UserDefined
    }

    public enum OcclusionInput
    {
        SimulationDefined,
        UserDefined
    }

    public enum TransmissionInput
    {
        SimulationDefined,
        UserDefined
    }

    public enum ReflectionsType
    {
        Realtime,
        BakedStaticSource,
        BakedStaticListener
    }

    public struct AudioSourceAttenuationData
    {
        public AudioRolloffMode rolloffMode;
        public float minDistance;
        public float maxDistance;
        public AnimationCurve curve;
    }

    [AddComponentMenu("Steam Audio/Steam Audio Source")]
    public class SteamAudioSource : MonoBehaviour
    {
        [Header("HRTF Settings")]
        public bool directBinaural = true;
        public HRTFInterpolation interpolation = HRTFInterpolation.Nearest;
        public bool perspectiveCorrection = false;

        [Header("Attenuation Settings")]
        public bool distanceAttenuation = false;
        public DistanceAttenuationInput distanceAttenuationInput = DistanceAttenuationInput.CurveDriven;
        public float distanceAttenuationValue = 1.0f;
        public bool airAbsorption = false;
        public AirAbsorptionInput airAbsorptionInput = AirAbsorptionInput.SimulationDefined;
        [Range(0.0f, 1.0f)]
        public float airAbsorptionLow = 1.0f;
        [Range(0.0f, 1.0f)]
        public float airAbsorptionMid = 1.0f;
        [Range(0.0f, 1.0f)]
        public float airAbsorptionHigh = 1.0f;

        [Header("Directivity Settings")]
        public bool directivity = false;
        public DirectivityInput directivityInput = DirectivityInput.SimulationDefined;
        [Range(0.0f, 1.0f)]
        public float dipoleWeight = 0.0f;
        [Range(0.0f, 4.0f)]
        public float dipolePower = 0.0f;
        [Range(0.0f, 1.0f)]
        public float directivityValue = 1.0f;

        [Header("Occlusion Settings")]
        public bool occlusion = false;
        public OcclusionInput occlusionInput = OcclusionInput.SimulationDefined;
        public OcclusionType occlusionType = OcclusionType.Raycast;
        [Range(0.0f, 4.0f)]
        public float occlusionRadius = 1.0f;
        [Range(1, 128)]
        public int occlusionSamples = 16;
        [Range(0.0f, 1.0f)]
        public float occlusionValue = 1.0f;
        public bool transmission = false;
        public TransmissionType transmissionType = TransmissionType.FrequencyIndependent;
        public TransmissionInput transmissionInput = TransmissionInput.SimulationDefined;
        [Range(0.0f, 1.0f)]
        public float transmissionLow = 1.0f;
        [Range(0.0f, 1.0f)]
        public float transmissionMid = 1.0f;
        [Range(0.0f, 1.0f)]
        public float transmissionHigh = 1.0f;
        [Range(1, 8)]
        public int maxTransmissionSurfaces = 1;

        [Header("Direct Mix Settings")]
        [Range(0.0f, 1.0f)]
        public float directMixLevel = 1.0f;

        [Header("Reflections Settings")]
        public bool reflections = false;
        public ReflectionsType reflectionsType = ReflectionsType.Realtime;
        public bool useDistanceCurveForReflections = false;
        public SteamAudioBakedSource currentBakedSource = null;
        public IntPtr reflectionsIR = IntPtr.Zero;
        public float reverbTimeLow = 0.0f;
        public float reverbTimeMid = 0.0f;
        public float reverbTimeHigh = 0.0f;
        public float hybridReverbEQLow = 1.0f;
        public float hybridReverbEQMid = 1.0f;
        public float hybridReverbEQHigh = 1.0f;
        public int hybridReverbDelay = 0;
        public bool applyHRTFToReflections = false;
        [Range(0.0f, 10.0f)]
        public float reflectionsMixLevel = 1.0f;

        [Header("Pathing Settings")]
        public bool pathing = false;
        public SteamAudioProbeBatch pathingProbeBatch = null;
        public bool pathValidation = true;
        public bool findAlternatePaths = true;
        public float[] pathingEQ = new float[3] { 1.0f, 1.0f, 1.0f };
        public float[] pathingSH = new float[16];
        public bool applyHRTFToPathing = false;
        [Range(0.0f, 10.0f)]
        public float pathingMixLevel = 1.0f;

#if STEAMAUDIO_ENABLED
        Simulator mSimulator = null;
        Source mSource = null;
        AudioEngineSource mAudioEngineSource = null;
        UnityEngine.Vector3[] mSphereVertices = null;
        UnityEngine.Vector3[] mDeformedSphereVertices = null;
        Mesh mDeformedSphereMesh = null;

        AudioSource mAudioSource = null;
        AudioSourceAttenuationData mAttenuationData = new AudioSourceAttenuationData { };
        DistanceAttenuationModel mCurveAttenuationModel = new DistanceAttenuationModel { };
        GCHandle mThis;
        SteamAudioSettings mSettings = null;

        private void Awake()
        {
            Transform = this.transform;
            mSimulator = SteamAudioManager.Simulator;

            var settings = SteamAudioManager.GetSimulationSettings(false);
            mSource = new Source(SteamAudioManager.Simulator, settings);
            mSettings = SteamAudioSettings.Singleton;

            mAudioEngineSource = AudioEngineSource.Create(mSettings.audioEngine);
            if (mAudioEngineSource != null)
            {
                mAudioEngineSource.Initialize(gameObject);
                mAudioEngineSource.UpdateParameters(this);
            }

            mAudioSource = GetComponent<AudioSource>();

            mThis = GCHandle.Alloc(this);

            if (mSettings.audioEngine == AudioEngineType.Unity &&
                distanceAttenuation &&
                distanceAttenuationInput == DistanceAttenuationInput.CurveDriven &&
                reflections &&
                useDistanceCurveForReflections)
            {
                mAttenuationData.rolloffMode = mAudioSource.rolloffMode;
                mAttenuationData.minDistance = mAudioSource.minDistance;
                mAttenuationData.maxDistance = mAudioSource.maxDistance;
                mAttenuationData.curve = mAudioSource.GetCustomCurve(AudioSourceCurveType.CustomRolloff);

                mCurveAttenuationModel.type = DistanceAttenuationModelType.Callback;
                mCurveAttenuationModel.callback = EvaluateDistanceCurve;
                mCurveAttenuationModel.userData = GCHandle.ToIntPtr(mThis);
                mCurveAttenuationModel.dirty = Bool.False;
            }
        }

        private void Start()
        {
            if (mAudioEngineSource != null)
            {
                mAudioEngineSource.UpdateParameters(this);
            }
        }

        private void OnDestroy()
        {
            if (mAudioEngineSource != null)
            {
                mAudioEngineSource.Destroy();
                mAudioEngineSource = null;
            }

            if (mSource != null)
            {
                mSource.Release();
                mSource = null;
            }
        }

        ~SteamAudioSource()
        {
            if (mThis.IsAllocated)
            {
                mThis.Free();
            }
        }
        public Transform Transform;
        private void OnEnable()
        {
            Transform = this.transform;
            mSource.AddToSimulator(mSimulator);
            SteamAudioManager.AddSource(this);

            if (mAudioEngineSource != null)
            {
                mAudioEngineSource.UpdateParameters(this);
            }
        }

        private void OnDisable()
        {
            SteamAudioManager.RemoveSource(this);
            mSource.RemoveFromSimulator(mSimulator);
        }

        private void Update()
        {
            if (mAudioEngineSource != null)
            {
                mAudioEngineSource.UpdateParameters(this);
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (directivity && directivityInput == DirectivityInput.SimulationDefined && dipoleWeight > 0.0f)
            {
                if (mDeformedSphereMesh == null)
                {
                    InitializeDeformedSphereMesh(32, 32);
                }

                DeformSphereMesh();

                var oldColor = Gizmos.color;
                Gizmos.color = Color.red;
                transform.GetPositionAndRotation(out UnityEngine.Vector3 Position, out UnityEngine.Quaternion Rotation);
                Gizmos.DrawWireMesh(mDeformedSphereMesh, Position, Rotation);
                Gizmos.color = oldColor;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetInputs(SimulationFlags flags)
        {
            // --- Fast transform read: one native hop ---
            Transform.GetPositionAndRotation(out var pos, out var rot);
            var ahead = rot * UnityEngine.Vector3.forward; // pure math (managed), no extra native calls
            var up = rot * UnityEngine.Vector3.up;
            var right = rot * UnityEngine.Vector3.right;

            // --- Cache frequently used refs/values ---
            var settings = mSettings; // SteamAudioSettings.Singleton if that's what mSettings is
            var listener = SteamAudioManager.GetSteamAudioListener();

            // Precompute booleans once
            bool reflectionsRealtime = reflectionsType == ReflectionsType.Realtime;
            bool reflectionsBakedSrcActive = reflectionsType == ReflectionsType.BakedStaticSource && currentBakedSource != null;
            bool reflectionsBakedLstActive = reflectionsType == ReflectionsType.BakedStaticListener && listener != null && listener.currentBakedListener != null;
            bool reflectionsEnabledAny = reflections && (reflectionsRealtime || reflectionsBakedSrcActive || reflectionsBakedLstActive);

            bool curveDrivenReflections = mSettings.audioEngine == AudioEngineType.Unity
                                           && distanceAttenuation
                                           && distanceAttenuationInput == DistanceAttenuationInput.CurveDriven
                                           && reflections
                                           && useDistanceCurveForReflections;

            // --- Build inputs with minimal writes ---
            var inputs = new SimulationInputs { };

            // Source transform (4 converts; see prev message if you want the 1-quat-convert path)
            inputs.source.origin = Common.ConvertVector(pos);
            inputs.source.ahead = Common.ConvertVector(ahead);
            inputs.source.up = Common.ConvertVector(up);
            inputs.source.right = Common.ConvertVector(right);

            // Distance attenuation model
            inputs.distanceAttenuationModel = curveDrivenReflections
                ? mCurveAttenuationModel
                : new DistanceAttenuationModel { type = DistanceAttenuationModelType.Default };

            // Air absorption + directivity
            inputs.airAbsorptionModel.type = AirAbsorptionModelType.Default;
            inputs.directivity.dipoleWeight = dipoleWeight;
            inputs.directivity.dipolePower = dipolePower;

            // Occlusion / transmission
            inputs.occlusionType = occlusionType;
            inputs.occlusionRadius = occlusionRadius;
            inputs.numOcclusionSamples = occlusionSamples;
            inputs.numTransmissionRays = maxTransmissionSurfaces;

            // Reverb/scales/transition
            inputs.reverbScaleLow = 1f;
            inputs.reverbScaleMid = 1f;
            inputs.reverbScaleHigh = 1f;
            inputs.hybridReverbTransitionTime = settings.hybridReverbTransitionTime;
            inputs.hybridReverbOverlapPercent = settings.hybridReverbOverlapPercent * 0.01f;

            // Baking / pathing config
            inputs.baked = (reflectionsType != ReflectionsType.Realtime) ? Bool.True : Bool.False;
            inputs.pathingProbes = (pathingProbeBatch != null) ? pathingProbeBatch.GetProbeBatch() : IntPtr.Zero;
            inputs.visRadius = settings.bakingVisibilityRadius;
            inputs.visThreshold = settings.bakingVisibilityThreshold;
            inputs.visRange = settings.bakingVisibilityRange;
            inputs.pathingOrder = settings.bakingAmbisonicOrder;
            inputs.enableValidation = pathValidation ? Bool.True : Bool.False;
            inputs.findAlternatePaths = findAlternatePaths ? Bool.True : Bool.False;

            // Baked identifiers (only when actually usable)
            if (reflectionsBakedSrcActive)
            {
                inputs.bakedDataIdentifier = currentBakedSource.GetBakedDataIdentifier();
            }
            else if (reflectionsBakedLstActive)
            {
                inputs.bakedDataIdentifier = listener.currentBakedListener.GetBakedDataIdentifier();
            }

            // --- Simulation flags (build once, no branches re-checking state) ---
            var simFlags = SimulationFlags.Direct;
            if (reflectionsEnabledAny) simFlags |= SimulationFlags.Reflections;

            if (pathing)
            {
                if (pathingProbeBatch == null)
                {
                    pathing = false; // preserve existing side-effect
                    Debug.LogWarningFormat("Pathing probe batch not set, disabling pathing for source {0}.", gameObject.name);
                }
                else
                {
                    simFlags |= SimulationFlags.Pathing;
                }
            }
            inputs.flags = simFlags;

            // Instead of: var direct = 0;
            DirectSimulationFlags direct = default; // == (DirectSimulationFlags)0

            if (distanceAttenuation) direct |= DirectSimulationFlags.DistanceAttenuation;
            if (airAbsorption) direct |= DirectSimulationFlags.AirAbsorption;
            if (directivity) direct |= DirectSimulationFlags.Directivity;
            if (occlusion) direct |= DirectSimulationFlags.Occlusion;
            if (transmission) direct |= DirectSimulationFlags.Transmission;

            inputs.directFlags = direct;

            // Final handoff
            mSource.SetInputs(flags, inputs);
        }

        public SimulationOutputs GetOutputs(SimulationFlags flags)
        {
            return mSource.GetOutputs(flags);
        }

        public Source GetSource()
        {
            return mSource;
        }

        public void UpdateOutputs(SimulationFlags flags)
        {
            var outputs = mSource.GetOutputs(flags);

            if (SteamAudioSettings.Singleton.audioEngine == AudioEngineType.Unity &&
                ((flags & SimulationFlags.Direct) != 0))
            {
                if (distanceAttenuation && distanceAttenuationInput == DistanceAttenuationInput.PhysicsBased)
                {
                    distanceAttenuationValue = outputs.direct.distanceAttenuation;
                }

                if (airAbsorption && airAbsorptionInput == AirAbsorptionInput.SimulationDefined)
                {
                    airAbsorptionLow = outputs.direct.airAbsorptionLow;
                    airAbsorptionMid = outputs.direct.airAbsorptionMid;
                    airAbsorptionHigh = outputs.direct.airAbsorptionHigh;
                }

                if (directivity && directivityInput == DirectivityInput.SimulationDefined)
                {
                    directivityValue = outputs.direct.directivity;
                }

                if (occlusion && occlusionInput == OcclusionInput.SimulationDefined)
                {
                    occlusionValue = outputs.direct.occlusion;
                }

                if (transmission && transmissionInput == TransmissionInput.SimulationDefined)
                {
                    transmissionLow = outputs.direct.transmissionLow;
                    transmissionMid = outputs.direct.transmissionMid;
                    transmissionHigh = outputs.direct.transmissionHigh;
                }
            }

            if (pathing && ((flags & SimulationFlags.Pathing) != 0))
            {
                outputs.pathing.eqCoeffsLow = Mathf.Max(0.1f, outputs.pathing.eqCoeffsLow);
                outputs.pathing.eqCoeffsMid = Mathf.Max(0.1f, outputs.pathing.eqCoeffsMid);
                outputs.pathing.eqCoeffsHigh = Mathf.Max(0.1f, outputs.pathing.eqCoeffsHigh);
            }
        }

        void InitializeDeformedSphereMesh(int nPhi, int nTheta)
        {
            var dPhi = (2.0f * Mathf.PI) / nPhi;
            var dTheta = Mathf.PI / nTheta;

            mSphereVertices = new UnityEngine.Vector3[nPhi * nTheta];
            var index = 0;
            for (var i = 0; i < nPhi; ++i)
            {
                var phi = i * dPhi;
                for (var j = 0; j < nTheta; ++j)
                {
                    var theta = (j * dTheta) - (0.5f * Mathf.PI);

                    var x = Mathf.Cos(theta) * Mathf.Sin(phi);
                    var y = Mathf.Sin(theta);
                    var z = Mathf.Cos(theta) * -Mathf.Cos(phi);

                    var vertex = new UnityEngine.Vector3(x, y, z);

                    mSphereVertices[index++] = vertex;
                }
            }

            mDeformedSphereVertices = new UnityEngine.Vector3[nPhi * nTheta];
            Array.Copy(mSphereVertices, mDeformedSphereVertices, mSphereVertices.Length);

            var indices = new int[6 * nPhi * (nTheta - 1)];
            index = 0;
            for (var i = 0; i < nPhi; ++i)
            {
                for (var j = 0; j < nTheta - 1; ++j)
                {
                    var i0 = i * nTheta + j;
                    var i1 = i * nTheta + (j + 1);
                    var i2 = ((i + 1) % nPhi) * nTheta + (j + 1);
                    var i3 = ((i + 1) % nPhi) * nTheta + j;

                    indices[index++] = i0;
                    indices[index++] = i1;
                    indices[index++] = i2;
                    indices[index++] = i0;
                    indices[index++] = i2;
                    indices[index++] = i3;
                }
            }

            mDeformedSphereMesh = new Mesh();
            mDeformedSphereMesh.vertices = mDeformedSphereVertices;
            mDeformedSphereMesh.triangles = indices;
            mDeformedSphereMesh.RecalculateNormals();
        }

        void DeformSphereMesh()
        {
            for (var i = 0; i < mSphereVertices.Length; ++i)
            {
                mDeformedSphereVertices[i] = DeformedVertex(mSphereVertices[i]);
            }

            mDeformedSphereMesh.vertices = mDeformedSphereVertices;
        }

        UnityEngine.Vector3 DeformedVertex(UnityEngine.Vector3 vertex)
        {
            var cosine = vertex.z;
            var r = Mathf.Pow(Mathf.Abs((1.0f - dipoleWeight) + dipoleWeight * cosine), dipolePower);
            var deformedVertex = vertex;
            deformedVertex.Scale(new UnityEngine.Vector3(r, r, r));
            return deformedVertex;
        }

        [MonoPInvokeCallback(typeof(DistanceAttenuationCallback))]
        public static float EvaluateDistanceCurve(float distance, IntPtr userData)
        {
            var target = (SteamAudioSource) GCHandle.FromIntPtr(userData).Target;

            var rMin = target.mAttenuationData.minDistance;
            var rMax = target.mAttenuationData.maxDistance;

            switch (target.mAttenuationData.rolloffMode)
            {
                case AudioRolloffMode.Logarithmic:
                    if (distance < rMin)
                        return 1.0f;
                    else if (distance > rMax)
                        return 0.0f;
                    else
                        return rMin / distance;

                case AudioRolloffMode.Linear:
                    if (distance < rMin)
                        return 1.0f;
                    else if (distance > rMax)
                        return 0.0f;
                    else
                        return (rMax - distance) / (rMax - rMin);

                case AudioRolloffMode.Custom:
#if UNITY_2018_1_OR_NEWER
                    return target.mAttenuationData.curve.Evaluate(distance / rMax);
#else
                    if (distance < rMin)
                        return 1.0f;
                    else if (distance > rMax)
                        return 0.0f;
                    else
                        return rMin / distance;
#endif

                default:
                    return 0.0f;
            }
        }
#endif
    }
}
