using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace BasisNetworkServer.Storage
{
    public sealed class BasisMemoryStorageBackend : IBasisStorageBackend, IBasisStorageCatalog
    {
        private readonly ConcurrentDictionary<string, Stored> store = new ConcurrentDictionary<string, Stored>(StringComparer.Ordinal);
        public string Name => "memory";
        public void Open()
        {
        }
        public bool TryLoad(string ownerKey, out byte[] blob) => TryLoad(ownerKey, out blob, out _);
        public bool TryLoad(string ownerKey, out byte[] blob, out long lastUsed)
        {
            if (store.TryGetValue(ownerKey, out Stored stored))
            {
                blob = stored.Blob;
                lastUsed = Interlocked.Read(ref stored.LastUsed);
                return true;
            }
            blob = null;
            lastUsed = 0;
            return false;
        }
        public bool TrySave(string ownerKey, byte[] blob, long expectedRevision, long revision)
        {
            if (blob == null || blob.Length > BasisStorageRecord.MaxBlobBytes) throw new ArgumentException("Storage record is missing or too large.", nameof(blob));
            store[ownerKey] = new Stored { Blob = blob, LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            return true;
        }
        public bool TryDelete(string ownerKey, long expectedRevision)
        {
            store.TryRemove(ownerKey, out _);
            return true;
        }
        public void Quarantine(string ownerKey) => store.TryRemove(ownerKey, out _);
        public void Measure(out long records, out long bytes)
        {
            records = 0;
            bytes = 0;
            foreach (Stored stored in store.Values)
            {
                records++;
                bytes += stored.Blob.Length;
            }
        }
        public void Touch(string ownerKey, long lastUsed)
        {
            if (!store.TryGetValue(ownerKey, out Stored stored)) return;
            for (long current = Interlocked.Read(ref stored.LastUsed); current < lastUsed; current = Interlocked.Read(ref stored.LastUsed))
            {
                if (Interlocked.CompareExchange(ref stored.LastUsed, lastUsed, current) == current) return;
            }
        }
        public void List(long usedBefore, Func<BasisStorageRecordInfo, bool> visit)
        {
            foreach (KeyValuePair<string, Stored> pair in store)
            {
                long lastUsed = Interlocked.Read(ref pair.Value.LastUsed);
                if (lastUsed < usedBefore && !visit(new BasisStorageRecordInfo { OwnerKey = pair.Key, Bytes = pair.Value.Blob.Length, LastUsed = lastUsed })) return;
            }
        }
        public bool TryExpire(string ownerKey, long usedBefore, out long bytes)
        {
            bytes = 0;
            if (!store.TryGetValue(ownerKey, out Stored stored) || Interlocked.Read(ref stored.LastUsed) >= usedBefore) return false;
            if (!((ICollection<KeyValuePair<string, Stored>>)store).Remove(new KeyValuePair<string, Stored>(ownerKey, stored))) return false;
            bytes = stored.Blob.Length;
            return true;
        }
        public void Dispose() => store.Clear();
        private sealed class Stored
        {
            public byte[] Blob;
            public long LastUsed;
        }
    }
}
