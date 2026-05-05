using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking.Behaviour;
using Basis.Network.Core;

namespace HVR.Basis.Comms
{
    internal class Transmitter : IHVRTransmitter
    {
        private readonly BasisNetworkAvatarBehaviour _behaviour;

        public Transmitter(BasisNetworkAvatarBehaviour behaviour)
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
