using System;

namespace HVR.Basis.Comms
{
    public interface IFeatureReceiver
    {
        void OnPacketReceived(byte localIdentifier, ArraySegment<byte> data);
        void OnResyncEveryoneRequested();
        void OnResyncRequested(ushort[] whoAsked);
    }
}
