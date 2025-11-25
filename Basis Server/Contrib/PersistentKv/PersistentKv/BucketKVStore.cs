using System;
using System.Threading.Tasks;

namespace PersistentKv
{
    public class BucketKVStore : IKVBucket
    {
        private readonly string _bucketId;

        public string BucketId => _bucketId;

        public BucketKVStore(string bucketId)
        {
            _bucketId = bucketId;
        }

        public Task<KvResult<Unit>> Set(string key, Memory<byte> value, bool create_only = false)
        {
            // ToArray kinda kills the point of Memory. hopefully a different backed will make use of it.
            return BasisPersistentKv.SetKeyAsync(_bucketId, key, value.ToArray(), create_only);
        }
        public Task<KvResult<Memory<byte>>> Get(string key)
        {
            return BasisPersistentKv.GetKeyAsync(_bucketId, key);
        }

        public Task<KvResult<bool>> Delete(string key)
        {
            return BasisPersistentKv.DeleteKeyAsync(_bucketId, key);
        }
        public Task<KvResult<bool>> Exists(string key)
        {
            return BasisPersistentKv.KeyExistsAsync(_bucketId, key);
        }

        public Task<KvResult<(string[] keys, bool more)>> ListKeys(uint offset = 0, uint limit = 10, string? prefix = null)
        {
            return BasisPersistentKv.ListKeysAsync(_bucketId, offset, limit, prefix);
        }
        public Task<KvResult<KvInfo>> KeyInfo(string key)
        {
            return BasisPersistentKv.GetKeyInfoAsync(_bucketId, key);
        }

        public Task<KvResult<QuotaInfo>> GetQuota()
        {
            return BasisPersistentKv.GetQuotaAsync(_bucketId);
        }
    }


}
