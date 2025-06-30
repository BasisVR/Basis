using Basis.Network;
using Basis.Network.Server;
using BasisNetworkConsole;
using BasisNetworking.InitalData;
namespace Basis
{
    class Program
    {
        public static BasisNetworkHealthCheck Check;
        public static bool isRunning = true;
        private static ManualResetEventSlim shutdownEvent = new ManualResetEventSlim(false);
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string configDir = Path.Combine(baseDir, Configuration.ConfigFolderName);
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
            string configFilePath = Path.Combine(configDir, "config.xml");
            Configuration config = Configuration.LoadFromXml(configFilePath);
            config.ProcessEnvironmentalOverrides();

            ThreadPool.SetMinThreads(config.MinThreadPoolThreads, config.MinThreadPoolThreads);
            ThreadPool.SetMaxThreads(config.MaxThreadPoolThreads, config.MaxThreadPoolThreads);

            string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.LogsFolderName);
            BasisServerSideLogging.Initialize(config, folderPath);

            BNL.Log("Server Booting");
            Check = new BasisNetworkHealthCheck(config);

            NetworkServer.StartServer(config);
            
            // Handle legacy resource directory name migrations and similar.
            // after a version bump or two this should be removed
            string[] legacyPaths = new string[] {
                "initalresources",    // dooly spelling
                "initialressources",  // if you're french
                "intialresources",   // another common typo
            };
            
            string correctPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.InitialResourcesFolderName);

            foreach (string legacyName in legacyPaths)
            {
                string legacyFullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, legacyName);
                
                if (Directory.Exists(legacyFullPath) && !Directory.Exists(correctPath))
                {
                    try
                    {
                        BNL.Log($"Found legacy '{legacyName}' directory, migrating to '{Configuration.InitialResourcesFolderName}'...");
                        Directory.Move(legacyFullPath, correctPath);
                        BNL.Log("Directory migration completed successfully");
                        break; // Exit after first successful migration
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"Failed to migrate legacy directory '{legacyName}': {ex.Message}");
                    }
                }
            }
            BasisLoadableLoader.LoadXML(Configuration.InitialResourcesFolderName);

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
                BasisConsoleCommands.RegisterCommand("/admin add", "Adds a user as an admin.", BasisConsoleCommands.HandleAddAdmin);
                BasisConsoleCommands.RegisterCommand("/players", "Lists all connected players.", BasisConsoleCommands.HandleShowPlayers);
                BasisConsoleCommands.RegisterCommand("/status", "Shows the current server status.", BasisConsoleCommands.HandleStatus);
                BasisConsoleCommands.RegisterCommand("/shutdown", "Shuts down the server.", BasisConsoleCommands.HandleShutdown);
                BasisConsoleCommands.RegisterCommand("/help", "Displays all available commands.", BasisConsoleCommands.HandleHelp);
                BasisConsoleCommands.RegisterCommand("/clear", "Clears the console", BasisConsoleCommands.HandleClear);
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
