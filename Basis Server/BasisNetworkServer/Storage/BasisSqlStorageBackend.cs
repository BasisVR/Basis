using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;

namespace BasisNetworkServer.Storage
{
    public sealed class BasisStorageSqlDialect
    {
        public readonly string Name, CreateTable;
        public readonly string[] SetupStatements;
        public BasisStorageSqlDialect(string name, string createTable, params string[] setupStatements)
        {
            Name = name;
            CreateTable = createTable;
            SetupStatements = setupStatements ?? Array.Empty<string>();
        }
        public static readonly BasisStorageSqlDialect Sqlite = new BasisStorageSqlDialect("sqlite", "CREATE TABLE IF NOT EXISTS {0} (owner_key VARCHAR(96) NOT NULL PRIMARY KEY, record BLOB NOT NULL, record_size INTEGER NOT NULL, revision BIGINT NOT NULL, updated_at BIGINT NOT NULL)", "PRAGMA journal_mode=WAL");
        public static readonly BasisStorageSqlDialect PostgreSql = new BasisStorageSqlDialect("postgresql", "CREATE TABLE IF NOT EXISTS {0} (owner_key VARCHAR(96) NOT NULL PRIMARY KEY, record BYTEA NOT NULL, record_size INTEGER NOT NULL, revision BIGINT NOT NULL, updated_at BIGINT NOT NULL)");
        public static readonly BasisStorageSqlDialect MySql = new BasisStorageSqlDialect("mysql", "CREATE TABLE IF NOT EXISTS {0} (owner_key VARCHAR(96) NOT NULL PRIMARY KEY, record MEDIUMBLOB NOT NULL, record_size INT NOT NULL, revision BIGINT NOT NULL, updated_at BIGINT NOT NULL)");
        public static readonly BasisStorageSqlDialect SqlServer = new BasisStorageSqlDialect("sqlserver", "IF OBJECT_ID(N'{0}', N'U') IS NULL CREATE TABLE {0} (owner_key VARCHAR(96) NOT NULL PRIMARY KEY, record VARBINARY(MAX) NOT NULL, record_size INT NOT NULL, revision BIGINT NOT NULL, updated_at BIGINT NOT NULL)");
    }
    public sealed class BasisSqlStorageBackend : IBasisStorageBackend, IBasisStorageCatalog, IBasisSharedStorageBackend
    {
        private const int CommandTimeoutSeconds = 15;
        private const string QuarantineMarker = "-corrupt-";
        private readonly DbProviderFactory provider;
        private readonly BasisStorageSqlDialect dialect;
        private readonly string connectionString, table, load, update, insert, delete, exists, move, measure, touch, list, idle, expire, probe;
        public string Name { get; }
        public BasisSqlStorageBackend(string name, DbProviderFactory provider, BasisStorageSqlDialect dialect, string connectionString, string table)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A database connection string is required.", nameof(connectionString));
            if (!IsValidTableName(table)) throw new ArgumentException("Storage table names are 1 to 63 letters, digits or underscores and do not start with a digit.", nameof(table));
            Name = name;
            this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
            this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
            this.connectionString = connectionString;
            this.table = table;
            load = $"SELECT record, updated_at FROM {table} WHERE owner_key = @owner";
            update = $"UPDATE {table} SET record = @record, record_size = @size, revision = @revision, updated_at = @updated WHERE owner_key = @owner AND revision = @expected";
            insert = $"INSERT INTO {table} (owner_key, record, record_size, revision, updated_at) VALUES (@owner, @record, @size, @revision, @updated)";
            delete = $"DELETE FROM {table} WHERE owner_key = @owner AND revision = @expected";
            exists = $"SELECT COUNT(*) FROM {table} WHERE owner_key = @owner";
            move = $"UPDATE {table} SET owner_key = @moved WHERE owner_key = @owner";
            measure = $"SELECT COUNT(*), SUM(record_size) FROM {table} WHERE owner_key NOT LIKE '%{QuarantineMarker}%'";
            touch = $"UPDATE {table} SET updated_at = @updated WHERE owner_key = @owner AND updated_at < @updated";
            list = $"SELECT owner_key, record_size, updated_at FROM {table} WHERE updated_at < @before";
            idle = $"SELECT record_size, revision FROM {table} WHERE owner_key = @owner AND updated_at < @before";
            expire = $"DELETE FROM {table} WHERE owner_key = @owner AND revision = @expected AND updated_at < @before";
            probe = $"SELECT revision FROM {table} WHERE owner_key = @owner";
        }
        public static bool IsValidTableName(string table)
        {
            if (string.IsNullOrEmpty(table) || table.Length > 63 || (table[0] >= '0' && table[0] <= '9')) return false;
            foreach (char c in table)
            {
                if ((c < 'a' || c > 'z') && (c < 'A' || c > 'Z') && (c < '0' || c > '9') && c != '_') return false;
            }
            return true;
        }
        public void Open()
        {
            using (DbConnection connection = Connect())
            {
                Execute(connection, string.Format(CultureInfo.InvariantCulture, dialect.CreateTable, table));
                foreach (string statement in dialect.SetupStatements) Execute(connection, statement);
            }
        }
        public bool TryLoad(string ownerKey, out byte[] blob) => TryLoad(ownerKey, out blob, out _);
        public bool TryLoad(string ownerKey, out byte[] blob, out long lastUsed)
        {
            blob = null;
            lastUsed = 0;
            using (DbConnection connection = Connect())
            using (DbCommand command = Command(connection, load))
            {
                Add(command, "@owner", DbType.String, Checked(ownerKey));
                using (DbDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read() || reader.IsDBNull(0)) return false;
                    if (!(reader.GetValue(0) is byte[] bytes) || bytes.Length > BasisStorageRecord.MaxBlobBytes) throw new InvalidDataException($"Storage record {ownerKey} in {table} is not a record blob within the size ceiling.");
                    blob = bytes;
                    lastUsed = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                    return true;
                }
            }
        }
        public bool TrySave(string ownerKey, byte[] blob, long expectedRevision, long revision)
        {
            if (blob == null || blob.Length > BasisStorageRecord.MaxBlobBytes) throw new ArgumentException("Storage record is missing or too large.", nameof(blob));
            using (DbConnection connection = Connect())
            {
                if (expectedRevision > 0)
                {
                    using (DbCommand command = Command(connection, update))
                    {
                        Fill(command, ownerKey, blob, revision);
                        Add(command, "@expected", DbType.Int64, expectedRevision);
                        return command.ExecuteNonQuery() == 1;
                    }
                }
                try
                {
                    using (DbCommand command = Command(connection, insert))
                    {
                        Fill(command, ownerKey, blob, revision);
                        return command.ExecuteNonQuery() == 1;
                    }
                }
                catch (DbException) when (Exists(connection, ownerKey))
                {
                    return false;
                }
            }
        }
        public bool TryDelete(string ownerKey, long expectedRevision)
        {
            using (DbConnection connection = Connect())
            {
                using (DbCommand command = Command(connection, delete))
                {
                    Add(command, "@owner", DbType.String, Checked(ownerKey));
                    Add(command, "@expected", DbType.Int64, expectedRevision);
                    if (command.ExecuteNonQuery() == 1) return true;
                }
                return !Exists(connection, ownerKey);
            }
        }
        public void Quarantine(string ownerKey)
        {
            using (DbConnection connection = Connect())
            using (DbCommand command = Command(connection, move))
            {
                Add(command, "@owner", DbType.String, Checked(ownerKey));
                Add(command, "@moved", DbType.String, ownerKey + QuarantineMarker + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
        }
        public void Measure(out long records, out long bytes)
        {
            records = 0;
            bytes = 0;
            using (DbConnection connection = Connect())
            using (DbCommand command = Command(connection, measure))
            using (DbDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read()) return;
                records = reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                bytes = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }
        public void Touch(string ownerKey, long lastUsed)
        {
            using (DbConnection connection = Connect())
            using (DbCommand command = Command(connection, touch))
            {
                Add(command, "@owner", DbType.String, Checked(ownerKey));
                Add(command, "@updated", DbType.Int64, lastUsed);
                command.ExecuteNonQuery();
            }
        }
        public void List(long usedBefore, Func<BasisStorageRecordInfo, bool> visit)
        {
            using (DbConnection connection = Connect())
            using (DbCommand command = Command(connection, list))
            {
                Add(command, "@before", DbType.Int64, usedBefore);
                using (DbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string ownerKey = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                        if (!BasisStorageRecord.IsOwnerKey(ownerKey)) continue;
                        BasisStorageRecordInfo info = new BasisStorageRecordInfo
                        {
                            OwnerKey = ownerKey,
                            Bytes = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                            LastUsed = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                        };
                        if (!visit(info)) return;
                    }
                }
            }
        }
        public bool TryExpire(string ownerKey, long usedBefore, out long bytes)
        {
            bytes = 0;
            long size, expected;
            using (DbConnection connection = Connect())
            {
                using (DbCommand command = Command(connection, idle))
                {
                    Add(command, "@owner", DbType.String, Checked(ownerKey));
                    Add(command, "@before", DbType.Int64, usedBefore);
                    using (DbDataReader reader = command.ExecuteReader())
                    {
                        if (!reader.Read() || reader.IsDBNull(1)) return false;
                        size = reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                        expected = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                    }
                }
                using (DbCommand command = Command(connection, expire))
                {
                    Add(command, "@owner", DbType.String, Checked(ownerKey));
                    Add(command, "@expected", DbType.Int64, expected);
                    Add(command, "@before", DbType.Int64, usedBefore);
                    if (command.ExecuteNonQuery() != 1) return false;
                }
            }
            bytes = size;
            return true;
        }
        public bool TryReadRevision(string ownerKey, out long revision)
        {
            revision = 0;
            using (DbConnection connection = Connect())
            using (DbCommand command = Command(connection, probe))
            {
                Add(command, "@owner", DbType.String, Checked(ownerKey));
                object value = command.ExecuteScalar();
                if (value == null || value is DBNull) return false;
                revision = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            }
        }
        public void Dispose()
        {
        }
        private DbConnection Connect()
        {
            DbConnection connection = provider.CreateConnection() ?? throw new InvalidOperationException($"The {Name} provider did not create a connection.");
            try
            {
                connection.ConnectionString = connectionString;
                connection.Open();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        private bool Exists(DbConnection connection, string ownerKey)
        {
            using (DbCommand command = Command(connection, exists))
            {
                Add(command, "@owner", DbType.String, Checked(ownerKey));
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }
        private static void Execute(DbConnection connection, string sql)
        {
            using (DbCommand command = Command(connection, sql)) command.ExecuteNonQuery();
        }
        private static DbCommand Command(DbConnection connection, string sql)
        {
            DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = CommandTimeoutSeconds;
            return command;
        }
        private static void Fill(DbCommand command, string ownerKey, byte[] blob, long revision)
        {
            Add(command, "@owner", DbType.String, Checked(ownerKey));
            Add(command, "@record", DbType.Binary, blob);
            Add(command, "@size", DbType.Int32, blob.Length);
            Add(command, "@revision", DbType.Int64, revision);
            Add(command, "@updated", DbType.Int64, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        private static void Add(DbCommand command, string name, DbType type, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = type;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        private static string Checked(string ownerKey)
        {
            if (!BasisStorageRecord.IsOwnerKey(ownerKey)) throw new ArgumentException("Storage owner keys are 64 lowercase hex characters.", nameof(ownerKey));
            return ownerKey;
        }
    }
}
