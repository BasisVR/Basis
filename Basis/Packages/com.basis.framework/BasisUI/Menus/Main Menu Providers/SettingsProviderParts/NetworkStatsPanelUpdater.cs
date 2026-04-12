using Basis.Scripts.Networking;
using Basis.Network.Core;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using static SerializableBasis;

namespace Basis.BasisUI
{
    public class NetworkStatsPanelUpdater : MonoBehaviour
    {
        public PanelElementDescriptor ConnectionField;
        public PanelElementDescriptor ServerField;
        public PanelElementDescriptor PingField;
        public PanelElementDescriptor PlayersField;
        public PanelElementDescriptor TransmissionField;
        public PanelElementDescriptor BandwidthField;
        public PanelElementDescriptor MetaField;

        private long _lastBytesSent;
        private long _lastBytesReceived;
        private float _bandwidthTimer;

        private float _updateTimer;
        private const float UpdateInterval = 0.25f;

        private void Update()
        {
            _updateTimer += Time.unscaledDeltaTime;
            if (_updateTimer < UpdateInterval) return;
            _updateTimer = 0f;

            bool connected = BasisNetworkManagement.NetworkRunning;
            NetPeer peer = connected ? BasisNetworkManagement.LocalPlayerPeer : null;

            // Connection
            if (ConnectionField != null)
            {
                if (!connected)
                {
                    ConnectionField.SetDescription("Disconnected");
                }
                else
                {
                    string peerIdStr = peer != null ? peer.Id.ToString() : "?";
                    ConnectionField.SetDescription($"Connected (Peer ID: {peerIdStr})");
                }
            }

            // Server
            if (ServerField != null)
            {
                if (!connected)
                {
                    ServerField.SetDescription("Not connected");
                }
                else
                {
                    var nm = BasisNetworkManagement.Instance;
                    string ip = nm != null ? nm.Ip : "?";
                    string port = nm != null ? nm.Port.ToString() : "?";
                    ServerField.SetDescription($"{ip}:{port}");
                }
            }

            // Ping / RTT
            if (PingField != null)
            {
                if (peer == null)
                {
                    PingField.SetDescription("N/A");
                }
                else
                {
                    int rtt = peer.RoundTripTime;
                    int ping = rtt / 2;
                    float lastPacket = peer.TimeSinceLastPacket;
                    PingField.SetDescription(
                        $"Ping: {ping}ms | RTT: {rtt}ms\n" +
                        $"Last Packet: {lastPacket:F1}s ago");
                }
            }

            // Players
            if (PlayersField != null)
            {
                int receiverCount = BasisNetworkPlayers.ReceiverCount;
                int totalPlayers = 0;
                int headlessPlayers = 0;
                Dictionary<string, int> platformCounts = new Dictionary<string, int>();
                foreach (var entry in BasisNetworkPlayers.Players)
                {
                    totalPlayers++; // We could have players leave during the count so better to just count them all the same time.
                    string playerPlatform = entry.Value?.Player?.PlayerPlatform;
                    string aggregatePlatform = NormalizePlatformAggregate(playerPlatform, out bool isHeadless);

                    if (isHeadless)
                    {
                        headlessPlayers++;
                    }

                    platformCounts[aggregatePlatform] = platformCounts.GetValueOrDefault(aggregatePlatform) + 1;

                    
                }
                int realPlayers = totalPlayers - headlessPlayers;
                ServerMetaDataMessage meta = BasisNetworkManagement.ServerMetaDataMessage;
                int capacity = meta.PeerLimit;
                StringBuilder description = new StringBuilder(160);
                description.Append(
                    $"Total: {totalPlayers} | Remote: {receiverCount}\n" +
                    $"Real: {realPlayers} | Headless: {headlessPlayers}\n" +
                    $"Server Capacity: {capacity}");

                if (platformCounts.Count > 0)
                {
                    description.Append("\nPlatforms: ");
                    bool hasAppendedPlatform = false;

                    AppendPlatformCount(description, platformCounts, "Windows", ref hasAppendedPlatform);
                    AppendPlatformCount(description, platformCounts, "macOS", ref hasAppendedPlatform);
                    AppendPlatformCount(description, platformCounts, "Linux", ref hasAppendedPlatform);
                    AppendPlatformCount(description, platformCounts, "Android", ref hasAppendedPlatform);
                    AppendPlatformCount(description, platformCounts, "iOS", ref hasAppendedPlatform);
                    AppendPlatformCount(description, platformCounts, "Windows Server", ref hasAppendedPlatform);
                    AppendPlatformCount(description, platformCounts, "Linux Server", ref hasAppendedPlatform);
                    AppendPlatformCount(description, platformCounts, "macOS Server", ref hasAppendedPlatform);
                    AppendPlatformCount(description, platformCounts, "Headless", ref hasAppendedPlatform);
                    AppendPlatformCount(description, platformCounts, "Unknown", ref hasAppendedPlatform);

                    foreach (KeyValuePair<string, int> platformCount in platformCounts)
                    {
                        if (hasAppendedPlatform)
                        {
                            description.Append(" | ");
                        }

                        description.Append(platformCount.Key);
                        description.Append(":");
                        description.Append(platformCount.Value);
                        hasAppendedPlatform = true;
                    }
                }

                PlayersField.SetDescription(description.ToString());
            }

            // Transmission
            if (TransmissionField != null)
            {
                var nm = BasisNetworkManagement.Instance;
                if (nm == null || nm.LocalAccessTransmitter == null)
                {
                    TransmissionField.SetDescription("No transmitter active");
                }
                else
                {
                    var results = nm.LocalAccessTransmitter.TransmissionResults;
                    if (results == null)
                    {
                        TransmissionField.SetDescription("No transmission data");
                    }
                    else
                    {
                        float dist = results.SquaredSmallestDistance > 0
                            ? Mathf.Sqrt(results.SquaredSmallestDistance)
                            : 0f;
                        TransmissionField.SetDescription(
                            $"Interval: {results.intervalSeconds * 1000f:F1}ms\n" +
                            $"Default: {results.DefaultInterval * 1000f:F1}ms\n" +
                            $"Unclamped: {results.UnClampedInterval * 1000f:F1}ms\n" +
                            $"Nearest Player: {dist:F1}m");
                    }
                }
            }

            // Bandwidth
            if (BandwidthField != null)
            {
                if (!connected || BasisNetworkConnection.NetworkClient?.client == null)
                {
                    BandwidthField.SetDescription("N/A");
                    _lastBytesSent = 0;
                    _lastBytesReceived = 0;
                    _bandwidthTimer = 0f;
                }
                else
                {
                    var stats = BasisNetworkConnection.NetworkClient.client.Statistics;
                    long totalSent = stats.BytesSent;
                    long totalRecv = stats.BytesReceived;

                    _bandwidthTimer += UpdateInterval;
                    long deltaSent = totalSent - _lastBytesSent;
                    long deltaRecv = totalRecv - _lastBytesReceived;

                    float sentPerSec = _bandwidthTimer > 0 ? deltaSent / _bandwidthTimer : 0;
                    float recvPerSec = _bandwidthTimer > 0 ? deltaRecv / _bandwidthTimer : 0;

                    _lastBytesSent = totalSent;
                    _lastBytesReceived = totalRecv;
                    _bandwidthTimer = 0f;

                    BandwidthField.SetDescription(
                        $"Sent: {FormatBytes(totalSent)} ({FormatRate(sentPerSec)})\n" +
                        $"Recv: {FormatBytes(totalRecv)} ({FormatRate(recvPerSec)})\n" +
                        $"Packets: {stats.PacketsSent} sent / {stats.PacketsReceived} recv\n" +
                        $"Packet Loss: {stats.PacketLoss}");
                }
            }

            // Server Metadata
            if (MetaField != null)
            {
                ServerMetaDataMessage meta = BasisNetworkManagement.ServerMetaDataMessage;
                MetaField.SetDescription(
                    $"Sync Interval: {meta.SyncInterval}ms\n" +
                    $"Base Multiplier: {meta.BaseMultiplier}\n" +
                    $"Increase Rate: {meta.IncreaseRate:F4}\n" +
                    $"Slowest Send Rate: {meta.SlowestSendRate:F2}s");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        private static string FormatRate(float bytesPerSec)
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:F1} KB/s";
            return $"{bytesPerSec / (1024.0 * 1024.0):F2} MB/s";
        }

        private static string NormalizePlatformAggregate(string platform, out bool isHeadless)
        {
            isHeadless = false;
            if (string.IsNullOrWhiteSpace(platform))
            {
                return "Unknown";
            }

            switch (platform)
            {
                case "WindowsServer":
                    isHeadless = true;
                    return "Windows Server";
                case "LinuxServer":
                    isHeadless = true;
                    return "Linux Server";
                case "OSXServer":
                    isHeadless = true;
                    return "macOS Server";
                case "Headless":
                    isHeadless = true;
                    return "Headless";
            }

            return BasisIOManagement.NormalizeCachePlatformName(platform) switch
            {
                "StandaloneWindows64" => "Windows",
                "StandaloneLinux64" => "Linux",
                "StandaloneOSX" => "macOS",
                "Android" => "Android",
                "iOS" => "iOS",
                _ => UserListProvider.GetPlatformLabel(platform)
            };
        }

        private static void AppendPlatformCount(StringBuilder description, Dictionary<string, int> platformCounts, string platform, ref bool hasAppendedPlatform)
        {
            if (!platformCounts.TryGetValue(platform, out int count))
            {
                return;
            }

            if (hasAppendedPlatform)
            {
                description.Append(" | ");
            }

            description.Append(platform);
            description.Append(": ");
            description.Append(count);
            hasAppendedPlatform = true;
            platformCounts.Remove(platform);
        }
    }
}
