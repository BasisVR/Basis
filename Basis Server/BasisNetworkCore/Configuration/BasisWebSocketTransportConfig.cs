using System;

namespace Basis.Network.Core
{
    [Serializable]
    public sealed class BasisWebSocketTransportConfig
    {
        public const int CurrentConfigVersion = 1;
        public int ConfigVersion = 0;
        public ushort Port = 0;
        public string Path = "";
        public string AllowedOrigins = "*";
        public bool TlsEnabled = false;
        public string TlsCertificatePath = "";
        public string TlsKeyPath = "";
        public string TlsCertificatePassword = "";
        public string TrustedProxies = "127.0.0.1,::1";
        public bool ClientUseTls = false;
        public int HandshakeTimeoutMs = 10000;
        public int MaxPendingHandshakes = 512;
        public int PingInterval = 1500;
        public int DisconnectTimeout = 30000;
        public int ConnectTimeoutMs = 15000;
        public int MaxMessageBytes = 16 * 1024 * 1024;
        public int MaxPendingBytesPerPeer = 32 * 1024 * 1024;
        public int MaxUnreliableBytesPerPeer = 512 * 1024;
        public int MaxVoiceBytesPerPeer = 512 * 1024;
        public int SocketSendBufferBytes = 256 * 1024;
        public int VirtualMtu = 1432;
    }
}
