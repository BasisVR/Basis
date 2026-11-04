using BasisNetworkServer.Storage;
using System.Globalization;
using System.Text;

namespace BasisNetworkConsole
{
    public static class BasisStorageCommands
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private const int DefaultListCount = 20, MaxListCount = 500, ListedScopes = 3;
        public static void Register()
        {
            BasisConsoleCommands.RegisterCommand("/storage", "Shows player storage status. /storage list lists the players keeping data, /storage show <uuid> lists one player's data, /storage wipe <uuid> deletes it, /storage prune <days> deletes data nobody has used for that long.", HandleStatus);
            BasisConsoleCommands.RegisterCommand("/storage list", "Lists the players keeping data in storage with its size and when it was last used, newest first unless told otherwise. Usage: /storage list [newest|oldest|largest] [count]", HandleList);
            BasisConsoleCommands.RegisterCommand("/storage show", "Lists the content names, keys and sizes one player keeps in storage, never the values. Usage: /storage show <uuid>", HandleShow);
            BasisConsoleCommands.RegisterCommand("/storage wipe", "Deletes everything one player keeps in storage. Usage: /storage wipe <uuid>", HandleWipe);
            BasisConsoleCommands.RegisterCommand("/storage prune", "Deletes the storage of every player who has not used it for the given number of days. Shows what would be deleted first; add confirm to delete it. Usage: /storage prune <days> [confirm]", HandlePrune);
        }
        private static void HandleStatus(string[] args)
        {
            BasisStorageService service = BasisServerStorage.Service;
            if (service == null)
            {
                BNL.Log($"Player storage is off. Backends available: {string.Join(", ", BasisStorageBackends.Names)}.");
                return;
            }
            BasisStorageNewOwnerLimiter newOwners = service.NewOwners;
            string expiry = !service.CanExpire ? "is kept until wiped (this backend cannot tell when data was last used)" : service.ExpiryDays > 0 ? $"is removed after {service.ExpiryDays} days unused" : "is kept forever";
            BNL.Log($"Player storage ({service.BackendName}): {service.TotalRecords} players, {service.TotalBytes} bytes of data, {service.UsedBytes} of {service.MaxTotalBytes} bytes in use counting {BasisStorageService.RecordOverheadBytes} bytes per player, {service.BytesPerPlayer} bytes per player. First saves allowed: {newOwners.PerAddress} per address every {(long)newOwners.Window.TotalMinutes} minutes and {newOwners.PerHour} an hour ({newOwners.Admitted} allowed, {newOwners.Refused} refused). Idle data {expiry}; {service.Expired} removed so far. Served {service.Served}, refused {service.Refused}, failed {service.Failures}.");
        }
        private static void HandleList(string[] args)
        {
            BasisStorageService service = BasisServerStorage.Service;
            if (service == null)
            {
                BNL.Log("Player storage is off.");
                return;
            }
            string order = "newest";
            int count = DefaultListCount;
            foreach (string arg in args)
            {
                string word = arg.ToLowerInvariant();
                if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0) count = Math.Min(parsed, MaxListCount);
                else if (word == "newest" || word == "oldest" || word == "largest") order = word;
                else
                {
                    BNL.Log("Usage: /storage list [newest|oldest|largest] [count]");
                    return;
                }
            }
            Comparison<BasisStorageRecordInfo> compare = order == "oldest" ? (a, b) => a.LastUsed.CompareTo(b.LastUsed) : order == "largest" ? (a, b) => b.Bytes.CompareTo(a.Bytes) : (a, b) => b.LastUsed.CompareTo(a.LastUsed);
            List<BasisStorageRecordInfo> top = new List<BasisStorageRecordInfo>(count + 1);
            long records = 0, bytes = 0;
            bool listed = service.TryList(long.MaxValue, info =>
            {
                records++;
                bytes += info.Bytes;
                Keep(top, info, count, compare);
                return true;
            });
            if (!listed)
            {
                BNL.Log($"The {service.BackendName} storage backend cannot list the players it holds.");
                return;
            }
            DateTime now = DateTime.UtcNow;
            StringBuilder text = new StringBuilder();
            text.Append($"{records} players keep {bytes} bytes in storage. The {top.Count} {order}:");
            foreach (BasisStorageRecordInfo info in top)
            {
                DateTime used = DateTimeOffset.FromUnixTimeMilliseconds(info.LastUsed).UtcDateTime;
                text.Append($"\n  {Describe(service, info.OwnerKey)}: {info.Bytes} bytes, last used {used.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC ({Math.Max(0, (int)(now - used).TotalDays)} days ago)");
            }
            BNL.Log(text.ToString());
        }
        private static string Describe(BasisStorageService service, string ownerKey)
        {
            if (!service.TryDescribe(ownerKey, Timeout, out BasisStorageRecord record)) return $"{ownerKey} (unreadable)";
            string[] scopes = record.ScopeNames();
            string named = string.Join(", ", scopes.Take(ListedScopes)) + (scopes.Length > ListedScopes ? ", ..." : string.Empty);
            return $"{record.Owner} ({record.Entries} keys in {record.Scopes} contents: {named})";
        }
        private static void Keep(List<BasisStorageRecordInfo> top, BasisStorageRecordInfo info, int count, Comparison<BasisStorageRecordInfo> compare)
        {
            if (top.Count == count && compare(info, top[count - 1]) >= 0) return;
            int at = top.Count;
            while (at > 0 && compare(info, top[at - 1]) < 0) at--;
            top.Insert(at, info);
            if (top.Count > count) top.RemoveAt(count);
        }
        private static void HandlePrune(string[] args)
        {
            bool confirm = args.Length == 2 && string.Equals(args[1], "confirm", StringComparison.OrdinalIgnoreCase);
            if ((args.Length != 1 && !confirm) || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days < 1 || days > BasisStorageService.MaxExpiryDays)
            {
                BNL.Log($"Usage: /storage prune <days> [confirm], days from 1 to {BasisStorageService.MaxExpiryDays}");
                return;
            }
            BasisStorageService service = BasisServerStorage.Service;
            if (service == null)
            {
                BNL.Log("Player storage is off.");
                return;
            }
            if (!service.CanExpire)
            {
                BNL.Log($"The {service.BackendName} storage backend cannot tell when data was last used, so it cannot prune.");
                return;
            }
            TimeSpan idle = TimeSpan.FromDays(days);
            if (!confirm)
            {
                if (service.TryPreviewExpiry(idle, out long found, out long size)) BNL.Log($"{found} players' storage ({size} bytes) has not been used for {days} days. Run /storage prune {days} confirm to delete it.");
                return;
            }
            if (service.IsExpiring)
            {
                BNL.Log("Player storage is already removing idle data; try again when it has finished.");
                return;
            }
            BNL.Log($"Deleting the storage nobody has used for {days} days in the background.");
            Task.Run(() =>
            {
                try
                {
                    BNL.Log(service.TryExpire(idle, out long records, out long bytes) ? $"Deleted the storage of {records} players ({bytes} bytes) that nobody had used for {days} days." : "Player storage was already removing idle data, so nothing more was deleted.");
                }
                catch (Exception ex)
                {
                    BNL.LogError($"Pruning player storage failed: {ex.GetBaseException().Message}");
                }
            });
        }
        private static void HandleShow(string[] args)
        {
            if (!TryService(args, "/storage show <uuid>", out BasisStorageService service, out string uuid)) return;
            if (!service.TryInspect(uuid, Timeout, out BasisStorageRecord record))
            {
                BNL.Log($"Could not read the storage of {uuid} in time.");
                return;
            }
            if (record.IsEmpty)
            {
                BNL.Log($"{uuid} keeps nothing in storage.");
                return;
            }
            StringBuilder text = new StringBuilder();
            text.Append($"{uuid}: {record.Used} of {service.BytesPerPlayer} bytes, {record.Entries} keys in {record.Scopes} contents, revision {record.Revision}.");
            foreach (string scope in record.ScopeNames())
            {
                text.Append($"\n  {scope}");
                foreach (string key in record.KeysOf(scope)) text.Append($"\n    {key} ({record.SizeOf(scope, key)} bytes)");
            }
            BNL.Log(text.ToString());
        }
        private static void HandleWipe(string[] args)
        {
            if (!TryService(args, "/storage wipe <uuid>", out BasisStorageService service, out string uuid)) return;
            BNL.Log(service.TryWipe(uuid, Timeout) ? $"Deleted the storage of {uuid}." : $"Could not delete the storage of {uuid}; see the errors above.");
        }
        private static bool TryService(string[] args, string usage, out BasisStorageService service, out string uuid)
        {
            service = BasisServerStorage.Service;
            uuid = args.Length == 1 ? args[0].Trim() : string.Empty;
            if (uuid.Length == 0 || !BasisStorageService.IsValidOwner(uuid))
            {
                BNL.Log($"Usage: {usage}");
                return false;
            }
            if (service != null) return true;
            BNL.Log("Player storage is off.");
            return false;
        }
    }
}
