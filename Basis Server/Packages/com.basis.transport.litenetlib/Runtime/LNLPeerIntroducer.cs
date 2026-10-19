using System.Net;

namespace Basis.Network.Core
{
    public sealed class LNLPeerIntroducer : IPeerIntroducer
    {
        private readonly LiteNetLib.NetManager manager;
        private LiteNetLib.EventBasedNatPunchListener listener;

        public LNLPeerIntroducer(NetManager transport)
        {
            manager = transport?.LiteNetLibManager();
        }

        public bool Initialize(PeerIntroductionRequest onRequest)
        {
            if (manager == null || onRequest == null) return false;
            if (!manager.NatPunchEnabled)
            {
                BNL.LogWarning("[P2P] NatPunchEnabled=false in server config — direct peer connections will not work. Set NatPunchEnabled=true to enable.");
            }
            listener = new LiteNetLib.EventBasedNatPunchListener();
            listener.NatIntroductionRequest += (localEndPoint, remoteEndPoint, token) => onRequest(localEndPoint, remoteEndPoint, token);
            manager.NatPunchModule.Init(listener);
            manager.NatPunchModule.UnsyncedEvents = true;
            return true;
        }

        public void Introduce(IPEndPoint aInternal, IPEndPoint aExternal,
                              IPEndPoint bInternal, IPEndPoint bExternal,
                              bool predictPorts, string token)
        {
            int spray = predictPorts ? PredictionRange() : 0;
            manager?.NatPunchModule.NatIntroduce(aInternal, aExternal, spray, bInternal, bExternal, spray, token);
        }

        public void Shutdown()
        {
            listener = null;
        }

        private static int PredictionRange()
        {
            LNLTransportConfig config = BasisTransportConfigStore.Get<LNLTransportConfig>(BasisNetworkStackRegistry.LiteNetLibId);
            return config != null ? config.NatPortPredictionRange : 0;
        }
    }
}
