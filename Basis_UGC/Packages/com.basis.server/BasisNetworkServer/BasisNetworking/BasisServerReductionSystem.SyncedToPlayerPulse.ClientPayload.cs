using LiteNetLib;
public partial class BasisServerReductionSystem
{
public partial class SyncedToPlayerPulse
    {
        public struct ClientPayload
        {
            public NetPeer localClient;
            public int dataCameFromThisUser;
        }
    }
}
