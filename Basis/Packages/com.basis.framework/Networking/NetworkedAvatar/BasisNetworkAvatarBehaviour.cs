using Basis.Network.Core;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking.NetworkedAvatar;

namespace Basis.Scripts.Networking.Behaviour
{
    public abstract class BasisNetworkAvatarBehaviour : BasisAvatarMonoBehaviour
    {
        //  [HideInInspector]
        public BasisNetworkPlayer NetworkedPlayer;

        public void OnNetworkAssign(byte messageIndex, BasisNetworkPlayer Player)
        {
            MessageIndex = messageIndex;
            NetworkedPlayer = Player;
            IsInitalized = true;
            OnNetworkReady(Player.IsLocal);
        }

        public virtual void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
         //   BasisDebug.LogError("Data was Received but nothing interpreted it! OnNetworkMessageReceived", this.gameObject, BasisDebug.LogTag.Avatar);
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
                NetworkedPlayer.OnAvatarNetworkMessageSend(MessageIndex, buffer, DeliveryMethod, Recipients);
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
                NetworkedPlayer.OnAvatarNetworkMessageSend(MessageIndex, null, DeliveryMethod);
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
                NetworkedPlayer.OnAvatarServerReductionSystemMessageSend(MessageIndex, buffer);
            }
            else
            {
                BasisDebug.LogError("Network Is Not Ready!", this.gameObject, BasisDebug.LogTag.Avatar);
            }
        }
    }
}
