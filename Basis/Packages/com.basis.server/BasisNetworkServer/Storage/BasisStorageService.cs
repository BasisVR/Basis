using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;

namespace BasisNetworkServer.Storage
{
    public sealed class BasisStorageService : IDisposable
    {
        public const int DefaultLanes = 4, LaneQueueLimit = 1024, MaxConflictRetries = 3, MaxTrackedBudgets = 8192, RecordOverheadBytes = 4096, ExpiryBatch = 4096, MaxExpiryDays = 36500;
        public const double ReadsPerSecond = 20, ReadBurst = 60, WritesPerSecond = 2, WriteBurst = 10;
        public static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1), ExpiryInterval = TimeSpan.FromHours(1), FirstExpiryDelay = TimeSpan.FromMinutes(5);
        private const long LaneCacheBytes = 16L * 1024 * 1024, IdleBudgetSeconds = 60;
        private static readonly TimeSpan ExpiryWait = TimeSpan.FromMinutes(5);
        private readonly IBasisStorageBackend backend;
        private readonly IBasisStorageCatalog catalog;
        private readonly IBasisSharedStorageBackend shared;
        private readonly BasisStorageNewOwnerLimiter newOwners = new BasisStorageNewOwnerLimiter();
        private readonly Lane[] lanes;
        private readonly ConcurrentDictionary<string, Budget> budgets = new ConcurrentDictionary<string, Budget>(StringComparer.Ordinal);
        private readonly Timer expiryTimer;
        private int bytesPerPlayer, expiryDays, disposed, expiring;
        private long maxTotalBytes, totalBytes, totalRecords, served, refused, failures, lastPrune, expired, touchFailures;
        public string BackendName => backend.Name;
        public int BytesPerPlayer => Volatile.Read(ref bytesPerPlayer);
        public long MaxTotalBytes => Interlocked.Read(ref maxTotalBytes);
        public long TotalBytes => Interlocked.Read(ref totalBytes);
        public long TotalRecords => Interlocked.Read(ref totalRecords);
        public long UsedBytes => TotalBytes + TotalRecords * RecordOverheadBytes;
        public long Served => Interlocked.Read(ref served);
        public long Refused => Interlocked.Read(ref refused);
        public long Failures => Interlocked.Read(ref failures);
        public long Expired => Interlocked.Read(ref expired);
        public int ExpiryDays => Volatile.Read(ref expiryDays);
        public bool CanExpire => catalog != null;
        public bool IsExpiring => Volatile.Read(ref expiring) != 0;
        public BasisStorageNewOwnerLimiter NewOwners => newOwners;
        public BasisStorageService(IBasisStorageBackend backend, int bytesPerPlayer, long maxTotalBytes, int laneCount = DefaultLanes)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            catalog = backend as IBasisStorageCatalog;
            shared = backend as IBasisSharedStorageBackend;
            Configure(bytesPerPlayer, maxTotalBytes);
            backend.Measure(out long records, out long bytes);
            totalRecords = records;
            totalBytes = bytes;
            lanes = new Lane[Math.Max(1, laneCount)];
            for (int i = 0; i < lanes.Length; i++) lanes[i] = new Lane(this, i);
            if (catalog != null) expiryTimer = new Timer(_ => ExpireIdle(), null, FirstExpiryDelay, ExpiryInterval);
        }
        public void Configure(int bytesPerPlayer, long maxTotalBytes)
        {
            Volatile.Write(ref this.bytesPerPlayer, Math.Min(Math.Max(1, bytesPerPlayer), BasisStorageProtocol.MaxBytesPerPlayer));
            Interlocked.Exchange(ref this.maxTotalBytes, Math.Max(0, maxTotalBytes));
        }
        public void ConfigureNewOwners(int perAddress, TimeSpan window, int perHour) => newOwners.Configure(perAddress, window, perHour);
        public void ConfigureExpiry(int days) => Volatile.Write(ref expiryDays, Math.Min(Math.Max(0, days), MaxExpiryDays));
        public BasisStorageStatus Submit(string owner, BasisStorageRequest request, Action<BasisStorageReply> reply, string origin = null)
        {
            if (reply == null) throw new ArgumentNullException(nameof(reply));
            if (Volatile.Read(ref disposed) != 0) return Refuse(BasisStorageStatus.Unavailable);
            if (!IsValidOwner(owner)) return Refuse(BasisStorageStatus.Denied);
            if (!Charge(owner, request)) return Refuse(BasisStorageStatus.RateLimited);
            if (!IsWellFormed(request)) return Refuse(BasisStorageStatus.Invalid);
            return Enqueue(new Work { Kind = WorkKind.Request, Owner = owner, Origin = origin, Request = request, Reply = reply });
        }
        public void Forget(string owner)
        {
            if (!IsValidOwner(owner) || Volatile.Read(ref disposed) != 0) return;
            Enqueue(new Work { Kind = WorkKind.Evict, Owner = owner });
        }
        public bool TryInspect(string owner, TimeSpan timeout, out BasisStorageRecord snapshot)
        {
            snapshot = null;
            Work work = new Work { Kind = WorkKind.Inspect, Owner = owner, Done = new ManualResetEventSlim(false) };
            if (!Wait(work, timeout)) return false;
            snapshot = work.Snapshot;
            return work.Result;
        }
        public bool TryWipe(string owner, TimeSpan timeout)
        {
            Work work = new Work { Kind = WorkKind.Wipe, Owner = owner, Done = new ManualResetEventSlim(false) };
            return Wait(work, timeout) && work.Result;
        }
        public bool TryDescribe(string ownerKey, TimeSpan timeout, out BasisStorageRecord snapshot)
        {
            snapshot = null;
            if (!BasisStorageRecord.IsOwnerKey(ownerKey) || Volatile.Read(ref disposed) != 0) return false;
            Work work = new Work { Kind = WorkKind.Describe, OwnerKey = ownerKey, Done = new ManualResetEventSlim(false) };
            if (Enqueue(work) != BasisStorageStatus.Ok || !work.Done.Wait(timeout)) return false;
            snapshot = work.Snapshot;
            return snapshot != null;
        }
        public bool TryList(long usedBefore, Func<BasisStorageRecordInfo, bool> visit)
        {
            if (catalog == null || visit == null || Volatile.Read(ref disposed) != 0) return false;
            catalog.List(usedBefore, visit);
            return true;
        }
        public bool TryPreviewExpiry(TimeSpan idle, out long records, out long bytes)
        {
            long count = 0, size = 0;
            bool listed = TryList(CutoffFor(idle), info =>
            {
                count++;
                size += info.Bytes;
                return true;
            });
            records = count;
            bytes = size;
            return listed;
        }
        public bool TryExpire(TimeSpan idle, out long records, out long bytes)
        {
            records = 0;
            bytes = 0;
            if (catalog == null || Volatile.Read(ref disposed) != 0 || Interlocked.CompareExchange(ref expiring, 1, 0) != 0) return false;
            try
            {
                long cutoff = CutoffFor(idle);
                List<Work> batch = new List<Work>();
                while (Volatile.Read(ref disposed) == 0)
                {
                    batch.Clear();
                    catalog.List(cutoff, info =>
                    {
                        batch.Add(new Work { Kind = WorkKind.Expire, OwnerKey = info.OwnerKey, UsedBefore = cutoff });
                        return batch.Count < ExpiryBatch;
                    });
                    if (batch.Count == 0) break;
                    CountdownEvent pending = new CountdownEvent(batch.Count);
                    foreach (Work work in batch)
                    {
                        work.Countdown = pending;
                        if (!EnqueueWaiting(work)) pending.Signal();
                    }
                    if (!pending.Wait(ExpiryWait)) break;
                    long removed = 0;
                    foreach (Work work in batch)
                    {
                        if (!work.Result) continue;
                        removed++;
                        bytes += work.Freed;
                    }
                    records += removed;
                    if (removed == 0 || batch.Count < ExpiryBatch) break;
                }
                return true;
            }
            finally
            {
                Volatile.Write(ref expiring, 0);
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            expiryTimer?.Dispose();
            foreach (Lane lane in lanes) lane.Queue.CompleteAdding();
            foreach (Lane lane in lanes)
            {
                if (!lane.Thread.Join(TimeSpan.FromSeconds(15))) BNL.LogWarning($"Player storage lane {lane.Index} did not finish its queued writes before shutdown.");
            }
            backend.Dispose();
        }
        public static bool IsValidOwner(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return false;
            try { return BasisStorageProtocol.StrictUtf8.GetByteCount(owner) <= BasisStorageRecord.MaxOwnerBytes; }
            catch (ArgumentException) { return false; }
        }
        private bool Wait(Work work, TimeSpan timeout)
        {
            if (!IsValidOwner(work.Owner) || Volatile.Read(ref disposed) != 0 || Enqueue(work) != BasisStorageStatus.Ok) return false;
            return work.Done.Wait(timeout);
        }
        private BasisStorageStatus Enqueue(Work work)
        {
            if (work.OwnerKey == null) work.OwnerKey = BasisStorageRecord.OwnerKeyOf(work.Owner);
            Lane lane = lanes[LaneOf(work.OwnerKey)];
            try
            {
                if (lane.Queue.TryAdd(work)) return BasisStorageStatus.Ok;
            }
            catch (InvalidOperationException)
            {
                return Refuse(BasisStorageStatus.Unavailable);
            }
            return Refuse(BasisStorageStatus.Busy);
        }
        private bool EnqueueWaiting(Work work)
        {
            Lane lane = lanes[LaneOf(work.OwnerKey)];
            try
            {
                return lane.Queue.TryAdd(work, ExpiryWait);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        private void ExpireIdle()
        {
            int days = ExpiryDays;
            if (days <= 0 || Volatile.Read(ref disposed) != 0) return;
            try
            {
                if (TryExpire(TimeSpan.FromDays(days), out long records, out long bytes) && records > 0) BNL.Log($"Player storage removed the data of {records} players ({bytes} bytes) that nobody had used for {days} days.");
            }
            catch (Exception ex)
            {
                BNL.LogError($"Player storage ({backend.Name}) could not remove idle data: {ex.GetBaseException().Message}");
            }
        }
        private static long CutoffFor(TimeSpan idle) => NowMilliseconds() - (long)Math.Max(0, idle.TotalMilliseconds) - (long)TouchInterval.TotalMilliseconds;
        private static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static long NewRevision()
        {
            byte[] bytes = new byte[8];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return (long)(BitConverter.ToUInt64(bytes, 0) >> 2) + 1;
        }
        private int LaneOf(string ownerKey) => (int)(Convert.ToUInt32(ownerKey.Substring(0, 8), 16) % (uint)lanes.Length);
        private BasisStorageStatus Refuse(BasisStorageStatus status)
        {
            Interlocked.Increment(ref refused);
            return status;
        }
        private static bool IsWellFormed(BasisStorageRequest request)
        {
            if (!BasisStorageProtocol.IsKnown(request.Op) || !BasisStorageProtocol.IsValidName(request.Scope)) return false;
            if (BasisStorageProtocol.HasKey(request.Op) && !BasisStorageProtocol.IsValidName(request.Key)) return false;
            return request.Op != BasisStorageOp.Set || (request.Value != null && request.Value.Length <= BasisStorageProtocol.MaxValueBytes);
        }
        private bool Charge(string owner, BasisStorageRequest request)
        {
            long now = Stopwatch.GetTimestamp();
            long pruned = Interlocked.Read(ref lastPrune);
            if (budgets.Count > MaxTrackedBudgets && now - pruned > Stopwatch.Frequency && Interlocked.CompareExchange(ref lastPrune, now, pruned) == pruned) PruneBudgets(now);
            int limit = BytesPerPlayer;
            double byteRate = limit / 2.0, byteBurst = limit * 2.0;
            Budget budget = budgets.GetOrAdd(owner, _ => new Budget { Reads = ReadBurst, Writes = WriteBurst, Bytes = byteBurst, Stamp = now });
            lock (budget)
            {
                double elapsed = Math.Max(0, (now - budget.Stamp) / (double)Stopwatch.Frequency);
                budget.Stamp = Math.Max(now, budget.Stamp);
                budget.Reads = Math.Min(ReadBurst, budget.Reads + elapsed * ReadsPerSecond);
                budget.Writes = Math.Min(WriteBurst, budget.Writes + elapsed * WritesPerSecond);
                budget.Bytes = Math.Min(byteBurst, budget.Bytes + elapsed * byteRate);
                if (!BasisStorageProtocol.Mutates(request.Op))
                {
                    if (budget.Reads < 1) return false;
                    budget.Reads -= 1;
                    return true;
                }
                double bytes = request.Op == BasisStorageOp.Set && request.Value != null ? Math.Min(request.Value.Length, byteBurst) : 0;
                if (budget.Writes < 1 || budget.Bytes < bytes) return false;
                budget.Writes -= 1;
                budget.Bytes -= bytes;
                return true;
            }
        }
        private void PruneBudgets(long now)
        {
            long idle = IdleBudgetSeconds * Stopwatch.Frequency;
            foreach (KeyValuePair<string, Budget> pair in budgets)
            {
                if (now - Volatile.Read(ref pair.Value.Stamp) > idle) budgets.TryRemove(pair.Key, out _);
            }
        }
        private void Process(Lane lane, Work work)
        {
            switch (work.Kind)
            {
                case WorkKind.Evict:
                    lane.Forget(work.OwnerKey);
                    break;
                case WorkKind.Inspect:
                    Entry inspected = Load(lane, work.OwnerKey, work.Owner);
                    work.Snapshot = inspected.Record.Clone();
                    work.Result = true;
                    break;
                case WorkKind.Wipe:
                    work.Result = Wipe(lane, work.OwnerKey, work.Owner);
                    break;
                case WorkKind.Describe:
                    work.Snapshot = Describe(lane, work.OwnerKey);
                    break;
                case WorkKind.Expire:
                    work.Result = Expire(lane, work.OwnerKey, work.UsedBefore, out work.Freed);
                    break;
                case WorkKind.Request:
                    BasisStorageReply reply = Execute(lane, work);
                    Interlocked.Increment(ref served);
                    work.Replied = true;
                    work.Reply(reply);
                    break;
            }
        }
        private BasisStorageReply Execute(Lane lane, Work work)
        {
            BasisStorageRequest request = work.Request;
            BasisStorageReply reply = new BasisStorageReply { Op = request.Op, RequestId = request.RequestId, Status = BasisStorageStatus.Ok };
            for (int attempt = 0; ; attempt++)
            {
                Entry entry = Load(lane, work.OwnerKey, work.Owner);
                BasisStorageRecord record = entry.Record;
                int limit = BytesPerPlayer;
                reply.Used = record.Used;
                reply.Limit = limit;
                if (!BasisStorageProtocol.Mutates(request.Op)) Use(entry);
                switch (request.Op)
                {
                    case BasisStorageOp.Get:
                        if (record.TryGet(request.Scope, request.Key, out byte[] value)) reply.Value = value;
                        else reply.Status = BasisStorageStatus.NotFound;
                        return reply;
                    case BasisStorageOp.Keys:
                        reply.Keys = record.KeysOf(request.Scope);
                        return reply;
                    case BasisStorageOp.Usage:
                        return reply;
                }
                BasisStorageRecord next = record.Clone();
                BasisStorageStatus status = Apply(next, request, limit, out bool changed);
                if (status != BasisStorageStatus.Ok || !changed)
                {
                    Use(entry);
                    reply.Status = status;
                    return reply;
                }
                int stored = next.IsEmpty ? 0 : next.BlobLength;
                bool creates = entry.StoredBytes == 0 && stored > 0, removes = entry.StoredBytes > 0 && stored == 0;
                long growth = stored - entry.StoredBytes + (creates ? RecordOverheadBytes : 0) - (removes ? RecordOverheadBytes : 0);
                if (growth > 0 && UsedBytes + growth > MaxTotalBytes)
                {
                    Use(entry);
                    reply.Status = BasisStorageStatus.Full;
                    return reply;
                }
                if (creates && !work.Admitted)
                {
                    if (!newOwners.TryAdmit(work.Origin))
                    {
                        reply.Status = BasisStorageStatus.RateLimited;
                        return reply;
                    }
                    work.Admitted = true;
                }
                next.Revision = creates ? NewRevision() : record.Revision + 1;
                bool saved = next.IsEmpty ? backend.TryDelete(work.OwnerKey, record.Revision) : backend.TrySave(work.OwnerKey, next.Serialize(), record.Revision, next.Revision);
                if (!saved)
                {
                    lane.Forget(work.OwnerKey);
                    if (attempt < MaxConflictRetries) continue;
                    reply.Status = BasisStorageStatus.Busy;
                    return reply;
                }
                Interlocked.Add(ref totalBytes, stored - entry.StoredBytes);
                if (creates) Interlocked.Increment(ref totalRecords);
                else if (removes) Interlocked.Decrement(ref totalRecords);
                long now = NowMilliseconds();
                lane.Remember(new Entry { OwnerKey = work.OwnerKey, Record = next.IsEmpty ? new BasisStorageRecord(work.Owner) : next, StoredBytes = stored, LastUsed = now, LastTouched = stored > 0 ? now : 0 });
                reply.Used = next.Used;
                return reply;
            }
        }
        private static BasisStorageStatus Apply(BasisStorageRecord record, BasisStorageRequest request, int limit, out bool changed)
        {
            changed = false;
            switch (request.Op)
            {
                case BasisStorageOp.Set:
                    BasisStorageStatus status = record.Set(request.Scope, request.Key, request.Value, limit);
                    changed = status == BasisStorageStatus.Ok;
                    return status;
                case BasisStorageOp.Delete:
                    changed = record.Remove(request.Scope, request.Key);
                    return changed ? BasisStorageStatus.Ok : BasisStorageStatus.NotFound;
                case BasisStorageOp.Clear:
                    changed = record.Clear(request.Scope) > 0;
                    return BasisStorageStatus.Ok;
                default:
                    return BasisStorageStatus.Invalid;
            }
        }
        private bool Wipe(Lane lane, string ownerKey, string owner)
        {
            for (int attempt = 0; attempt <= MaxConflictRetries; attempt++)
            {
                lane.Forget(ownerKey);
                Entry entry = Load(lane, ownerKey, owner);
                if (entry.StoredBytes == 0) return true;
                if (!backend.TryDelete(ownerKey, entry.Record.Revision)) continue;
                Interlocked.Add(ref totalBytes, -entry.StoredBytes);
                Interlocked.Decrement(ref totalRecords);
                lane.Forget(ownerKey);
                return true;
            }
            return false;
        }
        private BasisStorageRecord Describe(Lane lane, string ownerKey)
        {
            if (lane.TryRecall(ownerKey, out Entry cached) && cached.StoredBytes > 0 && IsCurrent(cached)) return cached.Record.Clone();
            if (!backend.TryLoad(ownerKey, out byte[] blob) || !BasisStorageRecord.TryReadOwner(blob, out string owner) || !string.Equals(BasisStorageRecord.OwnerKeyOf(owner), ownerKey, StringComparison.Ordinal)) return null;
            return BasisStorageRecord.TryParse(blob, owner, out BasisStorageRecord record) ? record : null;
        }
        private bool Expire(Lane lane, string ownerKey, long usedBefore, out long freed)
        {
            freed = 0;
            if (catalog == null || (lane.TryRecall(ownerKey, out Entry cached) && cached.LastUsed >= usedBefore)) return false;
            if (!catalog.TryExpire(ownerKey, usedBefore, out freed)) return false;
            lane.Forget(ownerKey);
            Interlocked.Add(ref totalBytes, -freed);
            Interlocked.Decrement(ref totalRecords);
            Interlocked.Increment(ref expired);
            return true;
        }
        private bool IsCurrent(Entry entry)
        {
            if (shared == null) return true;
            bool exists = shared.TryReadRevision(entry.OwnerKey, out long revision);
            return entry.StoredBytes > 0 ? exists && revision == entry.Record.Revision : !exists;
        }
        private void Use(Entry entry)
        {
            long now = NowMilliseconds();
            entry.LastUsed = now;
            if (catalog == null || entry.StoredBytes == 0 || now - entry.LastTouched < (long)TouchInterval.TotalMilliseconds) return;
            try
            {
                catalog.Touch(entry.OwnerKey, now);
                entry.LastTouched = now;
            }
            catch (Exception ex)
            {
                long count = Interlocked.Increment(ref touchFailures);
                if (count <= 5 || count % 100 == 0) BNL.LogWarning($"Player storage ({backend.Name}) could not record a use of {entry.OwnerKey} (failure #{count}): {ex.GetBaseException().Message}");
            }
        }
        private Entry Load(Lane lane, string ownerKey, string owner)
        {
            if (lane.TryRecall(ownerKey, out Entry entry))
            {
                if (IsCurrent(entry)) return entry;
                lane.Forget(ownerKey);
            }
            entry = new Entry { OwnerKey = ownerKey, Record = new BasisStorageRecord(owner) };
            byte[] blob;
            long lastUsed = 0;
            if (catalog != null ? catalog.TryLoad(ownerKey, out blob, out lastUsed) : backend.TryLoad(ownerKey, out blob))
            {
                if (BasisStorageRecord.TryParse(blob, owner, out BasisStorageRecord record))
                {
                    entry.Record = record;
                    entry.StoredBytes = blob.Length;
                    entry.LastUsed = lastUsed;
                    entry.LastTouched = lastUsed;
                }
                else
                {
                    backend.Quarantine(ownerKey);
                    Interlocked.Add(ref totalBytes, -blob.Length);
                    Interlocked.Decrement(ref totalRecords);
                    BNL.LogError($"Player storage record {ownerKey} failed its integrity check and was set aside; that player starts again from empty storage.");
                }
            }
            lane.Remember(entry);
            return entry;
        }
        private void Run(Lane lane)
        {
            foreach (Work work in lane.Queue.GetConsumingEnumerable())
            {
                try
                {
                    Process(lane, work);
                }
                catch (Exception ex)
                {
                    long count = Interlocked.Increment(ref failures);
                    if (count <= 5 || count % 100 == 0) BNL.LogError($"Player storage ({backend.Name}) failed a {work.Kind} (failure #{count}): {ex.GetBaseException().Message}");
                    lane.Forget(work.OwnerKey);
                    if (work.Kind == WorkKind.Request && !work.Replied)
                    {
                        try { work.Reply(new BasisStorageReply { Op = work.Request.Op, RequestId = work.Request.RequestId, Status = BasisStorageStatus.Unavailable, Limit = BytesPerPlayer }); }
                        catch (Exception replyFailure) { BNL.LogError($"Player storage could not report a failure: {replyFailure.Message}"); }
                    }
                }
                finally
                {
                    work.Done?.Set();
                    work.Countdown?.Signal();
                }
            }
        }
        private enum WorkKind : byte
        {
            Request,
            Evict,
            Inspect,
            Wipe,
            Describe,
            Expire,
        }
        private sealed class Work
        {
            public WorkKind Kind;
            public string Owner, OwnerKey, Origin;
            public BasisStorageRequest Request;
            public Action<BasisStorageReply> Reply;
            public BasisStorageRecord Snapshot;
            public bool Result, Replied, Admitted;
            public long UsedBefore, Freed;
            public ManualResetEventSlim Done;
            public CountdownEvent Countdown;
        }
        private sealed class Entry
        {
            public string OwnerKey;
            public BasisStorageRecord Record;
            public int StoredBytes;
            public long LastUsed, LastTouched;
        }
        private sealed class Budget
        {
            public double Reads, Writes, Bytes;
            public long Stamp;
        }
        private sealed class Lane
        {
            public readonly int Index;
            public readonly BlockingCollection<Work> Queue = new BlockingCollection<Work>(new ConcurrentQueue<Work>(), LaneQueueLimit);
            public readonly Thread Thread;
            private readonly Dictionary<string, LinkedListNode<Entry>> index = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);
            private readonly LinkedList<Entry> order = new LinkedList<Entry>();
            private long cachedBytes;
            public Lane(BasisStorageService service, int index)
            {
                Index = index;
                Thread = new Thread(() => service.Run(this)) { IsBackground = true, Name = $"BasisStorage{index}" };
                Thread.Start();
            }
            public bool TryRecall(string ownerKey, out Entry entry)
            {
                if (!index.TryGetValue(ownerKey, out LinkedListNode<Entry> node))
                {
                    entry = null;
                    return false;
                }
                order.Remove(node);
                order.AddFirst(node);
                entry = node.Value;
                return true;
            }
            public void Remember(Entry entry)
            {
                Forget(entry.OwnerKey);
                index[entry.OwnerKey] = order.AddFirst(entry);
                cachedBytes += entry.Record.BlobLength;
                while (cachedBytes > LaneCacheBytes && order.Count > 1) Forget(order.Last.Value.OwnerKey);
            }
            public void Forget(string ownerKey)
            {
                if (ownerKey == null || !index.TryGetValue(ownerKey, out LinkedListNode<Entry> node)) return;
                index.Remove(ownerKey);
                order.Remove(node);
                cachedBytes -= node.Value.Record.BlobLength;
            }
        }
    }
}
