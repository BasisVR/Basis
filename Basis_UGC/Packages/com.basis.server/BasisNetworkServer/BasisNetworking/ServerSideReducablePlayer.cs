using System.Threading;
using Basis.Scripts.Networking.Compression;
using LiteNetLib.Utils;
using static SerializableBasis;
public partial class BasisServerReductionSystem
{
    /// <summary>
    /// Structure representing a player's server-side data that can be reduced.
    /// </summary>
    public class ServerSideReducablePlayer
    {
        public Timer timer;//create a new timer
        public ServerSideSyncPlayerMessage serverSideSyncPlayerMessage;
        public NetDataWriter Writer;
        public Vector3 Position;
    }
}
