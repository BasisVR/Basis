using System;
using System.Collections.Generic;
using static SerializableBasis;
namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public class QueuedMessagePool
    {
        private const int ThreadLocalCapacity = 64;

        [ThreadStatic]
        private static List<QueuedMessage> t_pool;

        public static QueuedMessage Rent()
        {
            var local = t_pool;
            if (local != null && local.Count > 0)
            {
                int last = local.Count - 1;
                var msg = local[last];
                local.RemoveAt(last);
                return msg;
            }
            return new QueuedMessage();
        }

        public static void Return(QueuedMessage msg)
        {
            msg.FromPeer = null;
            msg.Sequence = 0;
            // Preserve msg.AvatarMessage.array so it can be reused on next Rent
            // instead of allocating a new byte[] every deserialization.
            var saved = msg.AvatarMessage;
            msg.AvatarMessage = new LocalAvatarSyncMessage { array = saved.array };

            var local = t_pool;
            if (local == null)
            {
                local = new List<QueuedMessage>(ThreadLocalCapacity);
                t_pool = local;
            }
            if (local.Count < ThreadLocalCapacity)
            {
                local.Add(msg);
            }
            // else: drop it — GC reclaims, keeps pool bounded
        }
    }
}
