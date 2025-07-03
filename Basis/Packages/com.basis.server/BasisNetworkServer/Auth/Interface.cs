using LiteNetLib;
namespace Basis.Network.Server.Auth
{
    /// <summary>
    /// class use to see if we can authenticate.
    /// (password correct)
    /// </summary>
    public interface IAuth
    {
        public bool IsAuthenticated(byte[] BytesMsg);
    }
    public interface IAuthIdentity
    {
        /// <summary>
        /// class we use to get the users identity
        /// the UUID of a player will become this.
        public void ProcessConnection(Configuration Configuration, ConnectionRequest ConnectionRequest, NetPeer NetPeer);
        public void DeInitalize();
        public void RemoveConnection(NetPeer NetPeer);
        public bool IsNetPeerAdmin(string UUID);
        public bool AddNetPeerAsAdmin(string UUID);
        public bool RemoveNetPeerAsAdmin(string UUID);
        public bool NetIDToUUID(NetPeer Peer, out string UUID);
        public bool UUIDToNetID(string UUID, out NetPeer Peer);

        public static bool HasFileSupport = false;
    }
}
