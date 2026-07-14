using Basis.Network.Core;
using Basis.Scripts.Device_Management;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Shared wire format for local-network server advertisements.
    /// Advertisements use a site-local multicast address plus directed IPv4 broadcasts,
    /// remain inside the LAN, and contain the hosted server's actual game port.
    /// </summary>
    internal static class BasisLanDiscoveryProtocol
    {
        public const int DiscoveryPort = 42960;
        public const uint Magic = 0xBA515201u;
        public const ushort Version = 1;
        public const int MaxPacketBytes = 1024;
        public const int MaxStackIdBytes = 64;
        public const int MaxServerNameBytes = 128;
        public const int MaxMotdBytes = 384;

        public static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.42.99");

        internal readonly struct Advertisement
        {
            public readonly Guid InstanceId;
            public readonly ushort ServerPort;
            public readonly bool RequiresPassword;
            public readonly string NetworkStackId;
            public readonly string ServerName;
            public readonly string Motd;

            public Advertisement(Guid instanceId, ushort serverPort, bool requiresPassword, string networkStackId, string serverName, string motd)
            {
                InstanceId = instanceId;
                ServerPort = serverPort;
                RequiresPassword = requiresPassword;
                NetworkStackId = networkStackId;
                ServerName = serverName;
                Motd = motd;
            }
        }

        public static byte[] Serialize(Advertisement advertisement)
        {
            using MemoryStream stream = new MemoryStream(256);
            using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(advertisement.InstanceId.ToByteArray());
            writer.Write(advertisement.ServerPort);
            writer.Write(advertisement.RequiresPassword ? (byte)1 : (byte)0);
            WriteString(writer, advertisement.NetworkStackId, MaxStackIdBytes);
            WriteString(writer, advertisement.ServerName, MaxServerNameBytes);
            WriteString(writer, advertisement.Motd, MaxMotdBytes);
            writer.Flush();
            return stream.ToArray();
        }

        public static bool TryDeserialize(byte[] data, out Advertisement advertisement)
        {
            advertisement = default;
            if (data == null || data.Length < 31 || data.Length > MaxPacketBytes)
            {
                return false;
            }

            try
            {
                using MemoryStream stream = new MemoryStream(data, false);
                using BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true);
                if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version)
                {
                    return false;
                }

                byte[] guidBytes = reader.ReadBytes(16);
                if (guidBytes.Length != 16)
                {
                    return false;
                }

                ushort serverPort = reader.ReadUInt16();
                byte flags = reader.ReadByte();
                if (serverPort == 0
                    || (flags & ~1) != 0
                    || !TryReadString(reader, stream, MaxStackIdBytes, out string stackId)
                    || !TryReadString(reader, stream, MaxServerNameBytes, out string serverName)
                    || !TryReadString(reader, stream, MaxMotdBytes, out string motd))
                {
                    return false;
                }

                advertisement = new Advertisement(new Guid(guidBytes), serverPort, (flags & 1) != 0, stackId, serverName, motd);
                return advertisement.InstanceId != Guid.Empty;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void WriteString(BinaryWriter writer, string value, int maxBytes)
        {
            string safeValue = value ?? string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(safeValue);
            if (bytes.Length > maxBytes)
            {
                int characterCount = safeValue.Length;
                do
                {
                    characterCount--;
                    bytes = Encoding.UTF8.GetBytes(safeValue.Substring(0, characterCount));
                }
                while (bytes.Length > maxBytes && characterCount > 0);
            }

            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static bool TryReadString(BinaryReader reader, MemoryStream stream, int maxBytes, out string value)
        {
            value = string.Empty;
            if (stream.Length - stream.Position < sizeof(ushort))
            {
                return false;
            }

            ushort byteCount = reader.ReadUInt16();
            if (byteCount > maxBytes || stream.Length - stream.Position < byteCount)
            {
                return false;
            }

            byte[] bytes = reader.ReadBytes(byteCount);
            if (bytes.Length != byteCount)
            {
                return false;
            }

            value = Encoding.UTF8.GetString(bytes);
            return true;
        }
    }

    /// <summary>
    /// Periodically announces an in-process hosted server through the custom LAN
    /// datagram protocol and standard mDNS/DNS-SD. The service is opt-in and is
    /// stopped with the hosted server lifecycle.
    /// </summary>
    public static class BasisLanServerAdvertiser
    {
        private const int InitialAdvertisementDelayMs = 750;
        private const int AdvertisementIntervalMs = 1500;
        private static readonly object Gate = new object();
        private static CancellationTokenSource _cancellation;
        private static BasisLanMdnsAdvertiser _mdnsAdvertiser;

        public static bool IsRunning
        {
            get
            {
                lock (Gate)
                {
                    return _cancellation != null;
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Stop();

        public static void Start(ushort serverPort, string networkStackId, string serverName, string motd, bool requiresPassword)
        {
            string effectiveStackId = string.IsNullOrWhiteSpace(networkStackId)
                ? BasisNetworkStackRegistry.DefaultId
                : networkStackId;
            BasisLanDiscoveryProtocol.Advertisement advertisement = new BasisLanDiscoveryProtocol.Advertisement(
                Guid.NewGuid(),
                serverPort,
                requiresPassword,
                effectiveStackId,
                string.IsNullOrWhiteSpace(serverName) ? "Basis Server" : serverName,
                motd ?? string.Empty);
            byte[] payload = BasisLanDiscoveryProtocol.Serialize(advertisement);
            BasisLanMdnsAdvertiser mdnsAdvertiser = null;
            try
            {
                mdnsAdvertiser = new BasisLanMdnsAdvertiser(advertisement);
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"LAN mDNS advertisement could not start: {ex.Message}");
            }

            CancellationTokenSource cancellation = new CancellationTokenSource();
            CancellationTokenSource previousCancellation;
            BasisLanMdnsAdvertiser previousMdnsAdvertiser;
            lock (Gate)
            {
                previousCancellation = _cancellation;
                previousMdnsAdvertiser = _mdnsAdvertiser;
                _cancellation = cancellation;
                _mdnsAdvertiser = mdnsAdvertiser;
            }

            StopResources(previousCancellation, previousMdnsAdvertiser);
            _ = Task.Run(() => AdvertiseAsync(payload, cancellation.Token));
        }

        public static void Stop()
        {
            CancellationTokenSource cancellation;
            BasisLanMdnsAdvertiser mdnsAdvertiser;
            lock (Gate)
            {
                cancellation = _cancellation;
                mdnsAdvertiser = _mdnsAdvertiser;
                _cancellation = null;
                _mdnsAdvertiser = null;
            }

            StopResources(cancellation, mdnsAdvertiser);
        }

        private static void StopResources(CancellationTokenSource cancellation, BasisLanMdnsAdvertiser mdnsAdvertiser)
        {
            if (cancellation != null)
            {
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                cancellation.Dispose();
            }
            mdnsAdvertiser?.Dispose();
        }

        private static async Task AdvertiseAsync(byte[] payload, CancellationToken cancellationToken)
        {
            try
            {
                using UdpClient sender = new UdpClient(AddressFamily.InterNetwork);
                sender.EnableBroadcast = true;
                sender.MulticastLoopback = true;
                sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

                await Task.Delay(InitialAdvertisementDelayMs, cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    SendAdvertisement(sender, payload);
                    await Task.Delay(AdvertisementIntervalMs, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"LAN server advertisement stopped: {ex.Message}");
            }
        }

        private static void SendAdvertisement(UdpClient sender, byte[] payload)
        {
            HashSet<string> sentEndpoints = new HashSet<string>(StringComparer.Ordinal);
            SendTo(sender, payload, new IPEndPoint(BasisLanDiscoveryProtocol.MulticastAddress, BasisLanDiscoveryProtocol.DiscoveryPort), sentEndpoints);
            SendTo(sender, payload, new IPEndPoint(IPAddress.Broadcast, BasisLanDiscoveryProtocol.DiscoveryPort), sentEndpoints);

            try
            {
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation addressInformation in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (addressInformation.Address.AddressFamily != AddressFamily.InterNetwork
                            || addressInformation.IPv4Mask == null)
                        {
                            continue;
                        }

                        byte[] addressBytes = addressInformation.Address.GetAddressBytes();
                        byte[] maskBytes = addressInformation.IPv4Mask.GetAddressBytes();
                        if (addressBytes.Length != 4 || maskBytes.Length != 4)
                        {
                            continue;
                        }

                        byte[] broadcastBytes = new byte[4];
                        for (int i = 0; i < broadcastBytes.Length; i++)
                        {
                            broadcastBytes[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
                        }

                        SendTo(sender, payload, new IPEndPoint(new IPAddress(broadcastBytes), BasisLanDiscoveryProtocol.DiscoveryPort), sentEndpoints);
                    }
                }
            }
            catch (Exception)
            {
                // Multicast and limited broadcast were already attempted above. Some Unity
                // platforms expose NetworkInterface but not its IPv4 mask details.
            }
        }

        private static void SendTo(UdpClient sender, byte[] payload, IPEndPoint endpoint, HashSet<string> sentEndpoints)
        {
            if (!sentEndpoints.Add(endpoint.ToString()))
            {
                return;
            }

            try { sender.Send(payload, payload.Length, endpoint); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

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
                        || !BasisLanDiscoveryProtocol.TryDeserialize(result.Buffer, out BasisLanDiscoveryProtocol.Advertisement advertisement))
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

        private void ProcessAdvertisement(BasisLanDiscoveryProtocol.Advertisement advertisement, IPAddress address)
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
                changed |= !_servers.TryGetValue(advertisement.InstanceId, out DiscoveredServer existing);
                if (existing == null)
                {
                    if (_servers.Count >= MaxTrackedServers)
                    {
                        RemoveOldestServerLocked();
                    }
                    existing = new DiscoveredServer { InstanceId = advertisement.InstanceId };
                    _servers.Add(advertisement.InstanceId, existing);
                }
                else
                {
                    changed |= !Equals(existing.Address, address)
                        || existing.Port != advertisement.ServerPort
                        || existing.RequiresPassword != advertisement.RequiresPassword
                        || !string.Equals(existing.NetworkStackId, advertisement.NetworkStackId, StringComparison.Ordinal)
                        || !string.Equals(existing.ServerName, advertisement.ServerName, StringComparison.Ordinal)
                        || !string.Equals(existing.Motd, advertisement.Motd, StringComparison.Ordinal);
                }

                existing.Address = address;
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
