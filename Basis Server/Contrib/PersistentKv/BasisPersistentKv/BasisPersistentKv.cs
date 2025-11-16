using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace BasisPersistentKv
{
    // TODO : Merge bucket meta tables
    public interface IKVBucket
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="create_only"></param>
        /// <returns></returns>
        Task<KvResult<Unit>> Set(string key, Memory<byte> value, bool create_only = false);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        Task<KvResult<Memory<byte>>> Get(string key);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <returns>key was found and deleted</returns>
        Task<KvResult<bool>> Delete(string key);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <returns>key exists</returns>
        Task<KvResult<bool>> Exists(string key);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        Task<KvResult<KvInfo>> KeyInfo(string key);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="limit"></param>
        /// <param name="prefix"></param>
        /// <returns>(keys, remaining)</returns>
        Task<KvResult<(string[] keys, bool more)>> ListKeys(uint offset = 0, uint limit = 10, string? prefix = null);
        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        Task<KvResult<QuotaInfo>> GetQuota();
    }
    
    public interface IKVManager : IAsyncDisposable
    {
        // Bucket lifecycle
        Task<KvResult<Unit>> CreateBucket(string bucket_id);
        Task<KvResult<bool>> DeleteBucket(string bucket_id, bool remove_keys = false);
        Task<KvResult<BucketInfo>> GetBucket(string bucket_id);
        Task<KvResult<BucketMeta>> GetBucketMeta(string bucket_id);

        Task<KvResult<string[]>> ListBuckets(
            uint offset = 0,
            uint limit = 50,
            string? prefix = null
        );

        // Quotas
        Task<KvResult<bool>> SetBucketQuotaUsage(string bucket_id, QuotaKind kind, long currentValue);
        Task<KvResult<bool>> SetBucketQuotaMax(string bucket_id, QuotaKind kind, long maxValue);
    }

    public struct BucketMeta
    {
        bool KeyLimitGaurd;
        bool ByteLimitGaurd;
        int CurrentKeys;
        int MaxKeys;
        long CurrentBytes;
        long MaxBytes;

        long TotalOps;
        long TotalReads;
        long TotalWrites;
        long TotalDeletes;
        long LastOpAt;
    }

    public struct BucketInfo
    {
        public bool Exists ;
        public ulong CreationTime;
    }

    public enum QuotaKind
    {
        Keys,
        Bytes,
    }

    public class UserKVStore : IKVBucket
    {
        private readonly string _userId;
        public UserKVStore(string userId)
        {
            _userId = userId;
        }

        public Task<KvResult<Unit>> Set(string key, Memory<byte> value, bool create_only = false)
        {
            // ToArray kinda kills the point of Memory. hopefully a different backed will make use of it.
            return BasisPersistientKVDatabase.SetKeyAsync(_userId, _userId, key, value.ToArray());
        }
        public Task<KvResult<Memory<byte>>> Get(string key)
        {
            return BasisPersistientKVDatabase.GetKeyAsync(_userId, _userId, key);
        }

        public Task<KvResult<bool>> Delete(string key)
        {
            return BasisPersistientKVDatabase.DeleteKeyAsync(_userId, _userId, key);
        }
        public Task<KvResult<bool>> Exists(string key)
        {
            return BasisPersistientKVDatabase.KeyExistsAsync(_userId, _userId, key);
        }

        public Task<KvResult<(string[] keys, bool more)>> ListKeys(uint offset = 0, uint limit = 10, string? prefix = null)
        {
            return BasisPersistientKVDatabase.ListKeysAsync(_userId, _userId, offset, limit, prefix);
        }
        public Task<KvResult<KvInfo>> KeyInfo(string key)
        {
            throw new NotImplementedException();
        }

        public Task<KvResult<QuotaInfo>> GetQuota()
        {
            return BasisPersistientKVDatabase.GetQuotaAsync(_userId, _userId);
        }
    }

    public struct QuotaInfo
    {
        public int CurrentKeys { get; set; }
        public int MaxKeys { get; set; }
        public long CurrentBytes { get; set; }
        public long MaxBytes { get; set; }
    }

    public struct KvInfo
    {
        public ulong creation;
        public ulong lastUpdate;
        public ulong version;
        public ulong valueSize;
    }

    public enum KvError : ushort
    {
        Success = 0,
        // Unknown (fallback)
        Unknown = 1,

        // Bucket errors (1000-1999)
        BucketNotFound = 1000,
        BucketImmutable = 1002,
        BucketIdSize = 1003,
        BucketListLengthInvalid = 1101,
        BucketListOffsetInvalid = 1102,
        KeyNotFound = 1201,

        // Validation errors (2000-2999)
        ValidationKeySize = 2000,
        ValidationValueNull = 2010,
        ValidationValueSize = 2011,
        ValidationBucketIdSize = 2020,
        ValidationLimitInvalid = 2021,

        // Quotas (3000-3999)
        QuotaKeys = 3000,
        QuotaBytes = 3001,
        QuotaKeySize = 3002,
        QuotaValueSize = 3003,

        // Rate limits (4000-4999)
        RateWriteMinute = 4000,
        RateWriteHour = 4001,
        RateReadMinute = 4002,
        RateReadHour = 4003,

        // Database Errors (6000 - 6999)
        DatabaseGenericError = 6001,
        DatabaseUnknownConstraint = 6002,

        // Server Error
        ServerInvalidParameter = 7001,
    }

    /// <summary>
    /// - No limits are enforced for now.
    /// - kv pairs need to be manually removed, deleting a bucket will not delete kv.
    /// </summary>
    public static class BasisPersistientKVDatabase
    {
        private static string _connectionString;
        private static string[] _pragmas;
        private static readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private static bool _initialized = false;

        static BasisPersistientKVDatabase()
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = "basis_kv_data.db",
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                Cache = SqliteCacheMode.Shared,
            }.ToString();


            _pragmas = new[]
            {
                "PRAGMA journal_mode = WAL;",
                "PRAGMA synchronous = normal;",
                "PRAGMA temp_store = MEMORY;",
                "PRAGMA foreign_keys = TRUE;"
            };

            Init().Wait();
        }


        private static async Task Init()
        {
            if (_initialized) return;

            await _initLock.WaitAsync();
            try
            {
                if (_initialized) return;

                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                foreach (var pragma in _pragmas)
                {
                    using var pragma_cmd = connection.CreateCommand();
                    pragma_cmd.CommandText = pragma;
                    await pragma_cmd.ExecuteNonQueryAsync();
                }

                using var cmd = connection.CreateCommand();

                cmd.CommandText = @"
                    -- Main KV storage
                    CREATE TABLE IF NOT EXISTS bucket_kv (
                        -- UUID is always string which sucks
                        bucket_id TEXT NOT NULL,
                        key TEXT NOT NULL,
                        value BLOB NOT NULL,
                        version INTEGER DEFAULT 0,
                        created_at INTEGER DEFAULT (strftime('%s', 'now')),
                        updated_at INTEGER DEFAULT (strftime('%s', 'now')),
                        PRIMARY KEY (bucket_id, key),
                        -- 256 byte (not char!) key limit
                        CONSTRAINT key_size CHECK(length(key) > 0 AND length(cast(key AS BLOB)) <= 256),
                        -- 8kB value limit
                        CONSTRAINT value_size CHECK(length(value) <= 8000),
                        -- might as well enforce, we dont want bucket_id to be too large as its part of primary key
                        CONSTRAINT bucket_id_size CHECK(length(bucket_id) > 0 AND length(cast(bucket_id AS BLOB)) <= 64)
                    );
                
                    CREATE INDEX IF NOT EXISTS idx_bucket_kv_bucket_id
                        ON bucket_kv(bucket_id);

                    -- Bucket registry (tracks buckets)
                    CREATE TABLE IF NOT EXISTS buckets (
                        bucket_id TEXT PRIMARY KEY,
                        created_at INTEGER DEFAULT (strftime('%s', 'now')),
                        CHECK(length(bucket_id) > 0 AND length(cast(bucket_id AS BLOB)) <= 64)
                    );
                
                    -- Bucket meta. Quotas and usage stats.
                    CREATE TABLE IF NOT EXISTS bucket_meta (
                        bucket_id TEXT PRIMARY KEY,
                        key_limit_guard INTEGER DEFAULT FALSE,
                        byte_limit_guard INTEGER DEFAULT FALSE,
                        current_keys INTEGER DEFAULT 0,
                        current_bytes INTEGER DEFAULT 0,
                        -- 1k keys 
                        max_keys INTEGER DEFAULT 1000,
                        -- 16kB total limit. note that 1000 keys @ 8kB/key > 16kB.
                        -- This is intended to encourage smaller values and more keys, which better for databases
                        max_bytes INTEGER DEFAULT 16000,

                        -- Usage statistics
                        total_operations INTEGER DEFAULT 0,
                        total_reads INTEGER DEFAULT 0,
                        total_writes INTEGER DEFAULT 0,
                        total_deletes INTEGER DEFAULT 0,
                        last_operation_at INTEGER DEFAULT (strftime('%s', 'now')),

                        CHECK(total_operations >= 0),
                        CHECK(total_reads >= 0),
                        CHECK(total_writes >= 0),
                        CHECK(total_deletes >= 0),
                        CHECK(current_keys >= 0),
                        CHECK(current_bytes >= 0),
                        CHECK(max_keys >= 0),
                        CHECK(max_bytes >= 0),
                        CHECK(key_limit_guard IN (TRUE, FALSE)),
                        CHECK(byte_limit_guard IN (TRUE, FALSE)),
                        FOREIGN KEY (bucket_id) REFERENCES buckets(bucket_id) ON DELETE CASCADE
                    );

                    -- Triggers go as follows:
                    -- - check for bucket before (insert, update)
                    -- - enforce limits before (insert, update, remove)
                    -- - update limits usage after (insert, update, remove)
                
                    -- Trigger: Check bucket exists before insert
                    CREATE TRIGGER IF NOT EXISTS enforce_bucket_before_insert
                    BEFORE INSERT ON bucket_kv
                    FOR EACH ROW
                    BEGIN
                        SELECT CASE
                            WHEN NOT EXISTS (
                                SELECT 1 FROM buckets 
                                WHERE bucket_id = NEW.bucket_id
                            )
                            THEN RAISE(ABORT, 'U_NOT_FOUND: Bucket does not exist')
                        END;
                    END;
                
                    -- Trigger: enforce quotas before insert
                    CREATE TRIGGER IF NOT EXISTS enforce_quota_before_insert
                    BEFORE INSERT ON bucket_kv
                    FOR EACH ROW
                    BEGIN
                    
                        -- Check key count limit
                        SELECT CASE
                            WHEN (
                                SELECT key_limit_guard = TRUE 
                                       AND current_keys >= max_keys
                                FROM bucket_meta
                                WHERE bucket_id = NEW.bucket_id
                            )
                            THEN RAISE(ABORT, 'Q_KEYS: Key quota exceeded')
                        END;
                    
                        -- Check storage limit if guard is enabled (including key size!)
                        SELECT CASE
                            WHEN (
                                SELECT byte_limit_guard = TRUE 
                                       AND current_bytes + 
                                           length(CAST(NEW.key AS BLOB)) + 
                                           length(CAST(NEW.value AS BLOB)) > max_bytes
                                FROM bucket_meta
                                WHERE bucket_id = NEW.bucket_id
                            )
                            THEN RAISE(ABORT, 'Q_BYTES: Storage quota exceeded')
                        END;
                    END;
                
                    -- Trigger: Update quota and stats after insert
                    CREATE TRIGGER IF NOT EXISTS update_quota_after_insert
                    AFTER INSERT ON bucket_kv
                    FOR EACH ROW
                    BEGIN
                        UPDATE bucket_meta
                        SET current_keys = current_keys + 1,
                            current_bytes = current_bytes + 
                                          length(CAST(NEW.key AS BLOB)) + 
                                          length(CAST(NEW.value AS BLOB))
                        WHERE bucket_id = NEW.bucket_id;
                    END;

                    -- Trigger: Check bucket_id exists. Prevents bucket ownership change before update.
                    CREATE TRIGGER IF NOT EXISTS enforce_bucket_before_update
                    BEFORE UPDATE ON bucket_kv
                    FOR EACH ROW
                    BEGIN
                        SELECT CASE
                            WHEN NOT EXISTS (
                                SELECT 1 FROM buckets 
                                WHERE bucket_id = NEW.bucket_id
                            )
                            THEN RAISE(ABORT, 'U_NOT_FOUND: Bucket does not exist')
                        END;
                        SELECT CASE
                            WHEN NEW.bucket_id != OLD.bucket_id THEN
                                RAISE(ABORT, 'U_IMMUTABLE: Changing bucket_id on bucket_kv rows is not allowed; delete+insert instead')
                        END;
                    END;
                
                    -- Trigger: enforce quota before update
                    CREATE TRIGGER IF NOT EXISTS enforce_quota_before_update
                    BEFORE UPDATE ON bucket_kv
                    FOR EACH ROW
                    BEGIN
                    
                        -- Check storage limit ONLY if guard is enabled (including key size!)
                        SELECT CASE
                            WHEN (
                                SELECT byte_limit_guard = TRUE 
                                       AND current_bytes 
                                           - length(CAST(OLD.key AS BLOB)) - length(CAST(OLD.value AS BLOB))
                                           + length(CAST(NEW.key AS BLOB)) + length(CAST(NEW.value AS BLOB))
                                           > max_bytes
                                FROM bucket_meta
                                WHERE bucket_id = NEW.bucket_id
                            )
                            THEN RAISE(ABORT, 'Q_BYTES: Storage quota exceeded')
                        END;
                    END;
                
                    -- Update quota and stats AFTER update
                    CREATE TRIGGER IF NOT EXISTS update_quota_after_update
                    AFTER UPDATE ON bucket_kv
                    FOR EACH ROW
                    BEGIN
                        UPDATE bucket_meta
                        SET current_bytes = current_bytes 
                                          - length(CAST(OLD.key AS BLOB)) - length(CAST(OLD.value AS BLOB))
                                          + length(CAST(NEW.key AS BLOB)) + length(CAST(NEW.value AS BLOB))
                        WHERE bucket_id = NEW.bucket_id;
                    END;
                
                    -- !! Bucket deletion causes cascade deletes across database, be careful with delete triggers. !!
                    -- bucket_kv does NOT use cascade so its fine.

                    -- Update quota and stats AFTER delete.
                    CREATE TRIGGER IF NOT EXISTS update_quota_after_delete
                    AFTER DELETE ON bucket_kv
                    FOR EACH ROW
                    BEGIN
                        -- Update quotas
                        UPDATE bucket_meta
                        SET current_keys = current_keys - 1,
                            current_bytes = current_bytes
                                - length(CAST(OLD.key AS BLOB))
                                - length(CAST(OLD.value AS BLOB))
                        WHERE bucket_id = OLD.bucket_id;
                    END;
                ";

                await cmd.ExecuteNonQueryAsync();
                _initialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public static async Task<KvResult<Unit>> SetKeyAsync(string callingUserId, string bucketId, string key, byte[] value)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();



                using var transaction = connection.BeginTransaction();
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;

                    cmd.CommandText = @"
                        INSERT INTO bucket_kv (bucket_id, key, value) 
                        VALUES ($bucketId, $key, $value)
                        ON CONFLICT (bucket_id, key) DO UPDATE
                        SET value = excluded.value,
                            updated_at = strftime('%s', 'now')
                    ";

                    cmd.Parameters.AddWithValue("$bucketId", bucketId);
                    cmd.Parameters.AddWithValue("$key", key);
                    cmd.Parameters.AddWithValue("$value", value);

                    await cmd.ExecuteNonQueryAsync();
                    transaction.Commit();
                    return KvResult<Unit>.Ok(new Unit());
                }
                catch (SqliteException ex)
                {
                    return KvResult<Unit>.FromSqlException(ex);
                }
                catch (Exception _e)
                {
                    return KvResult<Unit>.Fail(KvError.Unknown, "Unknown Server Error" + _e.Message);
                }
            }
            catch (SqliteException ex)
            {
                return KvResult<Unit>.FromSqlException(ex);
            }
            catch
            {
                return KvResult<Unit>.Fail(KvError.Unknown, "Unknown Server Error");
            }
        }
        public static async Task<KvResult<bool>> DeleteKeyAsync(string callingUserId, string bucketId, string key)
        {
            try
            {

                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();
                try
                {
                    bool deleted = false;

                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;

                    cmd.CommandText = @"
                        DELETE FROM bucket_kv 
                        WHERE bucket_id = $bucketId AND key = $key
                    ";

                    cmd.Parameters.AddWithValue("$bucketId", bucketId);
                    cmd.Parameters.AddWithValue("$key", key);

                    int rowsAffected = await cmd.ExecuteNonQueryAsync();
                    deleted = rowsAffected > 0;

                    // Update read counters & usage stats
                    using (var updateCmd = connection.CreateCommand())
                    {
                        updateCmd.Transaction = transaction;
                        updateCmd.CommandText = @"
                            UPDATE bucket_meta
                            SET total_operations = total_operations + 1,
                                total_deletes    = total_deletes      + 1,
                                last_operation_at = strftime('%s','now')
                            WHERE bucket_id = $bucketId;
                        ";
                        updateCmd.Parameters.AddWithValue("$bucketId", callingUserId);
                        await updateCmd.ExecuteNonQueryAsync();
                    }

                    transaction.Commit();
                    return KvResult<bool>.Ok(deleted);
                }
                catch (SqliteException ex)
                {
                    return KvResult<bool>.FromSqlException(ex);
                }
                catch (Exception _e)
                {
                    return KvResult<bool>.Fail(KvError.Unknown, "Unknown Server Error");
                }
            }
            catch (SqliteException ex)
            {
                return KvResult<bool>.FromSqlException(ex);
            }
            catch
            {
                return KvResult<bool>.Fail(KvError.Unknown, "Unknown Server Error");
            }
        }

        public static async Task<KvResult<Memory<byte>>> GetKeyAsync(string callingUserId, string bucketId, string key)
        {
            try
            {

                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();
                try
                {
                    byte[]? value = null;

                    // Read value
                    using (var selectCmd = connection.CreateCommand())
                    {
                        selectCmd.Transaction = transaction;
                        selectCmd.CommandText = @"
                            SELECT value
                            FROM bucket_kv
                            WHERE bucket_id = $bucketId AND key = $key
                        ";
                        selectCmd.Parameters.AddWithValue("$bucketId", bucketId);
                        selectCmd.Parameters.AddWithValue("$key", key);

                        
                        using var reader = await selectCmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow);
                        if (await reader.ReadAsync())
                        {
                            // value is stored as BLOB -> byte[]
                            var rawValue = reader.GetValue(0);
                            if (rawValue != DBNull.Value)
                            {
                                value = (byte[]?)rawValue;
                            }
                        }
                    }

                    // Update read counters & usage stats
                    using (var updateCmd = connection.CreateCommand())
                    {
                        updateCmd.Transaction = transaction;
                        updateCmd.CommandText = @"
                            UPDATE bucket_meta
                            SET total_operations = total_operations + 1,
                                total_reads      = total_reads      + 1,
                                last_operation_at = strftime('%s','now')
                            WHERE bucket_id = $bucketId;
                        ";
                        updateCmd.Parameters.AddWithValue("$bucketId", callingUserId);
                        await updateCmd.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();

                    if (value != null)
                    {
                        return KvResult<Memory<byte>>.Ok(value);
                    }
                    else
                    {
                        return KvResult<Memory<byte>>.Fail(KvError.KeyNotFound, "Key not found");
                    }
                }
                catch (SqliteException ex)
                {
                    return KvResult<Memory<byte>>.FromSqlException(ex);
                }
                catch (Exception)
                {
                    return KvResult<Memory<byte>>.Fail(KvError.Unknown, "Unknown Server Error");
                }
            }
            catch (SqliteException ex)
            {
                return KvResult<Memory<byte>>.FromSqlException(ex);
            }
            catch
            {
                return KvResult<Memory<byte>>.Fail(KvError.Unknown, "Unknown Server Error");
            }
        }


        public static async Task<KvResult<bool>> KeyExistsAsync(string callingUserId, string bucketId, string key)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();
                try
                {
                    bool value = false;

                    // Check value
                    using (var existCmd = connection.CreateCommand())
                    {
                        existCmd.Transaction = transaction;
                        existCmd.CommandText = @"
                            SELECT EXISTS(
                                SELECT 1 
                                FROM bucket_kv
                                WHERE bucket_id = $bucketId AND key = $key
                            );
                        ";
                        existCmd.Parameters.AddWithValue("$bucketId", bucketId);
                        existCmd.Parameters.AddWithValue("$key", key);

                        value = (long)(existCmd.ExecuteScalar() ?? 0) == 1;                       
                    }

                    // Update read counters & usage stats
                    using (var updateCmd = connection.CreateCommand())
                    {
                        updateCmd.Transaction = transaction;
                        updateCmd.CommandText = @"
                            UPDATE bucket_meta
                            SET total_operations = total_operations + 1,
                                total_reads      = total_reads      + 1,
                                last_operation_at = strftime('%s','now')
                            WHERE bucket_id = $bucketId;
                        ";
                        updateCmd.Parameters.AddWithValue("$bucketId", callingUserId);
                        await updateCmd.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();

                    return KvResult<bool>.Ok(value);
                }
                catch (SqliteException ex)
                {
                    return KvResult<bool>.FromSqlException(ex);
                }
                catch (Exception)
                {
                    return KvResult<bool>.Fail(KvError.Unknown, "Unknown Server Error");
                }
            }
            catch (SqliteException ex)
            {
                return KvResult<bool>.FromSqlException(ex);
            }
            catch
            {
                return KvResult<bool>.Fail(KvError.Unknown, "Unknown Server Error");
            }
        }


        public static async Task<KvResult<(string[] keys, bool more)>> ListKeysAsync(
            string callingUserId,
            string bucketId,
            uint offset = 0,
            uint limit = 10,
            string? prefix = null)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();
                try
                {
                    if (!await UserExistsCmd(connection, transaction, bucketId))
                        return KvResult<(string[], bool)>.Fail(
                            KvError.BucketNotFound,
                            "Bucket does not exist");

                    var keys = new List<string>();

                    int pageSize = (int)limit;
                    int fetchSize = pageSize + 1;

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;

                        if (!string.IsNullOrEmpty(prefix))
                        {
                            cmd.CommandText = @"
                                SELECT key
                                FROM bucket_kv
                                WHERE bucket_id = $bucketId AND key LIKE $prefix || '%'
                                ORDER BY key
                                LIMIT $limitPlusOne OFFSET $offset;
                            ";
                            cmd.Parameters.AddWithValue("$prefix", prefix);
                        }
                        else
                        {
                            cmd.CommandText = @"
                                SELECT key
                                FROM bucket_kv
                                WHERE bucket_id = $bucketId
                                ORDER BY key
                                LIMIT $limitPlusOne OFFSET $offset;
                            ";
                        }

                        cmd.Parameters.AddWithValue("$bucketId", bucketId);
                        cmd.Parameters.AddWithValue("$limitPlusOne", fetchSize);
                        cmd.Parameters.AddWithValue("$offset", (int)offset);

                        using var reader = await cmd.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            keys.Add(reader.GetString(0));
                        }
                    }

                    // Determine if there are more results
                    bool more = keys.Count > pageSize;
                    if (more)
                    {
                        // Drop the extra row so we only return up to "limit" keys
                        keys.RemoveAt(keys.Count - 1);
                    }

                    // Update read counters & usage stats
                    using (var updateCmd = connection.CreateCommand())
                    {
                        updateCmd.Transaction = transaction;
                        updateCmd.CommandText = @"
                            INSERT OR IGNORE INTO bucket_meta (bucket_id)
                            VALUES ($bucketId);

                            UPDATE bucket_meta
                            SET total_operations = total_operations + 1,
                                total_reads      = total_reads      + 1,
                                last_operation_at = strftime('%s','now')
                            WHERE bucket_id = $bucketId;
                        ";
                        // This should be the bucket ID, not callingUserId
                        updateCmd.Parameters.AddWithValue("$bucketId", bucketId);
                        await updateCmd.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();

                    return KvResult<(string[], bool)>.Ok((keys.ToArray(), more));
                }
                catch (SqliteException ex)
                {
                    return KvResult<(string[], bool)>.FromSqlException(ex);
                }
                catch
                {
                    return KvResult<(string[], bool)>.Fail(KvError.Unknown, "Unknown Server Error");
                }
            }
            catch (SqliteException ex)
            {
                return KvResult<(string[], bool)>.FromSqlException(ex);
            }
            catch
            {
                return KvResult<(string[], bool)>.Fail(KvError.Unknown, "Unknown Server Error");
            }
        }



        public static async Task<KvResult<QuotaInfo>> GetQuotaAsync(string callingUserId, string userId)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();
                try
                {
                    if (!await UserExistsCmd(connection, transaction, userId))
                        return KvResult<QuotaInfo>.Fail(KvError.BucketNotFound, "Bucket does not exist");
                    QuotaInfo? quota = null;

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;

                        cmd.CommandText = @"
                            SELECT current_keys, max_keys, current_bytes, max_bytes
                            FROM bucket_meta
                            WHERE bucket_id = $bucketId;
                        ";
                        cmd.Parameters.AddWithValue("$bucketId", userId);

                        using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow);
                        if (await reader.ReadAsync())
                        {
                            quota = new QuotaInfo
                            {
                                CurrentKeys = reader.GetInt32(0),
                                MaxKeys = reader.GetInt32(1),
                                CurrentBytes = reader.GetInt64(2),
                                MaxBytes = reader.GetInt64(3),
                            };
                        }
                    }

                    // Update read counters & usage stats
                    using (var updateCmd = connection.CreateCommand())
                    {
                        updateCmd.Transaction = transaction;
                        updateCmd.CommandText = @"
                            INSERT OR IGNORE INTO bucket_meta (bucket_id)
                            VALUES ($bucketId);

                            UPDATE bucket_meta
                            SET total_operations = total_operations + 1,
                                total_reads      = total_reads      + 1,
                                last_operation_at = strftime('%s','now')
                            WHERE bucket_id = $bucketId;
                        ";
                        updateCmd.Parameters.AddWithValue("$bucketId", callingUserId);
                        await updateCmd.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();

                    if (quota == null)
                        return KvResult<QuotaInfo>.Fail(KvError.BucketNotFound, "User not found");

                    return KvResult<QuotaInfo>.Ok((QuotaInfo)quota);
                }
                catch (SqliteException ex)
                {
                    return KvResult<QuotaInfo>.FromSqlException(ex);
                }
                catch
                {
                    return KvResult<QuotaInfo>.Fail(KvError.Unknown, "Unknown Server Error");
                }
            }
            catch (SqliteException ex)
            {
                return KvResult<QuotaInfo>.FromSqlException(ex);
            }
            catch
            {
                return KvResult<QuotaInfo>.Fail(KvError.Unknown, "Unknown Server Error");
            }
        }

        // --- Admin Only API ---

        public static Task<KvResult<Unit>> SetKeyCountGuard(string userId, bool enable)
        {
            return SetQuotaGuardInternal(userId, "key_limit_guard", enable);
        }

        public static Task<KvResult<Unit>> SetByteSizeGaurd(string userId, bool enable)
        {
            return SetQuotaGuardInternal(userId, "byte_limit_guard", enable);
        }

        public static async Task<KvResult<Unit>> SetQuotaGuardInternal(string userId, string guardColumnName, bool enable)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();
                try
                {
                    bool value = false;

                    // Check value
                    using (var gaurdCmd = connection.CreateCommand())
                    {
                        gaurdCmd.Transaction = transaction;
                        gaurdCmd.CommandText = @"
                            UPDATE bucket_meta
                            SET $guardColumn = $enable
                            WHERE bucket_id = $bucketId;
                        ";
                        gaurdCmd.Parameters.AddWithValue("$guardColumn", guardColumnName);
                        gaurdCmd.Parameters.AddWithValue("$bucketId", userId);
                        gaurdCmd.Parameters.AddWithValue("$enable", enable);

                        value = (long)(gaurdCmd.ExecuteScalar() ?? 0) == 1;
                    }

                    await transaction.CommitAsync();

                    return KvResult<Unit>.Ok(new Unit());
                }
                catch (SqliteException ex)
                {
                    return KvResult<Unit>.FromSqlException(ex);
                }
                catch (Exception)
                {
                    return KvResult<Unit>.Fail(KvError.Unknown, "Unknown Server Error");
                }
            }
            catch (SqliteException ex)
            {
                return KvResult<Unit>.FromSqlException(ex);
            }
            catch
            {
                return KvResult<Unit>.Fail(KvError.Unknown, "Unknown Server Error");
            }
        }

        //TODO bucket deletion. this MUST remove all keys the bucket has, otherwise thats untracked storage.

        // --- Internal API ---

        public static async Task<KvResult<Unit>> AddUserAsync(string bucketId)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();
                try
                {
                    // Insert bucket
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            INSERT OR IGNORE INTO buckets (bucket_id)
                            VALUES ($bucketId);
                        ";
                        cmd.Parameters.AddWithValue("$bucketId", bucketId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // Insert bucket meta
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            INSERT OR IGNORE INTO bucket_meta (bucket_id)
                            VALUES ($bucketId);
                        ";
                        cmd.Parameters.AddWithValue("$bucketId", bucketId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    transaction.Commit();
                    return KvResult<Unit>.Ok(new Unit());
                }
                catch (SqliteException ex)
                {
                    return KvResult<Unit>.FromSqlException(ex);
                }
                catch
                {
                    return KvResult<Unit>.Fail(KvError.Unknown, "Unknown Server Error");
                }
            }
            catch (SqliteException ex)
            {
                return KvResult<Unit>.FromSqlException(ex);
            }
            catch
            {
                return KvResult<Unit>.Fail(KvError.Unknown, "Unknown Server Error");
            }
        }


        private static async Task<bool> UserExistsCmd(SqliteConnection conn, SqliteTransaction tx, string userId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            cmd.CommandText = @"
                SELECT EXISTS(
                    SELECT 1 FROM buckets WHERE bucket_id = $bucketId
                );
            ";

            cmd.Parameters.AddWithValue("$bucketId", userId);

            var result = (long)(await cmd.ExecuteScalarAsync() ?? 0);
            return result == 1;
        }
    }


    public struct Unit { }

    public struct KvResult<T>
    {
        public KvError ErrorCode;
        public string Message;
        public T? Value;

        public KvResult(KvError code, string message, T? value)
        {
            ErrorCode = code;
            Message = message;
            Value = value;
        }

        public static KvResult<T> Ok(T value)
        {
            return new KvResult<T>(KvError.Success, "", value);
        }
        public static KvResult<T> Fail(KvError code, string msg)
        {
           return new(code, msg, default);
        }

        public static KvResult<T> FromSqlException(SqliteException ex)
        {
            var message = ex.Message;

            // Map SQLite error codes
            var kvError = ex.SqliteErrorCode switch
            {
                0 => KvError.Success,
                19 => KvError.DatabaseUnknownConstraint, // temp error
                > 0 and < 26 => KvError.DatabaseGenericError, // flatten sqlite errors
                _ => KvError.Unknown,
            };

            // Refine constraint errors based on message content
            if (kvError == KvError.DatabaseUnknownConstraint)
            {
                return Fail(FromSqlMessage(message), ex.Message);
            }
            else if (kvError == KvError.DatabaseGenericError)
            {
                return Fail(kvError, ex.Message);
            }
            else return Fail(kvError, ex.Message);
        }

        private static KvError FromSqlMessage(string message)
        {
            return message switch
            {
                var s when s.StartsWith("U_NOT_FOUND") => KvError.BucketNotFound,
                var s when s.StartsWith("U_IMMUTABLE") => KvError.BucketImmutable,
                var s when s.StartsWith("Q_KEYS") => KvError.QuotaKeys,
                var s when s.StartsWith("Q_BYTES") => KvError.QuotaBytes,
                var s when s.StartsWith("Q_KEY_SIZE") => KvError.QuotaKeySize,
                var s when s.StartsWith("Q_VAL_SIZE") => KvError.QuotaValueSize,
                // constraint errors
                var s when s.Contains("value_size") => KvError.ValidationValueSize,
                var s when s.Contains("bucket_id_size") => KvError.BucketIdSize,
                var s when s.Contains("key_size") => KvError.ValidationKeySize,
                _ => KvError.Unknown,
            };
        }

    }
}
