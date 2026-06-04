using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Network.Core;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Framework-side connection orchestration, independent of any menu UI. Owns the
    /// disconnect/reconnect/bundle-load/connect sequence, the loading-bar progress
    /// reporting, and the <c>--connection=</c> command-line bootstrap so a headless or
    /// custom-UI application can connect without the Servers panel package present.
    /// The Servers panel (com.basis.provider.servers) is a thin UI on top of this.
    /// </summary>
    public static class BasisConnectionService
    {
        public const string UsernameFileName = "CachedUserName.BAS";
        public const string LastConnectedServerIdFile = "LastConnectedServerId.BAS";

        public static bool AutoConnectAttempted;

        // Stable key the loading bar uses to merge updates for the same connection
        // attempt, distinct from the bundle-load key BasisSceneLoad reports under.
        private const string ConnectionProgressKey = "BasisServerConnection";

        public static void ReportConnectionProgress(float progress, string message) =>
            BasisSceneLoad.progressCallback.ReportProgress(ConnectionProgressKey, progress, message ?? string.Empty);

        public static void ReportConnectionError(string message)
        {
            CompleteConnectionProgress();

            string body = message ?? string.Empty;
            bool menuWasAlreadyOpen = BasisMainMenu.Instance != null;

            if (!menuWasAlreadyOpen)
            {
                BasisMainMenu.Open();
            }
            else if (BasisMainMenu.Instance.Dialogue)
            {
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();
            }

            if (BasisMainMenu.Instance != null)
            {
                BasisMainMenu.Instance.OpenDialogue(
                    "Server Error",
                    body,
                    BasisLocalization.Get("ui.ok"),
                    _ => { });
            }
            else
            {
                BasisDebug.LogError(body);
            }
        }

        public static void CompleteConnectionProgress() =>
            BasisSceneLoad.progressCallback.ReportProgress(ConnectionProgressKey, 100f, string.Empty);

        /// <summary>
        /// Panel-independent connection routine. Reports progress through the loading bar,
        /// disconnects any existing session, sets the network manager fields, loads the
        /// asset bundle, and triggers the connection. Host-mode configuration (server name,
        /// MOTD, locks, …) is applied by the caller onto <see cref="BasisNetworkManagement"/>
        /// before invoking this; both the Servers panel Connect button and the
        /// <c>--connection=</c> CLI bootstrap call this directly.
        /// </summary>
        public static async Task ConnectAsync(ServerDirectoryEntry entry, string userName, bool isHostMode = false)
        {
            try
            {
                ReportConnectionProgress(5f, BasisLocalization.Get("menu.servers.status.initializing"));

                if (BasisNetworkConnection.LocalPlayerIsConnected)
                {
                    ReportConnectionProgress(15f, BasisLocalization.Get("menu.servers.status.disconnecting"));
                    BasisDebug.Log("Disconnecting from current connection", BasisDebug.LogTag.Networking);

                    using CancellationTokenSource cts = new CancellationTokenSource();
                    Task rebootWait = BasisNetworkConnection.WaitForRebootCompleteAsync(cts.Token);
                    await BasisNetworkLifeCycle.Destroy();
                    await rebootWait;
                    BasisNetworkLifeCycle.Initialize();
                }

                if (!BasisNetworkManagement.IsInitialized)
                {
                    ReportConnectionError(BasisLocalization.Get("menu.servers.error.noNetworkLayer"));
                    BasisDebug.LogError("Missing Networking layer!");
                    return;
                }

                ReportConnectionProgress(40f, BasisLocalization.Get("menu.servers.status.preparing"));
                BasisLocalPlayer.Instance.DisplayName = userName;
                BasisLocalPlayer.Instance.SetSafeDisplayname();
                BasisDataStore.SaveString(BasisLocalPlayer.Instance.DisplayName, UsernameFileName);
                BasisDataStore.SaveString(entry.Id, LastConnectedServerIdFile);

                string address = entry.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
                string portString = entry.Target?.Get(ConnectionTarget.Keys.Port) ?? string.Empty;
                ushort port;
                if (!ushort.TryParse(portString, out port)) port = LNLConnectionTargetParser.DefaultPort;
                string stackId = entry.Target?.StackId ?? BasisNetworkStackRegistry.DefaultId;

                BasisNetworkManagement.Port = port;
                BasisNetworkManagement.Ip = address;
                BasisNetworkManagement.Password = entry.HasPassword ? entry.Password : string.Empty;
                BasisNetworkManagement.IsHostMode = isHostMode;
                BasisNetworkManagement.NetworkStackId = stackId;

                ReportConnectionProgress(60f, BasisLocalization.Get("menu.servers.status.loadingBundle"));
                await LoadDefaultAssetBundleAsync();

                ReportConnectionProgress(90f, BasisLocalization.Get("menu.servers.status.connecting"));
                BasisNetworkManagement.Connect();
                if (BasisDesktopEye.Instance != null)
                {
                    BasisDesktopEye.Instance.LockEye();
                }
                CompleteConnectionProgress();
            }
            catch (TimeoutException tex)
            {
                ReportConnectionError(BasisLocalization.Get("menu.servers.error.timeout"));
                BasisDebug.LogError(tex.ToString());
            }
            catch (Exception ex)
            {
                ReportConnectionError(BasisLocalization.Get("menu.servers.error.connectFailed"));
                BasisDebug.LogError(ex.ToString());
            }
        }

        public static async Task LoadDefaultAssetBundleAsync()
        {
            if (BundledContentHolder.Instance.UseSceneProvidedHere)
            {
                BasisDebug.Log("using Local Asset Bundle or Addressable", BasisDebug.LogTag.Networking);
                if (BundledContentHolder.Instance.UseAddressablesToLoadScene)
                {
                    await BasisSceneLoad.LoadSceneAddressables(
                        BundledContentHolder.Instance.DefaultScene
                            .BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
                }
                else
                {
                    await BasisSceneLoad.LoadSceneAssetBundle(BundledContentHolder.Instance.DefaultScene);
                }
            }
        }

        // ── --connection=address:port#password CLI bootstrap ──────────────────
        // Format of the value (everything after `=`):
        //   address           → port defaults to 4296, no password
        //   address:port      → custom port, no password
        //   address#password  → default port, custom password
        //   address:port#password
        // Both `:` and `#` are required *literally*; the parser tolerates them in
        // either order only as written above. Hostnames and IPv4 work; IPv6 needs
        // `[::1]:4296` brackets (port-detection uses LastIndexOf(':')).

        private const string ConnectionArgPrefix = "--connection=";
        private const string CommandLineEntryId = "__cmdline__";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterCommandLineAutoConnect()
        {
            if (!TryGetCommandLineConnection(out ServerDirectoryEntry target)) return;

            void Trigger()
            {
                if (AutoConnectAttempted) return;
                AutoConnectAttempted = true;

                if (BasisNetworkConnection.LocalPlayerIsConnected) return;

                string userName = BasisDataStore.LoadString(UsernameFileName, string.Empty);
                if (string.IsNullOrEmpty(userName))
                {
                    BasisDebug.LogWarning(
                        $"--connection arg ignored: no saved username yet. Set one via the Servers panel and relaunch.");
                    return;
                }

                _ = ConnectAsync(target, userName);
            }

            if (BasisNetworkManagement.IsInitialized) Trigger();
            else BasisNetworkManagement.OnIstanceCreated += Trigger;
        }

        private static bool TryGetCommandLineConnection(out ServerDirectoryEntry entry)
        {
            entry = null;
            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return false; }
            if (args == null) return false;

            foreach (string arg in args)
            {
                if (string.IsNullOrEmpty(arg)) continue;
                if (!arg.StartsWith(ConnectionArgPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                string value = arg.Substring(ConnectionArgPrefix.Length);
                if (!LNLConnectionTargetParser.TryParseConnectionString(value, out string addr, out ushort port, out _, out string password))
                {
                    BasisDebug.LogWarning($"--connection arg could not be parsed: {arg}");
                    return false;
                }

                ConnectionTarget target = new ConnectionTarget(BasisNetworkStackRegistry.DefaultId, $"{addr}:{port}");
                target.Set(ConnectionTarget.Keys.Address, addr);
                target.Set(ConnectionTarget.Keys.Port, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                target.Set(ConnectionTarget.Keys.Password, password ?? string.Empty);

                entry = new ServerDirectoryEntry
                {
                    Id = CommandLineEntryId,
                    SourceId = SavedServersDirectorySource.Id,
                    DisplayName = string.Empty,
                    Target = target,
                    HasPassword = !string.IsNullOrEmpty(password),
                    Password = password ?? string.Empty,
                    CanEdit = false,
                    CanRemove = false,
                };
                return true;
            }
            return false;
        }
    }
}
