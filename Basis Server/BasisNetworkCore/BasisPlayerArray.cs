using Basis.Network.Core;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Threading;

namespace BasisNetworkCore
{
    public static class BasisPlayerArray
    {
        private static readonly object PlayerLock = new object();

        // Mutable list for internal tracking
        private static readonly List<NetPeer> InternalList = new List<NetPeer>(BasisNetworkCommons.MaxConnections);

        // Atomic read-only snapshot for fast reads
        private static NetPeer[] Snapshot = Array.Empty<NetPeer>();

        public static void AddPlayer(NetPeer player)
        {
            if (player == null) return;

            lock (PlayerLock)
            {
                InternalList.Add(player);
                UpdateSnapshot();
            }
        }

        public static void RemovePlayer(NetPeer player)
        {
            if (player == null) return;

            lock (PlayerLock)
            {
                InternalList.Remove(player);
                UpdateSnapshot();
            }
        }

        private static void UpdateSnapshot()
        {
            // Replace the entire snapshot atomically
            NetPeer[] newSnapshot = InternalList.ToArray();
            Interlocked.Exchange(ref Snapshot, newSnapshot);
        }

        public static ReadOnlySpan<NetPeer> GetSnapshot()
        {
            // Zero-copy, zero-lock, atomic read
            return new ReadOnlySpan<NetPeer>(Volatile.Read(ref Snapshot));
        }
    }
}
