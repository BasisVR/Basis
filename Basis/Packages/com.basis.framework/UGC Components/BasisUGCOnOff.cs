using Basis.Scripts.Behaviour;
using LiteNetLib;
using UnityEngine;
namespace Basis.Scripts.UGC.OnOff
{
    public class BasisUGCOnOff : BasisAvatarMonoBehaviour
    {
        [SerializeField]
        public BasisUGCOnOffItem[] OffOnItems;

        public override void OnNetworkReady(byte messageIndex, bool IsLocallyOwned)
        {

        }

        public override void OnNetworkMessageReceived(ushort RemoteUser, byte[] buffer, DeliveryMethod DeliveryMethod)
        {

        }

        public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {

        }

        [System.Serializable]
        public struct BasisUGCOnOffItem
        {
            public BasisUGCMenuDescription Description;
            public GameObject[] ToggleableGameObjects;
            public Component[] ToggleableComponents;
        }
    }
}
