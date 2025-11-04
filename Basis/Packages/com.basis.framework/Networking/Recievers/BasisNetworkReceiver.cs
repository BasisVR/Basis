using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
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
        private const int EyesAndMouthOffset = 15;         // L/R up/down, L/R left/right, mouth open/smile
        private const int EyesAndMouthCount = 6;

        public const int EyeAndMouthSize = EyesAndMouthOffset * sizeof(float);
        public const int EyeAndMouthCountInBytes = EyesAndMouthCount * sizeof(float);

        /// <summary>
        /// If staging backlog exceeds this, older frames are dropped to reduce latency.
        /// </summary>
        public static int BufferCapacityBeforeCleanup = 5;

        [SerializeField] public BasisAudioReceiver AudioReceiverModule = new BasisAudioReceiver();
        [SerializeField] public ConcurrentQueue<BasisAvatarBuffer> PayloadQueue = new ConcurrentQueue<BasisAvatarBuffer>();

        public BasisRemotePlayer RemotePlayer;
        [SerializeField] public BasisRemoteAvatarBufferHolder BufferHolder = new BasisRemoteAvatarBufferHolder();

        public bool hasEvents = false;

        public float[] EyesAndMouth = new float[] { 0, 0, 0, 0, 1, 0 }; // default neutral eyes, mouth open=1 for breathing
        public float[] Muscles = new float[95];

        public quaternion ApplyingRotation;
        public float3 ApplyingPosition;
        public float3 ApplyingScale;

        private float interpolationTime = 0f; // 0..1 over current First→Last window

        private readonly List<BasisAvatarBuffer> _staged = new List<BasisAvatarBuffer>(16);

        public bool HasBufferHolds;
        public bool PassedSimulate = false;

        /// <summary>
        /// Main-thread simulation step. Pulls packets, maintains interpolation window,
        /// computes interpolationTime, and feeds inputs to the network driver.
        /// </summary>
        public void Compute(float unscaledDeltaTime)
        {
            if (Player == null)
            {
                BasisDebug.LogError("Player lost", BasisDebug.LogTag.Remote);
                return;
            }
            if (Player.BasisAvatar == null) return; // expected briefly on join
            if (Player.BasisAvatar.Animator == null)
            {
                BasisDebug.LogError($"Animator for {Player.DisplayName} lost", BasisDebug.LogTag.Remote);
                return;
            }
            if (Player.AvatarTransform == null)
            {
                BasisDebug.LogError($"AvatarTransform for {Player.DisplayName} lost", BasisDebug.LogTag.Remote);
                return;
            }

            // 1) Pull network packets to main-thread staging
            PumpQueueToStaging();

            // 2) Ensure we have a valid interpolation window (First -> Last)
            BuildOrAdvanceWindow();

            HasBufferHolds = BufferHolder.HasCurrentBuffer && BufferHolder.HasNextBuffer;

            // 3) If we have a window, compute interpolation fraction and feed the compute phase
            if (HasBufferHolds)
            {
                var first = BufferHolder.RequestCurrent();
                var last = BufferHolder.RequestNext();

                // Extra guard: refuse to simulate if either muscle array is invalid
                if (!IsValidMuscleArray(first.Muscles) || !IsValidMuscleArray(last.Muscles))
                {
                    // Attempt to repair once (in case they slipped in through legacy buffers)
                    ValidateOrFixup(ref first);
                    ValidateOrFixup(ref last);

                    if (!IsValidMuscleArray(first.Muscles) || !IsValidMuscleArray(last.Muscles))
                    {
                        // Drop this step; try again next frame
                        PassedSimulate = false;
                        return;
                    }

                    // If we fixed them here, store back so Holder keeps the repaired arrays
                    BufferHolder.SetCurrent(ref first);
                    BufferHolder.SetNext(ref last);
                }

                double windowDuration =
                    last.SecondsInterval > 0 ? last.SecondsInterval :
                    first.SecondsInterval > 0 ? first.SecondsInterval :
                    (1.0 / 60.0);

                if (!double.IsFinite(windowDuration) || windowDuration <= 1e-6) windowDuration = 1e-3;

                double step = Math.Max(unscaledDeltaTime, 0.0);
                interpolationTime += (float)(step / windowDuration);
                if (!float.IsFinite(interpolationTime)) interpolationTime = 0f;

                if (interpolationTime > 1f) interpolationTime = 1f;
                if (interpolationTime < 0f) interpolationTime = 0f;

                PassedSimulate = BasisRemoteNetworkDriver.SetInputs(
                    playerId, Player.BasisAvatar.HumanScale,
                    first.Position, last.Position,
                    first.Scale, last.Scale,
                    first.Rotation, last.Rotation,
                    interpolationTime,
                    first.Muscles, last.Muscles
                );
            }
        }

        /// <summary>
        /// Main-thread application step. Pulls posed outputs from the driver and applies
        /// body position/rotation/muscles to the avatar via PoseHandler.
        /// </summary>
        [BurstCompile]
        public void Apply()
        {
            if (!PassedSimulate) return;

            BasisRemoteNetworkDriver.GetOutputs_NoAlloc(
                playerId,
                out var outPos,                 // world pos (unused by HumanPose)
                out float3 applyingScale,
                out var applyingRotation,
                out float3 scaledBody,          // HumanPose.bodyPosition (units in avatar-space)
                Muscles
            );

            // Keep local fields current (useful for debug/telemetry)
            ApplyingPosition = outPos;
            ApplyingScale = applyingScale;
            ApplyingRotation = applyingRotation;

            HumanPose.bodyPosition = scaledBody;
            HumanPose.bodyRotation = applyingRotation;

            // Copy all 95 muscles
            Memcpy95(Muscles, HumanPose.muscles);

            // Overlay eyes/mouth in one shot
            unsafe
            {
                fixed (float* pDst = HumanPose.muscles)
                fixed (float* pSrc = EyesAndMouth)
                {
                    UnsafeUtility.MemCpy(pDst + EyesAndMouthOffset, pSrc, EyeAndMouthCountInBytes);
                }
            }

            // Scale must be applied on transform
            Player.AvatarTransform.localScale = applyingScale;

            // HumanPoseHandler must stay on main thread
            PoseHandler.SetHumanPose(ref HumanPose);

            PassedSimulate = false;
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

        public void EnQueueAvatarBuffer(BasisAvatarBuffer avatarBuffer)
        {
            PayloadQueue.Enqueue(avatarBuffer);
        }

        public override void Initialize()
        {
            RemotePlayer = (BasisRemotePlayer)Player;
            if (RemotePlayer == null)
            {
                BasisDebug.LogError("Remote Player was not found During Initialization!!");
                return;
            }
            if (RemotePlayer.RemoteAvatarDriver == null)
            {
                BasisDebug.LogError("Remote Player RemoteAvatarDriver was not found During Initialization!!");
                return;
            }

            AudioReceiverModule.Initalize(this);

            _staged.Clear();
            BufferHolder.ClearAndRelease();
            interpolationTime = 0f;
            PassedSimulate = false;

            if (!hasEvents)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete += OnCalibration;
                hasEvents = true;
            }
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
            if (_staged != null)
            {
                int Count = _staged.Count;
                for (int i = 0; i < Count; i++)
                {
                    BasisAvatarBufferPool.Release(_staged[i]);
                }
                _staged.Clear();
            }

            while (PayloadQueue.TryDequeue(out var buffer))
            {
                BasisAvatarBufferPool.Release(buffer);
            }

            BufferHolder.ClearAndRelease();


            if (hasEvents && RemotePlayer != null && RemotePlayer.RemoteAvatarDriver != null)
            {
                RemotePlayer.RemoteAvatarDriver.CalibrationComplete -= OnCalibration;
                hasEvents = false;
            }

            AudioReceiverModule?.OnDestroy();
            PassedSimulate = false;
        }

        public void ReceiveNetworkAudio(ServerAudioSegmentMessage msg)
        {
            int serverSilentUnits = msg.audioSegmentData.TotalPlayedInSilence; // 20ms units

            if (AudioReceiverModule == null)
            {
                BasisDebug.LogError("Missing Audio Receiver for remote player!", BasisDebug.LogTag.Remote);
                return;
            }

            if (serverSilentUnits > 0)
            {
                int localUnits = System.Threading.Interlocked.Exchange(ref AudioReceiverModule._silentUnits20ms, 0);
                int missing = serverSilentUnits - localUnits;
                if (missing > 0)
                {
                    for (int i = 0; i < missing; i++)
                        AudioReceiverModule.OnDecodeSilence();
                }
            }

            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAudioSegment, msg.audioSegmentData.LengthUsed);
            AudioReceiverModule.OnDecode(msg.audioSegmentData.buffer, msg.audioSegmentData.LengthUsed);
            Player.AudioReceived?.Invoke();
        }

        public async void ReceiveAvatarChangeRequest(ServerAvatarChangeMessage SACM)
        {
            RemotePlayer.CACM = SACM.clientAvatarChangeMessage;
            BasisLoadableBundle bundle = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(SACM.clientAvatarChangeMessage.byteArray);
            await RemotePlayer.CreateAvatar(SACM.clientAvatarChangeMessage.loadMode, bundle);
        }

        public BasisNetworkReceiver(ushort PlayerID)
        {
            playerId = PlayerID;
            hasID = true;
        }

        // ---------------- staging/window management ----------------

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
                BasisDebug.LogWarning($"Staging was larger than 64; dropping {drop}");
            }
        }

        private void BuildOrAdvanceWindow()
        {
            // Seed First if missing
            if (!BufferHolder.HasCurrentBuffer)
                TrySeedFirstFromStaging();

            // Fill Last if missing
            if (!BufferHolder.HasNextBuffer)
                TrySetLastFromStaging();

            HasBufferHolds = BufferHolder.HasCurrentBuffer && BufferHolder.HasNextBuffer;
            if (!HasBufferHolds) return;

            // Advance window while consumed and we have staged frames
            while (interpolationTime >= 1f && _staged.Count != 0)
            {
                BufferHolder.NextBecomesCurrent();

                interpolationTime = 0f;

                TrySetLastFromStaging();

                if (!(BufferHolder.HasCurrentBuffer && BufferHolder.HasNextBuffer))
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
            while (_staged.Count > 0)
            {
                var first = _staged[0];
                _staged.RemoveAt(0);

                if (ValidateOrFixup(ref first))
                {
                    BufferHolder.SetCurrent(ref first);
                    BufferHolder.HasCurrentBuffer = true;
                    return;
                }

                BasisAvatarBufferPool.Release(first);
            }
        }

        private void TrySetLastFromStaging()
        {
            if (!BufferHolder.HasCurrentBuffer) return;

            while (_staged.Count > 0)
            {
                var last = _staged[0];
                _staged.RemoveAt(0);

                if (ValidateOrFixup(ref last))
                {
                    BufferHolder.SetNext(ref last);
                    BufferHolder.HasNextBuffer = true;
                    return;
                }

                BasisAvatarBufferPool.Release(last);
            }
        }

        private void DropOldestFromStaging(int count)
        {
            count = Mathf.Min(count, _staged.Count);
            for (int i = 0; i < count; i++)
            {
                BasisAvatarBufferPool.Release(_staged[i]);
            }
            _staged.RemoveRange(0, count);
        }

        // ---------------- validation / fixup ----------------

        /// <summary>
        /// True if the NativeArray is created and has at least 95 muscle values.
        /// </summary>
        private static bool IsValidMuscleArray(NativeArray<float> arr)
            => arr.IsCreated && arr.Length >= 95;

        /// <summary>
        /// Validates a buffer and attempts to repair fixable fields in-place.
        /// Returns true if usable after fixup; false if unrecoverable.
        /// </summary>
        private bool ValidateOrFixup(ref BasisAvatarBuffer buf)
        {
            if (!IsValidMuscleArray(buf.Muscles))
            {
                // Allocate a zeroed 95-length buffer so downstream SIMD/memcpy logic is safe.
                // NOTE: these must be freed by the pool when the buffer is released.
                buf.Muscles = new NativeArray<float>(95, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }

            // 2) Sanitize transforms to avoid NaNs propagating into the driver.
            if (!math.all(math.isfinite(buf.Position)))
            {
                BasisDebug.LogError($"Infinite Position Detected setting to default", BasisDebug.LogTag.Remote);
                buf.Position = float3.zero;
            }

            if (!math.all(math.isfinite(buf.Scale)))
            {
                BasisDebug.LogError($"Infinite Scale Detected setting to default", BasisDebug.LogTag.Remote);
                buf.Scale = new float3(1f, 1f, 1f);
            }

            // 3) Clamp insane timing
            if (!double.IsFinite(buf.SecondsInterval) || buf.SecondsInterval < 0.0 || buf.SecondsInterval > 1.0)
            {
                BasisDebug.LogError($"Seconds Interval was {buf.SecondsInterval} correcting to 0.016f", BasisDebug.LogTag.Remote);
                buf.SecondsInterval = 1.0 / 60.0;
            }

            return true;
        }
    }
}
