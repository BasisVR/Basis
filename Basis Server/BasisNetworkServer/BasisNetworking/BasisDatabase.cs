using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BasisNetworkServer.BasisNetworking
{
    [DataContract]
    public class BasisData
    {
        [DataMember]
        public string Name { get; set; }

        [DataMember]
        public ConcurrentDictionary<string, object> JsonPayload { get; set; } = new();

        public BasisData(string name, ConcurrentDictionary<string, object> jsonPayload)
        {
            Name = name;
            JsonPayload = jsonPayload ?? new ConcurrentDictionary<string, object>();
        }
    }

    public static class BasisPersistentDatabase
    {
        private static readonly ConcurrentDictionary<string, BasisData> _dataByName = new();
        private static readonly object _fileLock = new();

        private static string _filePath = "basis_data.json";
        private static volatile bool _isDirty = false;
        private static readonly CancellationTokenSource _cts = new();
        private static readonly TimeSpan _saveInterval = TimeSpan.FromSeconds(5);
        private static readonly AutoResetEvent _saveTrigger = new(false);

        static BasisPersistentDatabase()
        {
            Load();
            StartAutoSaveLoop();
        }

        public static void SetFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));

            lock (_fileLock)
            {
                _filePath = path;
                Load();
            }
        }

        public static bool AddOrUpdate(BasisData item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                BNL.LogError("Name must not be null or whitespace. (basis database)");
                return false;
            }
            _dataByName.AddOrUpdate(item.Name,
                addValueFactory: _ => item,
                updateValueFactory: (_, existing) =>
                {
                    existing.JsonPayload = new ConcurrentDictionary<string, object>(item.JsonPayload);
                    return existing;
                });

            MarkDirty();
            return true;
        }

        public static IEnumerable<BasisData> GetAll() => _dataByName.Values.ToArray();

        public static bool GetByName(string name, out BasisData BasisData)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                BasisData = null;
                return false;
            }
            return _dataByName.TryGetValue(name, out BasisData);
        }
        public static bool Remove(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var result = _dataByName.TryRemove(name, out _);
            if (result) MarkDirty();
            return result;
        }

        private static void MarkDirty()
        {
            _isDirty = true;
            _saveTrigger.Set();
        }

        public static void Save()
        {
            lock (_fileLock)
            {
                try
                {
                    var list = GetAll().ToList();

                    using var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    var serializer = new DataContractJsonSerializer(typeof(List<BasisData>), new DataContractJsonSerializerSettings
                    {
                        UseSimpleDictionaryFormat = true
                    });

                    serializer.WriteObject(stream, list);
                    _isDirty = false;
                }
                catch (IOException ex)
                {
                    BNL.LogError($"Failed to save data: {ex.Message}");
                }
            }
        }

        public static void Load()
        {
            lock (_fileLock)
            {
                if (!File.Exists(_filePath))
                    return;

                try
                {
                    using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var serializer = new DataContractJsonSerializer(typeof(List<BasisData>), new DataContractJsonSerializerSettings
                    {
                        UseSimpleDictionaryFormat = true
                    });

                    if (serializer.ReadObject(stream) is List<BasisData> loadedData)
                    {
                        _dataByName.Clear();
                        foreach (var item in loadedData)
                        {
                            if (!string.IsNullOrWhiteSpace(item.Name))
                                _dataByName[item.Name] = item;
                        }
                    }
                }
                catch (SerializationException ex)
                {
                    BNL.LogError($"Deserialization error: {ex.Message}");
                }
                catch (IOException ex)
                {
                    BNL.LogError($"Failed to load data: {ex.Message}");
                }
            }
        }

        private static void StartAutoSaveLoop()
        {
            Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    _saveTrigger.WaitOne(_saveInterval);

                    if (_isDirty)
                    {
                        Save();
                    }
                }
            }, _cts.Token);
        }

        public static void Shutdown()
        {
            _cts.Cancel();
            _saveTrigger.Set(); // Wake the loop to exit
            Save(); // Final save on shutdown
        }
    }
}
