using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace PersistentKv
{

    /// <summary>
    /// - No limits are enforced for now (schema defaults guards to FALSE).
    /// - kv pairs need to be manually removed, deleting a bucket will not delete entries.
    /// - Management API needs an interface, therefore internal for now.
    /// </summary>
    public static class BasisPersistentKv
    {
        private static string _connectionString;
        private static string[] _pragmas;
        private static readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private static SqliteConnection? _sharedConnection;
        private static readonly object _connectionUseLock = new();
        private static bool _initialized = false;
        public static bool EnableStatsTracking { get; set; } = true;

        static BasisPersistentKv()
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = "basis_kv_data.db",
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString();

            _pragmas = new[]
            {
                "PRAGMA journal_mode = WAL;",
                "PRAGMA synchronous = normal;",
                "PRAGMA temp_store = MEMORY;",
                "PRAGMA foreign_keys = TRUE;",
                "PRAGMA case_sensitive_like = ON;"
                // PRAGMA user_version = 1;
            };

            Init();
        }

        private static SqliteConnection GetConnection()
        {
            if (_sharedConnection == null)
            {
                throw new InvalidOperationException("Database connection not initialized.");
            }

            if (_sharedConnection.State != System.Data.ConnectionState.Open)
            {
                _sharedConnection.Open();
            }

            return _sharedConnection;
        }


        private static void Init()
        {
            if (_initialized) return;

            _initLock.Wait();
            try
            {
                if (_initialized) return;

                SqliteConnection? connection = null;
                try
                {
                    connection = new SqliteConnection(_connectionString);
                    connection.Open();

                    foreach (var pragma in _pragmas)
                    {
                        using var pragma_cmd = connection.CreateCommand();
                        pragma_cmd.CommandText = pragma;
                        pragma_cmd.ExecuteNonQuery();
                    }

                    using var cmd = connection.CreateCommand();

                    cmd.CommandText = @"
                    -- Main KV storage
                    CREATE TABLE IF NOT EXISTS bucket_kv (
                        -- UUID is always string which sucks
                        bucket_id TEXT NOT NULL,
                        key TEXT NOT NULL,
                        value BLOB NOT NULL,
                        version INTEGER DEFAULT 1,
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
                    
                    CREATE INDEX IF NOT EXISTS idx_bucket_kv_bucket_updated
                        ON bucket_kv(bucket_id, updated_at DESC);

                    -- Bucket registry (tracks buckets)
                    CREATE TABLE IF NOT EXISTS buckets (
                        bucket_id TEXT PRIMARY KEY,
                        created_at INTEGER DEFAULT (strftime('%s', 'now')),
                        CHECK(length(bucket_id) > 0 AND length(cast(bucket_id AS BLOB)) <= 64)
                    );
                
                    -- Bucket meta. Quotas and usage stats.
                    CREATE TABLE IF NOT EXISTS bucket_meta (
                        bucket_id TEXT PRIMARY KEY,
                        key_limit_guard INTEGER DEFAULT TRUE,
                        byte_limit_guard INTEGER DEFAULT TRUE,
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

                        -- Check key count limit (only for new keys, not updates)
                        SELECT CASE
                            WHEN (
                                SELECT key_limit_guard = TRUE
                                       AND current_keys >= max_keys
                                       AND NOT EXISTS (
                                           SELECT 1 FROM bucket_kv
                                           WHERE bucket_id = NEW.bucket_id AND key = NEW.key
                                       )
                                FROM bucket_meta
                                WHERE bucket_id = NEW.bucket_id
                            )
                            THEN RAISE(ABORT, 'Q_KEYS: Key quota exceeded')
                        END;

                        -- Check storage limit if guard is enabled (including key size!)
                        -- For existing keys (updates), account for old size being removed
                        SELECT CASE
                            WHEN EXISTS (
                                SELECT 1 FROM bucket_meta
                                WHERE bucket_id = NEW.bucket_id
                                  AND byte_limit_guard = TRUE
                                  AND (
                                      -- Calculate projected bytes after this operation
                                      current_bytes
                                      - COALESCE((SELECT SUM(length(CAST(key AS BLOB)) + length(CAST(value AS BLOB)))
                                         FROM bucket_kv
                                         WHERE bucket_id = NEW.bucket_id AND key = NEW.key), 0)
                                      + length(CAST(NEW.key AS BLOB)) + length(CAST(NEW.value AS BLOB))
                                  ) > max_bytes
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

                        UPDATE bucket_kv
                        SET version = OLD.version + 1
                        WHERE rowid = OLD.rowid;

                        -- Auto-update timestamp
                        SELECT NEW.updated_at = strftime('%s', 'now');
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
                
                    -- !! Bucket deletion causes cascade deletes, be careful with delete triggers. !!
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

                    cmd.ExecuteNonQuery();
                    _sharedConnection = connection;
                    connection = null;
                    _initialized = true;
                }
                finally
                {
                    connection?.Dispose();
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        internal static Task<KvResult<Unit>> SetKeyAsync(string bucketId, string key, byte[] value, bool createOnly = false) =>
            Task.FromResult(SetKeySync(bucketId, key, value, createOnly));

        internal static KvResult<Unit> SetKeySync(string bucketId, string key, byte[] value, bool createOnly)
        {
            if (string.IsNullOrEmpty(key))
            {
                return KvResult<Unit>.Fail(KvError.ValidationKeySize, "Key must not be empty");
            }

            var keyByteCount = Encoding.UTF8.GetByteCount(key);
            if (keyByteCount > 256)
            {
                return KvResult<Unit>.Fail(KvError.ValidationKeySize, "Key exceeds maximum size of 256 bytes");
            }

            if (value == null)
            {
                return KvResult<Unit>.Fail(KvError.ValidationValueNull, "Value must not be null");
            }

            if (value.Length > 8000)
            {
                return KvResult<Unit>.Fail(KvError.ValidationValueSize, "Value exceeds maximum size of 8000 bytes");
            }

            lock (_connectionUseLock)
            {
                try
                {
                    var connection = GetConnection();

                    using var transaction = connection.BeginTransaction();

                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;

                    if (createOnly)
                    {
                        cmd.CommandText = @"
                                INSERT INTO bucket_kv (bucket_id, key, value)
                                VALUES ($bucketId, $key, $value)
                                ON CONFLICT (bucket_id, key) DO NOTHING
                            ";
                    }
                    else
                    {
                        cmd.CommandText = @"
                                INSERT INTO bucket_kv (bucket_id, key, value)
                                VALUES ($bucketId, $key, $value)
                                ON CONFLICT (bucket_id, key) DO UPDATE
                                SET value = excluded.value,
                                    version = version + 1,
                                    updated_at = strftime('%s', 'now')
                            ";
                    }

                    cmd.Parameters.AddWithValue("$bucketId", bucketId);
                    cmd.Parameters.AddWithValue("$key", key);
                    cmd.Parameters.AddWithValue("$value", value);

                    var rowsAffected = cmd.ExecuteNonQuery();

                    if (createOnly && rowsAffected == 0)
                    {
                        transaction.Rollback();
                        return KvResult<Unit>.Fail(KvError.KeyAlreadyExists, "Key already exists");
                    }

                    TrackBucketStats(connection, transaction, bucketId, BucketStatType.Write);

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
        }
        internal static Task<KvResult<bool>> DeleteKeyAsync(string bucketId, string key) =>
            Task.FromResult(DeleteKeySync(bucketId, key));

        internal static KvResult<bool> DeleteKeySync(string bucketId, string key)
        {
            var keyByteCount = Encoding.UTF8.GetByteCount(key);
            if (keyByteCount == 0 || keyByteCount > 256)
            {
                return KvResult<bool>.Fail(KvError.ValidationKeySize, "Key is empty or exceeds maximum size of 256 bytes");
            }

            lock (_connectionUseLock)
            {
                try
                {
                    var connection = GetConnection();

                    using var transaction = connection.BeginTransaction();

                    if (!BucketExistsCmd(connection, transaction, bucketId))
                        return KvResult<bool>.Fail(KvError.BucketNotFound, "Bucket does not exist");

                    bool deleted = false;

                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;

                    cmd.CommandText = @"
                            DELETE FROM bucket_kv
                            WHERE bucket_id = $bucketId AND key = $key
                        ";

                    cmd.Parameters.AddWithValue("$bucketId", bucketId);
                    cmd.Parameters.AddWithValue("$key", key);

                    int rowsAffected = cmd.ExecuteNonQuery();
                    deleted = rowsAffected > 0;

                    TrackBucketStats(connection, transaction, bucketId, BucketStatType.Delete);

                    transaction.Commit();
                    return KvResult<bool>.Ok(deleted);

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
        }

        internal static Task<KvResult<Memory<byte>>> GetKeyAsync(string bucketId, string key) =>
            Task.FromResult(GetKeySync(bucketId, key));

        internal static KvResult<Memory<byte>> GetKeySync(string bucketId, string key)
        {
            var keyByteCount = Encoding.UTF8.GetByteCount(key);
            if (keyByteCount == 0 || keyByteCount > 256)
            {
                return KvResult<Memory<byte>>.Fail(KvError.ValidationKeySize, "Key is empty or exceeds maximum size of 256 bytes");
            }

            lock (_connectionUseLock)
            {
                try
                {
                    var connection = GetConnection();

                    using var transaction = connection.BeginTransaction(deferred: true);
                    if (!BucketExistsCmd(connection, transaction, bucketId))
                        return KvResult<Memory<byte>>.Fail(KvError.BucketNotFound, "Bucket does not exist");

                    byte[]? value = null;

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

                        using var reader = selectCmd.ExecuteReader(System.Data.CommandBehavior.SingleRow);
                        if (reader.Read())
                        {
                            var rawValue = reader.GetValue(0);
                            if (rawValue != DBNull.Value)
                            {
                                value = (byte[]?)rawValue;
                            }
                        }
                    }

                    TrackBucketStats(connection, transaction, bucketId, BucketStatType.Read);

                    var result = value != null
                        ? KvResult<Memory<byte>>.Ok(value)
                        : KvResult<Memory<byte>>.Fail(KvError.KeyNotFound, "Key not found");

                    transaction.Commit();
                    return result;

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
        }


        internal static Task<KvResult<KvInfo>> GetKeyInfoAsync(string bucketId, string key) =>
            Task.FromResult(GetKeyInfoSync(bucketId, key));

        internal static KvResult<KvInfo> GetKeyInfoSync(string bucketId, string key)
        {
            var keyByteCount = Encoding.UTF8.GetByteCount(key);
            if (keyByteCount == 0 || keyByteCount > 256)
            {
                return KvResult<KvInfo>.Fail(KvError.ValidationKeySize, "Key is empty or exceeds maximum size of 256 bytes");
            }

            lock (_connectionUseLock)
            {
                try
                {
                    var connection = GetConnection();

                    using var transaction = connection.BeginTransaction(deferred: true);

                    if (!BucketExistsCmd(connection, transaction, bucketId))
                        return KvResult<KvInfo>.Fail(KvError.BucketNotFound, "Bucket does not exist");

                    KvInfo? info = null;

                    using (var selectCmd = connection.CreateCommand())
                    {
                        selectCmd.Transaction = transaction;
                        selectCmd.CommandText = @"
                                SELECT created_at, updated_at, version, length(value)
                                FROM bucket_kv
                                WHERE bucket_id = $bucketId AND key = $key
                            ";
                        selectCmd.Parameters.AddWithValue("$bucketId", bucketId);
                        selectCmd.Parameters.AddWithValue("$key", key);

                        using var reader = selectCmd.ExecuteReader(System.Data.CommandBehavior.SingleRow);
                        if (reader.Read())
                        {
                            info = new KvInfo
                            {
                                creation = (ulong)reader.GetInt64(0),
                                lastUpdate = (ulong)reader.GetInt64(1),
                                version = (ulong)reader.GetInt64(2),
                                valueSize = (ulong)reader.GetInt64(3),
                            };
                        }
                    }

                    TrackBucketStats(connection, transaction, bucketId, BucketStatType.Read);

                    if (info == null)
                    {
                        transaction.Commit();
                        return KvResult<KvInfo>.Fail(KvError.KeyNotFound, "Key not found");
                    }

                    transaction.Commit();
                    return KvResult<KvInfo>.Ok(info.Value);

                }
                catch (SqliteException ex)
                {
                    return KvResult<KvInfo>.FromSqlException(ex);
                }
                catch
                {
                    return KvResult<KvInfo>.Fail(KvError.Unknown, "Unknown Server Error");
                }
            }
        }


        internal static Task<KvResult<bool>> KeyExistsAsync(string bucketId, string key) =>
            Task.FromResult(KeyExistsSync(bucketId, key));

        internal static KvResult<bool> KeyExistsSync(string bucketId, string key)
        {
            var keyByteCount = Encoding.UTF8.GetByteCount(key);
            if (keyByteCount == 0 || keyByteCount > 256)
            {
                return KvResult<bool>.Fail(KvError.ValidationKeySize, "Key is empty or exceeds maximum size of 256 bytes");
            }

            lock (_connectionUseLock)
            {
                try
                {
                    var connection = GetConnection();

                    using var transaction = connection.BeginTransaction(deferred: true);

                    if (!BucketExistsCmd(connection, transaction, bucketId))
                        return KvResult<bool>.Fail(KvError.BucketNotFound, "Bucket does not exist");

                    bool value = false;

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

                        var scalar = existCmd.ExecuteScalar();
                        value = (long)(scalar ?? 0L) == 1L;
                    }

                    TrackBucketStats(connection, transaction, bucketId, BucketStatType.Read);

                    transaction.Commit();

                    return KvResult<bool>.Ok(value);
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
        }


        internal static Task<KvResult<(string[] keys, bool more)>> ListKeysAsync(
            string bucketId,
            uint offset = 0,
            uint limit = 10,
            string? prefix = null) =>
            Task.FromResult(ListKeysSync(bucketId, offset, limit, prefix));

        internal static KvResult<(string[] keys, bool more)> ListKeysSync(
            string bucketId,
            uint offset,
            uint limit,
            string? prefix)
        {
            var keyByteCount = Encoding.UTF8.GetByteCount(prefix ?? "");
            if (keyByteCount > 256)
            {
                return KvResult<(string[] keys, bool more)>.Fail(KvError.ValidationKeySize, "Key prefix exceeds maximum key size of 256 bytes");
            }

            lock (_connectionUseLock)
            {
                try
                {
                    var connection = GetConnection();

                    using var transaction = connection.BeginTransaction(deferred: true);

                    if (!BucketExistsCmd(connection, transaction, bucketId))
                        return KvResult<(string[], bool)>.Fail(
                            KvError.BucketNotFound,
                            "Bucket does not exist");

                    var keys = new List<string>();

                    int pageSize = (int)limit;
                    int fetchSize = pageSize + 1;

                    if (offset > int.MaxValue)
                    {
                        transaction.Commit();
                        return KvResult<(string[], bool)>.Ok((Array.Empty<string>(), false));
                    }

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;

                        if (!string.IsNullOrEmpty(prefix))
                        {
                            var likePattern = BuildLikePattern(prefix);
                            cmd.CommandText = @"
                                    SELECT key
                                    FROM bucket_kv
                                    WHERE bucket_id = $bucketId
                                      AND key COLLATE BINARY LIKE $prefixPattern ESCAPE '\'
                                    ORDER BY key COLLATE BINARY
                                    LIMIT $limitPlusOne OFFSET $offset;
                                ";
                            cmd.Parameters.AddWithValue("$prefixPattern", likePattern);
                        }
                        else
                        {
                            cmd.CommandText = @"
                                    SELECT key
                                    FROM bucket_kv
                                    WHERE bucket_id = $bucketId
                                    ORDER BY key COLLATE BINARY
                                    LIMIT $limitPlusOne OFFSET $offset;
                                ";
                        }

                        cmd.Parameters.AddWithValue("$bucketId", bucketId);
                        cmd.Parameters.AddWithValue("$limitPlusOne", fetchSize);
                        cmd.Parameters.AddWithValue("$offset", (int)offset);

                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            keys.Add(reader.GetString(0));
                        }
                    }

                    bool more = keys.Count > pageSize;
                    if (more)
                    {
                        keys.RemoveAt(keys.Count - 1);
                    }

                    TrackBucketStats(connection, transaction, bucketId, BucketStatType.Read, ensureBucketMeta: true);

                    transaction.Commit();

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
        }

        private static void TrackBucketStats(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string bucketId,
            BucketStatType statType,
            bool ensureBucketMeta = false)
        {
            if (!EnableStatsTracking)
            {
                return;
            }

            using var updateCmd = connection.CreateCommand();
            updateCmd.Transaction = transaction;

            var updateSql = statType switch
            {
                BucketStatType.Read => ReadStatsUpdateSql,
                BucketStatType.Write => WriteStatsUpdateSql,
                BucketStatType.Delete => DeleteStatsUpdateSql,
                _ => throw new ArgumentOutOfRangeException(nameof(statType)),
            };

            var prefix = ensureBucketMeta ? InsertBucketMetaSql : string.Empty;
            updateCmd.CommandText = prefix + updateSql;
            updateCmd.Parameters.AddWithValue("$bucketId", bucketId);
            updateCmd.ExecuteNonQuery();
        }

        private const string InsertBucketMetaSql = @"
                    INSERT OR IGNORE INTO bucket_meta (bucket_id)
                    VALUES ($bucketId);

                    ";

        private const string ReadStatsUpdateSql = @"
                    UPDATE bucket_meta
                    SET total_operations = total_operations + 1,
                        total_reads      = total_reads      + 1,
                        last_operation_at = strftime('%s','now')
                    WHERE bucket_id = $bucketId;
                ";

        private const string WriteStatsUpdateSql = @"
                    UPDATE bucket_meta
                    SET total_operations = total_operations + 1,
                        total_writes      = total_writes    + 1,
                        last_operation_at = strftime('%s','now')
                    WHERE bucket_id = $bucketId;
                ";

        private const string DeleteStatsUpdateSql = @"
                    UPDATE bucket_meta
                    SET total_operations = total_operations + 1,
                        total_deletes    = total_deletes    + 1,
                        last_operation_at = strftime('%s','now')
                    WHERE bucket_id = $bucketId;
                ";

        private enum BucketStatType
        {
            Read,
            Write,
            Delete,
        }

        /// <summary>
        /// Do a bit of sanitization and escaping to build a LIKE pattern for prefix matching.
        /// </summary>
        /// <param name="prefix"></param>
        /// <returns></returns>
        private static string BuildLikePattern(string prefix)
        {
            var builder = new StringBuilder(prefix.Length + 1);
            foreach (var ch in prefix)
            {
                if (ch is '%' or '_' or '\\')
                {
                    builder.Append('\\');
                }
                builder.Append(ch);
            }

            builder.Append('%');
            return builder.ToString();
        }



        internal static Task<KvResult<QuotaInfo>> GetQuotaAsync(string bucketId) =>
            Task.FromResult(GetQuotaSync(bucketId));

        internal static KvResult<QuotaInfo> GetQuotaSync(string bucketId)
        {
            lock (_connectionUseLock)
            {
                try
                {
                    var connection = GetConnection();

                    using var transaction = connection.BeginTransaction(deferred: true);
                    if (!BucketExistsCmd(connection, transaction, bucketId))
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
                        cmd.Parameters.AddWithValue("$bucketId", bucketId);

                        using var reader = cmd.ExecuteReader(System.Data.CommandBehavior.SingleRow);
                        if (reader.Read())
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

                    TrackBucketStats(connection, transaction, bucketId, BucketStatType.Read, ensureBucketMeta: true);

                    transaction.Commit();

                    if (quota == null)
                        return KvResult<QuotaInfo>.Fail(KvError.BucketNotFound, "Bucket not found");

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
        }

        // --- Admin Only API ---

        internal static Task<KvResult<Unit>> SetKeyCountGuard(string bucketId, bool enable)
        {
            return SetQuotaGuardInternal(bucketId, "key_limit_guard", enable);
        }

        internal static Task<KvResult<Unit>> SetByteSizeGaurd(string bucketId, bool enable)
        {
            return SetQuotaGuardInternal(bucketId, "byte_limit_guard", enable);
        }

        internal static Task<KvResult<Unit>> SetQuotaGuardInternal(string bucketId, string guardColumnName, bool enable) =>
            Task.FromResult(SetQuotaGuardSyncInternal(bucketId, guardColumnName, enable));

        private static KvResult<Unit> SetQuotaGuardSyncInternal(string bucketId, string guardColumnName, bool enable)
        {
            lock (_connectionUseLock)
            {
                try
                {
                    var connection = GetConnection();

                    using var transaction = connection.BeginTransaction();

                    var guardColumn = guardColumnName switch
                    {
                        "key_limit_guard" => "key_limit_guard",
                        "byte_limit_guard" => "byte_limit_guard",
                        _ => throw new ArgumentOutOfRangeException(nameof(guardColumnName), "Invalid guard column name")
                    };

                    using (var gaurdCmd = connection.CreateCommand())
                    {
                        gaurdCmd.Transaction = transaction;
                        gaurdCmd.CommandText = $@"
                                UPDATE bucket_meta
                                SET {guardColumn} = $enable
                                WHERE bucket_id = $bucketId;
                            ";
                        gaurdCmd.Parameters.AddWithValue("$bucketId", bucketId);
                        gaurdCmd.Parameters.AddWithValue("$enable", enable ? 1 : 0);

                        gaurdCmd.ExecuteNonQuery();
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
        }

        // --- Internal API ---

        internal static Task<KvResult<Unit>> AddBucketAsync(string bucketId) =>
            Task.FromResult(AddBucketSync(bucketId));

        internal static KvResult<Unit> AddBucketSync(string bucketId)
        {
            lock (_connectionUseLock)
            {
                try
                {
                    var connection = GetConnection();

                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        using (var cmd = connection.CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = @"
                                INSERT OR IGNORE INTO buckets (bucket_id)
                                VALUES ($bucketId);
                            ";
                            cmd.Parameters.AddWithValue("$bucketId", bucketId);
                            cmd.ExecuteNonQuery();
                        }

                        using (var cmd = connection.CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = @"
                                INSERT OR IGNORE INTO bucket_meta (bucket_id)
                                VALUES ($bucketId);
                            ";
                            cmd.Parameters.AddWithValue("$bucketId", bucketId);
                            cmd.ExecuteNonQuery();
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
        }


        internal static Task<KvResult<bool>> DeleteBucketAsync(string bucketId, bool removeKeys = true)
            => Task.FromResult(DeleteBucketSync(bucketId, removeKeys));

        internal static KvResult<bool> DeleteBucketSync(string bucketId, bool removeKeys)
        {
            lock (_connectionUseLock)
            {
                try
                {
                    var connection = GetConnection();

                    using var transaction = connection.BeginTransaction();

                    if (!BucketExistsCmd(connection, transaction, bucketId))
                    {
                        transaction.Rollback();
                        return KvResult<bool>.Fail(KvError.BucketNotFound, "Bucket does not exist");
                    }

                    if (removeKeys)
                    {
                        using var deleteKeys = connection.CreateCommand();
                        deleteKeys.Transaction = transaction;
                        deleteKeys.CommandText = @"
                                DELETE FROM bucket_kv
                                WHERE bucket_id = $bucketId;
                            ";
                        deleteKeys.Parameters.AddWithValue("$bucketId", bucketId);
                        deleteKeys.ExecuteNonQuery();
                    }
                    else
                    {
                        using var countCmd = connection.CreateCommand();
                        countCmd.Transaction = transaction;
                        countCmd.CommandText = @"
                                SELECT COUNT(1)
                                FROM bucket_kv
                                WHERE bucket_id = $bucketId;
                            ";
                        countCmd.Parameters.AddWithValue("$bucketId", bucketId);

                        var existingKeys = (long)(countCmd.ExecuteScalar() ?? 0);
                        if (existingKeys > 0)
                        {
                            transaction.Rollback();
                            return KvResult<bool>.Fail(
                                KvError.ServerInvalidParameter,
                                "Bucket still contains keys; set removeKeys=true to delete bucket and keys.");
                        }
                    }

                    using (var deleteBucket = connection.CreateCommand())
                    {
                        deleteBucket.Transaction = transaction;
                        deleteBucket.CommandText = @"
                                DELETE FROM buckets
                                WHERE bucket_id = $bucketId;
                            ";
                        deleteBucket.Parameters.AddWithValue("$bucketId", bucketId);
                        deleteBucket.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    return KvResult<bool>.Ok(true);
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
        }


        private static bool BucketExistsCmd(SqliteConnection conn, SqliteTransaction tx, string bucketId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            cmd.CommandText = @"
                SELECT EXISTS(
                    SELECT 1 FROM buckets WHERE bucket_id = $bucketId
                );
            ";

            cmd.Parameters.AddWithValue("$bucketId", bucketId);

            var result = (long)(cmd.ExecuteScalar() ?? 0);
            return result == 1;
        }
    }


}
