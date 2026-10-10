using System.Net;

namespace Basis.Network.Core
{
    public delegate void PeerIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token);

    public interface IPeerIntroducer
    {
        bool Initialize(PeerIntroductionRequest onRequest);
        void Introduce(IPEndPoint aInternal, IPEndPoint aExternal,
                       IPEndPoint bInternal, IPEndPoint bExternal,
                       bool predictPorts, string token);
        void Shutdown();
    }
}
