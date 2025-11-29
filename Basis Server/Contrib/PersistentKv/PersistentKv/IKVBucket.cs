using System;
using System.Threading.Tasks;

namespace PersistentKv
{
    /// <summary>
    /// Implementers of a KV bucket must reasonably gaurentee values are persisted across restarts and buckets.
    /// It is recommended that implementers enforce per-bucket quotas and rate limits.
    /// </summary>
    public interface IKVBucket
    {
        /// <summary>
        /// Set the value for key. 
        /// </summary>
        /// <param name="key">key</param>
        /// <param name="value">binary value to store</param>
        /// <param name="create_only">store only if key does not already exist, otherwise error</param>
        /// <returns></returns>
        Task<KvResult<Unit>> Set(string key, Memory<byte> value, bool create_only = false);
        /// <summary>
        /// Get the value for key.
        /// </summary>
        /// <param name="key">key</param>
        /// <returns>value of the key or an error</returns>
        Task<KvResult<Memory<byte>>> Get(string key);
        /// <summary>
        /// Delete the key and value pair.
        /// </summary>
        /// <param name="key">key</param>
        /// <returns>bool: key successfully deleted, otherwise error</returns>
        Task<KvResult<bool>> Delete(string key);
        /// <summary>
        /// Check if the key exists.
        /// </summary>
        /// <param name="key">key</param>
        /// <returns>bool: key exists, otherwise error</returns>
        Task<KvResult<bool>> Exists(string key);
        /// <summary>
        /// Get the info for a key.
        /// </summary>
        /// <param name="key">key</param>
        /// <returns>information about the key, otherwise error</returns>
        Task<KvResult<KvInfo>> KeyInfo(string key);
        /// <summary>
        /// List keys in the bucket.
        /// </summary>
        /// <param name="offset">offset to start listing keys from</param>
        /// <param name="limit">maximum number of keys to list</param>
        /// <param name="prefix">prefix to filter keys</param>
        /// <returns>(found keys, more keys to list), otherwise error</returns>
        Task<KvResult<(string[] keys, bool more)>> ListKeys(uint offset = 0, uint limit = 10, string? prefix = null);
        /// <summary>
        /// Get quota (limits and usage) of this bucket.
        /// </summary>
        /// <returns>quota information, otherwise error</returns>
        Task<KvResult<QuotaInfo>> GetQuota();
    }

    public struct QuotaInfo
    {
        public int CurrentKeys;
        public int MaxKeys;
        public long CurrentBytes;
        public long MaxBytes;
    }

    public struct KvInfo
    {
        public ulong creation;
        public ulong lastUpdate;
        public ulong version;
        public ulong valueSize;
    }
}
