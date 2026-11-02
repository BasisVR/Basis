using Basis.Network.Core;
using System;
using System.Collections.Concurrent;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Per-peer token bucket for handlers that fan a client message out to every peer.
    /// Same shape as the jiggle-grab limiter: silent drop on exhaustion (a reply or log
    /// line would hand the flooder an amplification vector), tracking capped so peer-id
    /// churn cannot grow memory unboundedly.
    /// </summary>
    public class BasisPeerRateLimiter
    {
        private readonly Func<float> tokensPerSecond;
        private readonly Func<float> tokenBurst;
        private readonly bool zeroMeansUnlimited;
        private const int MaxTrackedPeers = ushort.MaxValue + 1;
        private static readonly ConcurrentDictionary<BasisPeerRateLimiter, byte> all = new ConcurrentDictionary<BasisPeerRateLimiter, byte>();
        public static void RemovePeerFromAll(int peerId)
        {
            foreach (BasisPeerRateLimiter limiter in all.Keys) limiter.buckets.TryRemove(peerId, out _);
        }
        public void RemovePeer(int peerId) => buckets.TryRemove(peerId, out _);
        public int TrackedPeers => buckets.Count;

        private class TokenBucket
        {
            public float Tokens;
            public long LastRefillTicks = DateTime.UtcNow.Ticks;
        }

        private readonly ConcurrentDictionary<int, TokenBucket> buckets = new ConcurrentDictionary<int, TokenBucket>();

        public BasisPeerRateLimiter(float tokensPerSecond, float tokenBurst) : this(() => tokensPerSecond, () => tokenBurst, false)
        {
        }

        public BasisPeerRateLimiter(Func<float> tokensPerSecond, Func<float> tokenBurst, bool zeroMeansUnlimited = true)
        {
            this.tokensPerSecond = tokensPerSecond;
            this.tokenBurst = tokenBurst;
            this.zeroMeansUnlimited = zeroMeansUnlimited;
            all[this] = 0;
        }

        public bool TryConsume(NetPeer peer)
        {
            if (buckets.Count > MaxTrackedPeers)
            {
                buckets.Clear();
            }
            float burst = tokenBurst(), rate = tokensPerSecond();
            if (zeroMeansUnlimited)
            {
                if (rate <= 0f && burst <= 0f) return true;
                if (burst < 1f) burst = 1f;
            }
            TokenBucket bucket = buckets.GetOrAdd(peer.Id, _ => new TokenBucket { Tokens = burst });
            lock (bucket)
            {
                long now = DateTime.UtcNow.Ticks;
                float elapsedSeconds = (now - bucket.LastRefillTicks) / (float)TimeSpan.TicksPerSecond;
                bucket.LastRefillTicks = now;
                bucket.Tokens = Math.Min(burst, bucket.Tokens + elapsedSeconds * Math.Max(0f, rate));
                if (bucket.Tokens < 1f)
                {
                    return false;
                }
                bucket.Tokens -= 1f;
                return true;
            }
        }
    }
}
