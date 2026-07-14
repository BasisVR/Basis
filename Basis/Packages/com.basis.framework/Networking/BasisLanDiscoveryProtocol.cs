using Basis.Network.Core;
using Basis.Scripts.Device_Management;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Server-directory source backed by LAN advertisements. Entries expire automatically
    /// when their host stops announcing, matching Minecraft-style LAN discovery behavior.
    /// </summary>
    public sealed class LanServersDirectorySource : IServerDirectorySource, IDisposable
    {
        public const string Id = "lanServers";
        private const int EntryLifetimeMs = 5000;
        private const int CleanupIntervalMs = 1000;
        private const int MaxTrackedServers = 256;
        private static readonly long EntryLifetimeTicks =
            (long)(EntryLifetimeMs / 1000.0 * Stopwatch.Frequency);

        private sealed class DiscoveredServer
        {
            public Guid InstanceId;
            public IPAddress Address;
            public long AddressLastSeenTicks;
            public IPAddress AlternateAddress;
            public long AlternateAddressLastSeenTicks;
            public ushort Port;
            public bool RequiresPassword;
            public string NetworkStackId;
            public string ServerName;
            public string Motd;
            public long LastSeenTicks;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<Guid, DiscoveredServer> _servers = new Dictionary<Guid, DiscoveredServer>();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private UdpClient _listener;
        private BasisLanMdnsBrowser _mdnsBrowser;
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _androidMulticastLock;
#endif
        private int _notificationQueued;
        private volatile bool _disposed;

        public static LanServersDirectorySource Instance { get; private set; }

        public string SourceId => Id;
        public string DisplayName => Basis.BasisUI.BasisLocalization.Get("menu.servers.source.lanServers");
        public bool SupportsAdd => false;
        public event Action SourceChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Application.quitting -= Shutdown;
            Instance?.Dispose();
            Instance = null;
            BasisServerDirectoryRegistry.Unregister(Id);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoRegister()
        {
#if !UNITY_SERVER
            Initialize();
#endif
        }

        public static void Initialize()
        {
            if (Instance != null)
            {
                return;
            }

            Instance = new LanServersDirectorySource();
            BasisServerDirectoryRegistry.Register(Instance);
            Application.quitting += Shutdown;
            Instance.StartListening();
        }

        private static void Shutdown()
        {
            Application.quitting -= Shutdown;
            Instance?.Dispose();
            Instance = null;
            BasisServerDirectoryRegistry.Unregister(Id);
        }

        public Task<IReadOnlyList<ServerDirectoryEntry>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<ServerDirectoryEntry> entries = new List<ServerDirectoryEntry>();
            bool removedExpired;
            lock (_gate)
            {
                removedExpired = PruneExpiredLocked(Stopwatch.GetTimestamp());
                foreach (DiscoveredServer server in _servers.Values)
                {
                    entries.Add(BuildEntry(server));
                }
            }

            if (removedExpired)
            {
                QueueSourceChanged();
            }
            return Task.FromResult<IReadOnlyList<ServerDirectoryEntry>>(entries);
        }

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PruneExpired())
            {
                QueueSourceChanged();
            }
            return Task.CompletedTask;
        }

        private void StartListening()
        {
            AcquireAndroidMulticastLock();
            bool discoveryStarted = false;
            UdpClient listener = null;
            try
            {
                listener = new UdpClient(AddressFamily.InterNetwork);
                listener.Client.ExclusiveAddressUse = false;
                listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, BasisLanDiscoveryProtocol.DiscoveryPort));
                try { listener.JoinMulticastGroup(BasisLanDiscoveryProtocol.MulticastAddress); }
                catch (Exception ex) when (ex is SocketException || ex is PlatformNotSupportedException || ex is NotImplementedException) { }

                _listener = listener;
                UdpClient activeListener = listener;
                listener = null;
                discoveryStarted = true;
                _ = Task.Run(() => ReceiveLoopAsync(activeListener, _cancellation.Token));
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"LAN server discovery could not listen on UDP {BasisLanDiscoveryProtocol.DiscoveryPort}: {ex.Message}");
            }
            finally
            {
                listener?.Dispose();
            }

            try
            {
                _mdnsBrowser = new BasisLanMdnsBrowser(ProcessAdvertisement, RemoveAdvertisement);
                discoveryStarted = true;
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"LAN mDNS discovery could not start: {ex.Message}");
            }

            if (discoveryStarted)
            {
                _ = Task.Run(() => CleanupLoopAsync(_cancellation.Token));
            }
            else
            {
                ReleaseAndroidMulticastLock();
            }
        }

        private void AcquireAndroidMulticastLock()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using AndroidJavaObject applicationContext = activity.Call<AndroidJavaObject>("getApplicationContext");
                using AndroidJavaObject wifiManager = applicationContext.Call<AndroidJavaObject>("getSystemService", "wifi");
                _androidMulticastLock = wifiManager.Call<AndroidJavaObject>("createMulticastLock", "BasisLanDiscovery");
                _androidMulticastLock.Call("setReferenceCounted", false);
                _androidMulticastLock.Call("acquire");
            }
            catch (Exception ex)
            {
                _androidMulticastLock?.Dispose();
                _androidMulticastLock = null;
                BasisDebug.LogWarning($"Could not acquire Android LAN discovery multicast lock: {ex.Message}");
            }
#endif
        }

        private void ReleaseAndroidMulticastLock()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_androidMulticastLock == null)
            {
                return;
            }

            try
            {
                if (_androidMulticastLock.Call<bool>("isHeld"))
                {
                    _androidMulticastLock.Call("release");
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Could not release Android LAN discovery multicast lock: {ex.Message}");
            }
            finally
            {
                _androidMulticastLock.Dispose();
                _androidMulticastLock = null;
            }
#endif
        }

        private async Task ReceiveLoopAsync(UdpClient listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult result = await listener.ReceiveAsync().ConfigureAwait(false);
                    if (result.RemoteEndPoint == null
                        || result.RemoteEndPoint.Address == null
                        || !BasisLanDiscoveryProtocol.TryDeserialize(result.Buffer, out BasisLanAdvertisement advertisement))
                    {
                        continue;
                    }

                    ProcessAdvertisement(advertisement, result.RemoteEndPoint.Address);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        BasisDebug.LogWarning($"LAN server discovery receive failed: {ex.Message}");
                    }
                }
            }
        }

        private void ProcessAdvertisement(BasisLanAdvertisement advertisement, IPAddress address)
        {
            if (_disposed || address == null)
            {
                return;
            }

            bool changed;
            long nowTicks = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                changed = PruneExpiredLocked(nowTicks);
                bool isNew = !_servers.TryGetValue(advertisement.InstanceId, out DiscoveredServer existing);
                changed |= isNew;
                if (existing == null)
                {
                    if (_servers.Count >= MaxTrackedServers)
                    {
                        RemoveOldestServerLocked();
                    }
                    existing = new DiscoveredServer { InstanceId = advertisement.InstanceId };
                    _servers.Add(advertisement.InstanceId, existing);
                }

                changed |= UpdateAddressLocked(existing, address, nowTicks);

                if (!isNew)
                {
                    changed |= existing.Port != advertisement.ServerPort
                        || existing.RequiresPassword != advertisement.RequiresPassword
                        || !string.Equals(existing.NetworkStackId, advertisement.NetworkStackId, StringComparison.Ordinal)
                        || !string.Equals(existing.ServerName, advertisement.ServerName, StringComparison.Ordinal)
                        || !string.Equals(existing.Motd, advertisement.Motd, StringComparison.Ordinal);
                }

                existing.Port = advertisement.ServerPort;
                existing.RequiresPassword = advertisement.RequiresPassword;
                existing.NetworkStackId = advertisement.NetworkStackId;
                existing.ServerName = advertisement.ServerName;
                existing.Motd = advertisement.Motd;
                existing.LastSeenTicks = nowTicks;
            }

            if (changed)
            {
                QueueSourceChanged();
            }
        }

        private void RemoveAdvertisement(Guid instanceId)
        {
            bool removed;
            lock (_gate)
            {
                removed = _servers.Remove(instanceId);
            }
            if (removed)
            {
                QueueSourceChanged();
            }
        }

        private async Task CleanupLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(CleanupIntervalMs, cancellationToken).ConfigureAwait(false);
                    if (PruneExpired())
                    {
                        QueueSourceChanged();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private bool PruneExpired()
        {
            lock (_gate)
            {
                return PruneExpiredLocked(Stopwatch.GetTimestamp());
            }
        }

        private bool PruneExpiredLocked(long nowTicks)
        {
            List<Guid> expired = null;
            foreach (KeyValuePair<Guid, DiscoveredServer> pair in _servers)
            {
                if (nowTicks - pair.Value.LastSeenTicks <= EntryLifetimeTicks)
                {
                    continue;
                }

                expired ??= new List<Guid>();
                expired.Add(pair.Key);
            }

            if (expired == null)
            {
                return false;
            }

            foreach (Guid id in expired)
            {
                _servers.Remove(id);
            }
            return true;
        }

        private static bool UpdateAddressLocked(DiscoveredServer server, IPAddress address, long nowTicks)
        {
            if (server.Address == null)
            {
                server.Address = address;
                server.AddressLastSeenTicks = nowTicks;
                return true;
            }

            if (Equals(server.Address, address))
            {
                server.AddressLastSeenTicks = nowTicks;
                return false;
            }

            if (Equals(server.AlternateAddress, address))
            {
                server.AlternateAddressLastSeenTicks = nowTicks;
            }
            else if (server.AlternateAddress == null
                     || nowTicks - server.AlternateAddressLastSeenTicks > EntryLifetimeTicks
                     || AddressPreferenceRank(address) < AddressPreferenceRank(server.AlternateAddress))
            {
                server.AlternateAddress = address;
                server.AlternateAddressLastSeenTicks = nowTicks;
            }

            if (nowTicks - server.AddressLastSeenTicks <= EntryLifetimeTicks)
            {
                return false;
            }

            IPAddress replacement = server.AlternateAddress ?? address;
            server.Address = replacement;
            server.AddressLastSeenTicks = Equals(replacement, address)
                ? nowTicks
                : server.AlternateAddressLastSeenTicks;
            server.AlternateAddress = null;
            server.AlternateAddressLastSeenTicks = 0;
            return true;
        }

        private static int AddressPreferenceRank(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();
                return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254 ? 1 : 0;
            }
            return address.IsIPv6LinkLocal ? 3 : 2;
        }

        private void RemoveOldestServerLocked()
        {
            Guid oldestId = Guid.Empty;
            long oldestTicks = long.MaxValue;
            foreach (KeyValuePair<Guid, DiscoveredServer> pair in _servers)
            {
                if (pair.Value.LastSeenTicks < oldestTicks)
                {
                    oldestTicks = pair.Value.LastSeenTicks;
                    oldestId = pair.Key;
                }
            }

            if (oldestId != Guid.Empty)
            {
                _servers.Remove(oldestId);
            }
        }

        private static ServerDirectoryEntry BuildEntry(DiscoveredServer server)
        {
            string address = server.Address.ToString();
            string stackId = string.IsNullOrWhiteSpace(server.NetworkStackId)
                || !BasisNetworkStackRegistry.IsRegistered(server.NetworkStackId)
                ? BasisNetworkStackRegistry.DefaultId
                : server.NetworkStackId;
            string rawAddress = server.Address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{address}]:{server.Port}"
                : $"{address}:{server.Port}";
            ConnectionTarget target = new ConnectionTarget(stackId, rawAddress);
            target.Set(ConnectionTarget.Keys.Address, address);
            target.Set(ConnectionTarget.Keys.Port, server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            target.Set(ConnectionTarget.Keys.Password, string.Empty);

            return new ServerDirectoryEntry
            {
                Id = $"lan:{server.InstanceId:N}",
                SourceId = Id,
                DisplayName = string.IsNullOrWhiteSpace(server.ServerName) ? address : server.ServerName,
                Description = server.Motd ?? string.Empty,
                Password = string.Empty,
                HasPassword = server.RequiresPassword,
                Target = target,
                CanEdit = false,
                CanRemove = false,
            };
        }

        private void QueueSourceChanged()
        {
            if (Interlocked.Exchange(ref _notificationQueued, 1) != 0)
            {
                return;
            }

            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                Interlocked.Exchange(ref _notificationQueued, 0);
                if (_disposed)
                {
                    return;
                }

                try { SourceChanged?.Invoke(); }
                catch (Exception ex) { BasisDebug.LogError($"LanServersDirectorySource.SourceChanged threw: {ex.Message}"); }
            });
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            try { _listener?.DropMulticastGroup(BasisLanDiscoveryProtocol.MulticastAddress); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (PlatformNotSupportedException) { }
            catch (NotImplementedException) { }
            try { _listener?.Close(); }
            catch (ObjectDisposedException) { }
            _listener = null;
            try { _mdnsBrowser?.Dispose(); }
            catch (Exception ex) { BasisDebug.LogWarning($"LAN mDNS discovery shutdown failed: {ex.Message}"); }
            _mdnsBrowser = null;
            ReleaseAndroidMulticastLock();
            _cancellation.Dispose();

            lock (_gate)
            {
                _servers.Clear();
            }
        }
    }
}
