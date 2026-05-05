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
        public const int BoneCount = BasisBoneRotationCompression.SyncBoneCount; // 54

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
        [SerializeField]
        public ConcurrentQueue<BasisAvatarBuffer> PayloadQueue = new ConcurrentQueue<BasisAvatarBuffer>();
        // Volatile counter avoids ConcurrentQueue.TryDequeue on empty queues (1k volatile reads vs 1k TryDequeue).
        private volatile int _pendingCount;
        public BasisRemotePlayer RemotePlayer;

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
        public NativeArray<quaternion> TposeLocalRotations;

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
        private const int TargetJitterDepth = 3;          // desired staged depth cushion
        private const float CatchupGain = 0.12f;          // 0.05..0.25 tune
        private const float MinPlaybackRate = 0.85f;
        private const float MaxPlaybackRate = 1.35f;

        public bool HasCurrentBuffer = false;
        public bool HasNextBuffer = false;
        public bool SentLatest = false;
        public BasisAvatarBuffer Current { get; private set; }
        public BasisAvatarBuffer Next { get; private set; }
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
            // AudioReceiverModule is field-initialized at construction and never
            // assigned null; the lifecycle guarantees this. Drop the ?. so the
            // per-receiver hot path doesn't pay the null check.
            AudioReceiverModule.ApplyAudioState();
        }

        /// <summary>
        /// Thread-safe: audio decode + packet drain + window management + interpolation + SoA writes.
        /// Each receiver operates on its own state and writes only to its own playerId slot.
        /// Safe to call from worker threads after PreCompute completes on main thread.
        /// </summary>
        public void ComputeData(double unscaledDeltaTime)
        {
            // Audio decode is thread-safe (per-receiver decoder/buffers, no Unity API).
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            UnityEngine.Profiling.Profiler.BeginSample("ComputeData.AudioDecode");
#endif
            AudioReceiverModule.DrainAndDecodeThreadSafe();
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
                    else
                    {
                        if (_seenPackets == 1)
                        {
                            _highestSequence = buffer.Sequence;
                        }
                        _seenPackets++;
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
                double windowDuration = Next.ServerTimeSeconds - Current.ServerTimeSeconds;
                if (!(windowDuration > 1e-6 && windowDuration < 1e6))
                {
                    windowDuration = math.max(Next.SecondsInterval, 1e-3);
                }
                float rate = 1f + CatchupGain * (StagedCount - TargetJitterDepth);
                rate = math.clamp(rate, MinPlaybackRate, MaxPlaybackRate);

                interpolationTime += (unscaledDeltaTime / windowDuration * (double)rate);
                if (!math.isfinite(interpolationTime))
                {
                    interpolationTime = 1;
                }

                while (interpolationTime >= 1.0 && _stagedRing.Count != 0)
                {
                    if (HasCurrentBuffer)
                    {
                        ReleaseCurrent();
                    }

                    Current = Next;
                    HasCurrentBuffer = true;
                    HasNextBuffer = false;
                    Next = null;

                    interpolationTime -= 1.0;

                    TrySetLastFromStaging();

                    HasBufferHolds = HasCurrentBuffer && HasNextBuffer;
                    if (!HasBufferHolds)
                    {
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
                    var first = Current;
                    var last = Next;
                    BasisRemoteNetworkDriver.SetFrameInputs(
                        playerId,
                        CachedHumanScale,
                        first.Position, last.Position,
                        first.Scale, last.Scale,
                        first.Rotation, last.Rotation,
                        first.HipsLocalDelta, last.HipsLocalDelta,
                        first.HipsLocalRotation, last.HipsLocalRotation,
                        first.BoneRotations, last.BoneRotations
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

        public void EnQueueAvatarBuffer(BasisAvatarBuffer avatarBuffer)
        {
            Interlocked.Increment(ref _poseVersion);
            _latestNetworkPosition = avatarBuffer.Position;
            _latestNetworkRotation = avatarBuffer.Rotation;
            _latestNetworkScale = avatarBuffer.Scale;
            Interlocked.Increment(ref _poseVersion);
            PayloadQueue.Enqueue(avatarBuffer);
            System.Threading.Interlocked.Increment(ref _pendingCount);
        }

        public override void Initialize()
        {
            _avatarDirty = true;
            _serverClockSeconds = 0.0;
            _serverClockSeeded = false;
            _highestSequence = 0;
            _seenPackets = 0;
            RemotePlayer = (BasisRemotePlayer)Player;
            AudioReceiverModule.Initialize(this);

            // Reset staging
            _stagedRing = new BasisRingBuffer<BasisAvatarBuffer>(MaxStage);
            StagedCount = 0;
            ClearAndRelease();
            interpolationTime = 0f;
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

            List<byte> keysToRemove = new List<byte>();
            foreach (KeyValuePair<byte, ServerAvatarDataMessageQueue> message in NextMessages)
            {
                ServerAvatarDataMessage avatarMessage = message.Value.ServerAvatarDataMessage;

                RemoteAvatarDataMessage Remote = avatarMessage.avatarDataMessage;
                PlayerIdMessage playerIdMessage = avatarMessage.playerIdMessage;

                bool isSameAvatar = Remote.AvatarLinkIndex == LastLinkedAvatarIndex;
                if (isSameAvatar)
                {
                    NetworkBehaviours[message.Key].OnNetworkMessageReceived(
                        playerIdMessage.playerID,
                        Remote.payload,
                        message.Value.Method
                    );
                    keysToRemove.Add(message.Key);
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
                if (RemotePlayer.RemoteNamePlate != null)
                {
                    RemotePlayer.RemoteNamePlate.RefreshFailedStateColor();
                }

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
