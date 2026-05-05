using Basis.Config;
using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.BasisSdk.Players;
using Basis.Utilities;
using System.Text;
using System.Threading;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static SerializableBasis;

namespace Basis.Network
{
    public class ClientManager
    {
        public int ClientCount => ConfigManager.ClientCount;
        private readonly List<NetworkClient> clients = new();
        private readonly CancellationTokenSource cts = new();
        public NetPeer[] FinalPeers;
        public static int Size;

        // Cached once — config doesn't change at runtime
        private byte[] _cachedPasswordBytes;
        private byte[] _cachedAvatarBytes;

        public async Task StartClientsAsync()
        {
            Size = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
            BNL.Log($"Payload Size for muscles is now {Size}");
            List<NetPeer> peers = new();

            _cachedPasswordBytes = Encoding.UTF8.GetBytes(ConfigManager.Password);
            var avatarInfo = new BasisAvatarNetworkLoad
            {
                URL = ConfigManager.AvatarUrl,
                UnlockPassword = ConfigManager.Password
            };
            _cachedAvatarBytes = avatarInfo.EncodeToBytes();

            for (int Index = 0; Index < ClientCount; Index++)
            {
                var name = NameGenerator.GenerateRandomPlayerName();
                var uuid = Guid.NewGuid().ToString();

                var readyMessage = new ReadyMessage
                {
                    playerMetaDataMessage = new ClientMetaDataMessage
                    {
                        playerDisplayName = name,
                        playerUUID = uuid,
                        playerPlatform = "Headless",
                    },
                    clientAvatarChangeMessage = new ClientAvatarChangeMessage
                    {
                        byteArray = _cachedAvatarBytes,
                        loadMode = (byte)ConfigManager.AvatarLoadMode,
                        LocalAvatarIndex = 0,
                    },
                    localAvatarSyncMessage = new LocalAvatarSyncMessage
                    {
                        array = MovementSender.Generate().Message.array,
                        AdditionalAvatarDataSize = 0,
                        LinkedAvatarIndex = 0,
                        DataQualityLevel = (byte)BitQuality.High,
                        AdditionalAvatarDatas = null,

                    }
                };
                var netClient = new NetworkClient();
                var peer = netClient.StartClient(ConfigManager.Ip, ConfigManager.Port, readyMessage, _cachedPasswordBytes, CreateConfig());

                if (peer != null)
                {
                    netClient.listener.NetworkReceiveEvent += MessageHandler.OnReceive;
                    netClient.listener.PeerDisconnectedEvent += MessageHandler.OnDisconnect;

                    lock (clients) clients.Add(netClient);
                    lock (peers) peers.Add(peer);

                    BNL.Log($"Connected: {name} ({uuid})");
                }

                await Task.Delay(1, cts.Token);
            }
            FinalPeers = [.. peers];
        }
        public async Task ReconnectClientAsync(int index)
        {
            if (index < 0 || index >= clients.Count) return;

            var oldClient = clients[index];

            oldClient?.Disconnect();
            BNL.Log($"Disconnected client at index {index}");

            await Task.Delay(3000); // wait before reconnecting

            var name = NameGenerator.GenerateRandomPlayerName();
            var uuid = Guid.NewGuid().ToString();

            var readyMessage = new ReadyMessage
            {
                playerMetaDataMessage = new ClientMetaDataMessage
                {
                    playerDisplayName = name,
                    playerUUID = uuid,
                    playerPlatform = "Headless",
                },
                clientAvatarChangeMessage = new ClientAvatarChangeMessage
                {
                    byteArray = _cachedAvatarBytes,
                    loadMode = (byte)ConfigManager.AvatarLoadMode,
                    LocalAvatarIndex = 1,

                },
                localAvatarSyncMessage = new LocalAvatarSyncMessage
                {
                    array = MovementSender.Generate().Message.array,
                    AdditionalAvatarDataSize = 0,
                    LinkedAvatarIndex = 0,
                }
            };

            var netClient = new NetworkClient();
            var peer = netClient.StartClient(ConfigManager.Ip, ConfigManager.Port, readyMessage, _cachedPasswordBytes, CreateConfig());

            if (peer != null)
            {
                netClient.listener.NetworkReceiveEvent += MessageHandler.OnReceive;
                netClient.listener.PeerDisconnectedEvent += MessageHandler.OnDisconnect;

                lock (clients) clients[index] = netClient;
                Interlocked.Exchange(ref FinalPeers[index], peer);

                BNL.Log($"Reconnected: {name} ({uuid}) at index {index}");
            }
        }
        public Task StopClientsAsync()
        {
            foreach (var client in clients) client?.Disconnect();
            return Task.CompletedTask;
        }
        public Configuration CreateConfig()
        {
            Configuration Configuration = new Configuration();
            Configuration.UseNativeSockets = true;
            ///we dont use auth identiy as we are fake client system
            Configuration.UseAuthIdentity = false;

            return Configuration;
        }
    }
}
