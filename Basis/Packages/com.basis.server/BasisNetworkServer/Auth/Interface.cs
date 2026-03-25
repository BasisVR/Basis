using Basis.Network.Core;
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
        public void DeInitialize();
        public void RemoveConnection(int NetPeer);
        public bool NetIDToUUID(NetPeer Peer, out string UUID);
        public bool UUIDToNetID(string UUID, out int Peer);

        public static bool HasFileSupport = false;
    }
}
