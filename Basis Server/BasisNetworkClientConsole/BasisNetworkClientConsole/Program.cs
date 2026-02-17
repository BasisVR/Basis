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

            // Start smooth independent movement loops
            StartSmoothMovementLoops(clientManager.FinalPeers);

            // Start random reconnects
            _ = StartRandomReconnectLoop(clientManager);

            await Task.Delay(-1); // keep main alive
        }

        public static void StopClient(ClientManager manager, int index)
        {
            var peer = manager.FinalPeers[index];
            if (peer != null)
            {
                peer.Disconnect();
            }
        }

        /// <summary>
        /// One independent movement loop per peer.
        /// This avoids traffic spikes and creates smooth network flow.
        /// </summary>
        private static void StartSmoothMovementLoops(NetPeer[] peers)
        {
            for (int Index = 0; Index < peers.Length; Index++)
            {
                int peerIndex = Index;

                _ = Task.Run(async () =>
                {
                    // Unique RNG per peer to avoid sync
                    var rng = new Random(Guid.NewGuid().GetHashCode());

                    // Stable base interval per peer (ms)
                    int baseInterval = rng.Next(60, 120);

                    // Initial offset so peers don’t align at startup
                    await Task.Delay(rng.Next(0, baseInterval));

                    while (true)
                    {
                        var peer = peers[peerIndex];

                        if (peer != null)
                        {
                            MovementSender.ProcessSingle(peer, peerIndex);
                        }

                        // Small jitter keeps it natural but stable
                        int jitter = rng.Next(-5, 6);
                        await Task.Delay(baseInterval + jitter);
                    }
                });
            }
        }

        private static async Task StartRandomReconnectLoop(ClientManager clientManager)
        {
            var rng = new Random();
            int totalClients = clientManager.ClientCount;

            while (true)
            {
                int waitMinutes = rng.Next(1, 21); // 1–20 minutes
                await Task.Delay(TimeSpan.FromMinutes(waitMinutes));

                int indexToRestart = rng.Next(0, totalClients);
                BNL.Log($"Randomly restarting client at index {indexToRestart}");

                await clientManager.ReconnectClientAsync(indexToRestart);
            }
        }
    }
}
