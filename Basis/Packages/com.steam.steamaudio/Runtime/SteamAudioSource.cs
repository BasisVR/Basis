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

        public AudioSource mAudioSource = null;
        AudioSourceAttenuationData mAttenuationData = new AudioSourceAttenuationData { };
        DistanceAttenuationModel mCurveAttenuationModel = new DistanceAttenuationModel { };
        GCHandle mThis;
        SteamAudioSettings mSettings = null;

        // Extra user-added fields preserved
        public Transform Transform;
        public bool IsUnityEngineUsed;
        public bool AllowsUpdateParameters = false;
        private DistanceAttenuationModel mDefaultAttenuationModel;
        private SimulationInputsCore mCachedCore;

        private bool mCacheDirty = true;
        private bool mInitialized = false;

        // Index into SteamAudioManager's per-source NativeArray caches
        // (mSourceCoreCache/mSourceHandleCache). -1 when not registered. The manager
        // keeps this correct across AddSource/RemoveSource's swap-remove and the rare
        // RepairTransformArrayDesync compaction, both of which can move a live source
        // to a different slot — see SteamAudioManager for the corresponding updates.
        public int ManagerSlot { get; private set; } = -1;
        public void SetManagerSlot(int slot) { ManagerSlot = slot; }

        // ── Deferred initialization ──────────────────────────────────
        // iplSourceCreate is ~1ms per source. With 1k sources spawning,
        // that's a 1s+ hitch. Awake does only a cheap transform cache;
        // the heavy native init is queued and spread across frames.
        private static readonly System.Collections.Generic.Queue<SteamAudioSource> s_pendingInit
            = new System.Collections.Generic.Queue<SteamAudioSource>();

        /// <summary>Max native source creations per frame. Tune to taste.</summary>
        public static int InitBudgetPerFrame = 16;

        /// <summary>
        /// Call from SteamAudioManager.Update (or similar) to drain the init queue
        /// over multiple frames instead of all-at-once in Awake.
        /// </summary>
        public static void ProcessPendingInits()
        {
            int budget = InitBudgetPerFrame;
            while (budget > 0 && s_pendingInit.Count > 0)
            {
                var source = s_pendingInit.Dequeue();
                // Unity fake-null: destroyed MonoBehaviours compare == null but aren't C# null.
                if (source == null || source.mInitialized) continue;
                source.HeavyInit();
                budget--;
            }
        }

        /// <summary>
        /// Invalidates the cached simulation flags so the next <see cref="TryBuildInputsInto"/>
        /// rebuilds them.
        /// <para><b>Anything that writes the simulation booleans at runtime must call this.</b>
        /// <see cref="RebuildCache"/> folds <c>occlusion</c>, <c>transmission</c>,
        /// <c>directivity</c>, <c>airAbsorption</c>, <c>distanceAttenuation</c>, <c>reflections</c>
        /// and <c>pathing</c> into <c>mCachedDirectFlags</c>/<c>mCachedSimFlags</c> once and then
        /// clears the dirty bit, so a later assignment to the public field is simply never seen by
        /// the simulator. Every other invalidation site is a lifecycle callback — and
        /// <c>OnValidate</c>, the one that covers ordinary field edits, is <c>#if UNITY_EDITOR</c>,
        /// which is why this only ever showed up in builds and only for settings applied from
        /// code. <see cref="ForceUpdate"/> is NOT a substitute: it pushes DSP-side parameters to
        /// the audio engine source and does not touch the flags.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkCacheDirty()
        {
            mCacheDirty = true;
        }

        /// <summary>
        /// Re-reads the AudioSource's rolloff mode, distances and custom curve into the data
        /// <see cref="EvaluateDistanceCurve"/> serves to the native attenuation callback.
        /// <para>That data is otherwise captured exactly once, in <see cref="HeavyInit"/>, and
        /// <see cref="RebuildCache"/> only recomputes whether to <i>use</i> the curve model, not
        /// the curve itself — so a host that rewrites <c>maxDistance</c> or rebakes the rolloff
        /// curve (which Basis does on every hearing-range change) would keep being attenuated
        /// against the values the source happened to have at startup.</para>
        /// <para>Main thread only: it touches the AudioSource. Deliberately not folded into
        /// RebuildCache, which the direct worker can reach.</para>
        /// </summary>
        public void RefreshAttenuationData()
        {
            if (!mInitialized || mAudioSource == null) return;

            mAttenuationData.rolloffMode = mAudioSource.rolloffMode;
            mAttenuationData.minDistance = mAudioSource.minDistance;
            mAttenuationData.maxDistance = mAudioSource.maxDistance;
            mAttenuationData.curve = mAudioSource.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
        }

        private void Awake()
        {
            // Cheap: just cache transform. Heavy native init is deferred.
            if (transform != null)
            {
                Transform = this.transform;
            }
            if (mAudioSource == null)
            {
                TryGetComponent<AudioSource>(out mAudioSource);
            }
            s_pendingInit.Enqueue(this);
        }

        /// <summary>
        /// Performs the expensive native initialization (iplSourceCreate, audio engine setup).
        /// Called either from the frame-budgeted queue or on-demand via EnsureInitialized().
        /// </summary>
        private void HeavyInit()
        {
            if (mInitialized) return;
            mInitialized = true;

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

            mThis = GCHandle.Alloc(this);

            mDefaultAttenuationModel.type = DistanceAttenuationModelType.Default;

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

            MarkCacheDirty();

            // If OnEnable already fired before init completed, do the registration now.
            if (isActiveAndEnabled && mSource != null)
            {
                mSource.AddToSimulator(mSimulator);
                SteamAudioManager.AddSource(this);
                IsUnityEngineUsed = SteamAudioSettings.Singleton.audioEngine == AudioEngineType.Unity;
            }
        }

        /// <summary>
        /// Forces immediate initialization if not yet done (e.g., code needs the source NOW).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureInitialized()
        {
            if (!mInitialized) HeavyInit();
        }

        private void Start()
        {
            if (!mInitialized) return;
            if (mAudioEngineSource != null)
            {
                mAudioEngineSource.UpdateParameters(this);
            }

            MarkCacheDirty();
        }

        private void OnDestroy()
        {
            mInitialized = false;

            if (mAudioEngineSource != null)
            {
                mAudioEngineSource.Destroy();
                mAudioEngineSource = null;
            }

            if (mSource != null)
            {
                // The direct worker may still hold this native handle for one frame.
                // Defer the release to a worker-idle point; fall back to immediate.
                if (SteamAudioManager.TryDeferSourceRelease(mSource))
                    mSource = null;
                else
                {
                    mSource.Release();
                    mSource = null;
                }
            }
        }

        ~SteamAudioSource()
        {
            if (mThis.IsAllocated)
            {
                mThis.Free();
            }
        }

        private void OnEnable()
        {
            if (transform != null)
            {
                Transform = this.transform;
            }

            // If deferred init hasn't run yet, skip — HeavyInit will register when it completes.
            if (!mInitialized) return;

            mSource.AddToSimulator(mSimulator);
            SteamAudioManager.AddSource(this);

            IsUnityEngineUsed = SteamAudioSettings.Singleton.audioEngine == AudioEngineType.Unity;

            if (mAudioEngineSource != null)
            {
                mAudioEngineSource.UpdateParameters(this);
            }

            MarkCacheDirty();
        }

        private void OnDisable()
        {
            if (!mInitialized) return;
            SteamAudioManager.RemoveSource(this);
            mSource.RemoveFromSimulator(mSimulator);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // In editor, stuff can be reloaded / not ready yet.
            // Keep this lightweight: just mark dirty so next SetInputs rebuilds.
            MarkCacheDirty();
        }
#endif
        public void ForceUpdate()
        {
            if (!mInitialized || mAudioEngineSource == null) return;
            mAudioEngineSource.UpdateParameters(this);
        }

        public void ReapDirect()
        {
            if (!mInitialized) return;

            if (IsUnityEngineUsed && !HasSimulatedDirectOutput())
                return;

            UpdateOutputs(SimulationFlags.Direct);
            if (mAudioEngineSource != null)
                mAudioEngineSource.UpdateParameters(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasSimulatedDirectOutput()
        {
            return (distanceAttenuation && distanceAttenuationInput == DistanceAttenuationInput.PhysicsBased)
                || (airAbsorption && airAbsorptionInput == AirAbsorptionInput.SimulationDefined)
                || (directivity && directivityInput == DirectivityInput.SimulationDefined)
                || (occlusion && occlusionInput == OcclusionInput.SimulationDefined)
                || (transmission && transmissionInput == TransmissionInput.SimulationDefined);
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

        // Rebuilds the cached, blittable core so the hot path (TryBuildInputsInto,
        // and the manager's direct-pipeline unpack loop) can be a tight struct-copy
        // instead of a ~15-field walk. Pushes the result into this source's manager
        // slot (if registered) so the direct pipeline's per-frame job/loop sees it
        // without ever touching this managed object.
        private void RebuildCache(SteamAudioListener listener)
        {
            // Refresh settings ref (can change in editor / domain reloads)
            if (mSettings == null)
            {
                mSettings = SteamAudioSettings.Singleton;
            }

            // Default model cached
            mDefaultAttenuationModel.type = DistanceAttenuationModelType.Default;

            bool reflectionsRealtime = reflectionsType == ReflectionsType.Realtime;
            bool reflectionsBakedSrcActive = reflectionsType == ReflectionsType.BakedStaticSource && currentBakedSource != null;
            bool reflectionsBakedLstActive = reflectionsType == ReflectionsType.BakedStaticListener && listener != null && listener.currentBakedListener != null;
            bool reflectionsEnabledAny = reflections && (reflectionsRealtime || reflectionsBakedSrcActive || reflectionsBakedLstActive);

            bool useCurveDrivenAttenuationModel =
                (mSettings.audioEngine == AudioEngineType.Unity) &&
                distanceAttenuation &&
                (distanceAttenuationInput == DistanceAttenuationInput.CurveDriven) &&
                reflections &&
                useDistanceCurveForReflections;

            // Validate pathing once (no hot-path side effects)
            bool pathingEnabledAndValid = pathing && (pathingProbeBatch != null);
            if (pathing && pathingProbeBatch == null)
            {
                pathing = false; // preserve existing behavior, but do it once here
                Debug.LogWarning($"Pathing probe batch not set, disabling pathing for source {gameObject.name}.");
            }

            SimulationInputsCore core = default;
            core.useCurveAttenuation = useCurveDrivenAttenuationModel ? Bool.True : Bool.False;
            core.occlusionType = occlusionType;
            core.occlusionRadius = occlusionRadius;
            core.numOcclusionSamples = occlusionSamples;
            core.numTransmissionRays = maxTransmissionSurfaces;
            core.dipoleWeight = dipoleWeight;
            core.dipolePower = dipolePower;
            core.baked = (reflectionsType != ReflectionsType.Realtime) ? Bool.True : Bool.False;
            core.pathingProbes = pathingEnabledAndValid ? pathingProbeBatch.GetProbeBatch() : IntPtr.Zero;
            core.enableValidation = pathValidation ? Bool.True : Bool.False;
            core.findAlternatePaths = findAlternatePaths ? Bool.True : Bool.False;

            // Precompute flags once
            var simFlags = SimulationFlags.Direct;
            if (reflectionsEnabledAny) simFlags |= SimulationFlags.Reflections;
            if (pathingEnabledAndValid) simFlags |= SimulationFlags.Pathing;
            core.flags = simFlags;

            DirectSimulationFlags direct = default;
            if (distanceAttenuation) direct |= DirectSimulationFlags.DistanceAttenuation;
            if (airAbsorption) direct |= DirectSimulationFlags.AirAbsorption;
            if (directivity) direct |= DirectSimulationFlags.Directivity;
            if (occlusion) direct |= DirectSimulationFlags.Occlusion;
            if (transmission) direct |= DirectSimulationFlags.Transmission;
            core.directFlags = direct;

            mCachedCore = core;
            mCacheDirty = false;

            if (ManagerSlot >= 0)
            {
                SteamAudioManager.Singleton.WriteSourceCore(ManagerSlot, in mCachedCore);
            }
        }

        // Cheap per-frame entry point for the manager's direct-pipeline snapshot
        // build: rebuilds (and re-pushes) only if actually dirty. No-op field check
        // for the overwhelming majority of sources on the overwhelming majority of
        // frames.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureManagerCacheFresh(SteamAudioListener listener)
        {
            if (mCacheDirty) RebuildCache(listener);
        }

        // Forces the current cache into this source's manager slot, rebuilding
        // first only if dirty. Unlike EnsureManagerCacheFresh, this pushes even
        // when already clean — needed once at registration time: a source that was
        // disabled and is being re-enabled can have a perfectly clean mCachedCore
        // whose *previous* push went to a slot index that has since been reassigned
        // to a different source, so relying on the dirty check alone would leave
        // the new slot stale/zeroed until something happens to dirty this source.
        public void PushCurrentCoreToManager(SteamAudioListener listener)
        {
            if (mCacheDirty)
            {
                RebuildCache(listener);
            }
            else if (ManagerSlot >= 0)
            {
                SteamAudioManager.Singleton.WriteSourceCore(ManagerSlot, in mCachedCore);
            }
        }

        // Copies the blittable fields of a cached core into a SimulationInputs.
        // Shared by TryBuildInputsInto (reflections staging + the legacy
        // non-threaded direct path) and SteamAudioManager's direct-pipeline unpack
        // loop, so there is exactly one place that knows the field mapping.
        internal static void CopyCoreFields(in SimulationInputsCore core, ref SimulationInputs inputs)
        {
            inputs.flags = core.flags;
            inputs.directFlags = core.directFlags;

            inputs.airAbsorptionModel.type = AirAbsorptionModelType.Default;
            inputs.directivity.dipoleWeight = core.dipoleWeight;
            inputs.directivity.dipolePower = core.dipolePower;

            inputs.occlusionType = core.occlusionType;
            inputs.occlusionRadius = core.occlusionRadius;
            inputs.numOcclusionSamples = core.numOcclusionSamples;
            inputs.numTransmissionRays = core.numTransmissionRays;

            inputs.reverbScaleLow = 1f;
            inputs.reverbScaleMid = 1f;
            inputs.reverbScaleHigh = 1f;

            inputs.baked = core.baked;
            inputs.pathingProbes = core.pathingProbes;

            inputs.enableValidation = core.enableValidation;
            inputs.findAlternatePaths = core.findAlternatePaths;
        }

        // Resolves the fields that can never be cached in SimulationInputsCore: the
        // attenuation model (sometimes a live managed delegate) and the baked data
        // identifier (depends on whichever listener is currently active). Both are
        // resolved fresh on every call, exactly as TryBuildInputsInto always did, so
        // moving config into the cached core above changes nothing about how fresh
        // these two stay.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResolveLiveFields(SteamAudioListener listener, ref SimulationInputs inputs)
        {
            inputs.distanceAttenuationModel = (mCachedCore.useCurveAttenuation == Bool.True) ? mCurveAttenuationModel : mDefaultAttenuationModel;

            if (reflectionsType == ReflectionsType.BakedStaticSource && currentBakedSource != null)
            {
                inputs.bakedDataIdentifier = currentBakedSource.GetBakedDataIdentifier();
            }
            else if (reflectionsType == ReflectionsType.BakedStaticListener && listener != null && listener.currentBakedListener != null)
            {
                inputs.bakedDataIdentifier = listener.currentBakedListener.GetBakedDataIdentifier();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetInputs(SimulationFlags flags, Vector3 origin, Vector3 ahead, Vector3 up, Vector3 right, SteamAudioListener listener)
        {
            SimulationInputs inputs = default;
            if (TryBuildInputsInto(flags, origin, ahead, up, right, listener, ref inputs))
                mSource.SetInputs(flags, inputs);
        }

        // Builds the SimulationInputs but does NOT issue iplSourceSetInputs, so the
        // direct worker thread can make that native call off the main thread. Writes
        // the caller's slot in place to avoid copying the struct twice per source.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryBuildInputsInto(SimulationFlags flags, Vector3 origin, Vector3 ahead, Vector3 up, Vector3 right, SteamAudioListener listener, ref SimulationInputs inputs)
        {
            inputs = default;
            if (!mInitialized) return false;
            if (mCacheDirty)
            {
                RebuildCache(listener);
            }

            // Source transform
            inputs.source.origin = origin;
            inputs.source.ahead = ahead;
            inputs.source.up = up;
            inputs.source.right = right;

            CopyCoreFields(in mCachedCore, ref inputs);

            // Settings-derived fields not cached in the core (same value for every
            // source every frame; the manager's direct-pipeline loop reads these
            // straight off the shared settings singleton instead of per-source).
            inputs.hybridReverbTransitionTime = mSettings.hybridReverbTransitionTime;
            inputs.hybridReverbOverlapPercent = mSettings.hybridReverbOverlapPercent * 0.01f;
            inputs.visRadius = mSettings.bakingVisibilityRadius;
            inputs.visThreshold = mSettings.bakingVisibilityThreshold;
            inputs.visRange = mSettings.bakingVisibilityRange;
            inputs.pathingOrder = mSettings.bakingAmbisonicOrder;

            ResolveLiveFields(listener, ref inputs);

            return true;
        }

        // Native source handle for the direct worker; IntPtr.Zero until HeavyInit runs.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IntPtr GetNativeSourceHandle()
        {
            return (mInitialized && mSource != null) ? mSource.Get() : IntPtr.Zero;
        }

        public SimulationOutputs GetOutputs(SimulationFlags flags)
        {
            if (!mInitialized) return default;
            return mSource.GetOutputs(flags);
        }

        public Source GetSource()
        {
            EnsureInitialized();
            return mSource;
        }

        public void UpdateOutputs(SimulationFlags flags)
        {
            if (!mInitialized) return;
            var outputs = mSource.GetOutputs(flags);

            if (IsUnityEngineUsed && ((flags & SimulationFlags.Direct) != 0))
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

        // Reap path for the threaded direct pipeline: the worker has already fetched
        // outputs into a buffer, so this only copies sim-defined values to fields and
        // pushes them to the audio engine. The manager calls it only on the source's
        // push-cadence frames. Mirrors ReapDirect's early-out + UpdateOutputs(Direct)
        // field copy, minus the native GetOutputs.
        public void ApplyDirectOutputs(in DirectEffectParams direct)
        {
            if (!mInitialized) return;

            if (IsUnityEngineUsed && !HasSimulatedDirectOutput())
                return;

            if (IsUnityEngineUsed)
            {
                if (distanceAttenuation && distanceAttenuationInput == DistanceAttenuationInput.PhysicsBased)
                    distanceAttenuationValue = direct.distanceAttenuation;

                if (airAbsorption && airAbsorptionInput == AirAbsorptionInput.SimulationDefined)
                {
                    airAbsorptionLow = direct.airAbsorptionLow;
                    airAbsorptionMid = direct.airAbsorptionMid;
                    airAbsorptionHigh = direct.airAbsorptionHigh;
                }

                if (directivity && directivityInput == DirectivityInput.SimulationDefined)
                    directivityValue = direct.directivity;

                if (occlusion && occlusionInput == OcclusionInput.SimulationDefined)
                    occlusionValue = direct.occlusion;

                if (transmission && transmissionInput == TransmissionInput.SimulationDefined)
                {
                    transmissionLow = direct.transmissionLow;
                    transmissionMid = direct.transmissionMid;
                    transmissionHigh = direct.transmissionHigh;
                }
            }

            if (mAudioEngineSource != null)
                mAudioEngineSource.UpdateParameters(this);
        }

        // Reap path for the threaded reflections/pathing pipeline: the worker
        // (SteamAudioManager.RunSimulationInternal) has already fetched outputs into
        // mReflOutBuf, so this only mirrors UpdateOutputs' pathing branch against the
        // buffered struct instead of calling iplSourceGetOutputs itself. The manager
        // calls it only for sources that were part of the run that produced the buffer.
        public void ApplyReflectionsOutputs(in SimulationOutputs outputs)
        {
            if (!mInitialized) return;

            if (pathing)
            {
                var pathingOutputs = outputs.pathing;
                pathingOutputs.eqCoeffsLow = Mathf.Max(0.1f, pathingOutputs.eqCoeffsLow);
                pathingOutputs.eqCoeffsMid = Mathf.Max(0.1f, pathingOutputs.eqCoeffsMid);
                pathingOutputs.eqCoeffsHigh = Mathf.Max(0.1f, pathingOutputs.eqCoeffsHigh);
            }
        }

        void InitializeDeformedSphereMesh(int nPhi, int nTheta)
        {
            var dPhi = (2.0f * Mathf.PI) / nPhi;
            var dTheta = Mathf.PI / nTheta;

            mSphereVertices = new UnityEngine.Vector3[nPhi * nTheta];
            mDeformedSphereVertices = new UnityEngine.Vector3[nPhi * nTheta];

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

                    var v = new UnityEngine.Vector3(x, y, z);
                    mSphereVertices[index] = v;
                    mDeformedSphereVertices[index] = v;
                    index++;
                }
            }

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
            mDeformedSphereMesh.RecalculateNormals();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        UnityEngine.Vector3 DeformedVertex(UnityEngine.Vector3 v)
        {
            float cosine = v.z;
            float r = Mathf.Pow(Mathf.Abs((1.0f - dipoleWeight) + dipoleWeight * cosine), dipolePower);

            // Faster than Vector3.Scale
            v.x *= r;
            v.y *= r;
            v.z *= r;

            return v;
        }

        [MonoPInvokeCallback(typeof(DistanceAttenuationCallback))]
        public static float EvaluateDistanceCurve(float distance, IntPtr userData)
        {
            var target = (SteamAudioSource)GCHandle.FromIntPtr(userData).Target;

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
