using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Threading;
using UnityEngine;
using static BasisNetworkPrimitiveCompression;
using static SerializableBasis;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    /// <summary>
    /// the goal of this script is to be the glue of consistent data between remote and local
    /// </summary>
    [System.Serializable]
    public abstract class BasisNetworkPlayer
    {
        private readonly object _lock = new object(); // Lock object for thread-safety
        private bool _hasReasonToSendAudio;
        public static BasisRangedUshortFloatData RotationCompression = new BasisRangedUshortFloatData(-1f, 1f, 0.001f);
        [SerializeField]
        public HumanPose HumanPose = new HumanPose();
        [SerializeField]
        public HumanPoseHandler PoseHandler;
        public BasisBoneControl MouthBone;
        public BasisPlayer Player;
        [SerializeField]
        public PlayerIdMessage PlayerIDMessage;
        public bool hasID = false;
        public bool HasReasonToSendAudio
        {
            get
            {
                lock (_lock)
                {
                    return _hasReasonToSendAudio;
                }
            }
            set
            {
                lock (_lock)
                {
                    _hasReasonToSendAudio = value;
                }
            }
        }
        public ushort NetId
        {
            get
            {
                if (hasID)
                {
                    return PlayerIDMessage.playerID;
                }
                else
                {
                    BasisDebug.LogError("Missing Network ID!");
                    return 0;
                }
            }
        }
        public abstract void Initialize();
        public abstract void DeInitialize();
        public void OnAvatarCalibrationLocal()
        {
            OnAvatarCalibration();
        }
        public void OnAvatarCalibrationRemote()
        {
            OnAvatarCalibration();
        }
        public void OnAvatarCalibration()
        {
            if (IsMainThread())
            {
                AvatarCalibrationSetup();
            }
            else
            {
                if (BasisNetworkManagement.MainThreadContext == null)
                {
                    BasisDebug.LogError("Main thread context is not set. Ensure this script is started on the main thread.");
                    return;
                }

                // Post the task to the main thread
                BasisNetworkManagement.MainThreadContext.Post(_ =>
                {
                    AvatarCalibrationSetup();
                }, null);
            }
        }
        public static bool IsMainThread()
        {
            // Check if the current synchronization context matches the main thread's context
            return SynchronizationContext.Current == BasisNetworkManagement.MainThreadContext;
        }

        public void AvatarCalibrationSetup()
        {
            if (CheckForAvatar())
            {
                BasisAvatar basisAvatar = Player.BasisAvatar;
                // All checks passed
                PoseHandler = new HumanPoseHandler(
                    basisAvatar.Animator.avatar,
                    Player.BasisAvatarTransform
                );
                PoseHandler.GetHumanPose(ref HumanPose);
                if (!basisAvatar.HasSendEvent)
                {
                    basisAvatar.OnNetworkMessageSend += OnNetworkMessageSend;
                    basisAvatar.OnServerReductionSystemMessageSend += OnServerReductionSystemMessageSend;
                    basisAvatar.HasSendEvent = true;
                }

                basisAvatar.LinkedPlayerID = NetId;
                basisAvatar.OnAvatarNetworkReady?.Invoke(Player.IsLocal);
            }
        }
        public bool CheckForAvatar()
        {
            if (Player == null)
            {
                BasisDebug.LogError("NetworkedPlayer.Player is null! Cannot compute HumanPose.");
                return false;
            }

            if (Player.BasisAvatar == null)
            {
                BasisDebug.LogError("BasisAvatar is null! Cannot compute HumanPose.");
                return false;
            }
            return true;
        }
        private void OnServerReductionSystemMessageSend(byte MessageIndex, byte[] buffer = null)
        {
            if (BasisNetworkManagement.Instance != null && BasisNetworkManagement.Instance.Transmitter != null)
            {
                AdditionalAvatarData AAD = new AdditionalAvatarData
                {
                    array = buffer,
                    messageIndex = MessageIndex
                };
                BasisNetworkManagement.Instance.Transmitter.AddAdditional(AAD);
            }
            else
            {
                BasisDebug.LogError("Missing Transmitter or Network Management", BasisDebug.LogTag.Networking);
            }
        }
        private void OnNetworkMessageSend(byte MessageIndex, byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Sequenced, ushort[] Recipients = null)
        {
            // Handle cases based on presence of Recipients and buffer
            AvatarDataMessage AvatarDataMessage = new AvatarDataMessage
            {
                messageIndex = MessageIndex,
                payload = buffer,
                recipients = Recipients,
                PlayerIdMessage = new PlayerIdMessage() { playerID = NetId },
            };
            NetDataWriter netDataWriter = new NetDataWriter();
            if (DeliveryMethod == DeliveryMethod.Unreliable)
            {
                netDataWriter.Put(BasisNetworkCommons.AvatarChannel);
                AvatarDataMessage.Serialize(netDataWriter);
                BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.FallChannel, DeliveryMethod);
            }
            else
            {
                AvatarDataMessage.Serialize(netDataWriter);
                BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AvatarChannel, DeliveryMethod);
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AvatarDataMessage, netDataWriter.Length);
        }
       
        public void ProvideNetworkKey(ushort PlayerID)
        {
            PlayerIDMessage.playerID = PlayerID;
            hasID = true;
        }
    }
}
