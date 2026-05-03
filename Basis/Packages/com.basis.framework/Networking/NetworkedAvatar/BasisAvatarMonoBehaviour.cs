using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.BasisSdk;
using Basis.Network.Core;
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
            OnNetworkReady(Player.IsLocal);
        }
        public virtual void OnNetworkReady(bool IsLocallyOwned)
        {

        }
        public virtual void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
         //   BasisDebug.LogError("Data was Received but nothing interpreted it! OnNetworkMessageReceived", this.gameObject, BasisDebug.LogTag.Avatar);
        }
        /// <summary>
        /// data that came out of the server reduction system
        /// </summary>
        /// <param name="buffer"></param>
        public virtual void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {
           // BasisDebug.LogError("Data was Received but nothing interpreted it! OnNetworkMessageServerReductionSystem", this.gameObject, BasisDebug.LogTag.Avatar);
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
        /// <summary>
        /// Whether this behaviour type is visible in the Avatar SDK inspector's
        /// Network Behaviours section. Subclasses shadow with <c>new public static bool VisibleInAvatarMenu = false;</c> to hide.
        /// The inspector reads this per-type via reflection.
        /// </summary>
        public static bool VisibleInAvatarMenu = true;
#if UNITY_EDITOR
        /// <summary>
        /// Called in-editor when this component is added via the Avatar SDK inspector.
        /// Override to auto-configure serialized references (e.g., target meshes).
        /// Stripped from player builds.
        /// </summary>
        /// <param name="avatarRoot">The GameObject containing the BasisAvatar component.</param>
        /// <param name="avatar">The BasisAvatar component on the root.</param>
        public virtual void OnEditorSetup(GameObject avatarRoot, BasisAvatar avatar) { }
#endif
    }
}
