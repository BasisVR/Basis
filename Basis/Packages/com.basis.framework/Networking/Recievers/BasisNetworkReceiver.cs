using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private const int EyesAndMouthOffset = 15; // L/R up/down, L/R left/right, mouth open/smile
        private const int EyesAndMouthCount = 6;
        public const int EyeAndMouthCountInBytes = EyesAndMouthCount * sizeof(float);

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
        public float[] EyesAndMouth = new float[] { 0, 0, 0, 0, 1, 0 }; // default neutral eyes, mouth open=1 for breathing
        public float3 ApplyingScale;

        // When true, forces re-validation of avatar/animator/transform references.
        // Set on avatar change (CalibrationComplete), init, and deinit.
        // Avoids 3000+ Unity null checks per frame with 1k receivers.
        private bool _avatarDirty = true;

        private double interpolationTime = 0f; // 0..1 over current->next window

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
        /// Main-thread simulation step. Pulls packets, maintains interpolation window,
        /// computes interpolationTime, and feeds inputs to the network driver.
        /// </summary>
        public void Compute(double unscaledDeltaTime)
        {
            AudioReceiverModule?.DrainAndDecode();

            // Re-validate avatar references only when dirty (avatar change, init, etc.)
            // Avoids expensive Unity null checks (managed→native interop) every frame for all receivers.
            if (_avatarDirty)
            {
                // expected briefly on join — stay dirty so we retry next frame
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
                _avatarDirty = false;
            }

            if (!hasRequiredData) return;

            if (LastAvatarsTransform != Player.AvatarTransform)
            {
                LastAvatarsTransform = Player.AvatarTransform;
                DidLastAvatarTransformChanged = true;
            }
            // 1) Pull network packets, drop stale, sort by sequence, then stage
            if (System.Threading.Interlocked.Exchange(ref _pendingCount, 0) > 0)
            {
                _pendingSort.Clear();
                while (PayloadQueue.TryDequeue(out BasisAvatarBuffer buffer))
                {
                    if (_seenPackets >= 2)
                    {
                        // Forward distance from highest to buffer:
                        // [1,127] = buffer is newer, [128,255] = buffer is behind (stale)
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
                        // Warmup: skip stale checking, seed highest from second packet.
                        // First packet is initial join data with an unset sequence (0);
                        // second packet is the first streaming update with a real server sequence.
                        if (_seenPackets == 1)
                        {
                            _highestSequence = buffer.Sequence;
                        }
                        _seenPackets++;
                    }

                    _pendingSort.Add(buffer);
                }

                // Sort by sequence so out-of-order arrivals are staged in correct order
                if (_pendingSort.Count > 1)
                {
                    _pendingSort.Sort(s_sequenceCompare);
                }

                // Enqueue sorted items into staging ring with monotonic server clock
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
            // 2) Ensure we have a valid interpolation window (Current -> Next)
            if (!HasCurrentBuffer)
            {
                TrySeedFirstFromStaging();   // only takes ONE oldest
            }

            if (!HasNextBuffer)
            {
                TrySetLastFromStaging();     // only takes ONE next-oldest
            }

            HasBufferHolds = HasCurrentBuffer && HasNextBuffer;
            if (!HasBufferHolds)
            {
                // It's valid to be here if we haven't received enough frames yet.
                return;
            }

            // 2b) Advance window while consumed and we have staged frames
            while (interpolationTime >= 1f && _stagedRing.Count != 0)
            {
                if (HasCurrentBuffer)
                {
                    ReleaseCurrent();
                }

                // If we had holds, Next must be non-null here.
                Current = Next;
                HasCurrentBuffer = true;

                HasNextBuffer = false;
                Next = null;

                interpolationTime = 0f;

                TrySetLastFromStaging();

                HasBufferHolds = HasCurrentBuffer && HasNextBuffer;
                if (!HasBufferHolds)
                {
                    break;
                }
            }

            StagedCount = _stagedRing.Count;

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

            HasBufferHolds = HasCurrentBuffer && HasNextBuffer;

            // 3) If we have a window, compute interpolation fraction and feed the driver
            if (HasBufferHolds)
            {
                var first = Current;
                var last = Next;

                double windowDuration = last.ServerTimeSeconds - first.ServerTimeSeconds;
                // Catches NaN (comparison is false), negative, zero, tiny, and huge/Inf values
                if (!(windowDuration > 1e-6 && windowDuration < 1e6))
                {
                    windowDuration = math.max(last.SecondsInterval, 1e-3);
                }
                float rate = 1f + CatchupGain * (StagedCount - TargetJitterDepth);
                rate = Mathf.Clamp(rate, MinPlaybackRate, MaxPlaybackRate);
                interpolationTime += (unscaledDeltaTime / windowDuration * (double)rate);
                if (!math.isfinite(interpolationTime))
                {
                    interpolationTime = 1;
                }
                double effectiveDt = unscaledDeltaTime * (double)rate;
                BasisRemoteNetworkDriver.SetFrameTiming(playerId, interpolationTime, effectiveDt);

                if (SentLatest)
                {

                    BasisRemoteNetworkDriver.SetFrameInputs(
                        playerId,
                        Player.BasisAvatar.HumanScale,
                        first.Position, last.Position,
                        first.Scale, last.Scale,
                        first.Rotation, last.Rotation,
                         first.Muscles, last.Muscles
                    );
                    IsDataReady = true;
                    SentLatest = false;
                }
            }
        }
        public bool IsDataReady = false;
        /// <summary>
        /// Main-thread application step. Pulls posed outputs from the driver and applies
        /// body position/rotation/muscles to the avatar via PoseHandler.
        /// </summary>
        public void Apply()
        {
            if (!IsDataReady || !hasRequiredData) return;

            BasisRemoteNetworkDriver.GetMuscleArray(playerId, out bool outscale, out var ApplyingRotation, out float3 scaledBody, ref HumanPose, EyesAndMouth, EyesAndMouthOffset, EyeAndMouthCountInBytes);
            HumanPose.bodyPosition = scaledBody;
            HumanPose.bodyRotation = ApplyingRotation;

            if (outscale)
            {
                ApplyScale();
            }
            else if (DidLastAvatarTransformChanged)
            {
                ApplyScale();
                DidLastAvatarTransformChanged = false;
            }

            PoseHandler.SetHumanPose(ref HumanPose);
            if (HasOverridenDestination)
            {
                var References = RemotePlayer?.RemoteAvatarDriver?.References;
                if (References.Hips != null)
                {
                    References.Hips.SetPositionAndRotation(OverridenPosition, OverridenRotation);
                }
            }
        }
        public void ApplyScale()
        {
            BasisRemoteNetworkDriver.GetScaleOutput(playerId, out ApplyingScale);
            Player.AvatarTransform.localScale = ApplyingScale;
        }

        public void EnQueueAvatarBuffer(BasisAvatarBuffer avatarBuffer)
        {
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

            if (!hasEvents)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete += OnCalibration;
                hasEvents = true;
            }
        }

        public void OnCalibration()
        {
            _avatarDirty = true;
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
            // _stagedRing can be null if Initialize never completed, so don't Assert here—guard.
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

            if (hasEvents && RemotePlayer != null && RemotePlayer.RemoteAvatarDriver != null)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete -= OnCalibration;
                hasEvents = false;
            }

            AudioReceiverModule?.OnDestroy();
        }

        public void ReceiveNetworkAudio(ServerAudioSegmentMessage msg)
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAudioSegment, msg.audioSegmentData.LengthUsed);
            AudioReceiverModule.Insert(msg.audioSegmentData);
            Player.AudioReceived?.Invoke();
        }


        public async void ReceiveAvatarChangeRequest(ServerAvatarChangeMessage SACM)
        {
            LastLinkedAvatarIndex = SACM.clientAvatarChangeMessage.LocalAvatarIndex;
            RemotePlayer.CACM = SACM.clientAvatarChangeMessage;
            BasisLoadableBundle bundle = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(SACM.clientAvatarChangeMessage.byteArray);
            await RemotePlayer.CreateAvatar(SACM.clientAvatarChangeMessage.loadMode, bundle);
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
