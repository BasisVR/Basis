using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

namespace BasisNetworkServer.Security
{
    public class BasisBlackList
    {
        private readonly ConcurrentDictionary<string, byte> blacklistedPlayers = new();
        private readonly string filePath;

        public BasisBlackList(string path = "BasisBlackList.txt")
        {
            filePath = path;
            _ = LoadBlacklistAsync(); // Fire and forget
        }

        private async Task LoadBlacklistAsync()
        {
            blacklistedPlayers.Clear();
            if (File.Exists(filePath))
            {
                string[] lines = await File.ReadAllLinesAsync(filePath);
                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (!string.IsNullOrEmpty(trimmedLine))
                    {
                        blacklistedPlayers.TryAdd(trimmedLine, 0);
                    }
                }
            }
        }

        public bool IsBlacklisted(string playerId) => blacklistedPlayers.ContainsKey(playerId);

        public async Task ReloadBlacklistAsync()
        {
            await LoadBlacklistAsync();
            Console.WriteLine("Blacklist reloaded.");
        }

        public async Task AddToBlacklistAsync(string playerId)
        {
            if (!blacklistedPlayers.ContainsKey(playerId))
            {
                blacklistedPlayers.TryAdd(playerId, 0);
                await File.AppendAllTextAsync(filePath, playerId + Environment.NewLine);
                Console.WriteLine($"{playerId} added to blacklist.");
            }
        }

        public async Task RemoveFromBlacklistAsync(string playerId)
        {
            if (blacklistedPlayers.TryRemove(playerId, out _))
            {
                await SaveBlacklistAsync();
                Console.WriteLine($"{playerId} removed from blacklist.");
            }
        }

        private async Task SaveBlacklistAsync()
        {
            await File.WriteAllLinesAsync(filePath, blacklistedPlayers.Keys);
        }
    }
}
