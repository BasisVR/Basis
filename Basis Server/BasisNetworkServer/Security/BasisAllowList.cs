using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace BasisNetworkServer.Security
{
    public class BasisAllowList
    {
        private readonly ConcurrentDictionary<string, byte> allowlistedPlayers = new ConcurrentDictionary<string, byte>();
        private readonly string filePath;

        public BasisAllowList(string path = "BasisAllowList.txt")
        {
            filePath = path;
            _ = LoadAllowlistAsync(); // Fire and forget
        }

        private async Task LoadAllowlistAsync()
        {
            allowlistedPlayers.Clear();
            if (File.Exists(filePath))
            {
                string[] lines = await File.ReadAllLinesAsync(filePath);
                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (!string.IsNullOrEmpty(trimmedLine))
                    {
                        allowlistedPlayers.TryAdd(trimmedLine, 0);
                    }
                }
            }
        }

        public bool IsAllowed(string playerId) => allowlistedPlayers.ContainsKey(playerId);

        public async Task ReloadAllowlistAsync()
        {
            await LoadAllowlistAsync();
            Console.WriteLine("Allowlist reloaded.");
        }

        public async Task AddToAllowlistAsync(string playerId)
        {
            // The store is line-delimited, so an embedded newline would turn one requested entry
            // into several while the admin UI still reports one.
            playerId = playerId?.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
            if (string.IsNullOrEmpty(playerId))
            {
                return;
            }

            if (!allowlistedPlayers.ContainsKey(playerId))
            {
                allowlistedPlayers.TryAdd(playerId, 0);
                await File.AppendAllTextAsync(filePath, playerId + Environment.NewLine);
                Console.WriteLine($"{playerId} added to allowlist.");
            }
        }

        public async Task RemoveFromAllowlistAsync(string playerId)
        {
            if (allowlistedPlayers.TryRemove(playerId, out _))
            {
                await SaveAllowlistAsync();
                Console.WriteLine($"{playerId} removed from allowlist.");
            }
        }

        private async Task SaveAllowlistAsync()
        {
            await File.WriteAllLinesAsync(filePath, allowlistedPlayers.Keys);
        }
    }
}
