using Basis.Network.Core;
using System;
using System.Collections.Generic;
namespace Basis.Network.Server.Generic
{
    public interface IBasisAvatarHostSink
    {
        void OfferResult(int peerId, byte[] id, bool accepted, byte reason);
        void Fetch(int ownerPeerId, uint fetchId, byte[] id, long start, int length);
        void Chunk(int peerId, uint requestId, int offset, byte[] data, int dataOffset, int count);
        void Failed(int peerId, uint requestId, byte reason);
    }
    public struct BasisAvatarHostLimits
    {
        public bool Enabled;
        public long MaxRangeBytes, CacheBytes, InFlightBytes, RequesterBytesPerMinute;
    }
    public sealed class BasisAvatarHostLedger
    {
        private sealed class HostedBee
        {
            public byte[] Id;
            public long Length;
            public readonly List<int> Owners = new List<int>();
        }
        private struct Waiter
        {
            public int PeerId;
            public uint RequestId;
        }
        private sealed class PendingFetch
        {
            public uint FetchId;
            public (string Id, long Start, int Length) Key;
            public byte[] IdBytes;
            public int OwnerPeerId;
            public byte[] Buffer;
            public int Received;
            public readonly List<Waiter> Waiters = new List<Waiter>();
        }
        private sealed class CachedRange
        {
            public byte[] Data;
            public LinkedListNode<(string Id, long Start, int Length)> Node;
        }
        private readonly object sync = new object();
        private readonly IBasisAvatarHostSink sink;
        private readonly Dictionary<string, HostedBee> offers = new Dictionary<string, HostedBee>();
        private readonly Dictionary<int, List<string>> ownerOffers = new Dictionary<int, List<string>>();
        private readonly Dictionary<(string Id, long Start, int Length), CachedRange> cache = new Dictionary<(string Id, long Start, int Length), CachedRange>();
        private readonly LinkedList<(string Id, long Start, int Length)> lru = new LinkedList<(string Id, long Start, int Length)>();
        private readonly Dictionary<uint, PendingFetch> fetches = new Dictionary<uint, PendingFetch>();
        private readonly Dictionary<(string Id, long Start, int Length), PendingFetch> fetchesByRange = new Dictionary<(string Id, long Start, int Length), PendingFetch>();
        private readonly Dictionary<int, int> activeRequests = new Dictionary<int, int>();
        private readonly Dictionary<int, (double Tokens, long StampMs)> requesterBudgets = new Dictionary<int, (double Tokens, long StampMs)>();
        private readonly Func<long> clockMs;
        private long cachedBytes, inFlightBytes;
        private uint nextFetchId;
        public BasisAvatarHostLedger(IBasisAvatarHostSink sink, Func<long> clockMs = null)
        {
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.clockMs = clockMs ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency);
        }
        public long CachedBytes { get { lock (sync) return cachedBytes; } }
        public long InFlightBytes { get { lock (sync) return inFlightBytes; } }
        public int OfferCount { get { lock (sync) return offers.Count; } }
        public bool IsOffered(byte[] id)
        {
            string key = BasisAvatarHostProtocol.IdToHex(id);
            if (key == null) return false;
            lock (sync) return offers.ContainsKey(key);
        }
        public void Offer(int ownerPeerId, byte[] id, long totalLength, in BasisAvatarHostLimits limits)
        {
            string key = BasisAvatarHostProtocol.IdToHex(id);
            lock (sync)
            {
                if (key == null)
                {
                    sink.OfferResult(ownerPeerId, id, false, BasisAvatarHostProtocol.ReasonUnknownId);
                    return;
                }
                if (!limits.Enabled)
                {
                    sink.OfferResult(ownerPeerId, id, false, BasisAvatarHostProtocol.ReasonDisabled);
                    return;
                }
                if (totalLength <= 0 || totalLength > BasisAvatarHostProtocol.MaxFileBytes)
                {
                    sink.OfferResult(ownerPeerId, id, false, BasisAvatarHostProtocol.ReasonTooLarge);
                    return;
                }
                if (!ownerOffers.TryGetValue(ownerPeerId, out List<string> owned))
                {
                    owned = new List<string>();
                    ownerOffers[ownerPeerId] = owned;
                }
                if (owned.Contains(key))
                {
                    bool same = offers.TryGetValue(key, out HostedBee existing) && existing.Length == totalLength;
                    sink.OfferResult(ownerPeerId, id, same, same ? BasisAvatarHostProtocol.ReasonNone : BasisAvatarHostProtocol.ReasonBadRange);
                    return;
                }
                if (owned.Count >= BasisAvatarHostProtocol.MaxOffersPerOwner)
                {
                    sink.OfferResult(ownerPeerId, id, false, BasisAvatarHostProtocol.ReasonTooManyOffers);
                    return;
                }
                if (offers.TryGetValue(key, out HostedBee bee))
                {
                    if (bee.Length != totalLength)
                    {
                        sink.OfferResult(ownerPeerId, id, false, BasisAvatarHostProtocol.ReasonBadRange);
                        return;
                    }
                }
                else
                {
                    bee = new HostedBee { Id = (byte[])id.Clone(), Length = totalLength };
                    offers[key] = bee;
                }
                bee.Owners.Add(ownerPeerId);
                owned.Add(key);
                sink.OfferResult(ownerPeerId, id, true, BasisAvatarHostProtocol.ReasonNone);
            }
        }
        public void Withdraw(int ownerPeerId, byte[] id)
        {
            string key = BasisAvatarHostProtocol.IdToHex(id);
            if (key == null) return;
            lock (sync)
            {
                WithdrawLocked(ownerPeerId, key, BasisAvatarHostProtocol.ReasonOwnerLeft);
            }
        }
        public void Request(int requesterPeerId, uint requestId, byte[] id, long start, int length, in BasisAvatarHostLimits limits)
        {
            string key = BasisAvatarHostProtocol.IdToHex(id);
            lock (sync)
            {
                if (key == null || !offers.TryGetValue(key, out HostedBee bee))
                {
                    sink.Failed(requesterPeerId, requestId, BasisAvatarHostProtocol.ReasonUnknownId);
                    return;
                }
                if (start < 0 || length <= 0 || start > bee.Length - length)
                {
                    sink.Failed(requesterPeerId, requestId, BasisAvatarHostProtocol.ReasonBadRange);
                    return;
                }
                if (length > limits.MaxRangeBytes)
                {
                    sink.Failed(requesterPeerId, requestId, BasisAvatarHostProtocol.ReasonTooLarge);
                    return;
                }
                (string Id, long Start, int Length) range = (key, start, length);
                if (!TrySpendLocked(requesterPeerId, length, limits.RequesterBytesPerMinute))
                {
                    sink.Failed(requesterPeerId, requestId, BasisAvatarHostProtocol.ReasonBusy);
                    return;
                }
                if (cache.TryGetValue(range, out CachedRange cached))
                {
                    lru.Remove(cached.Node);
                    lru.AddLast(cached.Node);
                    SendAll(requesterPeerId, requestId, cached.Data, cached.Data.Length);
                    return;
                }
                activeRequests.TryGetValue(requesterPeerId, out int active);
                if (active >= BasisAvatarHostProtocol.MaxRequestsPerRequester)
                {
                    sink.Failed(requesterPeerId, requestId, BasisAvatarHostProtocol.ReasonBusy);
                    return;
                }
                if (fetchesByRange.TryGetValue(range, out PendingFetch pending))
                {
                    pending.Waiters.Add(new Waiter { PeerId = requesterPeerId, RequestId = requestId });
                    activeRequests[requesterPeerId] = active + 1;
                    if (pending.Received > 0) SendAll(requesterPeerId, requestId, pending.Buffer, pending.Received);
                    return;
                }
                if (inFlightBytes + length > limits.InFlightBytes || bee.Owners.Count == 0)
                {
                    sink.Failed(requesterPeerId, requestId, BasisAvatarHostProtocol.ReasonBusy);
                    return;
                }
                int owner = bee.Owners[0];
                if (owner == requesterPeerId)
                {
                    sink.Failed(requesterPeerId, requestId, BasisAvatarHostProtocol.ReasonBadRange);
                    return;
                }
                PendingFetch fetch = new PendingFetch
                {
                    FetchId = ++nextFetchId,
                    Key = range,
                    IdBytes = bee.Id,
                    OwnerPeerId = owner,
                    Buffer = new byte[length],
                };
                fetch.Waiters.Add(new Waiter { PeerId = requesterPeerId, RequestId = requestId });
                fetches[fetch.FetchId] = fetch;
                fetchesByRange[range] = fetch;
                inFlightBytes += length;
                activeRequests[requesterPeerId] = active + 1;
                sink.Fetch(owner, fetch.FetchId, bee.Id, start, length);
            }
        }
        public void OwnerChunk(int ownerPeerId, uint fetchId, int offset, byte[] data, int dataOffset, int count, in BasisAvatarHostLimits limits)
        {
            lock (sync)
            {
                if (!fetches.TryGetValue(fetchId, out PendingFetch fetch) || fetch.OwnerPeerId != ownerPeerId) return;
                if (offset != fetch.Received || count <= 0 || count > fetch.Buffer.Length - fetch.Received || data == null || dataOffset < 0 || count > data.Length - dataOffset)
                {
                    FailFetchLocked(fetch, BasisAvatarHostProtocol.ReasonOwnerFailed);
                    return;
                }
                Buffer.BlockCopy(data, dataOffset, fetch.Buffer, offset, count);
                fetch.Received += count;
                for (int index = 0; index < fetch.Waiters.Count; index++)
                {
                    sink.Chunk(fetch.Waiters[index].PeerId, fetch.Waiters[index].RequestId, offset, fetch.Buffer, offset, count);
                }
                if (fetch.Received < fetch.Buffer.Length) return;
                RemoveFetchLocked(fetch);
                for (int index = 0; index < fetch.Waiters.Count; index++) ReleaseActiveLocked(fetch.Waiters[index].PeerId);
                StoreLocked(fetch.Key, fetch.Buffer, limits.CacheBytes);
            }
        }
        public void OwnerFailed(int ownerPeerId, uint fetchId)
        {
            lock (sync)
            {
                if (fetches.TryGetValue(fetchId, out PendingFetch fetch) && fetch.OwnerPeerId == ownerPeerId) FailFetchLocked(fetch, BasisAvatarHostProtocol.ReasonOwnerFailed);
            }
        }
        public int[] OwnerPeerIds()
        {
            lock (sync)
            {
                int[] ids = new int[ownerOffers.Count];
                ownerOffers.Keys.CopyTo(ids, 0);
                return ids;
            }
        }
        public void WithdrawOwner(int ownerPeerId, byte reason)
        {
            lock (sync) WithdrawOwnerLocked(ownerPeerId, reason);
        }
        private void WithdrawOwnerLocked(int ownerPeerId, byte reason)
        {
            if (!ownerOffers.TryGetValue(ownerPeerId, out List<string> owned)) return;
            foreach (string key in owned.ToArray()) WithdrawLocked(ownerPeerId, key, reason);
            ownerOffers.Remove(ownerPeerId);
        }
        public void RemovePeer(int peerId)
        {
            lock (sync)
            {
                WithdrawOwnerLocked(peerId, BasisAvatarHostProtocol.ReasonOwnerLeft);
                foreach (PendingFetch fetch in fetches.Values)
                {
                    fetch.Waiters.RemoveAll(waiter => waiter.PeerId == peerId);
                }
                activeRequests.Remove(peerId);
                requesterBudgets.Remove(peerId);
            }
        }
        public void TrimCache(long cacheBytes)
        {
            lock (sync) EvictLocked(Math.Max(0, cacheBytes));
        }
        private void WithdrawLocked(int ownerPeerId, string key, byte reason)
        {
            if (ownerOffers.TryGetValue(ownerPeerId, out List<string> owned)) owned.Remove(key);
            if (!offers.TryGetValue(key, out HostedBee bee) || !bee.Owners.Remove(ownerPeerId)) return;
            List<PendingFetch> orphaned = new List<PendingFetch>();
            foreach (PendingFetch fetch in fetches.Values)
            {
                if (fetch.OwnerPeerId == ownerPeerId && fetch.Key.Id == key) orphaned.Add(fetch);
            }
            foreach (PendingFetch fetch in orphaned) FailFetchLocked(fetch, reason);
            if (bee.Owners.Count > 0) return;
            offers.Remove(key);
            List<(string Id, long Start, int Length)> stale = new List<(string Id, long Start, int Length)>();
            foreach ((string Id, long Start, int Length) range in cache.Keys)
            {
                if (range.Id == key) stale.Add(range);
            }
            foreach ((string Id, long Start, int Length) range in stale) DropCachedLocked(range);
        }
        private void FailFetchLocked(PendingFetch fetch, byte reason)
        {
            RemoveFetchLocked(fetch);
            for (int index = 0; index < fetch.Waiters.Count; index++)
            {
                ReleaseActiveLocked(fetch.Waiters[index].PeerId);
                sink.Failed(fetch.Waiters[index].PeerId, fetch.Waiters[index].RequestId, reason);
            }
        }
        private void RemoveFetchLocked(PendingFetch fetch)
        {
            if (fetches.Remove(fetch.FetchId)) inFlightBytes -= fetch.Buffer.Length;
            fetchesByRange.Remove(fetch.Key);
        }
        private bool TrySpendLocked(int peerId, int bytes, long bytesPerMinute)
        {
            if (bytesPerMinute <= 0) return true;
            long now = clockMs();
            double tokens = bytesPerMinute;
            if (requesterBudgets.TryGetValue(peerId, out (double Tokens, long StampMs) budget)) tokens = Math.Min(bytesPerMinute, budget.Tokens + (now - budget.StampMs) * (bytesPerMinute / 60000.0));
            if (tokens < bytes)
            {
                requesterBudgets[peerId] = (tokens, now);
                return false;
            }
            requesterBudgets[peerId] = (tokens - bytes, now);
            return true;
        }
        private void ReleaseActiveLocked(int peerId)
        {
            if (!activeRequests.TryGetValue(peerId, out int active)) return;
            if (active <= 1) activeRequests.Remove(peerId);
            else activeRequests[peerId] = active - 1;
        }
        private void StoreLocked((string Id, long Start, int Length) range, byte[] data, long cacheBytes)
        {
            if (cacheBytes <= 0 || data.Length > cacheBytes || !offers.ContainsKey(range.Id) || cache.ContainsKey(range)) return;
            EvictLocked(cacheBytes - data.Length);
            CachedRange entry = new CachedRange { Data = data, Node = lru.AddLast(range) };
            cache[range] = entry;
            cachedBytes += data.Length;
        }
        private void EvictLocked(long keepBytes)
        {
            while (cachedBytes > keepBytes && lru.First != null) DropCachedLocked(lru.First.Value);
        }
        private void DropCachedLocked((string Id, long Start, int Length) range)
        {
            if (!cache.TryGetValue(range, out CachedRange entry)) return;
            cache.Remove(range);
            lru.Remove(entry.Node);
            cachedBytes -= entry.Data.Length;
        }
        private void SendAll(int peerId, uint requestId, byte[] data, int count)
        {
            for (int offset = 0; offset < count; offset += BasisAvatarHostProtocol.ChunkBytes)
            {
                sink.Chunk(peerId, requestId, offset, data, offset, Math.Min(BasisAvatarHostProtocol.ChunkBytes, count - offset));
            }
        }
    }
}
