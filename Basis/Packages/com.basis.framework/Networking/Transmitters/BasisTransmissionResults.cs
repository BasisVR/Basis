using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Profiler;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static SerializableBasis;
[System.Serializable]
public partial class BasisTransmissionResults
{
    // Jobs
    public BasisDistanceJobParallel distanceJob;
    public BasisDistanceReduceJob reduceJob;
    public BasisAvatarCapJob avatarCapJob;
    public BasisAudioCapJob audioCapJob;
    public BasisDirectionalDampenJob dampenJob;
    public BasisViewConeAvatarJob viewConeJob;

    public JobHandle distanceJobHandle;
    public JobHandle reduceJobHandle;
    public JobHandle avatarCapJobHandle;
    public JobHandle audioCapJobHandle;
    public JobHandle dampenJobHandle;
    public JobHandle viewConeJobHandle;

    // Timing / interval control
    public float intervalSeconds = 0.05f;
    public float timer = 0f;
    public float SquaredSmallestDistance;
    public float UnClampedInterval;
    public float DefaultInterval;

    // Change flags (derived from mask)
    public bool AnyMicrophoneRangeChanged;
    public bool AnyHearingRangeChanged;
    public bool AnyAvatarRangeChanged;
    public bool AnyLodRangeChanged;

    // Track previous range values to detect setting changes that hysteresis would hide
    private float _lastAvatarRange;
    private float _lastHearingRange;
    private float _lastMicrophoneRange;

    // Network
    [SerializeReference] public BasisNetworkTransmitter BasisNetworkTransmitter;
    public NetDataWriter VRMWriter = new NetDataWriter(true, 0);

    // Recipients / excluded
    public List<ushort> TalkingPoints = new List<ushort>(128);
    public List<ushort> ExcludedPoints = new List<ushort>(128);
    private byte[] bitfieldBuffer = new byte[128];

    // Capacity / length
    public int LengthOfArrays = -1;
    private int capacity = 0;

    /// <summary>
    /// Pre-computed per-index flag: true when the remote player currently has their
    /// real avatar loaded (InAvatarRange and not fallback). Filled in the positions
    /// loop so managed objects are never touched during sorting.
    /// </summary>
    private NativeArray<bool> hasRealAvatarLoaded;

    /// <summary>
    /// Scratch buffer for avatar-cap sorting. Sized to capacity, reused each tick.
    /// </summary>
    private NativeArray<AvatarCapEntry> avatarCapEntries;

    /// <summary>
    /// Per-index directional dampening multiplier computed by the Burst parallel job.
    /// Copied to managed AudioReceiverModule after the job completes.
    /// </summary>
    private NativeArray<float> directionalDampening;

    /// <summary>
    /// Pre-computed per-index flag: true when the remote player currently has an
    /// active audio source. Filled in the positions loop so managed objects are
    /// never touched during the audio cap sort.
    /// </summary>
    private NativeArray<bool> hasActiveAudioSource;

    /// <summary>
    /// Scratch buffer for audio-cap sorting. Sized to capacity, reused each tick.
    /// </summary>
    private NativeArray<AudioCapEntry> audioCapEntries;

    // State
    public bool IndexChanged;

    // Arrays
    private NativeArray<float> distanceSq;
    private NativeArray<float3> targetPositions;

    public NativeArray<bool> MicrophoneRange;
    private NativeArray<bool> hearingRange;
    public NativeArray<bool> AvatarRange;

    public NativeArray<bool> PrevInMicrophoneRange;
    public NativeArray<bool> PrevInHearingRange;
    public NativeArray<bool> PrevInAvatarRange;

    public NativeArray<short> MeshLodLevel;
    public NativeArray<short> prevMeshLodLevel;
    public NativeArray<bool> MeshLodRange;

    // Scratch + reduced outputs
    private NativeArray<float> perIndexMinD2;
    private NativeArray<int> perIndexMask;

    private NativeArray<float> smallestD2; // length 1
    private NativeArray<int> changeMask;   // length 1

    public static float HysteresisPercent = 1.10f * 1.10f; // 10% hysteresis

    /// <summary>Half-angle (degrees) of the eye-gaze cone used to boost MeshLod detail for players the user is looking at.</summary>
    public static float GazeFoveationConeDegrees = 20f;

    /// <summary>Squared-distance multiplier applied at the gaze cone center; 0.25 ≈ "treat at half the actual distance".</summary>
    public static float GazeFoveationBoost = 0.25f;

    public static float LastHearingRange = -1;
    public static bool RevaluteAudioRanges = false;
    public static float ConvertedVoiceDistance;
    /// <summary>
    /// Called each frame; drives scheduling of distance job and network sync.
    /// </summary>
    public void Simulate()
    {
        float dt = Time.deltaTime;
        timer += dt;

        if (timer < intervalSeconds)
        {
#if UNITY_EDITOR
            if (BasisEventDriverProfilerData.Enabled) BasisEventDriverProfilerData.Net_TransmitSimRanThisTick = false;
#endif
            return;
        }

        float intervalUsedThisTick = intervalSeconds;

        if (!CanDoSimulate(intervalUsedThisTick, out BasisAvatar avatar))
        {
#if UNITY_EDITOR
            if (BasisEventDriverProfilerData.Enabled) BasisEventDriverProfilerData.Net_TransmitSimRanThisTick = false;
#endif
            return;
        }

        int receiverCount = BasisNetworkPlayers.ReceiverCount;
        var snapshot = BasisNetworkPlayers.ReceiversSnapshot;

        if (receiverCount <= 0)
        {
            // Still update interval pacing even with no receivers
            UpdateSendInterval(0f);
            timer = math.max(0f, timer - intervalUsedThisTick);
            IndexChanged = false;
            return;
        }

#if UNITY_EDITOR
        bool _prof = BasisEventDriverProfilerData.Enabled;
        System.Diagnostics.Stopwatch _psw = null;
        if (_prof)
        {
            BasisEventDriverProfilerData.Net_TransmitSimRanThisTick = true;
            _psw = System.Diagnostics.Stopwatch.StartNew();
        }
#endif
        EnsureCapacity(receiverCount);
        LengthOfArrays = receiverCount;

        // Fill target positions aligned to snapshot order.
        // Also pre-compute stickiness flags for the avatar cap so the
        // NativeArray sort never needs to touch managed objects.
        // Uses unsafe pointers to bypass NativeArray safety checks (~3ms savings at 1k players).
        unsafe
        {
            float3* pTargetPositions = (float3*)targetPositions.GetUnsafePtr();
            bool* pHasRealAvatar = (bool*)hasRealAvatarLoaded.GetUnsafePtr();
            bool* pHasActiveAudio = (bool*)hasActiveAudioSource.GetUnsafePtr();

            float3 farAway = BasisLocalCameraDriver.Position + new Vector3(900, 900, 900);

            for (int Index = 0; Index < receiverCount; Index++)
            {
                BasisNetworkReceiver remote = snapshot[Index];
                ushort id = remote.playerId;

                if (RemoteBoneJobSystem.GetOutGoingMouth(id, out float3 outgoing))
                {
                    pTargetPositions[Index] = outgoing;
                }
                else
                {
                    pTargetPositions[Index] = farAway;
                }

                var remotePlayer = remote.RemotePlayer;
                pHasRealAvatar[Index] = remotePlayer.InAvatarRange && !remotePlayer.IsConsideredFallBackAvatar;
                pHasActiveAudio[Index] = remote.AudioReceiverModule.HasAudioSource;
            }
        }
        var CurrentHearingRange = SMModuleDistanceBasedReductions.HearingRange;
        if (LastHearingRange != CurrentHearingRange)
        {
            LastHearingRange = CurrentHearingRange;
            ConvertedVoiceDistance = Mathf.Sqrt(LastHearingRange);
            RevaluteAudioRanges = true;
        }
        else
        {
            RevaluteAudioRanges = false;
        }
#if UNITY_EDITOR
        if (_prof)
        {
            _psw.Stop();
            BasisEventDriverProfilerData.Net_TransmitSim_FillPositionsMs = _psw.Elapsed.TotalMilliseconds;
            _psw.Restart();
        }
#endif
        // Configure job inputs (only what changes per tick)
        distanceJob.SquaredAvatarDistance = SMModuleDistanceBasedReductions.AvatarRange;
        distanceJob.SquaredHearingDistance = SMModuleDistanceBasedReductions.HearingRange;
        distanceJob.SquaredVoiceDistance = SMModuleDistanceBasedReductions.MicrophoneRange;

        // Range culling is keyed off the player's head, not the rendering camera, so
        // third-person doesn't push avatars/audio out of range from behind the player.
        distanceJob.referencePosition = BasisLocalCameraDriver.HeadPosition;
        distanceJob.ReductionMultiplier = SMModuleDistanceBasedReductions.MeshLod;

        distanceJob.UseEyeGaze = BasisLocalCameraDriver.HasEyeGaze;
        if (distanceJob.UseEyeGaze)
        {
            distanceJob.GazeForward = BasisLocalCameraDriver.GazeDirection;
            distanceJob.CosHalfGazeCone = math.cos(math.radians(GazeFoveationConeDegrees * 0.5f));
            distanceJob.GazeBoostFactor = GazeFoveationBoost;
        }

        distanceJob.HysteresisPercent = HysteresisPercent;

        // Schedule distance job (parallel)
        distanceJobHandle = distanceJob.Schedule(receiverCount, 64);

        // Reduce depends on distance job (reads PerIndexMinD2/PerIndexMask)
        reduceJobHandle = reduceJob.Schedule(distanceJobHandle);

        // Avatar cap job depends on distance job (reads AvatarRange/DistanceSq).
        // Runs in parallel with reduce — they touch disjoint arrays.
        if (SMModuleDistanceBasedReductions.UseMaxVisibleAvatars)
        {
            int maxVisible = SMModuleDistanceBasedReductions.MaxVisibleAvatars;
            avatarCapJob.MaxVisible = maxVisible;
            avatarCapJob.ReceiverCount = receiverCount;
            avatarCapJobHandle = avatarCapJob.Schedule(distanceJobHandle);
        }
        else
        {
            avatarCapJobHandle = distanceJobHandle;
        }

        // Audio cap job depends on distance job (reads hearingRange/DistanceSq).
        // Runs in parallel with reduce and avatar cap — they touch disjoint arrays.
        if (SMModuleDistanceBasedReductions.UseMaxAudioSources)
        {
            int maxAudio = SMModuleDistanceBasedReductions.MaxAudioSources;
            audioCapJob.MaxAudio = maxAudio;
            audioCapJob.ReceiverCount = receiverCount;
            audioCapJobHandle = audioCapJob.Schedule(distanceJobHandle);
        }
        else
        {
            audioCapJobHandle = distanceJobHandle;
        }

        // View cone avatar job: filters AvatarRange to only show avatars in the
        // direction the player is looking. Depends on distance + cap jobs.
        if (SMModuleDistanceBasedReductions.UseViewConeAvatars)
        {
            float viewAngle = SMModuleDistanceBasedReductions.ViewConeAngle;
            float halfConeRad = viewAngle * 0.5f * Mathf.Deg2Rad;
            // 10° wider exit cone prevents flickering when camera wobbles near the boundary
            float exitHalfConeRad = math.min(halfConeRad + 10f * Mathf.Deg2Rad, Mathf.PI);

            viewConeJob.ListenerPosition = BasisLocalCameraDriver.Position;
            viewConeJob.ListenerForward = BasisLocalCameraDriver.Forward();
            viewConeJob.CosHalfCone = Mathf.Cos(halfConeRad);
            viewConeJob.CosHalfConeExit = Mathf.Cos(exitHalfConeRad);

            viewConeJobHandle = viewConeJob.Schedule(receiverCount, 64, avatarCapJobHandle);
        }
        else
        {
            viewConeJobHandle = avatarCapJobHandle;
        }

        // Directional dampening job: only reads targetPositions (shared ReadOnly
        // with distance job) — no dependencies, runs in parallel with everything.
        float coneAngle = BasisSettingsDefaults.RAListenerConeAngle.RawValue;
        bool dampenEnabled = coneAngle < 360f;
        if (dampenEnabled)
        {
            float dampenPercent = Mathf.Clamp(BasisSettingsDefaults.RAListenerDampenAmount.RawValue, 1f, 95f);
            float halfConeRad = coneAngle * 0.5f * Mathf.Deg2Rad;
            float cosHalfCone = Mathf.Cos(halfConeRad);

            dampenJob.ListenerPosition = BasisLocalCameraDriver.Position;
            dampenJob.ListenerForward = BasisLocalCameraDriver.Forward();
            dampenJob.CosHalfCone = cosHalfCone;
            dampenJob.CosRange = cosHalfCone + 1f;
            dampenJob.MinVolume = 1f - (dampenPercent / 100f);

            dampenJobHandle = dampenJob.Schedule(receiverCount, 64);
        }
        else
        {
            dampenJobHandle = default;
        }

#if UNITY_EDITOR
        if (_prof) { _psw.Stop(); BasisEventDriverProfilerData.Net_TransmitSim_JobScheduleMs = _psw.Elapsed.TotalMilliseconds; _psw.Restart(); }
#endif
        // Do work that doesn't depend on distance results
        BasisNetworkAvatarCompressor.Compress(BasisNetworkTransmitter, avatar.Animator);

#if UNITY_EDITOR
        if (_prof)
        {
            _psw.Stop();
            BasisEventDriverProfilerData.Net_TransmitSim_CompressMs = _psw.Elapsed.TotalMilliseconds;
            _psw.Restart();
        }
#endif
        // Finish before consuming results — single sync point via CombineDependencies
        var combined = JobHandle.CombineDependencies(reduceJobHandle, viewConeJobHandle, audioCapJobHandle);
        if (dampenEnabled)
        {
            combined = JobHandle.CombineDependencies(combined, dampenJobHandle);
        }
        combined.Complete();

#if UNITY_EDITOR
        if (_prof)
        {
            _psw.Stop();
            BasisEventDriverProfilerData.Net_TransmitSim_JobCompleteMs = _psw.Elapsed.TotalMilliseconds;
            _psw.Restart();
        }
#endif
        int mask = changeMask[0];
        AnyMicrophoneRangeChanged = (mask & 1) != 0;
        AnyHearingRangeChanged = (mask & 2) != 0;
        AnyAvatarRangeChanged = (mask & 4) != 0;
        AnyLodRangeChanged = (mask & 8) != 0;

        // Detect setting slider changes that hysteresis would hide.
        // When the user decreases the range, players in the hysteresis band
        // (between newRange and newRange*1.21) don't trigger AnyXChanged because
        // they pass the exit threshold check. Force a full re-eval on range changes.
        float curAvatarRange = SMModuleDistanceBasedReductions.AvatarRange;
        float curHearingRange = SMModuleDistanceBasedReductions.HearingRange;
        float curMicRange = SMModuleDistanceBasedReductions.MicrophoneRange;

        if (_lastAvatarRange != curAvatarRange)
        {
            AnyAvatarRangeChanged = true;
            _lastAvatarRange = curAvatarRange;
        }
        if (_lastHearingRange != curHearingRange)
        {
            AnyHearingRangeChanged = true;
            _lastHearingRange = curHearingRange;
        }
        if (_lastMicrophoneRange != curMicRange)
        {
            AnyMicrophoneRangeChanged = true;
            _lastMicrophoneRange = curMicRange;
        }

        SquaredSmallestDistance = smallestD2[0];
        if (!float.IsFinite(SquaredSmallestDistance))
        {
            SquaredSmallestDistance = 0f;
        }

        bool microphoneChange = IndexChanged || AnyMicrophoneRangeChanged;
        bool lodChange = IndexChanged || AnyLodRangeChanged;

        // Avatar range is always evaluated per-player in the loop below — the debounce
        // logic needs to run every tick so pending transitions can commit on the
        // tick their timer expires, not only when some other avatar flag also flipped.

        // Single-pass post-processing: hearing, audio range, dampening, avatar, LOD.
        // Merging these loops avoids repeated cache-miss traversals of the same
        // managed snapshot[] objects (up to 6 separate passes before).
        // Uses unsafe pointers to bypass NativeArray safety checks.
        float visemeRangeSq = SMModuleDistanceBasedReductions.HearingRange * 0.25f;
        unsafe
        {
            bool* pHearingRange = (bool*)hearingRange.GetUnsafeReadOnlyPtr();
            float* pDistanceSq = (float*)distanceSq.GetUnsafeReadOnlyPtr();
            float* pDampening = dampenEnabled ? (float*)directionalDampening.GetUnsafeReadOnlyPtr() : null;
            bool* pAvatarRange = (bool*)AvatarRange.GetUnsafeReadOnlyPtr();
            bool* pMeshLodRange = (bool*)MeshLodRange.GetUnsafeReadOnlyPtr();
            short* pMeshLodLevel = (short*)MeshLodLevel.GetUnsafeReadOnlyPtr();

            for (int i = 0; i < receiverCount; i++)
            {
                var receiver = snapshot[i];
                var audio = receiver.AudioReceiverModule;
                var remote = receiver.RemotePlayer;

                // Always check for HasAudioSource/hearingRange mismatch rather than
                // only on transitions. This ensures StartAudio is retried if a previous
                // attempt failed (e.g. async exception), preventing permanent voice loss.
                bool canHear = pHearingRange[i];
                if (audio.HasAudioSource != canHear)
                {
                    if (canHear)
                    {
                        audio.StartAudio(ConvertedVoiceDistance);
                        remote.OutOfRangeFromLocal = false;
                    }
                    else
                    {
                        audio.StopAudio();
                        remote.OutOfRangeFromLocal = true;
                    }
                }

                if (RevaluteAudioRanges)
                {
                    audio.ApplyRangeData(ConvertedVoiceDistance);
                }

                audio.DirectionalDampeningMultiplier = pDampening != null ? pDampening[i] : 1f;

                // Viseme distance cutoff: skip lip-sync for players beyond half
                // the hearing distance — too far to see mouth shapes. Routed
                // through SetVisemeRange so BasisRemoteAudioDriver.ActiveDrivers
                // stays in sync on transitions.
                BasisRemoteAudioDriver.SetVisemeRange(audio.visemeDriver, pDistanceSq[i] < visemeRangeSq);

                // Avatar range transition with debounce. Always runs (not gated on
                // avatarChange) so a pending transition started on a previous tick can
                // continue to tick forward even when no other avatar state changed.
                //
                // View-cone and avatar-cap logic can cause rapid flips (e.g. the local
                // player rotating their head, or a crowd shifting around the cap limit).
                // Without this debounce, each flip tears down the real avatar, swaps to
                // the loading avatar, and re-enters the download queue — which is the
                // "avatars randomly fall back under load" symptom.
                {
                    bool inRange = pAvatarRange[i];
                    if (inRange != remote.InAvatarRange)
                    {
                        float now = Time.unscaledTime;
                        if (!remote.PendingRangeActive || remote.PendingRangeTarget != inRange)
                        {
                            // New transition (or target changed mid-debounce) — restart the timer.
                            remote.PendingRangeActive = true;
                            remote.PendingRangeTarget = inRange;
                            remote.PendingRangeCommitTime = now + BasisRemotePlayer.AvatarRangeDebounceSeconds;
                        }
                        else if (now >= remote.PendingRangeCommitTime)
                        {
                            // Target has remained stable for the debounce window — commit.
                            remote.InAvatarRange = inRange;
                            remote.PendingRangeActive = false;

                            if (!remote.IsLoadingAnAvatar && (inRange || !remote.IsConsideredFallBackAvatar))
                            {
                                remote.ReloadAvatar();
                            }
                        }
                    }
                    else if (remote.PendingRangeActive)
                    {
                        // The flip reverted before the debounce expired — discard it.
                        remote.PendingRangeActive = false;
                    }
                }

                if (lodChange && pMeshLodRange[i])
                {
                    remote.ChangeMeshLOD(pMeshLodLevel[i]);
                }

                // Update pose LOD from distance — independent of mesh LOD
                remote.CurrentLodLevel = pMeshLodLevel[i];
            }
        }

#if UNITY_EDITOR
        if (_prof)
        {
            _psw.Stop();
            BasisEventDriverProfilerData.Net_TransmitSim_PostProcessMs = _psw.Elapsed.TotalMilliseconds;
            _psw.Restart();
        }
#endif
        // Update who we are talking to (serialize without allocations)
        if (microphoneChange)
        {
            BuildAndSendTalkingPoints(snapshot, receiverCount);
        }
#if UNITY_EDITOR
        if (_prof) { _psw.Stop(); BasisEventDriverProfilerData.Net_TransmitSim_TalkingPointsMs = _psw.Elapsed.TotalMilliseconds; }
#endif

        UpdateSendInterval(SquaredSmallestDistance);

        // Recording hook
        if (BasisAvatarRecorder.IsRecording)
        {
            var anim = avatar.Animator;
            BasisAvatarRecorder.StoreData(
                intervalSeconds,
                anim.bodyRotation,
                anim.bodyPosition,
             null, //  BasisNetworkTransmitter.HumanPose.muscles,
                anim.transform.localScale.y);
        }

        // Swap buffers instead of CopyTo() each tick (avoid full-array memcopy on main thread)
        Swap(ref MicrophoneRange, ref PrevInMicrophoneRange);
        Swap(ref hearingRange, ref PrevInHearingRange);
        Swap(ref AvatarRange, ref PrevInAvatarRange);
        Swap(ref MeshLodLevel, ref prevMeshLodLevel);

        // Rebind swapped arrays to the job for next tick
        distanceJob.MicrophoneRange = MicrophoneRange;
        distanceJob.PrevInMicrophoneRange = PrevInMicrophoneRange;

        distanceJob.hearingRange = hearingRange;
        distanceJob.PrevInHearingRange = PrevInHearingRange;
        audioCapJob.HearingRange = hearingRange;

        distanceJob.AvatarRange = AvatarRange;
        distanceJob.PrevInAvatarRange = PrevInAvatarRange;
        avatarCapJob.AvatarRange = AvatarRange;
        viewConeJob.AvatarRange = AvatarRange;
        viewConeJob.PrevInAvatarRange = PrevInAvatarRange;

        distanceJob.MeshLodLevel = MeshLodLevel;
        distanceJob.PrevMeshLodLevel = prevMeshLodLevel;

        IndexChanged = false;

        // Consume one interval worth of accumulated time (robust to overshoot)
        timer = math.max(0f, timer - intervalUsedThisTick);
    }

    private void BuildAndSendTalkingPoints(IReadOnlyList<BasisNetworkReceiver> snapshot, int receiverCount)
    {
        if (TalkingPoints.Capacity < receiverCount)
        {
            TalkingPoints.Capacity = receiverCount;
        }

        if (ExcludedPoints.Capacity < receiverCount)
        {
            ExcludedPoints.Capacity = receiverCount;
        }

        TalkingPoints.Clear();
        ExcludedPoints.Clear();
        ushort maxId = 0;

        unsafe
        {
            bool* pMicRange = (bool*)MicrophoneRange.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < receiverCount; i++)
            {
                ushort id = snapshot[i].playerId;
                if (id > maxId)
                {
                    maxId = id;
                }

                if (pMicRange[i])
                {
                    TalkingPoints.Add(id);
                }
                else
                {
                    ExcludedPoints.Add(id);
                }
            }
        }

        int recipientCount = TalkingPoints.Count;
        int excludedCount = ExcludedPoints.Count;
        BasisNetworkTransmitter.HasReasonToSendAudio = recipientCount != 0;
        // Compute wire sizes for each mode
        int listSize = (recipientCount <= byte.MaxValue ? 1 : 2) + recipientCount * 2;
        int invertedSize = (excludedCount <= byte.MaxValue ? 1 : 2) + excludedCount * 2;
        int bitfieldBytes = (maxId / 8) + 1;
        int bitfieldSize = 2 + bitfieldBytes;

        VRMWriter.Reset();
        byte channel;

        if (bitfieldSize <= listSize && bitfieldSize <= invertedSize)
        {
            // Bitfield mode: [byteCount: ushort][bitfield bytes]
            channel = BasisNetworkCommons.AudioRecipientsBitfieldChannel;

            // Grow buffer if needed
            if (bitfieldBuffer.Length < bitfieldBytes)
                bitfieldBuffer = new byte[bitfieldBytes];

            System.Array.Clear(bitfieldBuffer, 0, bitfieldBytes);
            for (int Index = 0; Index < recipientCount; Index++)
            {
                int id = TalkingPoints[Index];
                bitfieldBuffer[id / 8] |= (byte)(1 << (id % 8));
            }

            VRMWriter.Put((ushort)bitfieldBytes);
            VRMWriter.Put(bitfieldBuffer, 0, bitfieldBytes);
        }
        else if (invertedSize < listSize)
        {
            // Inverted list mode: send excluded IDs
            bool largeCnt = excludedCount > byte.MaxValue;
            channel = largeCnt  ? BasisNetworkCommons.AudioRecipientsInvertedLargeChannel : BasisNetworkCommons.AudioRecipientsInvertedChannel;
            if (largeCnt)
            {
                VRMWriter.Put((ushort)excludedCount);
            }
            else
            {
                VRMWriter.Put((byte)excludedCount);
            }

            for (int i = 0; i < excludedCount; i++)
            {
                VRMWriter.Put(ExcludedPoints[i]);
            }
        }
        else
        {
            // Normal list mode: send recipient IDs
            bool largeCnt = recipientCount > byte.MaxValue;
            channel = largeCnt  ? BasisNetworkCommons.AudioRecipientsLargeChannel  : BasisNetworkCommons.AudioRecipientsChannel;
            if (largeCnt)
            {
                VRMWriter.Put((ushort)recipientCount);
            }
            else
            {
                VRMWriter.Put((byte)recipientCount);
            }

            for (int i = 0; i < recipientCount; i++)
            {
                VRMWriter.Put(TalkingPoints[i]);
            }
        }

        BasisNetworkConnection.LocalPlayerPeer.Send(
            VRMWriter,
            channel,
            DeliveryMethod.ReliableOrdered);

        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AudioRecipients, VRMWriter.Length);
    }

    private void UpdateSendInterval(float smallestD2)
    {
        ServerMetaDataMessage meta = BasisNetworkManagement.ServerMetaDataMessage;
        DefaultInterval = meta.SyncInterval / 1000f;

        float calculatedIntervalBase = meta.BaseMultiplier + (smallestD2 * meta.IncreaseRate);
        UnClampedInterval = DefaultInterval * calculatedIntervalBase;

        intervalSeconds = Mathf.Clamp(UnClampedInterval, DefaultInterval, meta.SlowestSendRate);
    }

    /// <summary>
    /// Capacity growth allocator; avoids dispose/realloc churn on player join/leave.
    /// </summary>
    private void EnsureCapacity(int receiverCount)
    {
        if (receiverCount <= capacity && distanceSq.IsCreated)
            return;

        int newCap = math.max(16, math.ceilpow2(receiverCount));
        Realloc(newCap);
        capacity = newCap;
    }

    private void Realloc(int newCap)
    {
        ReleaseResults();

        distanceSq = new NativeArray<float>(newCap, Allocator.Persistent);
        targetPositions = new NativeArray<float3>(newCap, Allocator.Persistent);

        MicrophoneRange = new NativeArray<bool>(newCap, Allocator.Persistent);
        hearingRange = new NativeArray<bool>(newCap, Allocator.Persistent);
        AvatarRange = new NativeArray<bool>(newCap, Allocator.Persistent);

        PrevInMicrophoneRange = new NativeArray<bool>(newCap, Allocator.Persistent);
        PrevInHearingRange = new NativeArray<bool>(newCap, Allocator.Persistent);
        PrevInAvatarRange = new NativeArray<bool>(newCap, Allocator.Persistent);

        MeshLodLevel = new NativeArray<short>(newCap, Allocator.Persistent);
        prevMeshLodLevel = new NativeArray<short>(newCap, Allocator.Persistent);
        MeshLodRange = new NativeArray<bool>(newCap, Allocator.Persistent);

        perIndexMinD2 = new NativeArray<float>(newCap, Allocator.Persistent);
        perIndexMask = new NativeArray<int>(newCap, Allocator.Persistent);

        hasRealAvatarLoaded = new NativeArray<bool>(newCap, Allocator.Persistent);
        avatarCapEntries = new NativeArray<AvatarCapEntry>(newCap, Allocator.Persistent);
        directionalDampening = new NativeArray<float>(newCap, Allocator.Persistent);
        hasActiveAudioSource = new NativeArray<bool>(newCap, Allocator.Persistent);
        audioCapEntries = new NativeArray<AudioCapEntry>(newCap, Allocator.Persistent);

        if (!smallestD2.IsCreated) smallestD2 = new NativeArray<float>(1, Allocator.Persistent);
        if (!changeMask.IsCreated) changeMask = new NativeArray<int>(1, Allocator.Persistent);

        // Bind constant array references to jobs (these remain valid until next Realloc)
        distanceJob.distanceSq = distanceSq;
        distanceJob.targetPositions = targetPositions;

        distanceJob.MicrophoneRange = MicrophoneRange;
        distanceJob.hearingRange = hearingRange;
        distanceJob.AvatarRange = AvatarRange;

        distanceJob.PrevInMicrophoneRange = PrevInMicrophoneRange;
        distanceJob.PrevInHearingRange = PrevInHearingRange;
        distanceJob.PrevInAvatarRange = PrevInAvatarRange;

        distanceJob.MeshLodLevel = MeshLodLevel;
        distanceJob.PrevMeshLodLevel = prevMeshLodLevel;
        distanceJob.MeshLodRange = MeshLodRange;

        distanceJob.PerIndexMinD2 = perIndexMinD2;
        distanceJob.PerIndexMask = perIndexMask;

        reduceJob.PerIndexMinD2 = perIndexMinD2;
        reduceJob.PerIndexMask = perIndexMask;
        reduceJob.SmallestD2 = smallestD2;
        reduceJob.ChangeMask = changeMask;

        avatarCapJob.DistanceSq = distanceSq;
        avatarCapJob.HasRealAvatarLoaded = hasRealAvatarLoaded;
        avatarCapJob.AvatarRange = AvatarRange;
        avatarCapJob.Entries = avatarCapEntries;
        avatarCapJob.StickinessBonus = 0.75f;

        audioCapJob.DistanceSq = distanceSq;
        audioCapJob.HasActiveAudioSource = hasActiveAudioSource;
        audioCapJob.HearingRange = hearingRange;
        audioCapJob.Entries = audioCapEntries;
        audioCapJob.StickinessBonus = 0.75f;

        viewConeJob.TargetPositions = targetPositions;
        viewConeJob.AvatarRange = AvatarRange;
        viewConeJob.PrevInAvatarRange = PrevInAvatarRange;

        dampenJob.TargetPositions = targetPositions;
        dampenJob.Multipliers = directionalDampening;

        LengthOfArrays = -1; // will be set on next Simulate call
    }

    public bool CanDoSimulate(float intervalUsed, out BasisAvatar basisAvatar)
    {
        var player = BasisNetworkTransmitter != null ? BasisNetworkTransmitter.Player : null;
        basisAvatar = player != null ? player.BasisAvatar : null;

        if (basisAvatar == null)
        {
            BasisDebug.LogError("Missing Basis Avatar. Cannot send network update.", BasisDebug.LogTag.System);
            timer = math.max(0f, timer - intervalUsed);
            return false;
        }

        return true;
    }

    public void Initalize()
    {
        // Track join/leave to force resync against index order changes
        BasisNetworkPlayer.OnRemotePlayerJoined += OnPlayerIndexChanged;
        BasisNetworkPlayer.OnRemotePlayerLeft += OnPlayerIndexChanged;
        capacity = 0;
        LengthOfArrays = -1;
    }

    public void DeInitalize()
    {
        BasisNetworkPlayer.OnRemotePlayerJoined -= OnPlayerIndexChanged;
        BasisNetworkPlayer.OnRemotePlayerLeft -= OnPlayerIndexChanged;

        ReleaseResults();

        if (smallestD2.IsCreated) smallestD2.Dispose();
        if (changeMask.IsCreated) changeMask.Dispose();
    }

    public void OnPlayerIndexChanged(BasisNetworkPlayer bnp, BasisRemotePlayer brp)
    {
        IndexChanged = true;
    }
    /// <summary>
    /// Dispose NativeArrays and complete outstanding jobs.
    /// </summary>
    public void ReleaseResults()
    {
        // Wait for in-flight jobs
        if (!distanceJobHandle.IsCompleted) distanceJobHandle.Complete();
        if (!reduceJobHandle.IsCompleted) reduceJobHandle.Complete();
        if (!avatarCapJobHandle.IsCompleted) avatarCapJobHandle.Complete();
        if (!audioCapJobHandle.IsCompleted) audioCapJobHandle.Complete();
        if (!viewConeJobHandle.IsCompleted) viewConeJobHandle.Complete();
        if (!dampenJobHandle.IsCompleted) dampenJobHandle.Complete();

        if (targetPositions.IsCreated) targetPositions.Dispose();
        if (distanceSq.IsCreated) distanceSq.Dispose();

        if (MicrophoneRange.IsCreated) MicrophoneRange.Dispose();
        if (hearingRange.IsCreated) hearingRange.Dispose();
        if (AvatarRange.IsCreated) AvatarRange.Dispose();

        if (PrevInMicrophoneRange.IsCreated) PrevInMicrophoneRange.Dispose();
        if (PrevInHearingRange.IsCreated) PrevInHearingRange.Dispose();
        if (PrevInAvatarRange.IsCreated) PrevInAvatarRange.Dispose();

        if (MeshLodLevel.IsCreated) MeshLodLevel.Dispose();
        if (prevMeshLodLevel.IsCreated) prevMeshLodLevel.Dispose();
        if (MeshLodRange.IsCreated) MeshLodRange.Dispose();

        if (perIndexMinD2.IsCreated) perIndexMinD2.Dispose();
        if (perIndexMask.IsCreated) perIndexMask.Dispose();

        if (hasRealAvatarLoaded.IsCreated) hasRealAvatarLoaded.Dispose();
        if (avatarCapEntries.IsCreated) avatarCapEntries.Dispose();
        if (directionalDampening.IsCreated) directionalDampening.Dispose();
        if (hasActiveAudioSource.IsCreated) hasActiveAudioSource.Dispose();
        if (audioCapEntries.IsCreated) audioCapEntries.Dispose();

        // Note: smallestD2/changeMask are 1-length arrays kept across reallocs; disposed in DeInitalize.
        capacity = 0;
        LengthOfArrays = -1;
    }

    private static void Swap<T>(ref NativeArray<T> a, ref NativeArray<T> b) where T : struct
    {
        NativeArray<T> tmp = a;
        a = b;
        b = tmp;
    }
}
