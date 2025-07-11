using Basis.Logging;
using Basis.Network;
using Basis.Config;
using Basis.Utils;
using LiteNetLib;
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

            while (true)
            {
                MovementSender.Process(clientManager.FinalPeers);
                Thread.Sleep(ClientManager.rng.Next(50, 250));///server default is 50ms and server max should be around 250ms
            }
        }
    }
}
