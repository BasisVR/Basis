using Basis.Network;
using Basis.Network.Server;
using BasisNetworkConsole;
using BasisNetworking.InitalData;
namespace Basis
{
    class Program
    {
        public static BasisNetworkHealthCheck Check;

        private const string ConfigFileName = "config.xml";
        private const string LogsFolderName = "Logs";
        private const string InitialResources = "initalresources";
        public static bool isRunning = true;
        private static ManualResetEventSlim shutdownEvent = new ManualResetEventSlim(false);
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
            Configuration config = Configuration.LoadFromXml(configFilePath);
            config.ProcessEnvironmentalOverrides();

            ThreadPool.SetMinThreads(config.MinThreadPoolThreads, config.MinThreadPoolThreads);
            ThreadPool.SetMaxThreads(config.MaxThreadPoolThreads, config.MaxThreadPoolThreads);

            string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogsFolderName);
            BasisServerSideLogging.Initialize(config, folderPath);

            BNL.Log("Server Booting");

            Check = new BasisNetworkHealthCheck(config);

            NetworkServer.StartServer(config);
            BasisLoadableLoader.LoadXML(InitialResources);

            AppDomain.CurrentDomain.ProcessExit += async (sender, eventArgs) =>
            {
                BNL.Log("Shutting down server...");
                isRunning = false;
                shutdownEvent.Set(); // Signal the main thread to exit

                if (config.EnableStatistics) BasisStatistics.StopWorkerThread();
                await BasisServerSideLogging.ShutdownAsync();
                BNL.Log("Server shut down successfully.");
            };
            if (config.EnableConsole)
            {
                BasisConsoleCommands.RegisterCommand("/admin add", BasisConsoleCommands.HandleAddAdmin);
                BasisConsoleCommands.RegisterCommand("/status", BasisConsoleCommands.HandleStatus);
                BasisConsoleCommands.RegisterCommand("/shutdown", BasisConsoleCommands.HandleShutdown);
                BasisConsoleCommands.RegisterCommand("/help", BasisConsoleCommands.HandleHelp);
                BasisConsoleCommands.RegisterConfigurationCommands(config);

                BasisConsoleCommands.StartConsoleListener();
            }
            // Wait for shutdown signal
            shutdownEvent.Wait();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            BNL.LogError($"Unhandled Exception: {e.ExceptionObject}");
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            BNL.LogError($"Unobserved Task Exception: {e.Exception.Message}");
            e.SetObserved();
        }
    }
}
