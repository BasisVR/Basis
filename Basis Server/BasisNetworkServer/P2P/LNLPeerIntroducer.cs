using Basis.Network.Core;
using System.Net;

namespace BasisNetworkServer
{
    public sealed class LNLPeerIntroducer : IPeerIntroducer
    {
        public bool Initialize(NetManager activeManager)
        {
            BasisServerP2PBroker.Initialize();
            return true;
        }

        public void Introduce(IPEndPoint aInternal, IPEndPoint aExternal,
                              IPEndPoint bInternal, IPEndPoint bExternal,
                              string token)
        {
            LiteNetLib.NetManager lnl = NetworkServer.Server?.LiteNetLibManager();
            if (lnl == null) return;
            lnl.NatPunchModule.NatIntroduce(aInternal, aExternal, bInternal, bExternal, token);
        }

        public bool IsPairOffloaded(int peerIdA, int peerIdB)
            => BasisServerP2PBroker.IsP2POffloaded(peerIdA, peerIdB);

        public void Shutdown() { }
    }
}
