using System.Collections.Concurrent;
using static SerializableBasis;
namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public class QueuedMessagePool
    {
        private static readonly ConcurrentQueue<QueuedMessage> pool = new();

        public static QueuedMessage Rent()
        {
            return pool.TryDequeue(out var msg) ? msg : new QueuedMessage();
        }

        public static void Return(QueuedMessage msg)
        {
            msg.FromPeer = null;
            msg.Sequence = 0;
            // Preserve msg.AvatarMessage.array so it can be reused on next Rent
            // instead of allocating a new byte[] every deserialization.
            var saved = msg.AvatarMessage;
            msg.AvatarMessage = new LocalAvatarSyncMessage { array = saved.array };
            pool.Enqueue(msg);
        }
    }
}
