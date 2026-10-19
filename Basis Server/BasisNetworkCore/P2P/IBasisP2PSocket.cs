using System.Net;

namespace Basis.Network.Core
{
    public delegate void BasisP2PIntroduced(IPEndPoint target, bool sameNetwork, string token);

    public interface IBasisP2PSocket
    {
        int LocalPort { get; }
        bool Start();
        void Stop();
        void RequestIntroduction(string serverHost, int serverPort, string token);
        NetPeer Connect(IPEndPoint endPoint, NetDataWriter connectData);
        void SetEndpointKeys(IPEndPoint endPoint, byte[] sendKey, byte[] recvKey, long initialSendCounter);
        void RemoveEndpoint(IPEndPoint endPoint);
    }
}
