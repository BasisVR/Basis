using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace BasisNetworkServer.Security
{
    public class BasisWhiteList
    {
        private readonly ConcurrentDictionary<string, byte> whitelistedPlayers = new ConcurrentDictionary<string, byte>();
        private readonly string filePath;

        public BasisWhiteList(string path = "BasisWhiteList.txt")
        {
            filePath = path;
            _ = LoadWhitelistAsync(); // Fire and forget
        }

        private async Task LoadWhitelistAsync()
        {
            whitelistedPlayers.Clear();
            if (File.Exists(filePath))
            {
                string[] lines = await File.ReadAllLinesAsync(filePath);
                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (!string.IsNullOrEmpty(trimmedLine))
                    {
                        whitelistedPlayers.TryAdd(trimmedLine, 0);
                    }
                }
            }
        }

        public bool IsWhitelisted(string playerId) => whitelistedPlayers.ContainsKey(playerId);

        public async Task ReloadWhitelistAsync()
        {
            await LoadWhitelistAsync();
            Console.WriteLine("Whitelist reloaded.");
        }

        public async Task AddToWhitelistAsync(string playerId)
        {
            if (!whitelistedPlayers.ContainsKey(playerId))
            {
                whitelistedPlayers.TryAdd(playerId, 0);
                await File.AppendAllTextAsync(filePath, playerId + Environment.NewLine);
                Console.WriteLine($"{playerId} added to whitelist.");
            }
        }

        public async Task RemoveFromWhitelistAsync(string playerId)
        {
            if (whitelistedPlayers.TryRemove(playerId, out _))
            {
                await SaveWhitelistAsync();
                Console.WriteLine($"{playerId} removed from whitelist.");
            }
        }

        private async Task SaveWhitelistAsync()
        {
            await File.WriteAllLinesAsync(filePath, whitelistedPlayers.Keys);
        }
    }
}
