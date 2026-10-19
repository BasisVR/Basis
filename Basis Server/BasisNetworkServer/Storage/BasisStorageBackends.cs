using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;

namespace BasisNetworkServer.Storage
{
    public delegate IBasisStorageBackend BasisStorageBackendFactory(Configuration configuration, string storageFolder);
    public static class BasisStorageBackends
    {
        public const string FileBackendName = "file", MemoryBackendName = "memory";
        private static readonly ConcurrentDictionary<string, BasisStorageBackendFactory> factories = new ConcurrentDictionary<string, BasisStorageBackendFactory>(StringComparer.OrdinalIgnoreCase);
        static BasisStorageBackends()
        {
            factories[FileBackendName] = (configuration, folder) => new BasisFileStorageBackend(folder);
            factories[MemoryBackendName] = (configuration, folder) => new BasisMemoryStorageBackend();
        }
        public static void Register(string name, BasisStorageBackendFactory factory)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A storage backend needs a name.", nameof(name));
            factories[name.Trim()] = factory ?? throw new ArgumentNullException(nameof(factory));
        }
        public static void RegisterSql(string name, DbProviderFactory provider, BasisStorageSqlDialect dialect, Func<string, string> defaultConnectionString = null)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (dialect == null) throw new ArgumentNullException(nameof(dialect));
            Register(name, (configuration, folder) =>
            {
                string connection = string.IsNullOrWhiteSpace(configuration.StorageConnectionString) ? defaultConnectionString?.Invoke(folder) : configuration.StorageConnectionString;
                return new BasisSqlStorageBackend(name, provider, dialect, connection, configuration.StorageSqlTable);
            });
        }
        public static bool Unregister(string name) => !string.IsNullOrWhiteSpace(name) && factories.TryRemove(name.Trim(), out _);
        public static bool IsRegistered(string name) => !string.IsNullOrWhiteSpace(name) && factories.ContainsKey(name.Trim());
        public static string[] Names
        {
            get
            {
                List<string> names = new List<string>(factories.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return names.ToArray();
            }
        }
        public static IBasisStorageBackend Create(string name, Configuration configuration, string storageFolder)
        {
            string chosen = string.IsNullOrWhiteSpace(name) ? FileBackendName : name.Trim();
            if (!factories.TryGetValue(chosen, out BasisStorageBackendFactory factory)) throw new ArgumentException($"No storage backend named '{chosen}' is registered. Registered: {string.Join(", ", Names)}.", nameof(name));
            return factory(configuration, storageFolder) ?? throw new InvalidOperationException($"The '{chosen}' storage backend factory returned nothing.");
        }
    }
}
