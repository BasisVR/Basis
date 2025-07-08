using LiteNetLib;
using Basis.Config;
using Basis.Scripts.BasisSdk.Players;
using System.Text;
using static SerializableBasis;
using Basis.Utilities;

namespace Basis.Network
{
    public class ClientManager
    {
        public static readonly Random rng = new();
        public int ClientCount => ConfigManager.ClientCount;
        private readonly List<NetworkClient> clients = new();
        private readonly CancellationTokenSource cts = new();
        public NetPeer[] FinalPeers;

        public async Task StartClientsAsync()
        {
            List<NetPeer> peers = new();
            var passwordBytes = Encoding.UTF8.GetBytes(ConfigManager.Password);
            var avatarInfo = new AvatarNetworkLoadInformation
            {
                AvatarMetaUrl = "LoadingAvatar",
                AvatarBundleUrl = "LoadingAvatar",
                UnlockPassword = "LoadingAvatar"
            };
            var avatarBytes = avatarInfo.EncodeToBytes();

            for (int Index = 0; Index < ClientCount; Index++)
            {
                var name = NameGenerator.GenerateRandomPlayerName();
                var uuid = Guid.NewGuid().ToString();

                var readyMessage = new ReadyMessage
                {
                    playerMetaDataMessage = new ClientMetaDataMessage
                    {
                        playerDisplayName = name,
                        playerUUID = uuid
                    },
                    clientAvatarChangeMessage = new ClientAvatarChangeMessage
                    {
                        byteArray = avatarBytes,
                        loadMode = 1
                    },
                    localAvatarSyncMessage = new LocalAvatarSyncMessage
                    {
                        array = MovementSender.AvatarMessage
                    }
                };

                var netClient = new NetworkClient();
                var peer = netClient.StartClient(ConfigManager.Ip, ConfigManager.Port, readyMessage, passwordBytes, true);

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
            FinalPeers = peers.ToArray();
        }

        public Task StopClientsAsync()
        {
            foreach (var client in clients) client?.Disconnect();
            return Task.CompletedTask;
        }
    }
}
