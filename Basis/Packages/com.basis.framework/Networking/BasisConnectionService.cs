using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.Drivers;
using Basis.Network.Core;
using System;
using System.Net;
using System.Net.Sockets;
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
        private static int _connectInProgress;
        private static ServerDirectoryEntry _pendingLanPasswordEntry;

        /// <summary>
        /// Raised when a password-protected LAN connection receives an authentication rejection.
        /// UI packages can use this to request a replacement without coupling the framework to a
        /// particular menu implementation.
        /// </summary>
        public static event Action<ServerDirectoryEntry> LanPasswordRequired;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetLanPasswordState()
        {
            Interlocked.Exchange(ref _pendingLanPasswordEntry, null);
            LanPasswordRequired = null;
            Volatile.Write(ref _connectInProgress, 0);
        }

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

        private static void SetPendingLanPasswordAttempt(ServerDirectoryEntry entry) =>
            Interlocked.Exchange(ref _pendingLanPasswordEntry, entry);

        internal static void ClearPendingLanPasswordAttempt() =>
            Interlocked.Exchange(ref _pendingLanPasswordEntry, null);

        internal static bool TryHandleLanAuthenticationRejected()
        {
            ServerDirectoryEntry entry = Interlocked.Exchange(ref _pendingLanPasswordEntry, null);

            if (entry == null)
            {
                return false;
            }

            entry.Password = string.Empty;
            entry.Target?.Set(ConnectionTarget.Keys.Password, string.Empty);

            Action<ServerDirectoryEntry> handler = LanPasswordRequired;
            if (handler == null)
            {
                return false;
            }

            try
            {
                handler(entry);
                return true;
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"LanPasswordRequired handler threw: {ex.Message}");
                return false;
            }
        }

        internal static void NotifyConnectionSucceeded()
        {
            ClearPendingLanPasswordAttempt();
        }

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
            if (Interlocked.CompareExchange(ref _connectInProgress, 1, 0) != 0)
            {
                BasisDebug.LogWarning("Connect requested while a connection attempt is already in progress; ignoring.");
                return;
            }
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
                BasisLocalPlayer.Instance.DisplayName = userName.Trim();
                BasisLocalPlayer.Instance.SetSafeDisplayname();
                BasisDataStore.SaveString(BasisLocalPlayer.Instance.DisplayName, UsernameFileName);
                bool isLan = string.Equals(
                    entry.SourceId,
                    LanServersDirectorySource.Id,
                    StringComparison.OrdinalIgnoreCase);
                if (!isLan)
                {
                    // LAN advertisements are session-scoped and their passwords are never persisted.
                    // Keep the previous durable auto-connect target instead of saving an expiring LAN id.
                    BasisDataStore.SaveString(entry.Id, LastConnectedServerIdFile);
                }

                if (isLan && entry.HasPassword && string.IsNullOrEmpty(entry.Password))
                {
                    entry.Password = SavedServersDirectorySource.DefaultServerPassword;
                    entry.Target?.Set(ConnectionTarget.Keys.Password, entry.Password);
                }
                SetPendingLanPasswordAttempt(isLan && entry.HasPassword ? entry : null);

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

                // Probe the server while the bundle loads to discover which address family
                // is actually reachable. Skipped in host-mode: the local server may not be
                // listening yet. Non-fatal: if the probe fails we fall back to the hostname.
                Task<string> resolveTask = isHostMode
                    ? Task.FromResult(address)
                    : ResolveConnectionAddressAsync(entry.Target, address);
                await LoadDefaultAssetBundleAsync();
                string resolvedIp = await resolveTask;
                if (!string.IsNullOrEmpty(resolvedIp) && resolvedIp != address)
                {
                    BasisDebug.Log($"Resolved {address} → {resolvedIp}", BasisDebug.LogTag.Networking);
                    BasisNetworkManagement.Ip = resolvedIp;
                }

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
                ClearPendingLanPasswordAttempt();
                ReportConnectionError(BasisLocalization.Get("menu.servers.error.timeout"));
                BasisDebug.LogError(tex.ToString());
            }
            catch (Exception ex)
            {
                ClearPendingLanPasswordAttempt();
                ReportConnectionError(BasisLocalization.Get("menu.servers.error.connectFailed"));
                BasisDebug.LogError(ex.ToString());
            }
            finally
            {
                Volatile.Write(ref _connectInProgress, 0);
            }
        }

        /// <summary>
        /// Probes the server and returns the <see cref="IPAddress"/> string that actually
        /// responded, or <paramref name="fallback"/> if the probe cannot reach either family.
        /// A 2.5-second budget is split: first half for IPv6, second half for IPv4.
        /// </summary>
        private static async Task<string> ResolveConnectionAddressAsync(ConnectionTarget target, string fallback)
        {
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(2500);
                ServerProbeResult probe = await BasisNetworkStackRegistry.ProbeAsync(
                    target, 2500, cts.Token).ConfigureAwait(false);
                if (probe?.ResolvedAddress != null)
                    return probe.ResolvedAddress.ToString();
            }
            catch { }
            return fallback;
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
            if (!TryGetBootstrapConnection(out ServerDirectoryEntry target)) return;

            void Trigger()
            {
                if (AutoConnectAttempted) return;
                AutoConnectAttempted = true;

                if (BasisNetworkConnection.LocalPlayerIsConnected) return;

                string userName = BasisDataStore.LoadString(UsernameFileName, string.Empty);
                if (string.IsNullOrWhiteSpace(userName))
                {
                    ReportConnectionError("Set a username before joining a server.");
                    return;
                }

                _ = ConnectAsync(target, userName);
            }

            if (BasisNetworkManagement.IsInitialized) Trigger();
            else BasisNetworkManagement.OnIstanceCreated += Trigger;
        }

        private static bool TryGetBootstrapConnection(out ServerDirectoryEntry entry)
        {
            if (TryGetCommandLineConnection(out entry)) return true;
            if (BasisDeepLinkProvider.TryConsumeStartupLink(out entry)) return true;
#if UNITY_EDITOR
            if (BasisAppRelaunch.TryConsumeEditorConnection(out string editorConnection)
                && BuildEntryFromConnectionString(editorConnection, out entry))
                return true;
#endif
            entry = null;
            return false;
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
                if (!BuildEntryFromConnectionString(value, out entry))
                {
                    BasisDebug.LogWarning($"--connection arg could not be parsed: {arg}");
                    return false;
                }
                return true;
            }
            return false;
        }

        private static bool BuildEntryFromConnectionString(string value, out ServerDirectoryEntry entry)
        {
            entry = null;
            if (!LNLConnectionTargetParser.TryParseConnectionString(value, out string addr, out ushort port, out _, out string password))
                return false;

            bool isIPv6 = IPAddress.TryParse(addr, out IPAddress parsedAddr)
                && parsedAddr.AddressFamily == AddressFamily.InterNetworkV6;
            string raw = isIPv6 ? $"[{addr}]:{port}" : $"{addr}:{port}";
            ConnectionTarget target = new ConnectionTarget(BasisNetworkStackRegistry.DefaultId, raw);
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
    }
}
