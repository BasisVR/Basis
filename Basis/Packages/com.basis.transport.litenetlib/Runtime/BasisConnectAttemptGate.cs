using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Basis.Network.Core
{
    public sealed class BasisConnectAttemptGate
    {
        public const int MaxTrackedAddresses = 65536;
        private readonly Dictionary<ulong, Bucket> addresses = new Dictionary<ulong, Bucket>();
        private readonly List<ulong> refilled = new List<ulong>();
        private readonly double perTick, burst;
        private long admitted, dropped;
        public bool ExemptLoopback = true;
        public long Admitted => Interlocked.Read(ref admitted);
        public long Dropped => Interlocked.Read(ref dropped);
        public int TrackedAddresses
        {
            get
            {
                lock (addresses) return addresses.Count;
            }
        }
        public BasisConnectAttemptGate(double perSecond, int burst)
        {
            perTick = Math.Max(0.000001, perSecond) / Stopwatch.Frequency;
            this.burst = Math.Max(1, burst);
        }
        public bool TryAdmit(IPEndPoint endPoint) => endPoint == null || TryAdmit(endPoint.Address, Stopwatch.GetTimestamp());
        public bool TryAdmit(IPAddress address, long now)
        {
            if (!TryKey(address, out ulong key) || (ExemptLoopback && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))) return true;
            lock (addresses)
            {
                if (addresses.TryGetValue(key, out Bucket bucket)) Refill(ref bucket, now);
                else
                {
                    if (addresses.Count >= MaxTrackedAddresses) Prune(now);
                    bucket = new Bucket { Tokens = burst, Stamp = now };
                }
                bool admit = bucket.Tokens >= 1;
                if (admit) bucket.Tokens -= 1;
                addresses[key] = bucket;
                if (admit) admitted++;
                else dropped++;
                return admit;
            }
        }
        public static bool TryKey(IPAddress address, out ulong key)
        {
            key = 0;
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            Span<byte> bytes = stackalloc byte[16];
            if (!address.TryWriteBytes(bytes, out int written)) return false;
            if (address.AddressFamily == AddressFamily.InterNetwork && written == 4)
            {
                key = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
                return true;
            }
            if (address.AddressFamily != AddressFamily.InterNetworkV6 || written != 16) return false;
            for (int i = 0; i < 8; i++) key = key << 8 | bytes[i];
            return true;
        }
        private void Prune(long now)
        {
            refilled.Clear();
            foreach (KeyValuePair<ulong, Bucket> pair in addresses)
            {
                Bucket bucket = pair.Value;
                Refill(ref bucket, now);
                if (bucket.Tokens >= burst) refilled.Add(pair.Key);
            }
            for (int i = 0; i < refilled.Count; i++) addresses.Remove(refilled[i]);
            refilled.Clear();
            if (addresses.Count >= MaxTrackedAddresses) addresses.Clear();
        }
        private void Refill(ref Bucket bucket, long now)
        {
            long elapsed = now - bucket.Stamp;
            if (elapsed <= 0) return;
            bucket.Tokens = Math.Min(burst, bucket.Tokens + elapsed * perTick);
            bucket.Stamp = now;
        }
        private struct Bucket
        {
            public double Tokens;
            public long Stamp;
        }
    }
}
