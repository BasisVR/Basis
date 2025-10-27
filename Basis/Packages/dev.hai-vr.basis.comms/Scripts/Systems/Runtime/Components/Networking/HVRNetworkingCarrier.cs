using System;
using Basis.Scripts.Behaviour;
using Basis.Network.Core;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Internal/HVR Networking Carrier")]
    [HelpURL("https://docs.hai-vr.dev/docs/basis/avatar-customization")]
    public class HVRNetworkingCarrier : BasisAvatarMonoBehaviour, IHVRTransmitter, IHVRInitializable
    {
        private bool _networkReady;

        private HVRAvatarComms _comms;

        [NonSerialized] public int index;

        public void Awake()
        {
            _comms = HVRCommsUtil.GetAvatar(this).GetComponentInChildren<HVRAvatarComms>(true);
            if(_comms == null)
            {
                BasisDebug.LogError("missing Comms Component network transmission of data for HVR related systems");
                _networkReady = false;
            }
        }

        public override void OnNetworkMessageReceived(ushort remoteUser, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (!_networkReady) return;

            _comms.WhenNetworkMessageReceived(index, remoteUser, buffer, deliveryMethod);
        }

        public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {
            if (!_networkReady) return;

            _comms.WhenNetworkMessageServerReductionSystem(index, buffer);
        }

        public void OnHVRAvatarReady(bool isWearer)
        {
        }

        public void OnHVRReadyBothAvatarAndNetwork(bool isWearer)
        {
            _networkReady = true;
        }
    }
}
