using Basis.Scripts.Networking.NetworkedAvatar;
using LiteNetLib;
using UnityEngine;
namespace Basis.Scripts.Behaviour
{
    public abstract class BasisAvatarMonoBehaviour : MonoBehaviour
    {
        // [HideInInspector]
        public bool IsInitalized = false;
        // [HideInInspector]
        public byte MessageIndex;
        //  [HideInInspector]
        public BasisNetworkPlayer NetworkedPlayer;
        public void OnNetworkAssign(byte messageIndex, BasisNetworkPlayer Player)
        {
            MessageIndex = messageIndex;
            NetworkedPlayer = Player;
            IsInitalized = true;
            OnNetworkReady(messageIndex, Player.IsLocal);
        }
        public virtual void OnNetworkReady(byte messageIndex, bool IsLocallyOwned)
        {

        }
        public virtual void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
            BasisDebug.LogError("Data was Received but nothing interpreted it! OnNetworkMessageReceived", this.gameObject, BasisDebug.LogTag.Avatar);
        }
        /// <summary>
        /// data that came out of the server reduction system
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="IsADifferentAvatarLocally">Indicates if the avatar worn matches or not</param>
        public virtual void OnNetworkMessageServerReductionSystem(byte[] buffer, bool IsADifferentAvatarLocally)
        {
            BasisDebug.LogError("Data was Received but nothing interpreted it! OnNetworkMessageServerReductionSystem", this.gameObject, BasisDebug.LogTag.Avatar);
        }
        /// <summary>
        /// this is used for sending Network Messages
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="DeliveryMethod"></param>
        /// <param name="Recipients">if null everyone but self, you can include yourself to make it loop back over the network</param>
        public void NetworkMessageSend(byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable, ushort[] Recipients = null)
        {
            if (IsInitalized)
            {
                NetworkedPlayer.OnNetworkMessageSend(MessageIndex, buffer, DeliveryMethod, Recipients);
            }
            else
            {
                BasisDebug.LogError("Network Is Not Ready!", this.gameObject, BasisDebug.LogTag.Avatar);
            }
        }
        /// <summary>
        /// this is used for sending Network Messages
        /// </summary>
        /// <param name="DeliveryMethod"></param>
        public void NetworkMessageSend(DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable)
        {
            if (IsInitalized)
            {
                NetworkedPlayer.OnNetworkMessageSend(MessageIndex, null, DeliveryMethod);
            }
            else
            {
                BasisDebug.LogError("Network Is Not Ready!", this.gameObject, BasisDebug.LogTag.Avatar);
            }
        }
        public void ServerReductionSystemMessageSend(byte[] buffer = null)
        {
            if (IsInitalized)
            {
                NetworkedPlayer.OnServerReductionSystemMessageSend(MessageIndex, buffer);
            }
            else
            {
                BasisDebug.LogError("Network Is Not Ready!", this.gameObject, BasisDebug.LogTag.Avatar);
            }
        }
    }
}
