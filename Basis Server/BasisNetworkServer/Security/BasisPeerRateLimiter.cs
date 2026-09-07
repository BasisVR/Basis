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
        private readonly float tokensPerSecond;
        private readonly float tokenBurst;
        private const int MaxTrackedPeers = 4096;

        private class TokenBucket
        {
            public float Tokens;
            public long LastRefillTicks = DateTime.UtcNow.Ticks;
        }

        private readonly ConcurrentDictionary<int, TokenBucket> buckets = new ConcurrentDictionary<int, TokenBucket>();

        public BasisPeerRateLimiter(float tokensPerSecond, float tokenBurst)
        {
            this.tokensPerSecond = tokensPerSecond;
            this.tokenBurst = tokenBurst;
        }

        public bool TryConsume(NetPeer peer)
        {
            if (buckets.Count > MaxTrackedPeers)
            {
                buckets.Clear();
            }
            TokenBucket bucket = buckets.GetOrAdd(peer.Id, _ => new TokenBucket { Tokens = tokenBurst });
            lock (bucket)
            {
                long now = DateTime.UtcNow.Ticks;
                float elapsedSeconds = (now - bucket.LastRefillTicks) / (float)TimeSpan.TicksPerSecond;
                bucket.LastRefillTicks = now;
                bucket.Tokens = Math.Min(tokenBurst, bucket.Tokens + elapsedSeconds * tokensPerSecond);
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
