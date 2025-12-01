using Basis.Scripts.Behaviour;
using Basis.Network.Core;
using UnityEngine;

namespace HVR.Basis.Comms
{
    internal class Transmitter : IHVRTransmitter
    {
        private readonly BasisAvatarMonoBehaviour _behaviour;

        public Transmitter(BasisAvatarMonoBehaviour behaviour)
        {
            _behaviour = behaviour;
        }

        public void NetworkMessageSend(byte[] buffer = null, DeliveryMethod deliveryMethod = DeliveryMethod.Unreliable, ushort[] recipients = null)
        {
            _behaviour.NetworkMessageSend(buffer, deliveryMethod, recipients);
        }

        public void ServerReductionSystemMessageSend(byte[] buffer = null)
        {
            _behaviour.ServerReductionSystemMessageSend(buffer);
        }
    }
}
