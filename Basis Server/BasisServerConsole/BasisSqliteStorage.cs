using Basis.Network.Core;
using BasisNetworkServer.Storage;
using Microsoft.Data.Sqlite;

[assembly: BasisNetworkPackage(typeof(BasisNetworkConsole.BasisSqliteStorage))]
namespace BasisNetworkConsole
{
    public sealed class BasisSqliteStorage : IBasisNetworkPackage
    {
        public const string BackendName = "sqlite", DatabaseFileName = "storage.db";
        public void Initialize()
        {
            BasisStorageBackends.RegisterSql(BackendName, SqliteFactory.Instance, BasisStorageSqlDialect.Sqlite, DefaultConnectionString);
        }
        public static string DefaultConnectionString(string storageFolder)
        {
            Directory.CreateDirectory(storageFolder);
            return new SqliteConnectionStringBuilder { DataSource = Path.Combine(storageFolder, DatabaseFileName), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        }
    }
}
