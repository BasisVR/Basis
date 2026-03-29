using Basis.Network.Core;

namespace Basis.Scripts.Networking.Steam
{
    public static class BasisTransportFactory
    {
        public static NetManager Create(EventBasedNetListener listener, Configuration configuration)
        {
            switch (configuration.TransportType)
            {
                case NetworkTransportType.Steam:
                    return new SteamNetManager(listener, configuration);
                case NetworkTransportType.LiteNetLib:
                default:
                    return new LNLNetManager(listener, configuration);
            }
        }
    }
}
