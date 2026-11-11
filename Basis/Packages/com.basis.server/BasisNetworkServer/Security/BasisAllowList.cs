using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BasisNetworkServer.Security
{
    public class BasisAllowList
    {
        public const int MaxIdLength = 256;

        private readonly ConcurrentDictionary<string, byte> allowlistedPlayers = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly List<string> ordered = new List<string>();
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
        private readonly string filePath;

        public BasisAllowList(string path = null)
        {
            filePath = path;
            Load();
        }

        public int Count => allowlistedPlayers.Count;

        public static string Sanitize(string playerId)
        {
            if (playerId == null) return null;
            playerId = playerId.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
            return playerId.Length == 0 || playerId.Length > MaxIdLength ? null : playerId;
        }

        private void Load()
        {
            string[] lines = Array.Empty<string>();
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try { lines = File.ReadAllLines(filePath); }
                catch (Exception e) { Console.WriteLine($"Allowlist could not be read from {filePath}: {e.Message}"); }
            }
            lock (ordered)
            {
                allowlistedPlayers.Clear();
                ordered.Clear();
                foreach (string line in lines)
                {
                    string id = Sanitize(line);
                    if (id != null && allowlistedPlayers.TryAdd(id, 0)) ordered.Add(id);
                }
            }
        }

        public bool IsAllowed(string playerId) => !string.IsNullOrEmpty(playerId) && allowlistedPlayers.ContainsKey(playerId);

        public string[] Snapshot()
        {
            lock (ordered) return ordered.ToArray();
        }

        public async Task ReloadAllowlistAsync()
        {
            await writeLock.WaitAsync();
            try { Load(); }
            finally { writeLock.Release(); }
            Console.WriteLine("Allowlist reloaded.");
        }

        public bool Add(string playerId)
        {
            playerId = Sanitize(playerId);
            if (playerId == null) return false;
            lock (ordered)
            {
                if (!allowlistedPlayers.TryAdd(playerId, 0)) return false;
                ordered.Add(playerId);
            }
            return true;
        }

        public bool Remove(string playerId)
        {
            playerId = Sanitize(playerId);
            if (playerId == null) return false;
            lock (ordered)
            {
                if (!allowlistedPlayers.TryRemove(playerId, out _)) return false;
                ordered.Remove(playerId);
            }
            return true;
        }

        public async Task<bool> AddToAllowlistAsync(string playerId)
        {
            if (!Add(playerId)) return false;
            await SaveAllowlistAsync();
            Console.WriteLine($"{Sanitize(playerId)} added to allowlist.");
            return true;
        }

        public async Task<bool> RemoveFromAllowlistAsync(string playerId)
        {
            if (!Remove(playerId)) return false;
            await SaveAllowlistAsync();
            Console.WriteLine($"{Sanitize(playerId)} removed from allowlist.");
            return true;
        }

        public async Task SaveAllowlistAsync()
        {
            if (string.IsNullOrEmpty(filePath)) return;
            await writeLock.WaitAsync();
            try
            {
                string temp = filePath + ".tmp";
                await File.WriteAllLinesAsync(temp, Snapshot());
                if (File.Exists(filePath)) File.Replace(temp, filePath, null);
                else File.Move(temp, filePath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Allowlist could not be saved to {filePath}: {e.Message}");
            }
            finally
            {
                writeLock.Release();
            }
        }
    }
}
