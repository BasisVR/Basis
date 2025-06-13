using Basis.Scripts.Behaviour;
using LiteNetLib;
using UnityEngine;
namespace Basis.Scripts.UGC.Animator
{
    public class BasisUGCAnimator : BasisAvatarMonoBehaviour
    {
        [SerializeField]
        public BasisUGCAnimatorItem[] AnimatorItems;

        public override void OnNetworkChange(byte messageIndex, bool IsLocallyOwned)
        {

        }

        public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
        { 
        }

        public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {

        }
    }
}
