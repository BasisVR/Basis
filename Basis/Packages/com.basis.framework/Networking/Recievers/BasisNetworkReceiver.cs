using Basis.Network.Core.Compression;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Receivers
{
    /// <summary>
    /// Receives networked avatar state for a remote player, stages and interpolates frames,
    /// and applies a posed result to the avatar each frame. Also brokers remote audio.
    /// </summary>
    [DefaultExecutionOrder(15001)]
    [Serializable]
    public class BasisNetworkReceiver : BasisNetworkPlayer
    {
        public const int BoneCount = BasisBoneRotationCompression.SyncBoneCount; // 51

        // Cached delegates — created once, avoids per-frame Action/Comparison heap allocations.
        private static readonly Action<BasisAvatarBuffer> s_releaseBuffer = BasisAvatarBufferPool.Release;
        private static readonly Comparison<BasisAvatarBuffer> s_sequenceCompare = static (a, b) => (sbyte)(a.Sequence - b.Sequence);

        private double _serverClockSeconds;
        private bool _serverClockSeeded;
        /// <summary>
        /// If staging backlog exceeds this, older frames are dropped to reduce latency.
        /// </summary>
        public static int BufferCapacityBeforeCleanup = 12;

        [SerializeReference]
        public BasisAudioReceiver AudioReceiverModule = new BasisAudioReceiver();
        [System.NonSerialized]
        public ConcurrentQueue<BasisAvatarBuffer> PayloadQueue = new ConcurrentQueue<BasisAvatarBuffer>();
        // Volatile counter avoids ConcurrentQueue.TryDequeue on empty queues (1k volatile reads vs 1k TryDequeue).
        private volatile int _pendingCount;
        [System.NonSerialized] public BasisRemotePlayer RemotePlayer;

        public bool hasEvents = false;
        /// <summary>
        /// Eye/mouth values consumed by BasisRemoteFaceDriver to drive the eye bones.
        /// Layout: [0]=vL, [1]=hL, [2]=vR, [3]=hR (signed [-1, 1]), [4][5]=mouth.
        /// Eye bones are not part of the bone rotation network stream — these floats
        /// are populated either by BasisRemoteFaceManagement (idle look-around) or by
        /// EyeTrackingBoneActuation (when face tracking is active on the remote).
        /// </summary>
        public float[] EyesAndMouth = new float[] { 0, 0, 0, 0, 1, 0 };
        public float3 ApplyingScale;

        /// <summary>
        /// Latest network hips position/rotation/scale, updated every time a buffer
        /// is enqueued. Available before Compute() processes the queue, so
        /// calibration can immediately pose the freshly spawned avatar instead of
        /// leaving it at its prefab transform (which caused remote avatars to
        /// render at scale (1,1,1) until the interp window seeded — visible as
        /// "scale is wrong when a new person joins").
        /// Thread-safe via seqlock: writer increments version before/after writes,
        /// reader retries if version changed or is odd (write in progress).
        /// </summary>
        private int _poseVersion;
        private float3 _latestNetworkPosition;
        private quaternion _latestNetworkRotation = quaternion.identity;
        private float3 _latestNetworkScale = new float3(1f, 1f, 1f);

        public void GetLatestNetworkPose(out float3 position, out quaternion rotation, out float3 scale)
        {
            int v1, v2;
            do
            {
                v1 = Volatile.Read(ref _poseVersion);
                position = _latestNetworkPosition;
                rotation = _latestNetworkRotation;
                scale = _latestNetworkScale;
                Thread.MemoryBarrier();
                v2 = Volatile.Read(ref _poseVersion);
            } while (v1 != v2 || (v1 & 1) != 0);
        }

        /// <summary>
        /// T-pose local rotations for this receiver's avatar bones.
        /// Set during calibration and passed to RemoteBoneJobSystem for the skeleton apply job.
        /// </summary>
        [System.NonSerialized] public NativeArray<quaternion> TposeLocalRotations;

        /// <summary>
        /// Bone transforms for this receiver's avatar.
        /// Set during calibration and passed to RemoteBoneJobSystem for the skeleton apply job.
        /// </summary>
        public Transform[] BoneTransforms;

        // When true, forces re-validation of avatar/animator/transform references.
        // Set on avatar change (CalibrationComplete), init, and deinit.
        // Avoids 3000+ Unity null checks per frame with 1k receivers.
        private bool _avatarDirty = true;

        private double interpolationTime = 0f; // 0..1 over current->next window
        // Cached on main thread during PreCompute so ComputeData can read it off-thread.
        internal float CachedHumanScale = 1f;

        public bool HasBufferHolds;

        // ---------------- sequence tracking for unreliable delivery ----------------
        private byte _highestSequence;
        /// <summary>
        /// 0 = no packets seen, 1 = initial data only (seq unset), 2+ = stale-check active.
        /// The first packet (initial join data, seq=0) doesn't seed the tracker;
        /// the second packet (first streaming update with real sequence) does.
        /// </summary>
        private int _seenPackets;
        private readonly List<BasisAvatarBuffer> _pendingSort = new List<BasisAvatarBuffer>(16);

        // ---------------- staging (ring buffer) ----------------
        private const int MaxStage = 64;
        public int StagedCount;

        // Main-thread-only jitter buffer. Bounded. Overwrites oldest when full.
        private BasisRingBuffer<BasisAvatarBuffer> _stagedRing;

        public Transform LastAvatarsTransform;
        public bool DidLastAvatarTransformChanged;

        // Playback rate control: catches up smoothly when backlog grows.
        private const float CatchupGain = 0.12f;          // 0.05..0.25 tune
        private const float MinPlaybackRate = 0.85f;
        private const float MaxPlaybackRate = 1.35f;
        // EMA time constant (s) for the applied playback rate, smooths rate steps.
        private const float RateSmoothingTau = 0.20f;

        // Adaptive jitter buffer depth. Floors at MinJitterDepth = 1 (one packet of
        // baseline cushion so the slowdown branch only fires on actual starvation, not
        // routine jitter). Grows toward MaxJitterDepth on underruns, decays back when
        // stable. Cold start begins at MinJitterDepth so a fresh remote join lerps at
        // rate 1.0 immediately — the adaptive logic only adds headroom after observing
        // real underruns. Driven by BasisSettingsDefaults.NetworkJitterBufferDepth via
        // SettingsProviderNetworkTab — set ApplyTargetJitterDepth to retune all three.
        public static int MinJitterDepth = 1;
        public static int MaxJitterDepth = 4;
        public static float InitialJitterDepth = 1f;
        private const float DepthBumpOnUnderrun = 0.5f;
        private const float DepthDecayPerSecond = 0.5f;
        // Hysteretic catch-up band: stay at rate 1.0 until backlog exceeds Enter, then
        // drain down to Exit before disengaging. The gap stops the boundary chattering
        // green<->amber on routine +/-1-packet jitter.
        private const float CatchupEnterDeadband = 2.0f;
        private const float CatchupExitDeadband = 1.0f;
        // When true, every receiver's _dynamicJitterDepth is hard-pinned to MinJitterDepth
        // each frame: decay-toward-floor and bump-on-underrun are both skipped, so the
        // depth never drifts. Lets the user pick a fixed cushion via the override setting
        // and have it actually stay there.
        public static bool JitterDepthLocked = false;
        private float _dynamicJitterDepth = InitialJitterDepth;
        private float _lastPlaybackRate = 1f;
        private bool _catchingUp;

        // Received bytes-on-wire metering for the per-player network gizmos. Accumulated off the
        // main thread in AccountReceivedBytes (Interlocked) and windowed into a rate in ComputeData.
        // Voice runs as its own pair so the gizmos can break the channels apart.
        private const double BandwidthWindow = 0.5;
        private long _bwBytes;
        private long _bwPackets;
        private long _voiceBwBytes;
        private long _voiceBwPackets;
        private double _bwTime;
        private float _bytesPerSecond;
        private float _packetsPerSecond;
        private float _voiceBytesPerSecond;
        private float _voicePacketsPerSecond;

        /// <summary>
        /// Sets the adaptive jitter depth parameters from a single user-facing "target depth"
        /// value. Initial matches target so cold starts don't overshoot into the slowdown
        /// branch; Max caps the underrun growth at target+2 but never below 4 so we still
        /// have room to react to unstable networks. Leaves the lock off, so depth still adapts.
        /// </summary>
        public static void ApplyTargetJitterDepth(int target)
        {
            target = math.clamp(target, 0, 6);
            MinJitterDepth = target;
            InitialJitterDepth = target;
            MaxJitterDepth = math.max(target + 2, 4);
            JitterDepthLocked = false;
        }

        /// <summary>
        /// Pins the jitter depth at a fixed target — no decay, no underrun growth. All three
        /// fields collapse to <paramref name="target"/>, and the per-frame hot path holds
        /// _dynamicJitterDepth at MinJitterDepth so the rate formula's equilibrium stays
        /// exactly where the user asked.
        /// </summary>
        public static void ApplyLockedJitterDepth(int target)
        {
            target = math.clamp(target, 0, 6);
            MinJitterDepth = target;
            InitialJitterDepth = target;
            MaxJitterDepth = target;
            JitterDepthLocked = true;
        }

        public bool HasCurrentBuffer = false;
        public bool HasNextBuffer = false;
        public bool HasPreviousBuffer = false;
        public bool SentLatest = false;
        // Catmull-Rom control points: Previous(p0) -> Current(p1) -> Next(p2) -> peek staged(p3).
        // Previous is the retained outgoing Current; it supplies the p0 tangent for the spline.
        public BasisAvatarBuffer Previous { get; private set; }
        public BasisAvatarBuffer Current { get; private set; }
        public BasisAvatarBuffer Next { get; private set; }

        public double InterpolationTimeDebug => interpolationTime;
        public float LastPlaybackRate => _lastPlaybackRate;
        public float DynamicJitterDepth => _dynamicJitterDepth;
        public byte HighestSequence => _highestSequence;
        public int SeenPackets => _seenPackets;
        public float CachedHumanScaleDebug => CachedHumanScale;
        public float BytesPerSecond => _bytesPerSecond;
        public float PacketsPerSecond => _packetsPerSecond;
        public float VoiceBytesPerSecond => _voiceBytesPerSecond;
        public float VoicePacketsPerSecond => _voicePacketsPerSecond;

        /// <summary>When true, effectors the sender marked anchored (mask on the wire) are two-bone-IK'd
        /// to their sent world targets after skeleton FK. On by default; a server admin can disable it
        /// server-wide (BasisNetworkModeration.GlobalEndEffectorIKDisabled → BroadcastLockState). Only
        /// world-stable effectors (tracked hands/feet) are ever anchored, so emotes and posed limbs are
        /// untouched.</summary>
        public static bool EndEffectorIKEnabled = true;

        /// <summary>
        /// Interpolates this player's anchored end-effector targets (hips-local offset + tip rotation)
        /// and writes them to the remote bone job system's playerId-keyed inputs. Runs on the
        /// pre-schedule receiver pass — no transform access; the Burst read/compute/write jobs do the
        /// actual anchoring. Only limbs anchored in BOTH bracketing frames stay masked; the rest FK.
        /// </summary>
        public unsafe void WriteEffectorJobInputs()
        {
            BasisAvatarBuffer cur = Current, nxt = Next;
            int mask = (cur != null && nxt != null) ? (cur.EffectorMask & nxt.EffectorMask) : 0;
            if (mask == 0)
            {
                BasisRemoteNetworkDriver.ClearEffectorMask(playerId);
                return;
            }

            float t = math.saturate((float)interpolationTime);
            int n = BasisAvatarEndEffectors.EffectorCount;
            float3* offsets = stackalloc float3[n];
            quaternion* tipRots = stackalloc quaternion[n];
            for (int i = 0; i < n; i++)
            {
                offsets[i] = math.lerp(cur.EffectorPos[i], nxt.EffectorPos[i], t);
                tipRots[i] = BasisRemoteInterpolationCore.NlerpShortest(cur.EffectorRot[i], nxt.EffectorRot[i], t);
            }
            BasisRemoteNetworkDriver.WriteEffectorInputs(playerId, (byte)mask, offsets, tipRots);
        }

        /// <summary>Records received bytes-on-wire for this player (call from the packet handler; thread-safe).</summary>
        public void AccountReceivedBytes(int bytes)
        {
            System.Threading.Interlocked.Add(ref _bwBytes, bytes);
            System.Threading.Interlocked.Increment(ref _bwPackets);
        }

        /// <summary>Records received voice bytes-on-wire for this player (call from the voice handler; thread-safe).</summary>
        public void AccountReceivedVoiceBytes(int bytes)
        {
            System.Threading.Interlocked.Add(ref _voiceBwBytes, bytes);
            System.Threading.Interlocked.Increment(ref _voiceBwPackets);
        }

        public bool hasRequiredData = false;
        /// <summary>
        /// Main-thread pre-pass: Unity object validation only (rare dirty path).
        /// Caches all Unity references so the parallel phase never touches Unity APIs.
        /// </summary>
        public void PreCompute()
        {
            // Re-validate avatar references only when dirty (avatar change, init, etc.)
            if (_avatarDirty)
            {
                if (Player.BasisAvatar == null)
                {
                    hasRequiredData = false;
                    return;
                }

                if (Player.BasisAvatar.Animator == null)
                {
                    hasRequiredData = false;
                    BasisDebug.LogError($"Animator for {Player.DisplayName} lost", BasisDebug.LogTag.Remote);
                    return;
                }

                if (Player.AvatarTransform == null)
                {
                    hasRequiredData = false;
                    BasisDebug.LogError($"AvatarTransform for {Player.DisplayName} lost", BasisDebug.LogTag.Remote);
                    return;
                }
                hasRequiredData = true;
                CachedHumanScale = Player.BasisAvatar.HumanScale;
                if (LastAvatarsTransform != Player.AvatarTransform)
                {
                    LastAvatarsTransform = Player.AvatarTransform;
                    DidLastAvatarTransformChanged = true;
                }
                _avatarDirty = false;
            }
        }

        /// <summary>
        /// Main-thread post-pass after parallel ComputeData: applies AudioSource state.
        /// Lightweight — just checks a bool per receiver.
        /// </summary>
        public void PostCompute()
        {
            AudioReceiverModule.ApplyAudioState();
        }

        /// <summary>
        /// Thread-safe: audio decode + packet drain + window management + interpolation + SoA writes.
        /// Each receiver operates on its own state and writes only to its own playerId slot.
        /// Safe to call from worker threads after PreCompute completes on main thread.
        /// </summary>
        public void ComputeData(double unscaledDeltaTime)
        {
            _bwTime += unscaledDeltaTime;
            if (_bwTime >= BandwidthWindow)
            {
                long b = System.Threading.Interlocked.Exchange(ref _bwBytes, 0);
                long p = System.Threading.Interlocked.Exchange(ref _bwPackets, 0);
                long vb = System.Threading.Interlocked.Exchange(ref _voiceBwBytes, 0);
                long vp = System.Threading.Interlocked.Exchange(ref _voiceBwPackets, 0);
                float inv = (float)(1.0 / _bwTime);
                _bytesPerSecond = b * inv;
                _packetsPerSecond = p * inv;
                _voiceBytesPerSecond = vb * inv;
                _voicePacketsPerSecond = vp * inv;
                _bwTime = 0.0;
            }

            // Audio decode is thread-safe (per-receiver decoder/buffers, no Unity API).
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UnityEngine.Profiling.Profiler.BeginSample("ComputeData.AudioDecode");
#endif
            if (!AudioReceiverModule.IsAudioActive || AudioReceiverModule.VoiceBuffer.DecodedFrameCount == 0)
            {
                AudioReceiverModule.DrainAndDecodeThreadSafe();
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UnityEngine.Profiling.Profiler.EndSample();
#endif

            if (!hasRequiredData) return;

            // 1) Pull network packets, drop stale, sort by sequence, then stage
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UnityEngine.Profiling.Profiler.BeginSample("ComputeData.PacketDrain");
#endif
            if (System.Threading.Interlocked.Exchange(ref _pendingCount, 0) > 0)
            {
                _pendingSort.Clear();
                while (PayloadQueue.TryDequeue(out BasisAvatarBuffer buffer))
                {
                    if (_seenPackets >= 2)
                    {
                        byte fwd = unchecked((byte)(buffer.Sequence - _highestSequence));
                        if (fwd >= 128)
                        {
                            BasisAvatarBufferPool.Release(buffer);
                            continue;
                        }
                        if (fwd > 0)
                        {
                            _highestSequence = buffer.Sequence;
                        }
                    }
                    else if (_seenPackets == 1)
                    {
                        // First real streaming packet — seq is the sender's true streaming index.
                        _highestSequence = buffer.Sequence;
                        _seenPackets++;
                    }
                    else
                    {
                        // _seenPackets == 0: initial join data. Sequence is left at the pool's
                        // default 0 (BasisNetworkAvatarDecompressor's LocalAvatarSyncMessage path
                        // never assigns it) and SecondsInterval is a 10 ms placeholder, not a
                        // real streaming cadence. If init lands in the same drain as the first
                        // streaming packets, the signed-byte sort puts init last and the lerp
                        // ends up interpolating BACKWARD to the init pose with a tiny 10 ms
                        // window. Stage it directly at the seeded clock so it becomes Current
                        // and never enters the sort.
                        _seenPackets++;
                        if (!_serverClockSeeded)
                        {
                            _serverClockSeconds = 0.0;
                            _serverClockSeeded = true;
                        }
                        buffer.ServerTimeSeconds = _serverClockSeconds;
                        _stagedRing.EnqueueOverwriteOldest(buffer, onOverwrite: s_releaseBuffer);
                        continue;
                    }

                    _pendingSort.Add(buffer);
                }

                if (_pendingSort.Count > 1)
                {
                    _pendingSort.Sort(s_sequenceCompare);
                }

                for (int i = 0; i < _pendingSort.Count; i++)
                {
                    var buffer = _pendingSort[i];

                    if (!_serverClockSeeded)
                    {
                        _serverClockSeconds = 0.0;
                        _serverClockSeeded = true;
                    }

                    // One interval per enqueued packet. Dropped packets aren't enqueued,
                    // so windowDuration always equals SecondsInterval and a single drop
                    // shows up as the avatar fast-forwarding briefly through one packet
                    // of motion — preferable to the gap-aware variant where any
                    // anomalous seqDelta clamps to 16 and stretches the window 16×.
                    _serverClockSeconds += buffer.SecondsInterval;
                    buffer.ServerTimeSeconds = _serverClockSeconds;

                    _stagedRing.EnqueueOverwriteOldest(buffer, onOverwrite: s_releaseBuffer);
                }
                StagedCount = _stagedRing.Count;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UnityEngine.Profiling.Profiler.EndSample();
#endif

            // 2) Ensure we have a valid interpolation window (Current -> Next)
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UnityEngine.Profiling.Profiler.BeginSample("ComputeData.BufferWindow");
#endif
            if (!HasCurrentBuffer)
            {
                TrySeedFirstFromStaging();
            }

            if (!HasNextBuffer)
            {
                TrySetLastFromStaging();
            }

            HasBufferHolds = HasCurrentBuffer && HasNextBuffer;
            if (!HasBufferHolds)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                UnityEngine.Profiling.Profiler.EndSample();
#endif
                return;
            }

            // 2b) Trim excess staging
            while (_stagedRing.Count > BufferCapacityBeforeCleanup)
            {
                if (_stagedRing.TryDequeueOldest(out var buf))
                {
                    BasisAvatarBufferPool.Release(buf);
                }
                else
                {
                    break;
                }
            }
            StagedCount = _stagedRing.Count;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UnityEngine.Profiling.Profiler.EndSample();
#endif

            // 3) Advance time and slide the interpolation window forward as needed.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UnityEngine.Profiling.Profiler.BeginSample("ComputeData.FrameInputs");
#endif
            if (HasBufferHolds)
            {
                if (JitterDepthLocked)
                {
                    _dynamicJitterDepth = MinJitterDepth;
                }
                else
                {
                    _dynamicJitterDepth = math.max((float)MinJitterDepth,
                        _dynamicJitterDepth - DepthDecayPerSecond * (float)unscaledDeltaTime);
                }

                double windowDuration = Next.ServerTimeSeconds - Current.ServerTimeSeconds;
                if (!(windowDuration > 1e-6 && windowDuration < 1e6))
                {
                    windowDuration = math.max(Next.SecondsInterval, 1e-3);
                }
                float diff = (float)StagedCount - _dynamicJitterDepth;
                float rate;
                if (diff < 0f)
                {
                    rate = 1f + CatchupGain * diff;
                    _catchingUp = false;
                }
                else
                {
                    if (_catchingUp)
                    {
                        if (diff <= CatchupExitDeadband) _catchingUp = false;
                    }
                    else if (diff > CatchupEnterDeadband)
                    {
                        _catchingUp = true;
                    }
                    rate = _catchingUp ? 1f + CatchupGain * (diff - CatchupExitDeadband) : 1f;
                }
                rate = math.clamp(rate, MinPlaybackRate, MaxPlaybackRate);
                float rateSmoothing = 1f - math.exp(-(float)unscaledDeltaTime / RateSmoothingTau);
                _lastPlaybackRate = math.lerp(_lastPlaybackRate, rate, rateSmoothing);

                interpolationTime += (unscaledDeltaTime / windowDuration * (double)_lastPlaybackRate);
                if (!math.isfinite(interpolationTime))
                {
                    interpolationTime = 1;
                }

                while (interpolationTime >= 1.0 && _stagedRing.Count != 0)
                {
                    // Retain the outgoing Current as Previous (p0 tangent) rather than releasing
                    // it; release the stale Previous first so the buffer pool doesn't leak.
                    if (HasPreviousBuffer)
                    {
                        BasisAvatarBufferPool.Release(Previous);
                    }
                    Previous = Current;
                    HasPreviousBuffer = HasCurrentBuffer;

                    Current = Next;
                    HasCurrentBuffer = true;
                    HasNextBuffer = false;
                    Next = null;

                    interpolationTime -= 1.0;

                    TrySetLastFromStaging();

                    HasBufferHolds = HasCurrentBuffer && HasNextBuffer;
                    if (!HasBufferHolds)
                    {
                        // Window starved during advance — grow target so next time has headroom.
                        // Skip the bump when the user has locked the depth; they explicitly
                        // asked for a fixed cushion, so don't grow past it.
                        if (!JitterDepthLocked)
                        {
                            _dynamicJitterDepth = math.min(_dynamicJitterDepth + DepthBumpOnUnderrun, (float)MaxJitterDepth);
                        }
                        break;
                    }

                    windowDuration = Next.ServerTimeSeconds - Current.ServerTimeSeconds;
                    if (!(windowDuration > 1e-6 && windowDuration < 1e6))
                    {
                        windowDuration = math.max(Next.SecondsInterval, 1e-3);
                    }
                }

                if (interpolationTime > 1.0)
                {
                    interpolationTime = 1.0;
                }

                StagedCount = _stagedRing.Count;

                BasisRemoteNetworkDriver.SetFrameTiming(playerId, interpolationTime, unscaledDeltaTime);

                if (SentLatest)
                {
                    var p1 = Current;
                    var p2 = Next;
                    // p0 = retained Previous (duplicate p1 at cold start); p3 = peek the next
                    // staged frame (duplicate p2 on underrun). Duplicated endpoints make the
                    // Catmull-Rom tangents one-sided — the spline stays bounded, no branch needed.
                    var p0 = HasPreviousBuffer ? Previous : p1;
                    var p3 = _stagedRing.TryPeekOldest(out var peek) ? peek : p2;
                    BasisRemoteNetworkDriver.SetFrameInputs(
                        playerId,
                        CachedHumanScale,
                        p0.Position, p1.Position, p2.Position, p3.Position,
                        p1.Scale, p2.Scale,
                        p0.Rotation, p1.Rotation, p2.Rotation, p3.Rotation,
                        p1.HipsLocalDelta, p2.HipsLocalDelta,
                        p1.HipsLocalRotation, p2.HipsLocalRotation,
                        p0.BoneRotations, p1.BoneRotations, p2.BoneRotations, p3.BoneRotations
                    );
                    IsDataReady = true;
                    SentLatest = false;
                }
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UnityEngine.Profiling.Profiler.EndSample();
#endif
        }

        /// <summary>
        /// Legacy single-call path (calls all phases sequentially on the main thread).
        /// </summary>
        public void Compute(double unscaledDeltaTime)
        {
            PreCompute();
            ComputeData(unscaledDeltaTime);
            PostCompute();
        }
        public bool IsDataReady = false;

        // ── Avatar delta baseline (last full keyframe payload received for this player) ──
        // Deltas on DeltaAvatarChannel reconstruct against this. Touched only on the network
        // receive thread (BasisNetworkHandleAvatar / BasisNetworkHandleAvatarDelta), no locking.
        private byte[] _keyframeBaseline;
        private byte _keyframeBaselineQuality;
        private byte _keyframeBaselineSequence;
        private bool _hasKeyframeBaseline;

        /// <summary>Stores the last full keyframe payload as the delta baseline for this player.</summary>
        public void CaptureKeyframeBaseline(byte quality, byte sequence, byte[] payload, int length)
        {
            if (payload == null || length <= 0) return;
            if (_keyframeBaseline == null || _keyframeBaseline.Length < length)
                _keyframeBaseline = new byte[length];
            Buffer.BlockCopy(payload, 0, _keyframeBaseline, 0, length);
            _keyframeBaselineQuality = quality;
            _keyframeBaselineSequence = sequence;
            _hasKeyframeBaseline = true;
        }

        /// <summary>Returns the baseline payload if one is held at the given quality and base sequence.</summary>
        public bool TryGetKeyframeBaseline(byte quality, byte baseSequence, out byte[] baseline)
        {
            if (_hasKeyframeBaseline && _keyframeBaselineQuality == quality && _keyframeBaselineSequence == baseSequence)
            {
                baseline = _keyframeBaseline;
                return true;
            }
            baseline = null;
            return false;
        }

        public void EnQueueAvatarBuffer(BasisAvatarBuffer avatarBuffer)
        {
            // Single choke point for every decoded remote pose (keyframe and delta). One
            // non-finite component would snap this player's skeleton (and everything keyed off
            // it — far avatar root, mouth/eye outputs, nameplate) to NaN on every client that
            // receives it, and the transforms never recover. Drop the pose and keep the last
            // good one instead.
            if (!IsFinite(avatarBuffer.Position) || !IsFinite(avatarBuffer.Rotation) || !IsFinite(avatarBuffer.Scale))
            {
                BasisDebug.LogErrorOnce($"Dropped a non-finite network pose for player {playerId}: pos={avatarBuffer.Position} rot={avatarBuffer.Rotation} scale={avatarBuffer.Scale}", BasisDebug.LogTag.Networking);
                return;
            }
            // Finite is not sufficient. The High-quality tier carries raw float32 positions, so a
            // peer can send values near 3.4e38 that pass the check above and then overflow to
            // +/-Inf inside the per-frame one-euro filter's derivative term. That produces an
            // Inf-Inf NaN which is written back into the filter's own history, so a single packet
            // latches the player's transform permanently. Bounding the magnitude here — once per
            // packet — keeps every downstream frame's arithmetic in range.
            if (!IsWithinWorldBounds(avatarBuffer.Position))
            {
                BasisDebug.LogErrorOnce($"Dropped an out-of-range network pose for player {playerId}: pos={avatarBuffer.Position}", BasisDebug.LogTag.Networking);
                return;
            }
            Interlocked.Increment(ref _poseVersion);
            _latestNetworkPosition = avatarBuffer.Position;
            _latestNetworkRotation = avatarBuffer.Rotation;
            _latestNetworkScale = avatarBuffer.Scale;
            Interlocked.Increment(ref _poseVersion);
            PayloadQueue.Enqueue(avatarBuffer);
            System.Threading.Interlocked.Increment(ref _pendingCount);
        }

        /// <summary>
        /// Half-extent of the coordinate range a remote pose may occupy. Chosen far beyond any
        /// playable world (1000 km) so it can never reject legitimate content, while staying
        /// small enough that squaring or differencing it cannot overflow a float.
        /// </summary>
        const float MaxNetworkPositionMagnitude = 1e6f;

        static bool IsWithinWorldBounds(float3 v) => math.all(math.abs(v) < MaxNetworkPositionMagnitude);

        static bool IsFinite(float3 v) => math.all(math.isfinite(v));

        static bool IsFinite(quaternion q) => math.all(math.isfinite(q.value));

        public override void Initialize()
        {
            _avatarDirty = true;
            _serverClockSeconds = 0.0;
            _serverClockSeeded = false;
            _highestSequence = 0;
            _seenPackets = 0;
            _hasKeyframeBaseline = false;
            RemotePlayer = (BasisRemotePlayer)Player;
            AudioReceiverModule.Initialize(this);

            // Reset staging
            _stagedRing = new BasisRingBuffer<BasisAvatarBuffer>(MaxStage);
            StagedCount = 0;
            ClearAndRelease();
            interpolationTime = 0f;
            _dynamicJitterDepth = InitialJitterDepth;
            _lastPlaybackRate = 1f;
            _catchingUp = false;
            // Clear any packets that arrived before init (rare, but safe)
            while (PayloadQueue.TryDequeue(out var buf))
            {
                Assert.IsNotNull(buf, "PayloadQueue contained null buffer during Initialize flush.");
                BasisAvatarBufferPool.Release(buf);
            }
            _pendingCount = 0;

            // The slot may have been reused from a player who already left; without
            // this the retained last-applied-scale suppresses the first-frame change
            // detection and the freshly spawned avatar is never rescaled.
            BasisRemoteNetworkDriver.ResetScaleTracking(playerId);

            if (!hasEvents)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete += OnCalibration;
                hasEvents = true;
            }
        }

        public void OnCalibration()
        {
            _avatarDirty = true;
            // Scale state is seeded inside RemoteCalibration via SeedScaleState
            // before CalibrationComplete fires, so no reset is needed here.
            AudioReceiverModule.AvatarChanged(this, true);

            int behaviourCount = NetworkBehaviours != null ? NetworkBehaviours.Length : 0;
            List<byte> keysToRemove = new List<byte>();
            foreach (KeyValuePair<byte, ServerAvatarDataMessageQueue> message in NextMessages)
            {
                ServerAvatarDataMessage avatarMessage = message.Value.ServerAvatarDataMessage;

                RemoteAvatarDataMessage Remote = avatarMessage.avatarDataMessage;
                PlayerIdMessage playerIdMessage = avatarMessage.playerIdMessage;

                bool isSameAvatar = Remote.AvatarLinkIndex == LastLinkedAvatarIndex;
                if (isSameAvatar)
                {
                    keysToRemove.Add(message.Key);

                    var behaviour = message.Key < behaviourCount ? NetworkBehaviours[message.Key] : null;
                    if (behaviour == null)
                    {
                        Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverAvatarChannelDropped);
                        continue;
                    }

                    try
                    {
                        if (message.Value.Direct)
                        {
                            behaviour.OnDirectNetworkMessageReceived(
                                playerIdMessage.playerID,
                                Remote.payload,
                                message.Value.Method
                            );
                        }
                        else
                        {
                            behaviour.OnNetworkMessageReceived(
                                playerIdMessage.playerID,
                                Remote.payload,
                                message.Value.Method
                            );
                        }
                        Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverAvatarChannelDispatched);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref BasisAdditionalDataDiagnostics.ReceiverAvatarChannelDropped);
                        BasisDebug.LogError($"Queued avatar message for behaviour {message.Key} threw during calibration replay: {ex}");
                    }
                }
                else
                {
                    bool isPastMessage = IsPastAvatar(Remote.AvatarLinkIndex, LastLinkedAvatarIndex);
                    if (isPastMessage)
                    {
                        BasisDebug.Log($"Discarding stale message with AvatarLinkIndex {Remote.AvatarLinkIndex}");
                        keysToRemove.Add(message.Key);
                    }
                }
            }

            foreach (byte key in keysToRemove)
            {
                NextMessages.Remove(key);
            }
        }

        private bool IsPastAvatar(byte messageIndex, byte currentIndex)
        {
            int diff = (currentIndex - messageIndex + 256) % 256;
            return diff > 0 && diff < 128;
        }

        public override void DeInitialize()
        {
            _avatarDirty = true;
            _serverClockSeconds = 0.0;
            _serverClockSeeded = false;
            _highestSequence = 0;
            _seenPackets = 0;
            _hasKeyframeBaseline = false;
            if (_stagedRing != null)
            {
                while (_stagedRing.TryDequeueOldest(out var buf))
                {
                    BasisAvatarBufferPool.Release(buf);
                }
                StagedCount = 0;
            }

            while (PayloadQueue.TryDequeue(out var buffer))
            {
                BasisAvatarBufferPool.Release(buffer);
            }
            _pendingCount = 0;

            ClearAndRelease();

            if (TposeLocalRotations.IsCreated) TposeLocalRotations.Dispose();
            BoneTransforms = null;

            if (hasEvents && RemotePlayer != null && RemotePlayer.RemoteAvatarDriver != null)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete -= OnCalibration;
                hasEvents = false;
            }

            AudioReceiverModule.OnDestroy();
        }

        public void ReceiveNetworkAudio(ServerAudioSegmentMessage msg)
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAudioSegment, msg.audioSegmentData.LengthUsed);
            AudioReceiverModule.Insert(msg.audioSegmentData);
            Player.AudioReceived?.Invoke();
        }


        public async void ReceiveAvatarChangeRequest(ServerAvatarChangeMessage SACM)
        {
            try
            {
                LastLinkedAvatarIndex = SACM.clientAvatarChangeMessage.LocalAvatarIndex;
                RemotePlayer.CACM = SACM.clientAvatarChangeMessage;

                // A new avatar is a fresh bundle URL — clear the global "bail on retries"
                // state from any prior failure so this one actually gets attempted. If THIS
                // load also fails, BasisAvatarFactory.MarkRemoteLoadFailed re-arms the flag.
                RemotePlayer.HasFailedAvatarLoadGlobally = false;
                RemotePlayer.AvatarLoadErrorMessage = null;
                RemotePlayer.OnAvatarFailedStateChanged?.Invoke();

                BasisLoadableBundle bundle = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(SACM.clientAvatarChangeMessage.byteArray);
                await RemotePlayer.CreateAvatar(SACM.clientAvatarChangeMessage.loadMode, bundle);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"ReceiveAvatarChangeRequest failed: {ex}");
            }
        }

        public BasisNetworkReceiver(ushort PlayerID)
        {
            playerId = PlayerID;
            hasID = true;
        }

        private void TrySeedFirstFromStaging()
        {
            if (HasCurrentBuffer) return;
            if (_stagedRing.TryDequeueOldest(out var first))
            {
                // Fresh window (cold start or recovery after starvation): any retained Previous
                // predates the gap and would poison the p0 tangent, so drop it — p0 duplicates p1.
                if (HasPreviousBuffer)
                {
                    BasisAvatarBufferPool.Release(Previous);
                    Previous = null;
                    HasPreviousBuffer = false;
                }
                Current = first;
                SentLatest = true;
                HasCurrentBuffer = true;
            }

            StagedCount = _stagedRing.Count;
        }

        // Seed Next with ONE next-oldest staged frame (do NOT drain staging)
        private void TrySetLastFromStaging()
        {
            if (!HasCurrentBuffer || HasNextBuffer)
            {
                return;
            }

            if (_stagedRing.TryDequeueOldest(out var next))
            {
                Next = next;
                SentLatest = true;
                HasNextBuffer = true;
            }

            StagedCount = _stagedRing.Count;
        }

        public void ClearAndRelease()
        {
            if (HasPreviousBuffer)
            {
                BasisAvatarBufferPool.Release(Previous);
                Previous = null;
                HasPreviousBuffer = false;
            }
            ReleaseCurrent();
            if (HasNextBuffer)
            {
                BasisAvatarBufferPool.Release(Next);
                Next = null;
                HasNextBuffer = false;
            }
        }

        public void ReleaseCurrent()
        {
            if (HasCurrentBuffer)
            {
                BasisAvatarBufferPool.Release(Current);
                Current = null;
                HasCurrentBuffer = false;
            }
        }
    }
}
