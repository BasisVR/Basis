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
    [DefaultExecutionOrder(15001)]
    [System.Serializable]
    public class BasisNetworkReceiver : BasisNetworkPlayer
    {
        private const int EyesAndMouthOffset = 15; // starting muscle index for eyes/mouth
        private const int EyesAndMouthCount = 6;   // number of floats to copy
        public const int EyeAndMouthSize = EyesAndMouthOffset * sizeof(float);      // bytes
        public const int EyeAndMountCountInBytes = EyesAndMouthCount * sizeof(float); // bytes

        /// <summary>
        /// If more than this many frames are queued, old frames will be dropped to catch up.
        /// </summary>
        public static int BufferCapacityBeforeCleanup = 5;

        [SerializeField] public BasisAudioReceiver AudioReceiverModule = new BasisAudioReceiver();
        [SerializeField] public ConcurrentQueue<BasisAvatarBuffer> PayloadQueue = new ConcurrentQueue<BasisAvatarBuffer>();
        public BasisRemotePlayer RemotePlayer;

        [SerializeField] public BasisRemoteAvatarBufferHolder BufferHolder = new BasisRemoteAvatarBufferHolder();

        public bool HasEvents = false;
        public bool HasAvatarQueue;
        public bool LogFirstError = false;

        // Eyes (L/R up-down; L/R left-right) + Mouth open/smile? (example shape order)
        public float[] EyesAndMouth = new float[] { 0, 0, 0, 0, 1, 0 };

        public float[] Muscles = new float[95];

        // Computed by driver
        public quaternion ApplyingRotation;
        public float3 ApplyingPosition;
        public float3 ApplyingScale;

        // Interpolation timing
        private float interpolationTime = 0f;

        // Main-thread staging for dequeued packets
        private readonly List<BasisAvatarBuffer> _staged = new List<BasisAvatarBuffer>(16);

        // ---------- Compute / Apply ----------
        public bool HasBufferHolds;
        /// <summary>
        /// Called from your network simulation (main thread).
        /// Pulls data to staging, builds/advances the interpolation window,
        /// computes the fraction using SecondsInterval, and pushes inputs to the driver.
        /// </summary>
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
                    // Feed driver (per-avatar transforms, scales, rotations, muscles, t)
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
        public void Apply()
        {
            if (HasBufferHolds)
            {
                // Pull outputs (position, scale, rotation, muscles). We also use outPos for a robust fallback path.
                if (BasisRemoteNetworkDriver.GetOutputs_NoAlloc(playerId, out var outPos, out float3 applyingScale, out var applyingRotation, out float3 scaledBody, Muscles))
                {
                    HumanPose.bodyPosition = scaledBody;
                    HumanPose.bodyRotation = applyingRotation;

                    // Muscles
                    Memcpy95(Muscles, HumanPose.muscles);

                    // Overlay eyes/mouth in one tiny copy
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
                    // its hard to move this out atm since the data that we supply only comes from a fixed size of ouputs but we need a more
                    //moving targeted solution to account for transforms
                     Player.AvatarTransform.localScale = applyingScale;

                    // HumanPoseHandler must stay on main thread
                    PoseHandler.SetHumanPose(ref HumanPose);
                }
            }
        }

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
        /// Called from a background/network thread. Thread-safe.
        /// </summary>
        public void EnQueueAvatarBuffer(BasisAvatarBuffer avatarBuffer)
        {
            PayloadQueue.Enqueue(avatarBuffer);
        }

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
                    // Send the message now
                    NetworkBehaviours[message.Key].OnNetworkMessageReceived(
                        playerIdMessage.playerID,
                        Remote.payload,
                        message.Value.Method
                    );

                    // mark this message as successfully sent
                    keysToRemove.Add(message.Key);
                }
                else
                {
                    // Check if this message is from a *past* avatar index
                    bool isPastMessage = IsPastAvatar(Remote.AvatarLinkIndex, LastLinkedAvatarIndex);
                    if (isPastMessage)
                    {
                        // Discard old/past messages
                        BasisDebug.Log($"Discarding stale message with AvatarLinkIndex {Remote.AvatarLinkIndex}");
                        keysToRemove.Add(message.Key);
                    }
                }
            }

            // remove all that were either sent or expired
            foreach (byte key in keysToRemove)
            {
                NextMessages.Remove(key);
            }
        }

        /// <summary>
        /// Determines if a given avatar index is "in the past" relative to the current.
        /// Handles wrap-around since AvatarLinkIndex is a byte (0-255).
        /// </summary>
        private bool IsPastAvatar(byte messageIndex, byte currentIndex)
        {
            // Compute difference modulo 256
            int diff = (currentIndex - messageIndex + 256) % 256;

            // If diff is between 1 and 127, then it's behind (old)
            return diff > 0 && diff < 128;
        }

        public override void DeInitialize()
        {
            // no need we pump data always before requesting so its not a necessary step
            // BasisRemoteNetworkDriver.ResetIndex(playerId);

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

        public void ReceiveNetworkAudio(ServerAudioSegmentMessage audioSegment)
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAudioSegment, audioSegment.audioSegmentData.LengthUsed);
            AudioReceiverModule.OnDecode(audioSegment.audioSegmentData.buffer, audioSegment.audioSegmentData.LengthUsed);
            Player.AudioReceived?.Invoke(true);
        }

        public void ReceiveSilentNetworkAudio(ServerAudioSegmentMessage audioSilentSegment)
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAudioSegment, 1);
            AudioReceiverModule.OnDecodeSilence();
            Player.AudioReceived?.Invoke(false);
        }

        // ---------- Avatar switching ----------
        public async void ReceiveAvatarChangeRequest(ServerAvatarChangeMessage ServerAvatarChangeMessage)
        {
            RemotePlayer.CACM = ServerAvatarChangeMessage.clientAvatarChangeMessage;
            BasisLoadableBundle BasisLoadableBundle = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(ServerAvatarChangeMessage.clientAvatarChangeMessage.byteArray);

            await RemotePlayer.CreateAvatar(ServerAvatarChangeMessage.clientAvatarChangeMessage.loadMode, BasisLoadableBundle);
        }

        // ---------- Ctor ----------
        public BasisNetworkReceiver(ushort PlayerID)
        {
            playerId = PlayerID;
            hasID = true;
        }

        /// <summary>
        /// Move packets from the concurrent queue to a main-thread staging list.
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
        /// Ensures we have a (First, Last) interpolation window and advances when consumed.
        /// Now robust against empty/invalid first frames.
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

            // If either still missing, bail; we'll try again next compute tick
            if (!BufferHolder.HasFirst || !BufferHolder.HasLast)
                return;

            // If we've consumed the current window, advance; repeat while we have more staged
            while (interpolationTime >= 1f && _staged.Count != 0)
            {
                // Release old First
                if (BufferHolder.HasFirst)
                {
                    BasisAvatarBufferPool.Release(ref BufferHolder.First);
                    BufferHolder.HasFirst = false;
                }

                // Promote Last -> First
                BufferHolder.First = BufferHolder.Last;
                BufferHolder.HasFirst = true;

                // Pull new Last
                BufferHolder.HasLast = false;

                interpolationTime = 0f;

                TrySetLastFromStaging();

                // If promotion produced an invalid window (rare), try to repair here
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

        private void TrySeedFirstFromStaging()
        {
            // Pull until we find a valid/repairable buffer
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

                // Unusable — release and continue
                BasisAvatarBufferPool.Release(ref first);
            }
        }

        private void TrySetLastFromStaging()
        {
            if (!BufferHolder.HasFirst) return;

            // Pull until we find a valid/repairable buffer
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

                // Unusable — release and continue
                BasisAvatarBufferPool.Release(ref last);
            }
        }

        private void ComputeInterpolationFraction(float unscaledDeltaTime)
        {
            var first = BufferHolder.First;
            var last = BufferHolder.Last;

            double windowDuration =
                last.SecondsInterval > 0 ? last.SecondsInterval :
                first.SecondsInterval > 0 ? first.SecondsInterval :
                (1.0 / 60.0);

            // Clamp to sane floor to avoid huge dt spikes dividing by tiny intervals
            if (windowDuration <= 1e-6) windowDuration = 1e-3;

            double step = Math.Max(unscaledDeltaTime, 0.0);
            interpolationTime += (float)(step / windowDuration);
            if (interpolationTime > 1f) interpolationTime = 1f;
            if (interpolationTime < 0f) interpolationTime = 0f;
        }

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

        private static bool IsValidMuscleArray(NativeArray<float> arr) => arr != null && arr.Length >= 95;

        /// <summary>
        /// Validates a buffer; attempts to repair fixable fields in-place.
        /// Returns true if usable after fixup; false if unrecoverable.
        /// </summary>
        private bool ValidateOrFixup(ref BasisAvatarBuffer buf)
        {
            // Muscles
            if (!IsValidMuscleArray(buf.Muscles))
            {
                // Replace with a zeroed buffer to keep pose valid; we still accept the frame.
                buf.Muscles = new NativeArray<float>(95, Allocator.Persistent);
            }
            return true;
        }
    }
}
