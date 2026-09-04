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
using Unity.Profiling;
using UnityEngine;
using static SerializableBasis;
[System.Serializable]
public partial class BasisTransmissionResults
{
    // Phase markers for the transmit tick. AfterAvatarChanges shows one number for the whole
    // tick; these split it so a spike attributes to the stage that owns it. The per-player
    // branches (audio start/stop, avatar reload, LOD swap) are marked individually because
    // they are the only work in the loop that can cost milliseconds on a single player —
    // everything else in the loop is flag arithmetic and stays under the loop marker.

    // Jobs
    [System.NonSerialized] public BasisDistanceJobParallel distanceJob;
    [System.NonSerialized] public BasisDistanceReduceJob reduceJob;
    [System.NonSerialized] public BasisAvatarCapJob avatarCapJob;
    [System.NonSerialized] public BasisAudioCapJob audioCapJob;
    [System.NonSerialized] public BasisDirectionalDampenJob dampenJob;
    [System.NonSerialized] public BasisJiggleLodJob jiggleLodJob;

    [System.NonSerialized] public JobHandle distanceJobHandle;
    [System.NonSerialized] public JobHandle reduceJobHandle;
    [System.NonSerialized] public JobHandle avatarCapJobHandle;
    [System.NonSerialized] public JobHandle audioCapJobHandle;
    [System.NonSerialized] public JobHandle dampenJobHandle;
    [System.NonSerialized] public JobHandle jiggleLodJobHandle;

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
    [System.NonSerialized] public NetDataWriter VRMWriter = new NetDataWriter(true, 0);

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
    /// Per-index mouth facing direction, the companion to <see cref="targetPositions"/>.
    /// Voice directivity needs the axis the talker's mouth radiates along.
    /// </summary>
    private NativeArray<float3> targetForwards;

    /// <summary>
    /// Per-index high-shelf depths, in dB, produced alongside the dampening
    /// multiplier: the listener's head shadow and the talker's mouth directivity.
    /// Applied on the audio thread by <see cref="BasisVoiceToneShaper"/>.
    /// </summary>
    private NativeArray<float> coneShelfDb;
    private NativeArray<float> directivityShelfDb;

    /// <summary>
    /// Pre-computed per-index flag: true when the remote player currently has an
    /// active audio source. Filled in the positions loop so managed objects are
    /// never touched during the audio cap sort.
    /// </summary>
    private NativeArray<bool> hasActiveAudioSource;

    /// <summary>
    /// Pre-computed per-index flag: true when the remote player is in
    /// <see cref="BasisTalkMode.Shout"/>. Their voice reaches
    /// <see cref="BasisShout.RangeMultiplier"/> times as far, so the hearing test in
    /// <see cref="BasisDistanceJobParallel"/> has to widen for them alone — a global
    /// range would widen it for everyone. Filled in the positions loop with the rest
    /// of the managed mirrors.
    /// </summary>
    private NativeArray<bool> remoteIsShouting;

    /// <summary>
    /// Scratch buffer for audio-cap sorting. Sized to capacity, reused each tick.
    /// </summary>
    private NativeArray<AudioCapEntry> audioCapEntries;

    /// <summary>
    /// Per-index mirror of RemoteAvatarDriver jiggle state, filled in the positions loop so
    /// <see cref="BasisJiggleLodJob"/> (Burst) never touches managed objects. The job reads these
    /// as "current" and writes <see cref="targetColliderTier"/>/<see cref="targetShouldSimulate"/>;
    /// PostProcess applies them through the same live driver reference instead of calling
    /// BasisJiggleColliderLOD.ComputeTier / BasisJiggleSimulationLOD.ShouldSimulate inline.
    /// </summary>
    private NativeArray<bool> hasJiggleCollidersState;
    private NativeArray<BasisJiggleColliderTier> currentColliderTier;
    private NativeArray<bool> hasJiggleRigsState;
    private NativeArray<bool> currentlySimulatingState;
    private NativeArray<BasisJiggleColliderTier> targetColliderTier;
    private NativeArray<bool> targetShouldSimulate;

    // State
    public bool IndexChanged;

    // Arrays
    private NativeArray<float> distanceSq;
    private NativeArray<float3> targetPositions;

    [System.NonSerialized] public NativeArray<bool> MicrophoneRange;
    private NativeArray<bool> hearingRange;
    [System.NonSerialized] public NativeArray<bool> AvatarRange;

    [System.NonSerialized] public NativeArray<bool> PrevInMicrophoneRange;
    [System.NonSerialized] public NativeArray<bool> PrevInHearingRange;
    [System.NonSerialized] public NativeArray<bool> PrevInAvatarRange;

    [System.NonSerialized] public NativeArray<short> MeshLodLevel;
    [System.NonSerialized] public NativeArray<short> prevMeshLodLevel;
    [System.NonSerialized] public NativeArray<bool> MeshLodRange;
    [System.NonSerialized] public NativeArray<short> PoseLodLevel;

    // Scratch + reduced outputs
    private NativeArray<float> perIndexMinD2;
    private NativeArray<int> perIndexMask;

    private NativeArray<float> smallestD2; // length 1
    private NativeArray<int> changeMask;   // length 1

    public static float HysteresisPercent = 1.10f * 1.10f; // 10% hysteresis

    /// <summary>
    /// Max avatar (re)loads admitted per transmit tick when players cross the avatar-range boundary.
    /// A bulk range change (e.g. the View Range slider 0->100 at 1k players) expires every player's
    /// debounce on the same tick; without this cap they all call ReloadAvatar in one frame, dumping
    /// ~1000 bundle loads + main-thread calibrations at once. Over-budget transitions stay pending and
    /// commit on later ticks, ramping the crowd in over a few seconds instead of freezing.
    /// </summary>
    public static int MaxAvatarReloadsPerTick = 8;

    /// <summary>
    /// Wall-clock ceiling on what this tick's range-commit reloads are allowed to cost, mirroring
    /// <c>BasisAvatarFarLOD.MaxTransitionMillisecondsPerTick</c>.
    /// </summary>
    /// <remarks>
    /// A count alone could not bound the spike: a reload that goes OUT of range installs the
    /// loading avatar inline — an Instantiate plus a full remote calibration — and unlike the
    /// in-range direction it never reaches <see cref="Basis.Scripts.Avatar.BasisAvatarSetupBudget"/>,
    /// which only gates the tail of <c>LoadAvatarRemote</c>. Measured in the field at ~0.4-0.6 ms
    /// each, so the count of 8 was worth up to ~5 ms of transmit tick on its own. Over-budget
    /// transitions stay pending (their commit time has already passed) and retry next tick, which
    /// is the behaviour the count already had.
    /// </remarks>
    public static float MaxAvatarReloadMillisecondsPerTick = 2f;

    private static readonly System.Diagnostics.Stopwatch sAvatarReloadClock = new System.Diagnostics.Stopwatch();

    /// <summary>
    /// Max remote voice sources admitted to <c>StartAudio</c> per transmit tick, for the same
    /// reason <see cref="MaxAvatarReloadsPerTick"/> exists one branch below.
    /// </summary>
    /// <remarks>
    /// A start is not a flag write: it pulls a voice object from the pool (or Instantiates the
    /// prefab once the pool is dry), adds an AudioSource + spatializer, allocates a clip, and
    /// pushes the whole Steam Audio parameter set — every one of which mutates the FMOD DSP
    /// graph. Measured at ~0.15 ms each, with the graph flushes dominating. A join burst or a
    /// hearing-range slider move crosses dozens of players at once: 22 starts on one tick was
    /// 3.4 ms of transmit tick, ~2.5 ms of it DSP flushes.
    /// <para>Over-budget starts need no pending state of their own — the
    /// <c>HasAudioSource != canHear</c> mismatch that admitted them is re-evaluated every tick,
    /// so they simply retry on the next one. Stops are deliberately NOT budgeted: they free
    /// resources rather than allocate them, and deferring one leaves an out-of-range player
    /// audible.</para>
    /// </remarks>
    public static int MaxAudioStartsPerTick = 4;

    /// <summary>
    /// Wall-clock ceiling on this tick's voice-source starts, mirroring
    /// <see cref="MaxAvatarReloadMillisecondsPerTick"/>. The count alone cannot bound the spike:
    /// a start that misses the object pool pays an Instantiate plus Steam Audio's native source
    /// creation, which is several times a pool hit.
    /// </summary>
    public static float MaxAudioStartMillisecondsPerTick = 1f;

    private static readonly System.Diagnostics.Stopwatch sAudioStartClock = new System.Diagnostics.Stopwatch();

    /// <summary>Half-angle (degrees) of the eye-gaze cone used to boost MeshLod detail for players the user is looking at.</summary>
    public static float GazeFoveationConeDegrees = 20f;

    /// <summary>Squared-distance multiplier applied at the gaze cone center; 0.25 ≈ "treat at half the actual distance".</summary>
    public static float GazeFoveationBoost = 0.25f;

    public static float LastHearingRange = -1;
    public static bool RevaluteAudioRanges = false;
    public static float ConvertedVoiceDistance;

    /// <summary>
    /// The squared microphone range actually fed to the distance job this tick — the setting,
    /// times <see cref="BasisShout.RangeMultiplierSquared"/> while the local player is shouting.
    /// Tracked separately from the setting so entering or leaving shout counts as a range change
    /// and re-evaluates the recipient list, exactly as moving the slider does.
    /// </summary>
    public static float EffectiveMicrophoneRange;

    /// <summary>Set by BasisTalkModeManager to force a recipient-list resend on the next tick after a talk-mode change.</summary>
    public static bool ForceVoiceRecipientResend;

    // Tick state carried from ScheduleTick to CompleteTick. The snapshot reference is pinned for
    // the window so a rebuild between the two halves can't re-index the arrays the jobs are
    // writing; joins/leaves only reach the driver from the Update-phase dispatch.
    private bool _tickScheduled;
    private BasisNetworkReceiver[] _tickSnapshot;
    private int _tickReceiverCount;
    private BasisAvatar _tickAvatar;
    private float _tickIntervalUsed;
    private bool _tickDampenEnabled;
#if UNITY_EDITOR
    private bool _tickProf;
    private System.Diagnostics.Stopwatch _tickStopwatch;
#endif

    /// <summary>
    /// Called each frame; drives scheduling of distance job and network sync.
    /// </summary>
    public void Simulate()
    {
        ScheduleTick();
        CompleteTick();
    }

    /// <summary>
    /// First half of the transmit tick: fills the distance job inputs off this frame's remote
    /// mouth positions and camera pose, then schedules and kicks the distance/reduce/cap/dampen
    /// chain. Runs early in LateUpdate so the chain overlaps the rest of the frame's main-thread
    /// work instead of being fenced a few microseconds after it is scheduled.
    /// </summary>
    public void ScheduleTick()
    {
        // A tick that was scheduled but never completed (the transmitter unsubscribed between the
        // halves) would leave this frame's schedule racing last frame's jobs on the same arrays.
        if (_tickScheduled)
        {
            CompleteScheduledJobs(_tickDampenEnabled);
            _tickSnapshot = null;
            _tickAvatar = null;
        }
        _tickScheduled = false;

        float dt = Time.deltaTime;
        timer += dt;
        timer = math.min(timer, intervalSeconds * 2f);

        if (timer < intervalSeconds)
        {
#if UNITY_EDITOR
            if (BasisEventDriverProfilerData.Enabled)
            {
                BasisEventDriverProfilerData.Net_TransmitSimRanThisTick = false;
            }
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
        _tickProf = _prof;
        System.Diagnostics.Stopwatch _psw = null;
        if (_prof)
        {
            BasisEventDriverProfilerData.Net_TransmitSimRanThisTick = true;
            _tickStopwatch ??= new System.Diagnostics.Stopwatch();
            _psw = _tickStopwatch;
            _psw.Restart();
        }
#endif
        EnsureCapacity(receiverCount);
        LengthOfArrays = receiverCount;

        // Fill target positions aligned to snapshot order.
        // Also pre-compute stickiness flags for the avatar cap so the
        // NativeArray sort never needs to touch managed objects.
        // Uses unsafe pointers to bypass NativeArray safety checks (~3ms savings at 1k players).
        using (BasisNetworkMarkers.TransmitFillPositions.Auto())
        unsafe
        {
            float3* pTargetPositions = (float3*)targetPositions.GetUnsafePtr();
            float3* pTargetForwards = (float3*)targetForwards.GetUnsafePtr();
            bool* pHasRealAvatar = (bool*)hasRealAvatarLoaded.GetUnsafePtr();
            bool* pHasActiveAudio = (bool*)hasActiveAudioSource.GetUnsafePtr();
            bool* pRemoteIsShouting = (bool*)remoteIsShouting.GetUnsafePtr();
            bool* pHasJiggleColliders = (bool*)hasJiggleCollidersState.GetUnsafePtr();
            BasisJiggleColliderTier* pCurrentColliderTier = (BasisJiggleColliderTier*)currentColliderTier.GetUnsafePtr();
            bool* pHasJiggleRigs = (bool*)hasJiggleRigsState.GetUnsafePtr();
            bool* pCurrentlySimulating = (bool*)currentlySimulatingState.GetUnsafePtr();

            float3 farAway = BasisLocalCameraDriver.Position + new Vector3(900, 900, 900);

            for (int Index = 0; Index < receiverCount; Index++)
            {
                BasisNetworkReceiver remote = snapshot[Index];
                ushort id = remote.playerId;
                var remotePlayer = remote.RemotePlayer;

                if (RemoteBoneJobSystem.GetOutGoingMouth(id, out float3 outgoing))
                {
                    pTargetPositions[Index] = outgoing;
                }
                else
                {
                    pTargetPositions[Index] = farAway;
                }
                RemoteBoneJobSystem.GetOutGoingMouthForward(id, out float3 mouthForward);
                pTargetForwards[Index] = mouthForward;
                pHasRealAvatar[Index] = remotePlayer.InAvatarRange && !remotePlayer.IsConsideredFallBackAvatar;
                pHasActiveAudio[Index] = remote.AudioReceiverModule.HasAudioSource;
                pRemoteIsShouting[Index] = remotePlayer.TalkMode == BasisTalkMode.Shout;

                // Mirror for BasisJiggleLodJob: same driver instance PostProcess later applies
                // any resulting tier/simulate change through, read once here (not twice, once per
                // LOD feature) same as everything else this loop already mirrors.
                var jiggleDriver = remotePlayer.RemoteAvatarDriver;
                bool hasJiggleDriver = jiggleDriver != null;
                pHasJiggleColliders[Index] = hasJiggleDriver && jiggleDriver.HasJiggleColliders;
                pCurrentColliderTier[Index] = hasJiggleDriver ? jiggleDriver.RegisteredColliderTier : BasisJiggleColliderTier.Full;
                pHasJiggleRigs[Index] = hasJiggleDriver && jiggleDriver.JiggleRigs.Length > 0;
                pCurrentlySimulating[Index] = !hasJiggleDriver || jiggleDriver.JiggleSimulating;
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

        // Shouting widens the recipient list this client sends to the server, so people between
        // one and two microphone ranges away start being relayed our voice. The listener half of
        // the same widening is per-remote (RemoteIsShouting) — the server only relays what the
        // talker asked for, and the listener only builds a source for who they can hear, so both
        // ends have to agree or a shout dies at whichever end stayed narrow.
        EffectiveMicrophoneRange = SMModuleDistanceBasedReductions.MicrophoneRange
            * (BasisTalkModeManager.LocalIsShouting ? BasisShout.RangeMultiplierSquared : 1f);
        distanceJob.SquaredVoiceDistance = EffectiveMicrophoneRange;
        distanceJob.ShoutRangeMultiplierSquared = BasisShout.RangeMultiplierSquared;

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
        reduceJob.ReceiverCount = receiverCount;
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

        // Directional dampening job: only reads targetPositions (shared ReadOnly
        // with distance job) — no dependencies, runs in parallel with everything.
        // Runs whenever EITHER the listener cone or the frequency-dependent tone
        // shaping is on: tone shaping is orientation-driven and is meaningful even
        // with the cone wide open.
        float coneAngle = BasisSettingsDefaults.RAListenerConeAngle.RawValue;
        bool coneEnabled = coneAngle < 360f;
        bool toneEnabled = BasisSettingsDefaults.RAVoiceToneShaping.RawValue;
        bool dampenEnabled = coneEnabled || toneEnabled;
        if (dampenEnabled)
        {
            float dampenPercent = Mathf.Clamp(BasisSettingsDefaults.RAListenerDampenAmount.RawValue, 1f, 95f);
            float halfConeRad = coneAngle * 0.5f * Mathf.Deg2Rad;
            float cosHalfCone = Mathf.Cos(halfConeRad);

            dampenJob.ListenerPosition = BasisLocalCameraDriver.Position;
            dampenJob.ListenerForward = BasisLocalCameraDriver.Forward();
            dampenJob.CosHalfCone = cosHalfCone;
            dampenJob.HalfConeRad = halfConeRad;
            dampenJob.MinVolume = 1f - (dampenPercent / 100f);

            dampenJob.ConeEnabled = coneEnabled;
            dampenJob.ToneEnabled = toneEnabled;
            dampenJob.ConeMaxShelfDb = BasisVoiceAcoustics.ConeMaxShelfDb;
            dampenJob.ConeHighFrequencyShare = BasisVoiceAcoustics.ConeHighFrequencyShare;
            dampenJob.ConeShelfBroadbandDb = BasisVoiceAcoustics.ConeShelfBroadbandDb;
            dampenJob.DirectivityShelfMaxDb = BasisVoiceAcoustics.DirectivityShelfMaxDb;
            dampenJob.DirectivityShapePower = BasisVoiceAcoustics.DirectivityShapePower;

            dampenJobHandle = dampenJob.Schedule(receiverCount, 64);
        }
        else
        {
            dampenJobHandle = default;
        }

        // Jiggle collider-tier / should-simulate decision, computed in parallel with
        // reduce/avatarCap/audioCap/dampen instead of inline in CompleteTick's PostProcess loop.
        // Reads distanceSq (a distanceJob output), so it depends on distanceJobHandle like the
        // other three. Always scheduled: BasisJiggleLodJob.Execute is a cheap no-op copy-through
        // for a receiver/feature that's off, so there's no enabled/disabled branch here to keep
        // in sync with CompleteTick's own (separately, freshly read) flag check.
        jiggleLodJob.ColliderLodEnabled = BasisJiggleColliderLOD.Enabled;
        jiggleLodJob.NearSqr = BasisJiggleColliderLOD._nearSqr;
        jiggleLodJob.MidSqr = BasisJiggleColliderLOD._midSqr;
        jiggleLodJob.FarSqr = BasisJiggleColliderLOD._farSqr;
        jiggleLodJob.ColliderHysteresisSqr = BasisJiggleColliderLOD.HysteresisSqr;
        jiggleLodJob.SimulationLodEnabled = BasisJiggleSimulationLOD.Enabled;
        jiggleLodJob.SimCutoffSqr = BasisJiggleSimulationLOD._cutoffSqr;
        jiggleLodJob.SimHysteresisSqr = BasisJiggleSimulationLOD.HysteresisSqr;
        jiggleLodJobHandle = jiggleLodJob.Schedule(receiverCount, 64, distanceJobHandle);

        // Kick the batch. Schedule() only queues into the pending batch — nothing reaches a
        // worker until something flushes it, and without this the first flush is the
        // Complete() in CompleteTick. That made every main-thread stage between the two halves
        // pure serial latency ahead of a job chain that had not started: the main thread paid
        // schedule + full chain instead of overlapping the chain with the rest of LateUpdate.
        // Several dependency stages deep (distance -> reduce/caps) at a full instance, that is
        // the whole tick.
        JobHandle.ScheduleBatchedJobs();

#if UNITY_EDITOR
        if (_prof) { _psw.Stop(); BasisEventDriverProfilerData.Net_TransmitSim_JobScheduleMs = _psw.Elapsed.TotalMilliseconds; }
#endif

        _tickSnapshot = snapshot;
        _tickReceiverCount = receiverCount;
        _tickAvatar = avatar;
        _tickIntervalUsed = intervalUsedThisTick;
        _tickDampenEnabled = dampenEnabled;
        _tickScheduled = true;
    }

    /// <summary>
    /// Second half of the transmit tick: compresses and sends the local avatar, then joins the
    /// chain <see cref="ScheduleTick"/> kicked and applies its results (audio start/stop, avatar
    /// range, mesh LOD, recipient list). Runs in the frame's job-free window — the compress reads
    /// local bone rotations inline on the main thread.
    /// </summary>
    public void CompleteTick()
    {
        if (!_tickScheduled)
        {
            return;
        }
        _tickScheduled = false;

        BasisNetworkReceiver[] snapshot = _tickSnapshot;
        int receiverCount = _tickReceiverCount;
        BasisAvatar avatar = _tickAvatar;
        float intervalUsedThisTick = _tickIntervalUsed;
        bool dampenEnabled = _tickDampenEnabled;
        _tickSnapshot = null;
        _tickAvatar = null;

#if UNITY_EDITOR
        bool _prof = _tickProf;
        System.Diagnostics.Stopwatch _psw = null;
        if (_prof)
        {
            _psw = _tickStopwatch;
            _psw.Restart();
        }
#endif
        // Do work that doesn't depend on distance results
        using (BasisNetworkMarkers.TransmitCompress.Auto())
        {
            BasisNetworkAvatarCompressor.Compress(BasisNetworkTransmitter, avatar.Animator, Time.timeAsDouble);
        }

#if UNITY_EDITOR
        if (_prof)
        {
            _psw.Stop();
            BasisEventDriverProfilerData.Net_TransmitSim_CompressMs = _psw.Elapsed.TotalMilliseconds;
            _psw.Restart();
        }
#endif
        // Finish before consuming results — single sync point via CombineDependencies
        using (BasisNetworkMarkers.TransmitJobComplete.Auto())
        {
            CompleteScheduledJobs(dampenEnabled);
        }

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
        float curMicRange = EffectiveMicrophoneRange;

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

        bool microphoneChange = IndexChanged || AnyMicrophoneRangeChanged || ForceVoiceRecipientResend;
        bool lodChange = IndexChanged || AnyLodRangeChanged;

        // Avatar range is always evaluated per-player in the loop below — the debounce
        // logic needs to run every tick so pending transitions can commit on the
        // tick their timer expires, not only when some other avatar flag also flipped.

        // Single-pass post-processing: hearing, audio range, dampening, avatar, LOD.
        // Merging these loops avoids repeated cache-miss traversals of the same
        // managed snapshot[] objects (up to 6 separate passes before).
        // Uses unsafe pointers to bypass NativeArray safety checks.
        float visemeRangeSq = SMModuleDistanceBasedReductions.HearingRange * 0.25f;
        bool jiggleColliderLodEnabled = BasisJiggleColliderLOD.Enabled;
        bool jiggleSimulationLodEnabled = BasisJiggleSimulationLOD.Enabled;
        // Per-tick budget of avatar (re)loads admitted below; reset each tick. See
        // MaxAvatarReloadsPerTick for the count and MaxAvatarReloadMillisecondsPerTick for the
        // wall clock that actually bounds the spike.
        int avatarReloadsAdmitted = 0;
        sAvatarReloadClock.Reset();
        long avatarReloadBudgetTicks = (long)(MaxAvatarReloadMillisecondsPerTick * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
        // Same shape for voice-source starts; see MaxAudioStartsPerTick for why they need one.
        int audioStartsAdmitted = 0;
        sAudioStartClock.Reset();
        long audioStartBudgetTicks = (long)(MaxAudioStartMillisecondsPerTick * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
        // Per-tick budget of far LOD swaps — each swap forces a bone-job sync. Two ceilings:
        // the count below, and a wall-clock budget opened here that bounds what those swaps are
        // allowed to cost, since an install is a build plus a full remote calibration.
        int farLodTransitionBudget = BasisAvatarFarLOD.MaxTransitionsPerTick;
        BasisAvatarFarLOD.BeginTickBudget();
        using (BasisNetworkMarkers.TransmitPostProcess.Auto())
        unsafe
        {
            bool* pHearingRange = (bool*)hearingRange.GetUnsafeReadOnlyPtr();
            float* pDistanceSq = (float*)distanceSq.GetUnsafeReadOnlyPtr();
            float* pDampening = dampenEnabled ? (float*)directionalDampening.GetUnsafeReadOnlyPtr() : null;
            float* pConeShelf = dampenEnabled ? (float*)coneShelfDb.GetUnsafeReadOnlyPtr() : null;
            float* pDirectivityShelf = dampenEnabled ? (float*)directivityShelfDb.GetUnsafeReadOnlyPtr() : null;
            bool* pAvatarRange = (bool*)AvatarRange.GetUnsafeReadOnlyPtr();
            bool* pMeshLodRange = (bool*)MeshLodRange.GetUnsafeReadOnlyPtr();
            short* pMeshLodLevel = (short*)MeshLodLevel.GetUnsafeReadOnlyPtr();
            short* pPoseLodLevel = (short*)PoseLodLevel.GetUnsafeReadOnlyPtr();
            BasisJiggleColliderTier* pTargetColliderTier = (BasisJiggleColliderTier*)targetColliderTier.GetUnsafeReadOnlyPtr();
            bool* pTargetShouldSimulate = (bool*)targetShouldSimulate.GetUnsafeReadOnlyPtr();
            bool* pRemoteShouting = (bool*)remoteIsShouting.GetUnsafeReadOnlyPtr();
            float shoutVoiceDistance = ConvertedVoiceDistance * BasisShout.RangeMultiplier;
            // Same minDistance the rolloff curve is baked against, because the boost is defined
            // as the fraction of that curve's loss a shout gives back.
            float shoutMinDistance = BasisSettingsDefaults.RAMinDistance.RawValue;

            for (int i = 0; i < receiverCount; i++)
            {
                var receiver = snapshot[i];
                var audio = receiver.AudioReceiverModule;
                var remote = receiver.RemotePlayer;

                bool remoteShouting = pRemoteShouting[i];
                float wantVoiceDistance = remoteShouting ? shoutVoiceDistance : ConvertedVoiceDistance;

                // Always check for HasAudioSource/hearingRange mismatch rather than
                // only on transitions. This ensures StartAudio is retried if a previous
                // attempt failed (e.g. async exception), preventing permanent voice loss.
                bool canHear = pHearingRange[i];
                if (audio.HasAudioSource != canHear)
                {
                    if (canHear)
                    {
                        // Stagger, exactly as the avatar reload below does: a start builds a
                        // spatialized voice source and rewrites the DSP graph, and a join burst
                        // flips dozens on the same tick. Skipped starts carry no pending state —
                        // the mismatch tested above re-admits them next tick.
                        // ElapsedTicks, not Elapsed — this runs once per remote per tick and
                        // TimeSpan construction is not free at crowd scale.
                        bool audioStartBudgetSpent = audioStartsAdmitted >= MaxAudioStartsPerTick
                            || sAudioStartClock.ElapsedTicks >= audioStartBudgetTicks;
                        if (!audioStartBudgetSpent)
                        {
                            audioStartsAdmitted++;
                            sAudioStartClock.Start();
                            using (BasisNetworkMarkers.TransmitAudioStartStop.Auto())
                            {
                                audio.StartAudio(wantVoiceDistance);
                                remote.OutOfRangeFromLocal = false;
                            }
                            sAudioStartClock.Stop();
                        }
                    }
                    else
                    {
                        using (BasisNetworkMarkers.TransmitAudioStartStop.Auto())
                        {
                            audio.StopAudio();
                            remote.OutOfRangeFromLocal = true;
                        }
                    }
                }

                // AppliedRange, not just RevaluteAudioRanges: the global hearing range can sit
                // still while this one player starts or stops shouting, and only their source
                // needs rebuilding against the new distance.
                if (RevaluteAudioRanges || audio.AppliedRange != wantVoiceDistance)
                {
                    audio.ApplyRangeData(wantVoiceDistance);
                }

                // After ApplyRangeData, because the boost is bounded by the rolloff that range
                // just rebuilt. Not a constant: it gives back what distance took, so it is
                // nothing at all point blank and largest out where they were barely audible.
                float wantShoutGain = remoteShouting
                    ? BasisVoiceAcoustics.ShoutBoost(pDistanceSq[i], shoutMinDistance, BasisShout.Gain,
                                                     audio.RolloffAt(Mathf.Sqrt(pDistanceSq[i])))
                    : 1f;
                if (audio.ShoutGain != wantShoutGain)
                {
                    audio.ShoutGain = wantShoutGain;
                }

                // Guarded because the field is volatile: the write is a release store the audio
                // thread orders against, and the value is unchanged for almost every player on
                // almost every tick. Reading first keeps the barrier for the handful that moved.
                float dampening = pDampening != null ? pDampening[i] : 1f;
                if (audio.DirectionalDampeningMultiplier != dampening)
                {
                    audio.DirectionalDampeningMultiplier = dampening;
                }

                // Same read-before-write reasoning as the dampening multiplier
                // above: these are volatile, and for most players on most ticks
                // the value has not moved.
                float coneShelf = pConeShelf != null ? pConeShelf[i] : 0f;
                if (audio.ConeShelfDb != coneShelf)
                {
                    audio.ConeShelfDb = coneShelf;
                }
                float directivityShelf = pDirectivityShelf != null ? pDirectivityShelf[i] : 0f;
                if (audio.DirectivityShelfDb != directivityShelf)
                {
                    audio.DirectivityShelfDb = directivityShelf;
                }

                // Viseme distance cutoff: skip lip-sync for players beyond half
                // the hearing distance — too far to see mouth shapes. Routed
                // through SetVisemeRange so BasisRemoteAudioDriver.ActiveDrivers
                // stays in sync on transitions. A shouter is audible further out,
                // so their cutoff scales with the range their voice actually carries.
                float cutoffSq = remoteShouting ? visemeRangeSq * BasisShout.RangeMultiplierSquared : visemeRangeSq;
                BasisRemoteAudioDriver.SetVisemeRange(audio.visemeDriver, pDistanceSq[i] < cutoffSq);

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
                    if (remote.AvatarAlwaysLoaded)
                    {
                        inRange = true;
                    }
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
                            // Target has remained stable for the debounce window — ready to commit.
                            bool willReload = !remote.IsLoadingAnAvatar && (inRange || !remote.IsConsideredFallBackAvatar);

                            // Stagger: cap the avatar (re)loads started per tick. A bulk range change makes
                            // every player's debounce expire on the same tick; admitting them all at once
                            // fires ~1000 ReloadAvatar calls in one frame. Over-budget transitions stay
                            // pending (their commit time has already passed) and retry next tick. Commits
                            // that don't start a load (e.g. already mid-load) are never gated — they're free.
                            // ElapsedTicks, not Elapsed — this runs once per remote per tick and
                            // TimeSpan construction is not free at crowd scale.
                            bool reloadBudgetSpent = avatarReloadsAdmitted >= MaxAvatarReloadsPerTick
                                || sAvatarReloadClock.ElapsedTicks >= avatarReloadBudgetTicks;
                            if (willReload && reloadBudgetSpent)
                            {
                                // Budget spent this tick — leave pending; revisited next tick.
                            }
                            else
                            {
                                remote.InAvatarRange = inRange;
                                remote.PendingRangeActive = false;

                                if (willReload)
                                {
                                    avatarReloadsAdmitted++;
                                    sAvatarReloadClock.Start();
                                    using (BasisNetworkMarkers.TransmitReloadAvatar.Auto())
                                    {
                                        remote.ReloadAvatar();
                                    }
                                    sAvatarReloadClock.Stop();
                                }
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
                    using (BasisNetworkMarkers.TransmitChangeMeshLOD.Auto())
                    {
                        remote.ChangeMeshLOD(pMeshLodLevel[i]);
                    }
                }

                // Update pose LOD from distance — independent of mesh LOD
                remote.CurrentLodLevel = pPoseLodLevel[i];

                // Far avatar stand-in upkeep (past avatar range, mid-download, platform
                // missing). Edge-triggered; only actual swaps consume budget.
                BasisAvatarFarLOD.Tick(remote, ref farLodTransitionBudget);

                // Nameplate follows avatar range: past it the player is a far avatar (or the
                // dummy) and the plate is too far to read. Inherits the range hysteresis
                // and debounce.
                bool plateVisible = remote.InAvatarRange;
                if (plateVisible != remote.InNamePlateRange)
                {
                    remote.InNamePlateRange = plateVisible;
                    remote.OnNamePlateActiveStateShouldRefresh?.Invoke();
                }

                // Distance-based jiggle collider/simulation reduction. The tier/should-simulate
                // decision itself now runs ahead of this loop in BasisJiggleLodJob (Burst,
                // parallel with distance/reduce/cap/dampen) — this just reads its precomputed
                // result and, if it disagrees with the driver's live current state, applies it.
                // Both gates still read remote.RemoteAvatarDriver, so fetch it once instead of
                // twice when both LODs are enabled (the common case at crowd scale — collider
                // trimming and sim pausing are normally turned on together).
                if (jiggleColliderLodEnabled || jiggleSimulationLodEnabled)
                {
                    var jiggleDriver = remote.RemoteAvatarDriver;
                    if (jiggleDriver != null)
                    {
                        // Trim a remote's arm/finger/foot colliders as it gets farther so distant
                        // crowds stop dominating the jiggle sim.
                        if (jiggleColliderLodEnabled && jiggleDriver.HasJiggleColliders)
                        {
                            var jiggleTier = pTargetColliderTier[i];
                            if (jiggleTier != jiggleDriver.RegisteredColliderTier)
                            {
                                jiggleDriver.ApplyColliderLOD(jiggleTier);
                            }
                        }

                        // Past the cutoff, unregister the remote's jiggle rigs from the global sim
                        // entirely (Verlet integrate + transform I/O), not just their colliders.
                        // See BasisJiggleSimulationLOD.
                        if (jiggleSimulationLodEnabled && jiggleDriver.JiggleRigs.Length > 0)
                        {
                            bool shouldSimulate = pTargetShouldSimulate[i];
                            if (shouldSimulate != jiggleDriver.JiggleSimulating)
                            {
                                jiggleDriver.SetJiggleSimulating(shouldSimulate);
                            }
                        }
                    }
                }
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
            using (BasisNetworkMarkers.TransmitTalkingPoints.Auto())
            {
                BuildAndSendTalkingPoints(snapshot, receiverCount);
            }
            ForceVoiceRecipientResend = false;
        }
#if UNITY_EDITOR
        if (_prof) { _psw.Stop(); BasisEventDriverProfilerData.Net_TransmitSim_TalkingPointsMs = _psw.Elapsed.TotalMilliseconds; }
#endif

        UpdateSendInterval(SquaredSmallestDistance);

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

        distanceJob.MeshLodLevel = MeshLodLevel;
        distanceJob.PrevMeshLodLevel = prevMeshLodLevel;

        IndexChanged = false;

        // Consume one interval worth of accumulated time (robust to overshoot)
        timer = math.max(0f, timer - intervalUsedThisTick);
    }

    private void CompleteScheduledJobs(bool dampenEnabled)
    {
        JobHandle combined = JobHandle.CombineDependencies(reduceJobHandle, avatarCapJobHandle, audioCapJobHandle);
        combined = JobHandle.CombineDependencies(combined, jiggleLodJobHandle);
        if (dampenEnabled)
        {
            combined = JobHandle.CombineDependencies(combined, dampenJobHandle);
        }
        combined.Complete();
    }

    // Takes the snapshot as its concrete array type, not IReadOnlyList: indexing an array
    // through the interface goes out to the covariance stub per element instead of a bounds-
    // checked load, and this walks every receiver in the instance on a recipient change.
    private void BuildAndSendTalkingPoints(BasisNetworkReceiver[] snapshot, int receiverCount)
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

        BasisTalkMode talkMode = BasisTalkModeManager.CurrentMode;
        bool restricted = talkMode == BasisTalkMode.Private || talkMode == BasisTalkMode.ThisPerson;
        bool direct = talkMode == BasisTalkMode.Direct;

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

                bool include;
                if (restricted)
                {
                    include = BasisTalkModeManager.IsRecipient(id);
                }
                else if (direct)
                {
                    include = false;
                }
                else
                {
                    include = pMicRange[i];
                }

                if (include)
                {
                    TalkingPoints.Add(id);
                }
                else
                {
                    ExcludedPoints.Add(id);
                }
            }
        }

        if (restricted)
        {
            // Private / This-person route entirely through the server recipient list; P2P
            // broadcast is suppressed (see BasisAudioTransmission) so non-members can't hear.
            BasisNetworkTransmitter.HasReasonToSendAudio = TalkingPoints.Count != 0;
        }
        else if (direct)
        {
            // Direct mode: nobody via the server, audio reaches P2P-connected peers only.
            BasisNetworkTransmitter.HasReasonToSendAudio = Basis.Scripts.Networking.BasisP2PManager.GetConnectedSessionCount() > 0;
        }
        else
        {
            // micRangeCount captured BEFORE the P2P strip so HasReasonToSendAudio
            // (which gates EncodeAndSend upstream) reflects "anyone in mic range",
            // not "anyone the server still relays to".
            int micRangeCount = TalkingPoints.Count;

            Basis.Scripts.Networking.BasisP2PManager.StripP2PConnectedFromRecipients(TalkingPoints);
            Basis.Scripts.Networking.BasisP2PManager.AddP2PConnectedToExcluded(ExcludedPoints);

            BasisNetworkTransmitter.HasReasonToSendAudio = micRangeCount != 0;
        }

        int recipientCount = TalkingPoints.Count;
        int excludedCount = ExcludedPoints.Count;
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
        else if (!restricted && invertedSize < listSize)
        {
            // Inverted list mode: send excluded IDs (denylist — never used for allowlist modes)
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

        if (Basis.Scripts.Networking.BasisP2PManager.HasAnyConnectedSession())
        {
            // Dropdown is authoritative for P2P. Distance scaling exists to
            // reduce server fan-out cost — irrelevant for a direct UDP link.
            float fast = Basis.Scripts.Networking.BasisP2PManager.FastAvatarIntervalSeconds;
            DefaultInterval = fast;
            UnClampedInterval = fast;
            intervalSeconds = fast;

            // Floor the advertised interval at the real frame interval so we never advertise a
            // faster rate than we can actually send.
            float frameInterval = Time.smoothDeltaTime > 0f ? Time.smoothDeltaTime : fast;
            Basis.Scripts.Networking.BasisAvatarRateRegistry.MaybeAnnounceLocalRate(Mathf.Max(fast, frameInterval));
        }
        else
        {
            DefaultInterval = meta.SyncInterval / 1000f;
            float calculatedIntervalBase = meta.BaseMultiplier + (smallestD2 * meta.IncreaseRate);
            UnClampedInterval = DefaultInterval * calculatedIntervalBase;
            intervalSeconds = Mathf.Clamp(UnClampedInterval, DefaultInterval, meta.SlowestSendRate);
        }
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
        PoseLodLevel = new NativeArray<short>(newCap, Allocator.Persistent);

        perIndexMinD2 = new NativeArray<float>(newCap, Allocator.Persistent);
        perIndexMask = new NativeArray<int>(newCap, Allocator.Persistent);

        hasRealAvatarLoaded = new NativeArray<bool>(newCap, Allocator.Persistent);
        avatarCapEntries = new NativeArray<AvatarCapEntry>(newCap, Allocator.Persistent);
        directionalDampening = new NativeArray<float>(newCap, Allocator.Persistent);
        targetForwards = new NativeArray<float3>(newCap, Allocator.Persistent);
        coneShelfDb = new NativeArray<float>(newCap, Allocator.Persistent);
        directivityShelfDb = new NativeArray<float>(newCap, Allocator.Persistent);
        hasActiveAudioSource = new NativeArray<bool>(newCap, Allocator.Persistent);
        audioCapEntries = new NativeArray<AudioCapEntry>(newCap, Allocator.Persistent);
        remoteIsShouting = new NativeArray<bool>(newCap, Allocator.Persistent);

        hasJiggleCollidersState = new NativeArray<bool>(newCap, Allocator.Persistent);
        currentColliderTier = new NativeArray<BasisJiggleColliderTier>(newCap, Allocator.Persistent);
        hasJiggleRigsState = new NativeArray<bool>(newCap, Allocator.Persistent);
        currentlySimulatingState = new NativeArray<bool>(newCap, Allocator.Persistent);
        targetColliderTier = new NativeArray<BasisJiggleColliderTier>(newCap, Allocator.Persistent);
        targetShouldSimulate = new NativeArray<bool>(newCap, Allocator.Persistent);

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
        distanceJob.PoseLodLevel = PoseLodLevel;

        distanceJob.PerIndexMinD2 = perIndexMinD2;
        distanceJob.PerIndexMask = perIndexMask;
        distanceJob.RemoteIsShouting = remoteIsShouting;

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

        dampenJob.TargetPositions = targetPositions;
        dampenJob.TargetForwards = targetForwards;
        dampenJob.Multipliers = directionalDampening;
        dampenJob.ConeShelfDb = coneShelfDb;
        dampenJob.DirectivityShelfDb = directivityShelfDb;

        jiggleLodJob.distanceSq = distanceSq;
        jiggleLodJob.HasJiggleColliders = hasJiggleCollidersState;
        jiggleLodJob.CurrentColliderTier = currentColliderTier;
        jiggleLodJob.HasJiggleRigs = hasJiggleRigsState;
        jiggleLodJob.CurrentlySimulating = currentlySimulatingState;
        jiggleLodJob.TargetColliderTier = targetColliderTier;
        jiggleLodJob.TargetShouldSimulate = targetShouldSimulate;

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

    public void Initialize()
    {
        // Track join/leave to force resync against index order changes
        BasisNetworkPlayer.OnRemotePlayerJoined += OnPlayerIndexChanged;
        BasisNetworkPlayer.OnRemotePlayerLeft += OnPlayerIndexChanged;
        capacity = 0;
        LengthOfArrays = -1;
    }

    public void DeInitialize()
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
        if (!dampenJobHandle.IsCompleted) dampenJobHandle.Complete();
        if (!jiggleLodJobHandle.IsCompleted) jiggleLodJobHandle.Complete();

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
        if (PoseLodLevel.IsCreated) PoseLodLevel.Dispose();

        if (perIndexMinD2.IsCreated) perIndexMinD2.Dispose();
        if (perIndexMask.IsCreated) perIndexMask.Dispose();

        if (hasRealAvatarLoaded.IsCreated) hasRealAvatarLoaded.Dispose();
        if (avatarCapEntries.IsCreated) avatarCapEntries.Dispose();
        if (directionalDampening.IsCreated) directionalDampening.Dispose();
        if (targetForwards.IsCreated) targetForwards.Dispose();
        if (coneShelfDb.IsCreated) coneShelfDb.Dispose();
        if (directivityShelfDb.IsCreated) directivityShelfDb.Dispose();
        if (hasActiveAudioSource.IsCreated) hasActiveAudioSource.Dispose();
        if (audioCapEntries.IsCreated) audioCapEntries.Dispose();
        if (remoteIsShouting.IsCreated) remoteIsShouting.Dispose();

        if (hasJiggleCollidersState.IsCreated) hasJiggleCollidersState.Dispose();
        if (currentColliderTier.IsCreated) currentColliderTier.Dispose();
        if (hasJiggleRigsState.IsCreated) hasJiggleRigsState.Dispose();
        if (currentlySimulatingState.IsCreated) currentlySimulatingState.Dispose();
        if (targetColliderTier.IsCreated) targetColliderTier.Dispose();
        if (targetShouldSimulate.IsCreated) targetShouldSimulate.Dispose();

        // Note: smallestD2/changeMask are 1-length arrays kept across reallocs; disposed in DeInitialize.
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
