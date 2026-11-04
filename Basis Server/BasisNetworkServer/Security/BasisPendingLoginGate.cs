using System.Collections.Generic;
using System.Net;
using Basis.Network.Core;

namespace BasisNetworkServer.Security
{
    public static class BasisPendingLoginGate
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<ulong, int> perAddress = new Dictionary<ulong, int>();
        private static readonly Dictionary<int, Reservation> byPeer = new Dictionary<int, Reservation>();
        private static int total;
        public const ulong Unlimited = ulong.MaxValue;
        public static int Pending
        {
            get
            {
                lock (gate) return total;
            }
        }
        public static bool TryReserve(IPAddress address, int maxTotal, int maxPerAddress, out ulong key)
        {
            key = Unlimited;
            if (address == null) return true;
            IPAddress plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (IPAddress.IsLoopback(plain) || !BasisConnectAttemptGate.TryKey(plain, out ulong found)) return true;
            lock (gate)
            {
                perAddress.TryGetValue(found, out int count);
                if ((maxTotal > 0 && total >= maxTotal) || (maxPerAddress > 0 && count >= maxPerAddress)) return false;
                perAddress[found] = count + 1;
                total++;
            }
            key = found;
            return true;
        }
        public static void Bind(int peerId, NetPeer peer, ulong key)
        {
            if (key == Unlimited) return;
            lock (gate)
            {
                if (byPeer.TryGetValue(peerId, out Reservation previous)) Drop(previous.Key);
                byPeer[peerId] = new Reservation { Peer = peer, Key = key };
            }
        }
        public static void Cancel(ulong key)
        {
            if (key == Unlimited) return;
            lock (gate) Drop(key);
        }
        public static void Release(int peerId, NetPeer peer)
        {
            lock (gate)
            {
                if (!byPeer.TryGetValue(peerId, out Reservation reservation) || (peer != null && !Equals(reservation.Peer, peer))) return;
                byPeer.Remove(peerId);
                Drop(reservation.Key);
            }
        }
        public static void Reset()
        {
            lock (gate)
            {
                perAddress.Clear();
                byPeer.Clear();
                total = 0;
            }
        }
        private static void Drop(ulong key)
        {
            if (!perAddress.TryGetValue(key, out int count)) return;
            if (count <= 1) perAddress.Remove(key);
            else perAddress[key] = count - 1;
            if (total > 0) total--;
        }
        private struct Reservation
        {
            public NetPeer Peer;
            public ulong Key;
        }
    }
}
