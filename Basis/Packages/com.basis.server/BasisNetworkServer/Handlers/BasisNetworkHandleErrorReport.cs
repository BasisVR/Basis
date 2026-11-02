using BasisNetworkServer.Security;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.Network.Core;
using static SerializableBasis;

namespace BasisNetworkServer
{
    /// <summary>
    /// Server-side receiver for client error/exception reports sent on
    /// <see cref="BasisNetworkCommons.EventsChannel"/> with sub-byte
    /// <see cref="BasisNetworkCommons.EventType_ErrorReport"/>.
    ///
    /// Wire (client→server): [eventType:1][severity:1][lenPrefixed PermissionCompression blob of (system, message, stack)]
    ///
    /// The reporting client never sends its own identity — the authoritative UUID,
    /// display name and platform are attached here from the peer's connect metadata.
    /// Only the first occurrence of each unique error per user (this server session)
    /// is written, to CrashReports/&lt;uuid&gt;.jsonl. Gated by BasisCrashReportStateManager.
    /// </summary>
    public static class BasisNetworkHandleErrorReport
    {
        private const int MaxMessageChars = 2000;
        private const int MaxStackChars = 12000;

        // Dedup state is bounded on every axis: entries are 8-byte hashes (the key used to be the
        // concatenated raw strings — multi-KB per distinct report, client-controlled), each user
        // stops recording after MaxSeenPerUser distinct errors, and the whole table is wiped if
        // the bucket count somehow reaches MaxTrackedUsers (reports landing under "unknown"
        // belong to no disconnect, so that bucket would otherwise live forever).
        private const int MaxSeenPerUser = 256;
        private const int MaxTrackedUsers = 4096;

        private static readonly ConcurrentDictionary<string, HashSet<long>> SeenPerUser =
            new ConcurrentDictionary<string, HashSet<long>>();
        private static readonly object FileLock = new object();
        private static readonly BasisPeerRateLimiter ReportLimiter = new BasisPeerRateLimiter(() => NetworkServer.Configuration?.CrashReportsPerSecondPerPlayer ?? 1f, () => NetworkServer.Configuration?.CrashReportBurstPerPlayer ?? 20f);
        private static int MaxDecompressedReportBytes => Math.Max(1, NetworkServer.Configuration?.MaxCrashReportDecompressedKilobytes ?? 32) * 1024;
        private static long MaxBytesPerUserFile => Math.Max(1L, NetworkServer.Configuration?.MaxCrashReportKilobytesPerPlayer ?? 1024) * 1024L;
        private static long MaxCrashReportFolderBytes => Math.Max(1L, NetworkServer.Configuration?.MaxCrashReportFolderMegabytes ?? 256) * 1024L * 1024L;
        private static long folderBytes = -1;

        public static void ClearAllSeen()
        {
            SeenPerUser.Clear();
            lock (FileLock) folderBytes = -1;
        }

        public static void HandleEvent(NetPacketReader reader, NetPeer peer, byte eventType)
        {
            try
            {
                if (!BasisCrashReportStateManager.Enabled || !NetworkServer.Configuration.HasFileSupport || !ReportLimiter.TryConsume(peer)) return;

                string uuid = "unknown";
                string displayName = string.Empty;
                string platform = string.Empty;
                if (Basis.Network.Server.Generic.BasisSavedState.GetLastPlayerMetaData(peer, out ClientMetaDataMessage meta))
                {
                    if (!string.IsNullOrEmpty(meta.playerUUID)) uuid = meta.playerUUID;
                    displayName = meta.playerDisplayName ?? string.Empty;
                    platform = meta.playerPlatform ?? string.Empty;
                }
                if (SeenPerUser.TryGetValue(uuid, out HashSet<long> known))
                {
                    lock (known)
                    {
                        if (known.Count >= MaxSeenPerUser) return;
                    }
                }

                if (!reader.TryGetByte(out byte severity)) return;
                if (!reader.TryGetBytesWithLength(out byte[] compressed)) return;
                string[] parts = PermissionCompression.DecompressExtras(compressed, 3, MaxDecompressedReportBytes, true);
                string system = parts.Length > 0 ? parts[0] : string.Empty;
                string message = parts.Length > 1 ? parts[1] : string.Empty;
                string stack = parts.Length > 2 ? parts[2] : string.Empty;

                // Truncate before hashing so the dedup key and the written report agree.
                if (message != null && message.Length > MaxMessageChars) message = message.Substring(0, MaxMessageChars);
                if (stack != null && stack.Length > MaxStackChars) stack = stack.Substring(0, MaxStackChars);

                if (SeenPerUser.Count >= MaxTrackedUsers && !SeenPerUser.ContainsKey(uuid))
                {
                    SeenPerUser.Clear();
                }

                long hash = ComputeHash(severity, system, message, stack);
                HashSet<long> seen = SeenPerUser.GetOrAdd(uuid, _ => new HashSet<long>());
                lock (seen)
                {
                    if (seen.Count >= MaxSeenPerUser) return;
                    if (!seen.Add(hash)) return;
                }

                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        WriteReport(uuid, displayName, platform, severity, system, message, stack);
                    }
                    catch (Exception e)
                    {
                        BNLThrottle.LogError("errorReport.write", $"Failed to write error report: {e.Message}");
                    }
                });
            }
            catch (Exception e)
            {
                BNLThrottle.LogError("errorReport.handle", $"Failed to handle error report: {e.Message}");
            }
            finally
            {
                reader.Recycle();
            }
        }

        private static long ComputeHash(byte severity, string system, string message, string stack)
        {
            string firstStackLine = stack ?? string.Empty;
            int nl = firstStackLine.IndexOf('\n');
            if (nl >= 0) firstStackLine = firstStackLine.Substring(0, nl);

            // FNV-1a 64. A collision only suppresses a duplicate report line; it never loses data.
            long hash = unchecked((long)14695981039346656037UL);
            hash = FnvMix(hash, severity);
            hash = FnvMix(hash, system);
            hash = FnvMix(hash, message);
            hash = FnvMix(hash, firstStackLine);
            return hash;
        }

        private static long FnvMix(long hash, byte value)
        {
            unchecked
            {
                return (hash ^ value) * (long)1099511628211UL;
            }
        }

        private static long FnvMix(long hash, string value)
        {
            unchecked
            {
                hash = (hash ^ 0x1F) * (long)1099511628211UL;
                if (string.IsNullOrEmpty(value)) return hash;
                foreach (char c in value)
                {
                    hash = (hash ^ (byte)c) * (long)1099511628211UL;
                    hash = (hash ^ (byte)(c >> 8)) * (long)1099511628211UL;
                }
                return hash;
            }
        }

        private static void WriteReport(string uuid, string displayName, string platform, byte severity, string system, string message, string stack)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "CrashReports");
            string file = Path.Combine(dir, SanitizeFileName(uuid) + ".jsonl");

            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"timeUtc\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",");
            sb.Append("\"uuid\":\"").Append(JsonEscape(uuid)).Append("\",");
            sb.Append("\"displayName\":\"").Append(JsonEscape(displayName)).Append("\",");
            sb.Append("\"platform\":\"").Append(JsonEscape(platform)).Append("\",");
            sb.Append("\"severity\":\"").Append(severity == 1 ? "exception" : severity == 2 ? "crash" : "error").Append("\",");
            sb.Append("\"system\":\"").Append(JsonEscape(system)).Append("\",");
            sb.Append("\"message\":\"").Append(JsonEscape(message)).Append("\",");
            sb.Append("\"stack\":\"").Append(JsonEscape(stack)).Append("\"}");

            sb.Append('\n');
            string line = sb.ToString();
            long lineBytes = Encoding.UTF8.GetByteCount(line);
            lock (FileLock)
            {
                Directory.CreateDirectory(dir);
                if (folderBytes < 0)
                {
                    folderBytes = 0;
                    foreach (string existing in Directory.EnumerateFiles(dir, "*.jsonl")) folderBytes += new FileInfo(existing).Length;
                }
                long fileBytes = File.Exists(file) ? new FileInfo(file).Length : 0;
                if (fileBytes + lineBytes > MaxBytesPerUserFile || folderBytes + lineBytes > MaxCrashReportFolderBytes)
                {
                    BNLThrottle.LogWarning("errorReport.quota", $"Crash report storage is full ({MaxBytesPerUserFile / 1024} KB per player, {MaxCrashReportFolderBytes / (1024 * 1024)} MB in total); report from {uuid} not saved.");
                    return;
                }
                File.AppendAllText(file, line);
                folderBytes += lineBytes;
            }
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value;
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            StringBuilder sb = new StringBuilder(value.Length + 16);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
