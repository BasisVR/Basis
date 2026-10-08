using Basis.Network;
using Basis.Network.Server;
using BasisNetworkConsole;
using BasisNetworking.InitialData;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using System.Runtime.InteropServices;
using static BasisPermissions.PermissionManager;
namespace Basis
{
    class Program
    {
        public static BasisNetworkHealthCheck Check;
#if !UNITY_2017_1_OR_NEWER
        public static BasisRestApiHandler Api;
#endif
        public static bool isRunning = true;
        public static readonly DateTime StartedUtc = DateTime.UtcNow;
        private static ManualResetEventSlim shutdownEvent = new ManualResetEventSlim(false);
        private static readonly List<PosixSignalRegistration> stopSignalRegistrations = new List<PosixSignalRegistration>();
        private static BasisControlServer? control;
        private static string shutdownReason = string.Empty;
        private static int exitCode = BasisExitCode.Clean;
        private static int shutdownRequested;
        private static int stopSignalCount;
        private static int loggingFinished;
        private static volatile bool booted;
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
            BasisSystemd.Initialize();
            BasisSystemLog.Install();
            RegisterStopSignals();

            BasisConsoleCommands.WaitForPredecessorExit(args);
            Basis.Network.Core.BasisNetworkPackages.Initialize(typeof(Basis.Network.Core.BasisNetworkPackages).Assembly, typeof(NetworkServer).Assembly, typeof(Program).Assembly);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string configDir = Path.Combine(baseDir, Configuration.ConfigFolderName);
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
            string configFilePath = Path.Combine(configDir, "config.xml");
            // Capture this before LoadFromXml, which creates config.xml when it's missing.
            bool isFirstBoot = !File.Exists(configFilePath);
            Configuration config;
            try
            {
                config = Configuration.LoadFromXml(configFilePath);
            }
            catch (Exception e)
            {
                Fail(BasisExitCode.Config, $"Could not load {configFilePath}: {e.GetBaseException().Message}");
                return;
            }

            // Settings the benchmark fitted to this machine, if it left any. Applied once and
            // folded into config.xml, so it never shadows a later hand edit.
            //
            // ⚠️ Before the environment overrides, not after, and the order is load-bearing in both
            // directions. Applying this persists the config, and an override is a per-run pin — so
            // running it second would write whatever was in the environment permanently into
            // config.xml, turning a temporary override into a setting nobody remembers making.
            // Going first also leaves the overrides applied last, which is what makes them still
            // win for this run.
            BasisTuningProfile.ApplyIfPresent(configDir, config);

            config.ProcessEnvironmentalOverrides();

            string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.LogsFolderName);
            BasisServerSideLogging.Initialize(config, folderPath);
            BasisSystemLog.ServerLoggingStarted();

            // Brand-new server: walk the operator through core settings and force them to
            // designate an admin before anything boots.
            if (isFirstBoot)
            {
                BasisSetupWizard.Run(config, configFilePath);

                // Offer to fit the settings to this machine before it ever serves anyone. Runs the
                // benchmark as a separate process, so nothing from it is ever loaded here.
                if (BasisFirstBootTuning.Run(baseDir, configDir))
                {
                    // Re-read from disc rather than applying onto the object in hand. That object
                    // has already had this run's environment overrides folded into it, and applying
                    // a profile persists the config — which would write a per-run pin into
                    // config.xml permanently. Loading fresh also picks up the transport sidecars the
                    // benchmark's own server runs rewrote underneath us.
                    config = Configuration.LoadFromXml(configFilePath);
                    BasisTuningProfile.ApplyIfPresent(configDir, config);
                    config.ProcessEnvironmentalOverrides();
                }
            }

            if (Volatile.Read(ref shutdownRequested) != 0)
            {
                Exit(exitCode);
                return;
            }

            BNL.Log("Server Booting");
            Check = new BasisNetworkHealthCheck(config);
#if !UNITY_2017_1_OR_NEWER
            if (config.ApiEnabled && !string.IsNullOrEmpty(config.ApiKey))
            {
                try
                {
                    Api = new BasisRestApiHandler(config);
                }
                catch (Exception e)
                {
                    Fail(BasisExitCode.Unavailable, $"The REST API could not listen on {config.ApiHost}:{config.ApiPort}: {e.Message}");
                    return;
                }
            }
#endif

            if (!NetworkServer.StartServer(config))
            {
                Fail(BasisExitCode.Unavailable, $"Exiting because UDP port {config.SetPort} is unavailable.");
                return;
            }

            // Handle legacy resource directory name migrations and similar.
            // after a version bump or two this should be removed
            string[] legacyPaths = [
                "initalresources",    // dooly spelling
                "initialressources",  // if you're french
                "intialresources",   // another common typo
            ];

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
            BasisDefaultLibraryLoader.LoadXML(Configuration.DefaultLibraryFolderName);

            BasisConsoleCommands.RegisterCommand("/players", "Lists all connected players.", BasisConsoleCommands.HandleShowPlayers);
            BasisConsoleCommands.RegisterCommand("/status", "Shows the current server status.", BasisConsoleCommands.HandleStatus);
            BasisConsoleCommands.RegisterCommand("/shutdown", "Shuts down the server.", BasisConsoleCommands.HandleShutdown);
            BasisConsoleCommands.RegisterCommand("/restart", "Restarts the server, applying settings that need a restart.", BasisConsoleCommands.HandleRestart);
            BasisConsoleCommands.RegisterCommand("/help", "Displays all available commands.", BasisConsoleCommands.HandleHelp);
            BasisConsoleCommands.RegisterCommand("/clear", "Clears the console", BasisConsoleCommands.HandleClear);
            BasisConsoleCommands.RegisterCommand("/packages", "Lists the server packages built into this server.", HandlePackages);
            BasisConsoleCommands.RegisterPermissionCommands();
            BasisConsoleCommands.RegisterConfigurationCommands(config);
            if (config.EnableConsole)
            {
                BasisConsoleCommands.StartConsoleListener();
            }

            string? socketPath = BasisControlProtocol.ServerSocketPath(baseDir);
            if (socketPath != null)
            {
                control = BasisControlServer.Start(socketPath, BasisConsoleCommands.Execute);
                if (control != null) BasisSystemLog.Line += control.Publish;
            }

            booted = true;
            if (Volatile.Read(ref shutdownRequested) == 0)
            {
                BasisSystemd.Ready($"Listening on UDP port {config.SetPort}");
                BasisSystemd.StartHeartbeat(() => $"{NetworkServer.AuthenticatedPeers.Count} of {config.PeerLimit} players, UDP port {config.SetPort}");
            }

            // Wait for shutdown signal
            shutdownEvent.Wait();
            Shutdown(config);
        }

        public static bool RequestShutdown(int code, string reason)
        {
            if (Interlocked.CompareExchange(ref shutdownRequested, 1, 0) != 0) return false;

            exitCode = code;
            shutdownReason = reason;
            isRunning = false;
            shutdownEvent.Set();
            return true;
        }

        private static void Shutdown(Configuration config)
        {
            BasisSystemd.StopHeartbeat();
            BasisSystemd.Stopping(exitCode == BasisExitCode.Restart ? "Restarting" : "Shutting down");
            Monitor.TryEnter(BasisConsoleCommands.ExecutionGate, TimeSpan.FromSeconds(5));
            BNL.Log($"Shutting down server... ({shutdownReason})");
#if !UNITY_2017_1_OR_NEWER
            Attempt("REST API", () => Api?.Dispose());
#endif
            Attempt("health check", () => Check?.Dispose());
            Attempt("reduction system", BasisServerReductionSystemEvents.Shutdown);
            Attempt("network server", NetworkServer.StopServer);
            if (config.HasFileSupport) Attempt("permissions save", PermissionIntegration.Manager.FlushPendingSave);
            if (config.EnableStatistics) BasisStatistics.StopWorkerThread();
            BNL.Log("Server shut down successfully.");
            Exit(exitCode);
        }

        private static void HandlePackages(string[] args)
        {
            const string prefix = "BasisServerPackage:";
            int count = 0;
            foreach (System.Reflection.AssemblyMetadataAttribute metadata in typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            {
                if (!metadata.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                BNL.Log($"{metadata.Key.Substring(prefix.Length)} {metadata.Value}");
                count++;
            }
            if (count == 0) BNL.Log("No server packages are built into this server.");
            foreach (Type entry in Basis.Network.Core.BasisNetworkPackages.Loaded) BNL.Log($"Running {entry.FullName}");
            foreach (string failure in Basis.Network.Core.BasisNetworkPackages.Failures) BNL.LogError(failure);
        }

        private static void Attempt(string step, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                BNL.LogError($"Shutdown: {step} failed: {e.Message}");
            }
        }

        private static void Fail(int code, string message)
        {
            BNL.LogError(message);
            BasisSystemd.Status(message);
            Exit(code);
        }

        private static void Exit(int code)
        {
            FinishLogging();
            Environment.Exit(code);
        }

        private static void FinishLogging()
        {
            if (Interlocked.Exchange(ref loggingFinished, 1) == 1) return;

            try
            {
                control?.Dispose();
            }
            catch
            {
            }

            if (!BasisServerSideLogging.UseLogging) return;

            BasisServerSideLogging.UseLogging = false;
            try
            {
                BasisServerSideLogging.ShutdownAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }

        private static void RegisterStopSignals()
        {
            foreach (PosixSignal signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
            {
                try
                {
                    stopSignalRegistrations.Add(PosixSignalRegistration.Create(signal, OnStopSignal));
                }
                catch (Exception e) when (e is PlatformNotSupportedException || e is IOException)
                {
                }
            }
        }

        private static void OnStopSignal(PosixSignalContext context)
        {
            if (Interlocked.Increment(ref stopSignalCount) > 1) return;
            if (!booted && !Console.IsInputRedirected) return;

            context.Cancel = true;
            RequestShutdown(BasisExitCode.Clean, $"{context.Signal} received");
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            BNL.LogError($"Unhandled Exception: {e.ExceptionObject}");
            if (Volatile.Read(ref shutdownRequested) != 0)
            {
                Exit(exitCode);
                return;
            }

            BasisSystemd.Status("Crashed: " + ((e.ExceptionObject as Exception)?.Message ?? "unhandled exception"));
            Exit(BasisExitCode.Software);
        }

        private static void CurrentDomain_ProcessExit(object? sender, EventArgs e)
        {
            FinishLogging();
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            BNL.LogError($"Unobserved Task Exception: {e.Exception.Message}");
            e.SetObserved();
        }
    }
}
