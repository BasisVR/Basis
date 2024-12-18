using Basis.Network.Core;
using Basis.Scripts.Networking.Compression;
using Basis.Scripts.Networking.NetworkedPlayer;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using static BasisNetworkPrimitiveCompression;
using static SerializableBasis;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    /// <summary>
    /// the goal of this script is to be the glue of consistent data between remote and local
    /// </summary>
    public abstract class BasisNetworkSendBase : MonoBehaviour
    {
        public bool Ready;
        public BasisNetworkedPlayer NetworkedPlayer;
        private readonly object _lock = new object(); // Lock object for thread-safety
        private bool _hasReasonToSendAudio;
        public int Offset = 0;
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
        public static BasisRangedUshortFloatData RotationCompression = new BasisRangedUshortFloatData(-1f, 1f, 0.001f);
        [SerializeField]
        public HumanPose HumanPose = new HumanPose();
        [SerializeField]
        public HumanPoseHandler PoseHandler;
        [SerializeField]
        public static BasisRangedUshortFloatData PositionRanged = new BasisRangedUshortFloatData(-BasisNetworkConstants.MaxPosition, BasisNetworkConstants.MaxPosition, BasisNetworkConstants.PositionPrecision);
        [SerializeField]
        public static BasisRangedUshortFloatData ScaleRanged = new BasisRangedUshortFloatData(BasisNetworkConstants.MinimumScale, BasisNetworkConstants.MaximumScale, BasisNetworkConstants.ScalePrecision);
        public const int SizeAfterGap = 95 - SecondBuffer;
        public const int FirstBuffer = 15;
        public const int SecondBuffer = 21;
        public abstract void Initialize(BasisNetworkedPlayer NetworkedPlayer);
        public abstract void DeInitialize();
        public void OnAvatarCalibration()
        {
            if (BasisNetworkManagement.MainThreadContext == null)
            {
                Debug.LogError("Main thread context is not set. Ensure this script is started on the main thread.");
                return;
            }

            // Post the task to the main thread
            BasisNetworkManagement.MainThreadContext.Post(_ =>
            {
                if (NetworkedPlayer != null && NetworkedPlayer.Player != null && NetworkedPlayer.Player.Avatar != null)
                {

                    ComputeHumanPose();
                    if (!NetworkedPlayer.Player.Avatar.HasSendEvent)
                    {
                        NetworkedPlayer.Player.Avatar.OnNetworkMessageSend += OnNetworkMessageSend;
                        NetworkedPlayer.Player.Avatar.HasSendEvent = true;
                    }

                    NetworkedPlayer.Player.Avatar.LinkedPlayerID = NetworkedPlayer.NetId;
                    NetworkedPlayer.Player.Avatar.OnAvatarNetworkReady?.Invoke();
                }
            }, null);
        }
        public void ComputeHumanPose()
        {
            if (NetworkedPlayer != null && NetworkedPlayer.Player != null && NetworkedPlayer.Player.Avatar != null)
            {
                PoseHandler = new HumanPoseHandler(
                NetworkedPlayer.Player.Avatar.Animator.avatar,
                NetworkedPlayer.Player.Avatar.transform
            );
                PoseHandler.GetHumanPose(ref HumanPose);
            }
        }
        private void OnNetworkMessageSend(byte MessageIndex, byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Sequenced, ushort[] Recipients = null)
        {
            NetDataWriter netDataWriter = new NetDataWriter();
            // Handle cases based on presence of Recipients and buffer
            AvatarDataMessage AvatarDataMessage = new AvatarDataMessage
            {
                messageIndex = MessageIndex,
                payload = buffer,
                recipients = Recipients
            };
            AvatarDataMessage.Serialize(netDataWriter);
            // Recipients present, payload may or may not be present
            WriteAndSendMessage(netDataWriter, DeliveryMethod);
        }

        // Helper method to avoid code duplication
        private void WriteAndSendMessage(NetDataWriter writer, DeliveryMethod deliveryMethod)
        {
            BasisNetworkManagement.LocalPlayerPeer.Send(writer, BasisNetworkCommons.AvatarChannel, deliveryMethod);
        }

    }
}