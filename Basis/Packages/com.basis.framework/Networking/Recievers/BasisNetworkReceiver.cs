using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private const int EyesAndMouthCount = 6;  // number of floats to copy
        public const int EyeAndMouthSize = EyesAndMouthOffset * sizeof(float); // bytes
        public const int EyeAndMouthcount = EyesAndMouthCount * sizeof(float); // bytes

        /// <summary>
        /// If more than this many frames are queued, old frames will be dropped to catch up.
        /// </summary>
        public static int BufferCapacityBeforeCleanup = 5;

        public BasisRemoteBoneControl MouthBone;

        [SerializeField]
        public BasisAudioReceiver AudioReceiverModule = new BasisAudioReceiver();

        [SerializeField]
        public ConcurrentQueue<BasisAvatarBuffer> PayloadQueue = new ConcurrentQueue<BasisAvatarBuffer>();

        public BasisRemotePlayer RemotePlayer;

        [SerializeField]
        public BasisRemoteAvatarBufferHolder BufferHolder = new BasisRemoteAvatarBufferHolder();

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

        // Shared zero array for safety
        private static readonly float[] ZeroMuscles = new float[95];

        // ---------- Compute / Apply ----------

        /// <summary>
        /// Called from your network simulation (main thread).
        /// Pulls data to staging, builds/advances the interpolation window,
        /// computes the fraction using SecondsInterval, and pushes inputs to the driver.
        /// </summary>
        public void Compute()
        {
            // 1) Pull network packets to main-thread staging
            PumpQueueToStaging();

            // 2) Ensure we have a valid interpolation window (First -> Last)
            BuildOrAdvanceWindow();

            // 3) If we have a window, compute interpolation fraction and feed the compute phase
            if (BufferHolder.HasFirst && BufferHolder.HasLast)
            {
                ComputeInterpolationFraction();

                var first = BufferHolder.First;
                var last = BufferHolder.Last;

                // Ensure muscles are non-null and correct length
                var prevMuscles = first.Muscles;
                var targetMuscles = last.Muscles;

                if (!IsValidMuscleArray(prevMuscles))
                {
                    if (LogFirstError)
                        BasisDebug.LogWarning("BasisNetworkReceiver: First frame muscles were null/invalid; using zeros.");
                    prevMuscles = ZeroMuscles;
                }

                if (!IsValidMuscleArray(targetMuscles))
                {
                    if (LogFirstError)
                        BasisDebug.LogWarning("BasisNetworkReceiver: Last frame muscles were null/invalid; using zeros.");
                    targetMuscles = ZeroMuscles;
                }

                // Feed driver
                BasisRemoteNetworkDriver.SetInputs(
                    playerId,
                    first.Position, last.Position,
                    first.Scale, last.Scale,
                    first.rotation, last.rotation,
                    interpolationTime,
                    prevMuscles, targetMuscles
                );
            }
        }

        public void Apply()
        {
            if (BufferHolder.HasFirst && BufferHolder.HasLast)
            {
                if (BasisRemoteNetworkDriver.GetOutputs(playerId, out ApplyingPosition, out ApplyingScale, out ApplyingRotation, ref Muscles))
                {
                    ApplyComputedData();
                }
            }
        }

        public void ApplyComputedData()
        {
            // Inline what ApplyPoseData used to do
            Transform AnimatorsTransform = Player.AvatarTransform;
            float3 Scaling = Player.BasisAvatar.AnimatorHumanScale;
            float3 Scale = ApplyingScale;
            float3 Position = ApplyingPosition;
            Quaternion Rotation = ApplyingRotation;
            float[] MusclesLocal = Muscles ?? ZeroMuscles;

            // Guard scale to avoid NaNs / zero
            Scale = SanitizeScale(Scale);
            Scaling = SafeDivide(Scaling, Scale);

            // Body transform
            HumanPose.bodyPosition = Vector3.Scale(Position, Scaling);
            HumanPose.bodyRotation = Rotation;

            // Muscles (95)
            if (!IsValidMuscleArray(HumanPose.muscles))
            {
                // HumanPose.muscles must exist & be 95; if the engine ever gives us less, bail safely
                BasisDebug.LogError("BasisNetworkReceiver: HumanPose.muscles is invalid; aborting muscle copy this frame.");
            }
            else
            {
                Array.Copy(MusclesLocal, HumanPose.muscles, 95);

                // Eyes/Mouth overlay — only if we have enough space and source
                if (HumanPose.muscles.Length >= (EyesAndMouthOffset + EyesAndMouthCount)
                    && EyesAndMouth != null && EyesAndMouth.Length >= EyesAndMouthCount)
                {
                    Buffer.BlockCopy(EyesAndMouth, 0, HumanPose.muscles, EyeAndMouthSize, EyeAndMouthcount);
                }
            }

            AnimatorsTransform.localScale = Scale;
            PoseHandler.SetHumanPose(ref HumanPose);

            RemotePlayer.RemoteBoneDriver.SimulateAndApplyRemote(ApplyingScale);

            if (AudioReceiverModule.HasTransform)
            {
                var outgoing = RemotePlayer.RemoteBoneDriver.Mouth.OutGoingData;
               //AudioReceiverModule.AudioSourceTransform.SetPositionAndRotation(outgoing.position, outgoing.rotation);
                BasisAudioTransformDriver.EnqueueSet(AudioReceiverModule.AudioSourceTransform, outgoing.position, outgoing.rotation);
            }

            if (RemotePlayer.HasRemoteNamePlate)
            {
                RemotePlayer.RemoteNamePlate.Simulate();
            }
        }

        public static float3 SafeDivide(float3 a, float3 b, float epsilon = 1e-5f)
        {
            return new float3(
                math.abs(b.x) > epsilon ? a.x / b.x : a.x,
                math.abs(b.y) > epsilon ? a.y / b.y : a.y,
                math.abs(b.z) > epsilon ? a.z / b.z : a.z);
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
            Player.BasisAvatar.AnimatorHumanScale = Vector3.one / Player.BasisAvatar.Animator.humanScale;
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
                        message.Value.Method,
                        false
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
          //no need we pump data always before requesting so its not a necessary step
          //BasisRemoteNetworkDriver.ResetIndex(playerId);
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
            while (interpolationTime >= 1f && _staged.Count > 0)
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

        private void ComputeInterpolationFraction()
        {
            var first = BufferHolder.First;
            var last = BufferHolder.Last;

            double windowDuration =
                last.SecondsInterval > 0 ? last.SecondsInterval :
                first.SecondsInterval > 0 ? first.SecondsInterval :
                (1.0 / 60.0);

            // Clamp to sane floor to avoid huge dt spikes dividing by tiny intervals
            if (windowDuration <= 1e-6) windowDuration = 1e-3;

            double step = Math.Max(Time.unscaledDeltaTime, 0.0);
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

        private static bool IsValidMuscleArray(float[] arr) => arr != null && arr.Length >= 95;

        private static bool IsFinite(float3 v) =>
            math.isfinite(v.x) && math.isfinite(v.y) && math.isfinite(v.z);

        private static float3 SanitizeScale(float3 s)
        {
            // Treat non-finite or near-zero as 1
            const float eps = 1e-4f;
            if (!IsFinite(s))
                return new float3(1, 1, 1);

            return new float3(
                math.abs(s.x) < eps ? 1f : s.x,
                math.abs(s.y) < eps ? 1f : s.y,
                math.abs(s.z) < eps ? 1f : s.z
            );
        }

        private static quaternion SanitizeRotation(quaternion q)
        {
            // If not finite or nearly zero length, use identity
            if (!math.isfinite(q.value.x) || !math.isfinite(q.value.y) || !math.isfinite(q.value.z) || !math.isfinite(q.value.w))
                return quaternion.identity;

            float magSq = q.value.x * q.value.x + q.value.y * q.value.y + q.value.z * q.value.z + q.value.w * q.value.w;
            if (magSq < 1e-8f) return quaternion.identity;
            return math.normalize(q);
        }

        /// <summary>
        /// Validates a buffer; attempts to repair fixable fields in-place.
        /// Returns true if usable after fixup; false if unrecoverable.
        /// </summary>
        private bool ValidateOrFixup(ref BasisAvatarBuffer buf)
        {
            // Muscles
            if (!IsValidMuscleArray(buf.Muscles))
            {
                // Replace with shared zeros to keep pose valid; we still accept the frame
                buf.Muscles = ZeroMuscles;
            }

            // Scale
            buf.Scale = SanitizeScale(buf.Scale);

            // Position
            if (!IsFinite(buf.Position))
            {
                // If position broke, keep last known good (0 if none)
                buf.Position = float3.zero;
            }

            // Rotation
            buf.rotation = SanitizeRotation(buf.rotation);

            // Seconds interval — clamp to a sane minimum so interpolation works
            if (!math.isfinite((float)buf.SecondsInterval) || buf.SecondsInterval <= 0)
                buf.SecondsInterval = 1.0 / 60.0;

            // If we got here, the frame is usable
            return true;
        }
    }
}
