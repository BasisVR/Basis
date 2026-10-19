using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    [AutoStaticsCleanup]
    public sealed partial class SavedServersDirectorySource : IServerDirectorySource
    {
        public const string Id = "savedServers";

        public const string DefaultServerId = "__default__";
        public const string DefaultServerAddress = "server1.basisvr.org";
        public const ushort DefaultServerPort = 4296;
        public const string DefaultServerPassword = "default_password";

        public string SourceId => Id;
        public string DisplayName => BasisLocalization.Get("menu.servers.source.savedServers");
        public bool SupportsAdd => true;
        public event Action SourceChanged;

        public static SavedServersDirectorySource Instance { get; private set; }

        public static void Initialize()
        {
            if (Instance != null)
            {
                return;
            }

            Instance = new SavedServersDirectorySource();
            BasisServerDirectoryRegistry.Register(Instance);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoRegister() => Initialize();

        public Task<IReadOnlyList<ServerDirectoryEntry>> ListAsync(CancellationToken ct)
        {
            List<ServerDirectoryEntry> entries = new List<ServerDirectoryEntry>();
            entries.Add(BuildDefaultEntry());
            foreach (SavedServerEntry s in SavedServerStore.Load())
            {
                if (s == null) continue;
                entries.Add(BuildEntry(s));
            }
            return Task.FromResult<IReadOnlyList<ServerDirectoryEntry>>(entries);
        }

        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;

        public void NotifyChanged()
        {
            try { SourceChanged?.Invoke(); }
            catch (Exception ex) { BasisDebug.LogError($"SavedServersDirectorySource.SourceChanged threw: {ex.Message}"); }
        }

        private ServerDirectoryEntry BuildDefaultEntry()
        {
            ConnectionTarget target = new ConnectionTarget(BasisNetworkStackRegistry.DefaultId, $"{DefaultServerAddress}:{DefaultServerPort}");
            target.Set(ConnectionTarget.Keys.Address, DefaultServerAddress);
            target.Set(ConnectionTarget.Keys.Port, DefaultServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            target.Set(ConnectionTarget.Keys.Password, DefaultServerPassword);
            return new ServerDirectoryEntry
            {
                Id = DefaultServerId,
                SourceId = Id,
                DisplayName = BasisLocalization.Get("menu.servers.list.default"),
                Target = target,
                Password = DefaultServerPassword,
                HasPassword = true,
                CanEdit = false,
                CanRemove = false,
            };
        }

        private static string FormatRaw(string address, ushort port)
        {
            return IPAddress.TryParse(address, out IPAddress ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{address}]:{port}"
                : $"{address}:{port}";
        }

        private ServerDirectoryEntry BuildEntry(SavedServerEntry s)
        {
            string stackId = string.IsNullOrEmpty(s.NetworkStackId) ? BasisNetworkStackRegistry.DefaultId : s.NetworkStackId;
            ConnectionTarget target = new ConnectionTarget(stackId, FormatRaw(s.Address, s.Port));
            target.Set(ConnectionTarget.Keys.Address, s.Address);
            target.Set(ConnectionTarget.Keys.Port, s.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            target.Set(ConnectionTarget.Keys.Password, s.HasPassword ? (s.Password ?? string.Empty) : string.Empty);
            return new ServerDirectoryEntry
            {
                Id = s.Id,
                SourceId = Id,
                DisplayName = string.IsNullOrEmpty(s.DisplayName) ? s.Address : s.DisplayName,
                Target = target,
                Password = s.Password,
                HasPassword = s.HasPassword,
                CanEdit = true,
                CanRemove = true,
            };
        }

        public static bool IsDefaultEntryId(string id) => string.Equals(id, DefaultServerId, StringComparison.OrdinalIgnoreCase);
    }
}
