using Basis.Network.Core;

namespace Basis.Scripts.Networking
{
    public static class BasisNetworkHandleCustomServerData
    {
        public delegate void CustomServerDataMessageDelegate(byte[] buffer, DeliveryMethod deliveryMethod);
        public static event CustomServerDataMessageDelegate OnCustomServerDataMessageReceived;

        public static void HandleMessage(NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            if (OnCustomServerDataMessageReceived == null) return;
            
            try
            {
                byte[] data = reader.GetRemainingBytes();
                OnCustomServerDataMessageReceived?.Invoke(data, deliveryMethod);
            }
            finally
            {
                reader.Recycle();
            }
        }
    }
}
