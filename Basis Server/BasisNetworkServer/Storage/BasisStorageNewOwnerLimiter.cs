using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BasisNetworkServer.Storage
{
    public sealed class BasisStorageNewOwnerLimiter
    {
        public const int DefaultPerAddress = 4, DefaultWindowMinutes = 60, DefaultPerHour = 60;
        private const long HourMilliseconds = 3600000;
        private readonly Dictionary<string, Bucket> addresses = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        private readonly Bucket server = new Bucket();
        private readonly Func<long> clock;
        private int perAddress = DefaultPerAddress, perHour = DefaultPerHour;
        private long window = DefaultWindowMinutes * 60000L, lastPrune, admitted, refused;
        public int PerAddress => Volatile.Read(ref perAddress);
        public int PerHour => Volatile.Read(ref perHour);
        public TimeSpan Window => TimeSpan.FromMilliseconds(Interlocked.Read(ref window));
        public long Admitted => Interlocked.Read(ref admitted);
        public long Refused => Interlocked.Read(ref refused);
        public int TrackedAddresses
        {
            get
            {
                lock (server) return addresses.Count;
            }
        }
        public BasisStorageNewOwnerLimiter(Func<long> clock = null)
        {
            this.clock = clock ?? Milliseconds;
            long now = this.clock();
            server.Tokens = perHour;
            server.Stamp = now;
            lastPrune = now;
        }
        public void Configure(int perAddress, TimeSpan window, int perHour)
        {
            lock (server)
            {
                int hourly = perHour > 0 ? perHour : DefaultPerHour;
                server.Tokens = Math.Min(hourly, server.Tokens + Math.Max(0, hourly - this.perHour));
                Volatile.Write(ref this.perAddress, perAddress > 0 ? perAddress : DefaultPerAddress);
                Volatile.Write(ref this.perHour, hourly);
                Interlocked.Exchange(ref this.window, window > TimeSpan.Zero ? Math.Max(1L, (long)window.TotalMilliseconds) : DefaultWindowMinutes * 60000L);
            }
        }
        public bool TryAdmit(string origin)
        {
            long now = clock();
            lock (server)
            {
                Refill(server, perHour, HourMilliseconds, now);
                if (server.Tokens < 1) return Refuse();
                Bucket address = null;
                if (origin != null)
                {
                    if (now - lastPrune >= window) Prune(now);
                    if (!addresses.TryGetValue(origin, out address))
                    {
                        address = new Bucket { Tokens = perAddress, Stamp = now };
                        addresses.Add(origin, address);
                    }
                    Refill(address, perAddress, window, now);
                    if (address.Tokens < 1) return Refuse();
                    address.Tokens -= 1;
                }
                server.Tokens -= 1;
                Interlocked.Increment(ref admitted);
                return true;
            }
        }
        public static string OriginOf(IPAddress address)
        {
            if (address == null) return null;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
            byte[] bytes = address.GetAddressBytes();
            for (int i = 8; i < bytes.Length; i++) bytes[i] = 0;
            return new IPAddress(bytes).ToString() + "/64";
        }
        private bool Refuse()
        {
            Interlocked.Increment(ref refused);
            return false;
        }
        private void Prune(long now)
        {
            lastPrune = now;
            List<string> refilled = null;
            foreach (KeyValuePair<string, Bucket> pair in addresses)
            {
                Refill(pair.Value, perAddress, window, now);
                if (pair.Value.Tokens < perAddress) continue;
                if (refilled == null) refilled = new List<string>();
                refilled.Add(pair.Key);
            }
            if (refilled == null) return;
            foreach (string key in refilled) addresses.Remove(key);
        }
        private static void Refill(Bucket bucket, int limit, long period, long now)
        {
            long elapsed = now - bucket.Stamp;
            if (elapsed > 0)
            {
                bucket.Tokens = Math.Min(limit, bucket.Tokens + elapsed * (double)limit / period);
                bucket.Stamp = now;
            }
            else if (bucket.Tokens > limit) bucket.Tokens = limit;
        }
        private static long Milliseconds() => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        private sealed class Bucket
        {
            public double Tokens;
            public long Stamp;
        }
    }
}
