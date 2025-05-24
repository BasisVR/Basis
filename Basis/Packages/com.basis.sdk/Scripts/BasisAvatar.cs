using LiteNetLib;
using UnityEngine;

namespace Basis.Scripts.BasisSdk
{
    public class BasisAvatar : BasisContentBase
    {
        public Animator Animator;
        public SkinnedMeshRenderer FaceVisemeMesh;
        public SkinnedMeshRenderer FaceBlinkMesh;
        public Vector2 AvatarEyePosition;
        public Vector2 AvatarMouthPosition;
        public int[] FaceVisemeMovement = new int[] { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
        public int[] BlinkViseme = new int[] { -1 };
        public int laughterBlendTarget = -1;

        private ushort linkedPlayerID;
        public bool HasLinkedPlayer { get; private set; } = false;
        public OnNetworkReady OnAvatarNetworkReady;
        /// <summary>
        /// this is called when the owner of this gameobject is ready for you to request data about it
        /// </summary>
        public delegate void OnNetworkReady(bool IsOwner);
        public bool IsOwnedLocally;
        // Property for LinkedPlayerID with logic to set HasLinkedPlayer
        public ushort LinkedPlayerID
        {
            get => linkedPlayerID;
            set
            {
                linkedPlayerID = value;
                HasLinkedPlayer = true;
            }
        }

        // Try to set the linked player
        public bool TryGetLinkedPlayer(out ushort Id)
        {
            if (HasLinkedPlayer)
            {
                Id = LinkedPlayerID;
                return true;
            }
            else
            {
                Id = 0;
            }
            return false;
        }

        [SerializeField]
        public Renderer[] Renders;
        [HideInInspector]
        [SerializeField]
        public BasisJiggleStrain[] JiggleStrains;
        public bool HasSendEvent;
        public AvatarNetworkMessageSendEventServerReductionSystem OnServerReductionSystemMessageReceived;
        public AvatarNetworkMessageReceiveEvent OnNetworkMessageReceived;
        public AvatarNetworkMessageSendEvent OnNetworkMessageSend;
        public AvatarNetworkMessageSendEventServerReductionSystem OnServerReductionSystemMessageSend;

        public OnReady OnAvatarReady;
        /// <summary>
        /// this is called when the owner of this gameobject is ready for you to request data about it
        /// </summary>
        public delegate void OnReady(bool IsOwner);
        /// <summary>
        /// this is used for sending Network Messages
        /// </summary>
        /// <param name="MessageIndex"></param>
        /// <param name="buffer"></param>
        /// <param name="DeliveryMethod"></param>
        /// <param name="Recipients">if null everyone but self, you can include yourself to make it loop back over the network</param>
        public void NetworkMessageSend(byte MessageIndex, byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable, ushort[] Recipients = null)
        {
            OnNetworkMessageSend?.Invoke(MessageIndex, buffer, DeliveryMethod, Recipients);
        }
        /// <summary>
        /// this is used for sending Network Messages
        /// </summary>
        /// <param name="MessageIndex"></param>
        /// <param name="DeliveryMethod"></param>
        public void NetworkMessageSend(byte MessageIndex, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable)
        {
            OnNetworkMessageSend?.Invoke(MessageIndex, null, DeliveryMethod);
        }
        public void ServerReductionSystemMessageSend(byte MessageIndex, byte[] buffer = null)
        {
            OnServerReductionSystemMessageSend?.Invoke(MessageIndex,buffer);
        }
        /// <summary>
        /// this is used for Receiving Network Messages
        /// </summary>
        /// <param name="MessageIndex"></param>
        /// <param name="buffer"></param>
        public delegate void AvatarNetworkMessageReceiveEvent(ushort PlayerID, byte MessageIndex, byte[] buffer, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable);


        /// <summary>
        /// this is used for sending Network Messages
        /// </summary>
        /// <param name="MessageIndex"></param>
        /// <param name="buffer"></param>
        /// <param name="DeliveryMethod"></param>
        /// <param name="Recipients">if null everyone but self, you can include yourself to make it loop back over the network</param>
        public delegate void AvatarNetworkMessageSendEvent(byte MessageIndex, byte[] buffer, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable, ushort[] Recipients = null);

        /// <summary>
        /// this is used for sending Network Messages with mode sequence
        /// is delivered through the avatar reduction system.
        /// </summary>
        /// <param name="MessageIndex"></param>
        /// <param name="buffer"></param>
        public delegate void AvatarNetworkMessageSendEventServerReductionSystem(byte MessageIndex, byte[] buffer);
    }
}
