using Basis.Scripts.Networking;
using Basis.Network.Core;
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
                int totalPlayers = BasisNetworkPlayers.Players.Count;
                int headlessPlayers = 0;
                foreach (var entry in BasisNetworkPlayers.Players)
                {
                    switch (entry.Value?.Player?.PlayerPlatform)
                    {
                        case "Headless":
                        case "WindowsServer":
                        case "LinuxServer":
                        case "OSXServer":
                            headlessPlayers++;
                            break;
                    }
                }
                int realPlayers = totalPlayers - headlessPlayers;
                ServerMetaDataMessage meta = BasisNetworkManagement.ServerMetaDataMessage;
                int capacity = meta.PeerLimit;
                PlayersField.SetDescription(
                    $"Total: {totalPlayers} | Remote: {receiverCount}\n" +
                    $"Real: {realPlayers} | Headless: {headlessPlayers}\n" +
                    $"Server Capacity: {capacity}");
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
    }
}
