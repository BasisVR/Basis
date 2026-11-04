using System;

namespace BasisNetworkServer.Storage
{
    public interface IBasisStorageBackend : IDisposable
    {
        string Name { get; }
        void Open();
        bool TryLoad(string ownerKey, out byte[] blob);
        bool TrySave(string ownerKey, byte[] blob, long expectedRevision, long revision);
        bool TryDelete(string ownerKey, long expectedRevision);
        void Quarantine(string ownerKey);
        void Measure(out long records, out long bytes);
    }
    public interface IBasisStorageCatalog
    {
        bool TryLoad(string ownerKey, out byte[] blob, out long lastUsed);
        void Touch(string ownerKey, long lastUsed);
        void List(long usedBefore, Func<BasisStorageRecordInfo, bool> visit);
        bool TryExpire(string ownerKey, long usedBefore, out long bytes);
    }
    public interface IBasisSharedStorageBackend
    {
        bool TryReadRevision(string ownerKey, out long revision);
    }
    public struct BasisStorageRecordInfo
    {
        public string OwnerKey;
        public long Bytes;
        public long LastUsed;
    }
}
