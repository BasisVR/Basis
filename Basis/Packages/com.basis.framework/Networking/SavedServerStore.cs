using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Persisted entry in the user's "saved servers" list — the analogue of a row
    /// in Minecraft's multiplayer server list. The user-typed display name is the
    /// fallback row title until a successful info query overrides it with the
    /// server's own name.
    /// </summary>
    [Serializable]
    public class SavedServerEntry
    {
        public string Id;
        public string DisplayName;
        public string Address;
        public ushort Port;
        public string Password;
        public bool HasPassword;

        public SavedServerEntry()
        {
            Id = Guid.NewGuid().ToString("N");
            DisplayName = string.Empty;
            Address = string.Empty;
            Port = 4296;
            Password = "default_password";
            HasPassword = true;
        }
    }

    /// <summary>
    /// JSON-backed list of <see cref="SavedServerEntry"/>. Mirrors the "string" save
    /// pattern in <c>BasisDataStore</c> but holds a list of structured records.
    /// </summary>
    public static class SavedServerStore
    {
        public const string FileName = "SavedServers.BAS";

        [Serializable]
        private class SavedServerListWrapper
        {
            public List<SavedServerEntry> Entries = new List<SavedServerEntry>();
        }

        public static List<SavedServerEntry> Load()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, FileName);
                if (!File.Exists(path)) return new List<SavedServerEntry>();
                string json = File.ReadAllText(path);
                SavedServerListWrapper wrapper = JsonUtility.FromJson<SavedServerListWrapper>(json);
                if (wrapper == null || wrapper.Entries == null) return new List<SavedServerEntry>();
                foreach (SavedServerEntry e in wrapper.Entries)
                {
                    if (e == null) continue;
                    if (string.IsNullOrEmpty(e.Id)) e.Id = Guid.NewGuid().ToString("N");
                    if (e.Port == 0) e.Port = 4296;
                }
                return wrapper.Entries;
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"SavedServerStore.Load failed: {ex.Message}");
                return new List<SavedServerEntry>();
            }
        }

        public static void Save(List<SavedServerEntry> entries)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, FileName);
                SavedServerListWrapper wrapper = new SavedServerListWrapper { Entries = entries ?? new List<SavedServerEntry>() };
                File.WriteAllText(path, JsonUtility.ToJson(wrapper, prettyPrint: true));
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"SavedServerStore.Save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Splits a connection string of the form <c>address[:port][#password]</c>.
        /// Shared by the Servers panel editor, the <c>--connection=</c> CLI bootstrap,
        /// and the Server-typed content-share orb so they all parse identically.
        /// Returns false if the address segment ends up empty.
        /// </summary>
        public static bool TryParseConnectionString(
            string raw, out string address, out ushort port, out bool portProvided, out string password)
        {
            address = string.Empty;
            port = 4296;
            portProvided = false;
            password = string.Empty;
            if (string.IsNullOrEmpty(raw)) return false;

            string left = raw;
            int hashIdx = raw.IndexOf('#');
            if (hashIdx >= 0)
            {
                password = raw.Substring(hashIdx + 1);
                left = raw.Substring(0, hashIdx);
            }

            int colonIdx = left.LastIndexOf(':');
            if (colonIdx > 0
                && colonIdx < left.Length - 1
                && ushort.TryParse(left.Substring(colonIdx + 1), out ushort parsedPort)
                && parsedPort > 0)
            {
                address = left.Substring(0, colonIdx).Trim();
                port = parsedPort;
                portProvided = true;
            }
            else
            {
                address = left.Trim();
            }

            // Strip IPv6 brackets if the user wrote them.
            if (address.StartsWith("[") && address.EndsWith("]") && address.Length >= 2)
                address = address.Substring(1, address.Length - 2);

            return !string.IsNullOrEmpty(address);
        }
    }
}
