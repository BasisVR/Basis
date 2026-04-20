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

            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                Console.WriteLine("Shutting down...");
                clientManager.StopClientsAsync().GetAwaiter().GetResult();
            };

            MovementSender.Initialize(clientManager.ClientCount);

            // Start batched movement loops (one per CPU core instead of one per peer)
            StartSmoothMovementLoops(clientManager.FinalPeers);

            // Start random reconnects
            _ = StartRandomReconnectLoop(clientManager);

            await Task.Delay(-1); // keep main alive
        }

        public static void StopClient(ClientManager manager, int index)
        {
            var peer = Volatile.Read(ref manager.FinalPeers[index]);
            if (peer != null)
            {
                peer.Disconnect();
            }
        }

        /// <summary>
        /// Batched movement loops — one worker per CPU core instead of one per peer.
        /// Each worker processes a slice of the peer array using PeriodicTimer
        /// to avoid 250+ concurrent async state machines and timer allocations.
        /// </summary>
        private static void StartSmoothMovementLoops(NetPeer[] peers)
        {
            int workerCount = Math.Min(Environment.ProcessorCount, peers.Length);
            int chunkSize = (peers.Length + workerCount - 1) / workerCount;

            for (int w = 0; w < workerCount; w++)
            {
                int start = w * chunkSize;
                int end = Math.Min(start + chunkSize, peers.Length);

                _ = Task.Run(async () =>
                {
                    // Stagger worker startup to spread initial traffic
                    await Task.Delay(start * 2);

                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(90));
                    while (await timer.WaitForNextTickAsync())
                    {
                        for (int i = start; i < end; i++)
                        {
                            var peer = Volatile.Read(ref peers[i]);
                            if (peer != null)
                            {
                                MovementSender.ProcessSingle(peer, i);
                            }
                        }
                    }
                });
            }
        }

        private static async Task StartRandomReconnectLoop(ClientManager clientManager)
        {
            int totalClients = clientManager.ClientCount;

            while (true)
            {
                int waitMinutes = Random.Shared.Next(1, 21); // 1–20 minutes
                await Task.Delay(TimeSpan.FromMinutes(waitMinutes));

                int indexToRestart = Random.Shared.Next(0, totalClients);
                BNL.Log($"Randomly restarting client at index {indexToRestart}");

                await clientManager.ReconnectClientAsync(indexToRestart);
            }
        }
    }
}
