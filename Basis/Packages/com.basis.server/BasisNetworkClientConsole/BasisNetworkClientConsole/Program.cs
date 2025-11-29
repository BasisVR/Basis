using Basis.Logging;
using Basis.Network;
using Basis.Config;
using Basis.Utils;
using Basis.Network.Core;

namespace Basis
{
    partial class Program
    {
        public static async Task Main(string[] args)
        {
            ErrorHandlers.AttachGlobalHandlers();
            ConfigManager.LoadOrCreateConfigXml("Config.xml");
            NetDebug.Logger = new BasisClientLogger();

            var clientManager = new ClientManager();
            await clientManager.StartClientsAsync();

            AppDomain.CurrentDomain.ProcessExit += async (_, __) =>
            {
                Console.WriteLine("Shutting down...");
                await clientManager.StopClientsAsync();
            };

            MovementSender.Initialize(clientManager.ClientCount);

            // Start staggered movement
            _ = StartStaggeredMovementLoop(clientManager.FinalPeers);

            // Start random reconnects
            _ = StartRandomReconnectLoop(clientManager);

            await Task.Delay(-1); // keep main alive
        }
        public static void StopClient(ClientManager Manager, int Index)
        {
            var Value = Manager.FinalPeers[Index];
            if(Value != null)
            {
                Value.Disconnect();
            }
        }
        private static async Task StartStaggeredMovementLoop(NetPeer[] peers)
        {
            int peerCount = peers.Length;
            var random = new Random();

            while (true)
            {
                int baseDelay = ClientManager.rng.Next(50, 250);
                int intervalSpread = baseDelay / Math.Max(peerCount, 1); // stagger time between each peer

                var tasks = new List<Task>();

                for (int i = 0; i < peerCount; i++)
                {
                    int delay = intervalSpread * i + random.Next(0, 5); // small randomness to avoid perfect rhythm
                    int peerIndex = i;

                    tasks.Add(Task.Run(async () =>
                    {
                        await Task.Delay(delay); // staggered delay
                        MovementSender.ProcessSingle(peers[peerIndex], peerIndex);
                    }));
                }

                await Task.WhenAll(tasks);

                await Task.Delay(baseDelay); // global cycle delay before next batch
            }
        }
        private static async Task StartRandomReconnectLoop(ClientManager clientManager)
        {
            var rng = new Random();
            int totalClients = clientManager.ClientCount;

            while (true)
            {
                int waitMinutes = rng.Next(1, 21); // 1 to 20 minutes
                await Task.Delay(TimeSpan.FromMinutes(waitMinutes));

                int indexToRestart = rng.Next(0, totalClients);

                BNL.Log($"Randomly restarting client at index {indexToRestart}");
                await clientManager.ReconnectClientAsync(indexToRestart);
            }
        }
    }
}
