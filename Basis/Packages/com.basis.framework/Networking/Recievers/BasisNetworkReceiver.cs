using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
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
        /// <summary>Starting muscle index for eyes/mouth overlay.</summary>
        private const int EyesAndMouthOffset = 15;

        /// <summary>Number of floats in the eyes/mouth overlay block.</summary>
        private const int EyesAndMouthCount = 6;

        /// <summary>Eyes/mouth block offset in bytes.</summary>
        public const int EyeAndMouthSize = EyesAndMouthOffset * sizeof(float);

        /// <summary>Eyes/mouth block length in bytes.</summary>
        public const int EyeAndMountCountInBytes = EyesAndMouthCount * sizeof(float);

        /// <summary>
        /// If the staging backlog exceeds this, older frames are dropped to reduce latency.
        /// </summary>
        public static int BufferCapacityBeforeCleanup = 5;

        /// <summary>Module that receives/decodes/plays remote voice audio.</summary>
        [SerializeField] public BasisAudioReceiver AudioReceiverModule = new BasisAudioReceiver();

        /// <summary>Thread-safe queue of incoming avatar frames (producer: net thread).</summary>
        [SerializeField] public ConcurrentQueue<BasisAvatarBuffer> PayloadQueue = new ConcurrentQueue<BasisAvatarBuffer>();

        /// <summary>Concrete remote player this receiver animates.</summary>
        public BasisRemotePlayer RemotePlayer;

        /// <summary>Holds the current interpolation window (First→Last) for animation.</summary>
        [SerializeField] public BasisRemoteAvatarBufferHolder BufferHolder = new BasisRemoteAvatarBufferHolder();

        /// <summary>Whether we've subscribed to avatar-driver events.</summary>
        public bool HasEvents = false;

        /// <summary>Whether this receiver should keep consuming avatar frames.</summary>
        public bool HasAvatarQueue;

        /// <summary>Used to log only the first error of repeated errors.</summary>
        public bool LogFirstError = false;

        /// <summary>
        /// Eye (L/R up/down, L/R left/right) + mouth open/smile block, overlaid on muscles.
        /// </summary>
        public float[] EyesAndMouth = new float[] { 0, 0, 0, 0, 1, 0 };

        /// <summary>Working array for 95 human pose muscles.</summary>
        public float[] Muscles = new float[95];

        /// <summary>Computed rotation written by the driver.</summary>
        public quaternion ApplyingRotation;

        /// <summary>Computed position written by the driver.</summary>
        public float3 ApplyingPosition;

        /// <summary>Computed scale written by the driver.</summary>
        public float3 ApplyingScale;

        /// <summary>Normalized time (0..1) across the current interpolation window.</summary>
        private float interpolationTime = 0f;

        /// <summary>Main-thread staging list pulled from <see cref="PayloadQueue"/>.</summary>
        private readonly List<BasisAvatarBuffer> _staged = new List<BasisAvatarBuffer>(16);

        /// <summary>True when both First and Last buffers exist.</summary>
        public bool HasBufferHolds;

        /// <summary>
        /// Main-thread simulation step. Pulls packets, maintains interpolation window,
        /// computes <see cref="interpolationTime"/>, and feeds inputs to the network driver.
        /// </summary>
        /// <param name="unscaledDeltaTime">Unscaled delta time for interpolation.</param>
        public void Compute(float unscaledDeltaTime)
        {
            // 1) Pull network packets to main-thread staging
            PumpQueueToStaging();

            // 2) Ensure we have a valid interpolation window (First -> Last)
            BuildOrAdvanceWindow();
            HasBufferHolds = BufferHolder.HasFirst && BufferHolder.HasLast;

            // 3) If we have a window, compute interpolation fraction and feed the compute phase
            if (HasBufferHolds)
            {
                ComputeInterpolationFraction(unscaledDeltaTime);

                if (Player.BasisAvatar != null && Player.BasisAvatar.Animator != null)
                {
                    var first = BufferHolder.First;
                    var last = BufferHolder.Last;

                    BasisRemoteNetworkDriver.SetInputs(
                        playerId, Player.BasisAvatar.Animator.humanScale,
                        first.Position, last.Position,
                        first.Scale, last.Scale,
                        first.rotation, last.rotation,
                        interpolationTime,
                        first.Muscles, last.Muscles
                    );
                }
            }
        }

        /// <summary>
        /// Main-thread application step. Pulls posed outputs from the driver and applies
        /// body position/rotation/muscles to the avatar via <see cref="PoseHandler"/>.
        /// </summary>
        public void Apply()
        {
            if (HasBufferHolds)
            {
                if (BasisRemoteNetworkDriver.GetOutputs_NoAlloc(
                        playerId,
                        out var outPos,
                        out float3 applyingScale,
                        out var applyingRotation,
                        out float3 scaledBody,
                        Muscles))
                {
                    HumanPose.bodyPosition = scaledBody;
                    HumanPose.bodyRotation = applyingRotation;

                    // Copy all 95 muscles
                    Memcpy95(Muscles, HumanPose.muscles);

                    // Overlay eyes/mouth block in one shot
                    unsafe
                    {
                        fixed (float* pDst = HumanPose.muscles)
                        fixed (float* pSrc = EyesAndMouth)
                        {
                            UnsafeUtility.MemCpy(
                                pDst + (EyeAndMouthSize / sizeof(float)),
                                pSrc,
                                EyeAndMountCountInBytes
                            );
                        }
                    }

                    // Scale must be applied on transform
                    Player.AvatarTransform.localScale = applyingScale;

                    // HumanPoseHandler must stay on main thread
                    PoseHandler.SetHumanPose(ref HumanPose);
                }
            }
        }

        /// <summary>
        /// Copies 95 floats from <paramref name="src"/> to <paramref name="dst"/> using a fast memcpy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Memcpy95(float[] src, float[] dst)
        {
            const int MuscleCount = 95;
            unsafe
            {
                fixed (float* pSrc = src)
                fixed (float* pDst = dst)
                {
                    UnsafeUtility.MemCpy(pDst, pSrc, MuscleCount * sizeof(float));
                }
            }
        }

        /// <summary>
        /// Called from a background/network thread. Enqueues an incoming avatar frame.
        /// </summary>
        /// <param name="avatarBuffer">The decoded network frame.</param>
        public void EnQueueAvatarBuffer(BasisAvatarBuffer avatarBuffer)
        {
            PayloadQueue.Enqueue(avatarBuffer);
        }

        /// <summary>
        /// Initializes this receiver for its remote player and subscribes to events.
        /// </summary>
        public override void Initialize()
        {
            RemotePlayer = (BasisRemotePlayer)Player;
            AudioReceiverModule.Initalize(this);

            if (!HasEvents && RemotePlayer?.RemoteAvatarDriver != null)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete += OnCalibration;
                HasEvents = true;
            }

            HasAvatarQueue = true;
            _staged.Clear();
            BufferHolder.ClearAndRelease();
            interpolationTime = 0f;
        }

        /// <summary>
        /// Called when the remote avatar driver reports calibration is complete.
        /// Flushes any queued messages that match the current avatar index and
        /// discards stale ones from previous avatars.
        /// </summary>
        public void OnCalibration()
        {
            AudioReceiverModule.AvatarChanged(this);

            // Track which keys got successfully sent
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

        /// <summary>
        /// Returns true if <paramref name="messageIndex"/> is "older" than <paramref name="currentIndex"/>
        /// in modular arithmetic (byte wrap-around safe).
        /// </summary>
        private bool IsPastAvatar(byte messageIndex, byte currentIndex)
        {
            int diff = (currentIndex - messageIndex + 256) % 256;
            return diff > 0 && diff < 128;
        }

        /// <summary>
        /// Tears down staging/buffer state, unsubscribes events, and stops audio.
        /// </summary>
        public override void DeInitialize()
        {
            BufferHolder.ClearAndRelease();

            if (_staged != null)
            {
                int Count = _staged.Count;
                for (int i = 0; i < Count; i++)
                {
                    var b = _staged[i];
                    BasisAvatarBufferPool.Release(ref b);
                }
                _staged.Clear();
            }

            while (PayloadQueue.TryDequeue(out var buffer))
            {
                BasisAvatarBufferPool.Release(ref buffer);
            }

            if (RemotePlayer != null && HasEvents && RemotePlayer.RemoteAvatarDriver != null)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete -= OnCalibration;
                HasEvents = false;
            }

            AudioReceiverModule?.OnDestroy();
            HasAvatarQueue = false;
        }

        /// <summary>
        /// Handles a non-silent voice segment for this remote player.
        /// </summary>
        /// <param name="audioSegment">Decoded server audio segment message.</param>
        public void ReceiveNetworkAudio(ServerAudioSegmentMessage audioSegment)
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAudioSegment, audioSegment.audioSegmentData.LengthUsed);
            AudioReceiverModule.OnDecode(audioSegment.audioSegmentData.buffer, audioSegment.audioSegmentData.LengthUsed);
            Player.AudioReceived?.Invoke(true);
        }

        /// <summary>
        /// Handles a silent voice segment for this remote player.
        /// </summary>
        /// <param name="audioSilentSegment">Server audio segment message with zero payload.</param>
        public void ReceiveSilentNetworkAudio(ServerAudioSegmentMessage audioSilentSegment)
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAudioSegment, 1);
            AudioReceiverModule.OnDecodeSilence();
            Player.AudioReceived?.Invoke(false);
        }

        /// <summary>
        /// Receives a request to switch the remote player's avatar and triggers creation.
        /// </summary>
        /// <param name="ServerAvatarChangeMessage">Avatar change payload from the server.</param>
        public async void ReceiveAvatarChangeRequest(ServerAvatarChangeMessage ServerAvatarChangeMessage)
        {
            RemotePlayer.CACM = ServerAvatarChangeMessage.clientAvatarChangeMessage;
            BasisLoadableBundle BasisLoadableBundle =
                BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(
                    ServerAvatarChangeMessage.clientAvatarChangeMessage.byteArray);

            await RemotePlayer.CreateAvatar(ServerAvatarChangeMessage.clientAvatarChangeMessage.loadMode, BasisLoadableBundle);
        }

        /// <summary>
        /// Constructs a receiver with an assigned player ID.
        /// </summary>
        /// <param name="PlayerID">Unique player identifier.</param>
        public BasisNetworkReceiver(ushort PlayerID)
        {
            playerId = PlayerID;
            hasID = true;
        }

        /// <summary>
        /// Moves all queued packets from <see cref="PayloadQueue"/> to the main-thread staging list.
        /// Drops the oldest if staging exceeds a soft cap to bound latency.
        /// </summary>
        private void PumpQueueToStaging()
        {
            while (PayloadQueue.TryDequeue(out var buffer))
            {
                _staged.Add(buffer);
            }

            const int MaxStage = 64;
            if (_staged.Count > MaxStage)
            {
                int drop = _staged.Count - MaxStage;
                DropOldestFromStaging(drop);
            }
        }

        /// <summary>
        /// Ensures a valid (First, Last) interpolation window exists; advances it when consumed.
        /// Robust to missing/invalid frames; attempts to repair.
        /// </summary>
        private void BuildOrAdvanceWindow()
        {
            // Seed First if missing
            if (!BufferHolder.HasFirst)
            {
                TrySeedFirstFromStaging();
            }

            // Fill Last if missing
            if (!BufferHolder.HasLast)
            {
                TrySetLastFromStaging();
            }

            if (!BufferHolder.HasFirst || !BufferHolder.HasLast)
                return;

            // Advance window while we've consumed it and have staged frames
            while (interpolationTime >= 1f && _staged.Count != 0)
            {
                if (BufferHolder.HasFirst)
                {
                    BasisAvatarBufferPool.Release(ref BufferHolder.First);
                    BufferHolder.HasFirst = false;
                }

                BufferHolder.First = BufferHolder.Last;
                BufferHolder.HasFirst = true;

                BufferHolder.HasLast = false;
                interpolationTime = 0f;

                TrySetLastFromStaging();

                if (!(BufferHolder.HasFirst && BufferHolder.HasLast))
                    break;
            }

            // If staging backlog is large, drop old frames to reduce latency
            if (_staged.Count > BufferCapacityBeforeCleanup)
            {
                int drop = _staged.Count - BufferCapacityBeforeCleanup;
                DropOldestFromStaging(drop);
            }
        }

        /// <summary>
        /// Attempts to seed the First buffer from staging, validating/fixing as needed.
        /// Releases unusable frames back to the pool.
        /// </summary>
        private void TrySeedFirstFromStaging()
        {
            while (_staged.Count > 0)
            {
                var first = _staged[0];
                _staged.RemoveAt(0);

                if (ValidateOrFixup(ref first))
                {
                    BufferHolder.First = first;
                    BufferHolder.HasFirst = true;
                    return;
                }

                BasisAvatarBufferPool.Release(ref first);
            }
        }

        /// <summary>
        /// Attempts to set the Last buffer from staging, validating/fixing as needed.
        /// </summary>
        private void TrySetLastFromStaging()
        {
            if (!BufferHolder.HasFirst) return;

            while (_staged.Count > 0)
            {
                var last = _staged[0];
                _staged.RemoveAt(0);

                if (ValidateOrFixup(ref last))
                {
                    BufferHolder.Last = last;
                    BufferHolder.HasLast = true;
                    return;
                }

                BasisAvatarBufferPool.Release(ref last);
            }
        }

        /// <summary>
        /// Updates <see cref="interpolationTime"/> using the effective window duration.
        /// Falls back to ~60Hz if per-frame intervals are unavailable.
        /// </summary>
        private void ComputeInterpolationFraction(float unscaledDeltaTime)
        {
            var first = BufferHolder.First;
            var last = BufferHolder.Last;

            double windowDuration =
                last.SecondsInterval > 0 ? last.SecondsInterval :
                first.SecondsInterval > 0 ? first.SecondsInterval :
                (1.0 / 60.0);

            if (windowDuration <= 1e-6) windowDuration = 1e-3;

            double step = Math.Max(unscaledDeltaTime, 0.0);
            interpolationTime += (float)(step / windowDuration);
            if (interpolationTime > 1f) interpolationTime = 1f;
            if (interpolationTime < 0f) interpolationTime = 0f;
        }

        /// <summary>
        /// Drops the oldest <paramref name="count"/> staged frames and returns them to the pool.
        /// </summary>
        private void DropOldestFromStaging(int count)
        {
            count = Mathf.Min(count, _staged.Count);
            for (int i = 0; i < count; i++)
            {
                var b = _staged[i];
                BasisAvatarBufferPool.Release(ref b);
            }
            _staged.RemoveRange(0, count);
        }

        // ---------- Validation / Fixup helpers ----------

        /// <summary>
        /// Returns true if the native float array contains at least 95 muscle values.
        /// </summary>
        private static bool IsValidMuscleArray(NativeArray<float> arr) => arr != null && arr.Length >= 95;

        /// <summary>
        /// Validates a buffer and attempts to repair fixable fields in-place.
        /// Returns true if usable after fixup; false if unrecoverable.
        /// </summary>
        private bool ValidateOrFixup(ref BasisAvatarBuffer buf)
        {
            if (!IsValidMuscleArray(buf.Muscles))
            {
                // Replace with a zeroed buffer to keep pose valid; still accept the frame.
                buf.Muscles = new NativeArray<float>(95, Allocator.Persistent);
            }
            return true;
        }
    }
}
