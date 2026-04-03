using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.Avatar;
using Basis.Scripts.Common;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Headless;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Headless bootstrap/cleanup and auto-connect flow for server builds.
/// Handles scene stripping (textures, probes, UI), config load, and network connect.
/// </summary>
#if UNITY_SERVER
public class BasisHeadlessManagement : BasisBaseTypeManagement
{
    /// <summary>Injected/created headless eye input.</summary>
    public BasisHeadlessInput BasisHeadlessInput;

    /// <summary>Network password loaded from config or default.</summary>
    public static string Password = "default_password";

    /// <summary>Server IP loaded from config or default.</summary>
    public static string Ip = "localhost";

    /// <summary>Server port loaded from config or default.</summary>
    public static int Port = 4296;
    public static string AvatarFileLocation = string.Empty;
    public static string AvatarPassword = string.Empty;

    public static bool HealthCheckEnabled = false;
    public static string HealthCheckHost = "0.0.0.0";
    public static int HealthCheckPort = 10666;
    public static string HealthPath = "/health";
    public static bool ReconnectEnabled = true;
    public static int ReconnectDelaySeconds = 5;
    public static int MaxReconnectAttempts = 10;
    public static bool StrictMemoryCleanupEnabled = true;
    public static int StrictMemoryCleanupIntervalSeconds = 120;

    private BasisHeadlessHealthCheck healthCheck;
    private CancellationTokenSource reconnectCts;
    private bool isShuttingDown;
    private bool hasLoadedStartupContent;
    private bool reconnectScheduled;
    private bool configuredAvatarApplied;
    private bool strictMemoryCleanupInProgress;
    private float nextStrictMemoryCleanupTime;

    /// <summary>
    /// Scene change hook used in headless to aggressively strip visuals and free memory.
    /// </summary>
    private void OnSceneLoadeded(Scene arg0, Scene arg1)
    {
        RemoveAllMaterialTextures();
        RemoveAllReflectionProbes();
        RemoveAllText();
        TriggerStrictMemoryCleanup("scene load");
    }

    /// <summary>
    /// Iterates all renderers and clears common texture slots on their materials.
    /// </summary>
    private void RemoveAllMaterialTextures()
    {
        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        HashSet<Material> processedMats = new HashSet<Material>();

        foreach (Renderer renderer in renderers)
        {
            foreach (Material mat in renderer.materials)
            {
                if (mat == null || processedMats.Contains(mat))
                {
                    continue;
                }

                ShaderUtilSafe.ClearAllKnownTextures(mat);
                processedMats.Add(mat);
            }
        }

        Debug.Log("All textures cleared from all materials.");
    }

    /// <summary>
    /// Utility to clear commonly-used texture properties without Editor-only APIs.
    /// </summary>
    public static class ShaderUtilSafe
    {
        private static readonly string[] commonTextureProps =
        {
            "_MainTex", "_BaseMap", "_BumpMap", "_EmissionMap", "_MetallicGlossMap",
            "_ParallaxMap", "_OcclusionMap", "_DetailMask", "_DetailAlbedoMap", "_DetailNormalMap"
        };

        public static void ClearAllKnownTextures(Material material)
        {
            foreach (string prop in commonTextureProps)
            {
                if (material.HasProperty(prop))
                {
                    material.SetTexture(prop, null);
                }
            }
        }
    }

    /// <summary>
    /// Destroys all ReflectionProbe GameObjects in the scene.
    /// </summary>
    private void RemoveAllReflectionProbes()
    {
        ReflectionProbe[] probes = FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (ReflectionProbe probe in probes)
        {
            Destroy(probe.gameObject);
        }

        Debug.Log("All reflection probes removed from scene.");
    }

    /// <summary>
    /// Destroys all Canvas components (to remove headless UI).
    /// </summary>
    private void RemoveAllText()
    {
        Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (Canvas canvas in canvases)
        {
            Destroy(canvas);
        }

        Debug.Log("All reflection probes removed from scene.");
    }

    /// <summary>
    /// Loads config.xml from <see cref="Application.dataPath"/> or creates it with defaults.
    /// Updates headless runtime settings from env/config/defaults.
    /// </summary>
    public static void LoadOrCreateConfigXml()
    {
        string filePath = Path.Combine(Application.dataPath, "config.xml");
        string defaultPassword = Password;
        string defaultIp = Ip;
        int defaultPort = Port;
        string defaultAvatarFileLocation = AvatarFileLocation;
        string defaultAvatarPassword = AvatarPassword;
        bool defaultHealthCheckEnabled = HealthCheckEnabled;
        string defaultHealthCheckHost = HealthCheckHost;
        int defaultHealthCheckPort = HealthCheckPort;
        string defaultHealthPath = HealthPath;
        bool defaultReconnectEnabled = ReconnectEnabled;
        int defaultReconnectDelaySeconds = ReconnectDelaySeconds;
        int defaultMaxReconnectAttempts = MaxReconnectAttempts;
        bool defaultStrictMemoryCleanupEnabled = StrictMemoryCleanupEnabled;
        int defaultStrictMemoryCleanupIntervalSeconds = StrictMemoryCleanupIntervalSeconds;

        string envPassword = ReadEnvironmentString("Password");
        string envIp = ReadEnvironmentString("Ip");
        int? envPort = ReadEnvironmentInt("Port");
        string envAvatarFileLocation = ReadEnvironmentString("AvatarFileLocation");
        string envAvatarPassword = ReadEnvironmentString("AvatarPassword");
        bool? envHealthCheckEnabled = ReadEnvironmentBool("HealthCheckEnabled");
        string envHealthCheckHost = ReadEnvironmentString("HealthCheckHost");
        int? envHealthCheckPort = ReadEnvironmentInt("HealthCheckPort");
        string envHealthPath = ReadEnvironmentString("HealthPath");
        bool? envReconnectEnabled = ReadEnvironmentBool("ReconnectEnabled");
        int? envReconnectDelaySeconds = ReadEnvironmentInt("ReconnectDelaySeconds");
        int? envMaxReconnectAttempts = ReadEnvironmentInt("MaxReconnectAttempts");
        bool? envStrictMemoryCleanupEnabled = ReadEnvironmentBool("StrictMemoryCleanupEnabled");
        int? envStrictMemoryCleanupIntervalSeconds = ReadEnvironmentInt("StrictMemoryCleanupIntervalSeconds");

        XElement root = null;
        if (File.Exists(filePath))
        {
            XDocument doc = XDocument.Load(filePath);
            root = doc.Element("Configuration");
        }
        else
        {
            TryCreateDefaultConfigXml(
                filePath,
                defaultPassword,
                defaultIp,
                defaultPort,
                defaultAvatarFileLocation,
                defaultAvatarPassword,
                defaultHealthCheckEnabled,
                defaultHealthCheckHost,
                defaultHealthCheckPort,
                defaultHealthPath,
                defaultReconnectEnabled,
                defaultReconnectDelaySeconds,
                defaultMaxReconnectAttempts,
                defaultStrictMemoryCleanupEnabled,
                defaultStrictMemoryCleanupIntervalSeconds);
        }

        Password = envPassword ?? root?.Element("Password")?.Value ?? defaultPassword;
        Ip = envIp ?? root?.Element("Ip")?.Value ?? defaultIp;
        Port = envPort ?? ReadXmlInt(root?.Element("Port")?.Value, defaultPort);
        AvatarFileLocation = envAvatarFileLocation ?? root?.Element("AvatarFileLocation")?.Value ?? defaultAvatarFileLocation;
        AvatarPassword = envAvatarPassword ?? root?.Element("AvatarPassword")?.Value ?? defaultAvatarPassword;
        HealthCheckEnabled = envHealthCheckEnabled ?? ReadXmlBool(root?.Element("HealthCheckEnabled")?.Value, defaultHealthCheckEnabled);
        HealthCheckHost = envHealthCheckHost ?? root?.Element("HealthCheckHost")?.Value ?? defaultHealthCheckHost;
        HealthCheckPort = envHealthCheckPort ?? ReadXmlInt(root?.Element("HealthCheckPort")?.Value, defaultHealthCheckPort);
        HealthPath = BasisHeadlessHealthCheck.NormalizePath(envHealthPath ?? root?.Element("HealthPath")?.Value ?? defaultHealthPath);
        ReconnectEnabled = envReconnectEnabled ?? ReadXmlBool(root?.Element("ReconnectEnabled")?.Value, defaultReconnectEnabled);
        ReconnectDelaySeconds = Mathf.Max(1, envReconnectDelaySeconds ?? ReadXmlInt(root?.Element("ReconnectDelaySeconds")?.Value, defaultReconnectDelaySeconds));
        MaxReconnectAttempts = Mathf.Max(0, envMaxReconnectAttempts ?? ReadXmlInt(root?.Element("MaxReconnectAttempts")?.Value, defaultMaxReconnectAttempts));
        StrictMemoryCleanupEnabled = envStrictMemoryCleanupEnabled ?? ReadXmlBool(root?.Element("StrictMemoryCleanupEnabled")?.Value, defaultStrictMemoryCleanupEnabled);
        StrictMemoryCleanupIntervalSeconds = Mathf.Max(15, envStrictMemoryCleanupIntervalSeconds ?? ReadXmlInt(root?.Element("StrictMemoryCleanupIntervalSeconds")?.Value, defaultStrictMemoryCleanupIntervalSeconds));
        NormalizeConfiguredAvatarFields();
    }

    private static void TryCreateDefaultConfigXml(
        string filePath,
        string password,
        string ip,
        int port,
        string avatarFileLocation,
        string avatarPassword,
        bool healthCheckEnabled,
        string healthCheckHost,
        int healthCheckPort,
        string healthPath,
        bool reconnectEnabled,
        int reconnectDelaySeconds,
        int maxReconnectAttempts,
        bool strictMemoryCleanupEnabled,
        int strictMemoryCleanupIntervalSeconds)
    {
        try
        {
            XElement defaultConfig = new XElement("Configuration",
                new XElement("Password", password),
                new XElement("Ip", ip),
                new XElement("Port", port),
                new XElement("AvatarFileLocation", avatarFileLocation ?? string.Empty),
                new XElement("AvatarPassword", avatarPassword ?? string.Empty),
                new XElement("HealthCheckEnabled", healthCheckEnabled),
                new XElement("HealthCheckHost", healthCheckHost),
                new XElement("HealthCheckPort", healthCheckPort),
                new XElement("HealthPath", BasisHeadlessHealthCheck.NormalizePath(healthPath)),
                new XElement("ReconnectEnabled", reconnectEnabled),
                new XElement("ReconnectDelaySeconds", reconnectDelaySeconds),
                new XElement("MaxReconnectAttempts", maxReconnectAttempts),
                new XElement("StrictMemoryCleanupEnabled", strictMemoryCleanupEnabled),
                new XElement("StrictMemoryCleanupIntervalSeconds", strictMemoryCleanupIntervalSeconds)
            );
            new XDocument(defaultConfig).Save(filePath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Unable to create default headless config at '{filePath}'. Continuing with environment/default values. {ex.Message}");
        }
    }

    private static string ReadEnvironmentString(string envName)
    {
        string envValue = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(envValue))
        {
            return null;
        }

        return envValue;
    }

    private static bool? ReadEnvironmentBool(string envName)
    {
        string envValue = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(envValue))
        {
            return null;
        }

        if (bool.TryParse(envValue, out bool parsed))
        {
            return parsed;
        }

        Debug.LogWarning($"Invalid headless environment variable '{envName}' value '{envValue}'. Falling back to config.xml/defaults.");
        return null;
    }

    private static int? ReadEnvironmentInt(string envName)
    {
        string envValue = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(envValue))
        {
            return null;
        }

        if (int.TryParse(envValue, out int parsed))
        {
            return parsed;
        }

        Debug.LogWarning($"Invalid headless environment variable '{envName}' value '{envValue}'. Falling back to config.xml/defaults.");
        return null;
    }

    private static int ReadXmlInt(string value, int fallback)
    {
        return int.TryParse(value, out int parsed) ? parsed : fallback;
    }

    private static bool ReadXmlBool(string value, bool fallback)
    {
        return bool.TryParse(value, out bool parsed) ? parsed : fallback;
    }

    private static void NormalizeConfiguredAvatarFields()
    {
        AvatarFileLocation = AvatarFileLocation?.Trim() ?? string.Empty;
        AvatarPassword = AvatarPassword?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(AvatarFileLocation))
        {
            AvatarFileLocation = string.Empty;
            return;
        }

        int fragmentIndex = AvatarFileLocation.IndexOf('#');
        if (fragmentIndex < 0)
        {
            return;
        }

        string baseUrl = AvatarFileLocation[..fragmentIndex].Trim();
        string encodedPassword = AvatarFileLocation[(fragmentIndex + 1)..].Trim();
        AvatarFileLocation = baseUrl;

        if (!string.IsNullOrEmpty(AvatarPassword) || string.IsNullOrEmpty(encodedPassword))
        {
            return;
        }

        try
        {
            AvatarPassword = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPassword));
        }
        catch (FormatException)
        {
            Debug.LogWarning("AvatarFileLocation contains a '#' fragment, but the suffix is not valid base64. Ignoring inline avatar password.");
        }
    }

    /// <summary>
    /// Reads config, loads default scene once, and connects to network as client.
    /// </summary>
    public async void ConnectToNetwork()
    {
        LoadOrCreateConfigXml();
        ApplyRuntimeConfiguration();
        StartHealthEndpoint();

        if (!hasLoadedStartupContent)
        {
            hasLoadedStartupContent = true;
            await CreateAssetBundle();
        }

        AttemptConnect();
    }

    /// <summary>
    /// Loads the configured default scene via Addressables or AssetBundle when in headless.
    /// No-op if not configured for scene provided here.
    /// </summary>
    public async Task CreateAssetBundle()
    {
        BasisDebug.Log("Skipping visual scene asset initialization on dedicated server build.", BasisDebug.LogTag.Networking);
        await Task.CompletedTask;
        return;
        if (BundledContentHolder.Instance.UseSceneProvidedHere)
        {
            BasisDebug.Log("using Local Asset Bundle or Addressable", BasisDebug.LogTag.Networking);
            if (BundledContentHolder.Instance.UseAddressablesToLoadScene)
            {
                await BasisSceneLoad.LoadSceneAddressables(BundledContentHolder.Instance.DefaultScene.BasisRemoteBundleEncrypted.RemoteBeeFileLocation);
            }
            else
            {
                await BasisSceneLoad.LoadSceneAssetBundle(BundledContentHolder.Instance.DefaultScene);
            }
        }
    }

    /// <inheritdoc/>
    public override void StartSDK()
    {
        isShuttingDown = false;
        hasLoadedStartupContent = false;
        reconnectScheduled = false;
        configuredAvatarApplied = false;
        reconnectCts = new CancellationTokenSource();
        strictMemoryCleanupInProgress = false;
        nextStrictMemoryCleanupTime = Time.unscaledTime + StrictMemoryCleanupIntervalSeconds;
        BasisHeadlessRuntimeStatus.Reset();
        LoadOrCreateConfigXml();
        ApplyRuntimeConfiguration();
        BasisNetworkConnection.HeadlessReconnectSuppressed = false;
        BasisNetworkConnection.OnDisconnectedAfterReboot -= OnDisconnectedAfterReboot;
        BasisNetworkConnection.OnDisconnectedAfterReboot += OnDisconnectedAfterReboot;
        BasisNetworkConnection.OnConnectedAfterBootstrap -= OnConnectedAfterBootstrap;
        BasisNetworkConnection.OnConnectedAfterBootstrap += OnConnectedAfterBootstrap;

        if (BasisLocalPlayer.PlayerReady && BasisLocalPlayer.Instance != null)
        {
            EnsureHeadlessInput();
            _ = ApplyConfiguredAvatarAsync();
        }
        else
        {
            BasisLocalPlayer.OnLocalPlayerInitalized -= OnLocalPlayerReadyForHeadless;
            BasisLocalPlayer.OnLocalPlayerInitalized += OnLocalPlayerReadyForHeadless;
        }
        BasisDebug.Log(nameof(StartSDK), BasisDebug.LogTag.Device);

        BasisLocalPlayer.Instance.DisplayName = GenerateRandomPlayerName();
        BasisLocalPlayer.Instance.SetSafeDisplayname();

        if (BasisNetworkManagement.Instance != null)
        {
            ConnectToNetwork();
        }
        else
        {
            BasisNetworkManagement.OnEnableInstanceCreate += ConnectToNetwork;
        }

        SceneManager.activeSceneChanged += OnSceneLoadeded;
        BasisDebug.Log(nameof(StartSDK), BasisDebug.LogTag.Device);
    }

    private void OnDestroy()
    {
        isShuttingDown = true;
        BasisNetworkConnection.HeadlessReconnectSuppressed = true;
        BasisNetworkConnection.OnDisconnectedAfterReboot -= OnDisconnectedAfterReboot;
        BasisNetworkConnection.OnConnectedAfterBootstrap -= OnConnectedAfterBootstrap;
        BasisNetworkManagement.OnEnableInstanceCreate -= ConnectToNetwork;
        SceneManager.activeSceneChanged -= OnSceneLoadeded;
        CancelReconnectLoop();
        StopHealthEndpoint();
        BasisHeadlessRuntimeStatus.MarkStopping();
        BasisLocalPlayer.OnLocalPlayerInitalized -= OnLocalPlayerReadyForHeadless;
    }

    private void OnLocalPlayerReadyForHeadless()
    {
        BasisLocalPlayer.OnLocalPlayerInitalized -= OnLocalPlayerReadyForHeadless;
        EnsureHeadlessInput();
        _ = ApplyConfiguredAvatarAsync();
    }

    public override void Simulate()
    {
        if (!StrictMemoryCleanupEnabled || strictMemoryCleanupInProgress || isShuttingDown)
        {
            return;
        }

        if (Time.unscaledTime < nextStrictMemoryCleanupTime)
        {
            return;
        }

        TriggerStrictMemoryCleanup("periodic sweep");
    }

    private void OnConnectedAfterBootstrap()
    {
        TriggerStrictMemoryCleanup("connect or reconnect");
    }

    private void EnsureHeadlessInput()
    {
        if (BasisHeadlessInput != null)
        {
            BasisHeadlessInput.StopMovement();
            return;
        }

        if (BasisLocalPlayer.Instance == null)
        {
            BasisDebug.LogWarning("Headless input creation delayed: LocalPlayer instance is null.", BasisDebug.LogTag.Device);
            return;
        }

        GameObject gameObject = new GameObject("Headless Eye");
        gameObject.transform.parent = BasisLocalPlayer.Instance.transform;

        BasisHeadlessInput = gameObject.AddComponent<BasisHeadlessInput>();
        BasisHeadlessInput.Initialize("Desktop Eye", nameof(Basis.Scripts.Device_Management.Devices.Headless.BasisHeadlessInput));
        BasisHeadlessInput.StopMovement();
        BasisDeviceManagement.Instance.TryAdd(BasisHeadlessInput);
    }

    private void ApplyRuntimeConfiguration()
    {
        BasisHeadlessRuntimeStatus.ApplyConfiguration(
            Ip,
            Port,
            HealthCheckEnabled,
            HealthCheckHost,
            HealthCheckPort,
            HealthPath,
            ReconnectEnabled,
            ReconnectDelaySeconds,
            MaxReconnectAttempts);
    }

    private async Task ApplyConfiguredAvatarAsync()
    {
        if (configuredAvatarApplied || BasisLocalPlayer.Instance == null)
        {
            return;
        }

        if (!TryResolveHeadlessAvatarSelection(out string avatarLocation, out byte avatarLoadMode, out string avatarPassword, out string avatarSource))
        {
            configuredAvatarApplied = true;
            return;
        }

        configuredAvatarApplied = true;

        BasisLoadableBundle bundle = new BasisLoadableBundle
        {
            UnlockPassword = avatarPassword ?? string.Empty,
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
            {
                RemoteBeeFileLocation = avatarLocation
            },
            BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
        };

        try
        {
            BasisDebug.Log($"Loading headless avatar from {avatarSource}: {avatarLocation} (mode {avatarLoadMode})", BasisDebug.LogTag.Avatar);
            await BasisLocalPlayer.Instance.CreateAvatar(avatarLoadMode, bundle);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Failed to load headless avatar from {avatarSource} '{avatarLocation}': {ex.Message}", BasisDebug.LogTag.Avatar);
        }
    }

    private static bool TryResolveHeadlessAvatarSelection(out string avatarLocation, out byte avatarLoadMode, out string avatarPassword, out string avatarSource)
    {
        if (!string.IsNullOrWhiteSpace(AvatarFileLocation))
        {
            avatarLocation = AvatarFileLocation;
            avatarLoadMode = ResolveAvatarLoadMode(avatarLocation, BasisPlayer.LoadModeLocal);
            avatarPassword = AvatarPassword ?? string.Empty;
            avatarSource = "headless override";
            return true;
        }

        if (BasisDataStore.LoadAvatar(
                BasisLocalPlayer.LoadFileNameAndExtension,
                BasisBeeConstants.DefaultAvatar,
                BasisPlayer.LoadModeLocal,
                out BasisDataStore.BasisSavedAvatar savedAvatar) &&
            !string.IsNullOrWhiteSpace(savedAvatar?.UniqueID))
        {
            avatarLocation = savedAvatar.UniqueID;
            avatarLoadMode = ResolveAvatarLoadMode(avatarLocation, savedAvatar.loadmode);
            avatarPassword = string.Empty;
            avatarSource = BasisLocalPlayer.LoadFileNameAndExtension;
            return true;
        }

        avatarLocation = string.Empty;
        avatarLoadMode = BasisPlayer.LoadModeLocal;
        avatarPassword = string.Empty;
        avatarSource = string.Empty;
        return false;
    }

    private static byte ResolveAvatarLoadMode(string avatarLocation, byte fallbackMode)
    {
        if (IsRemoteUrl(avatarLocation))
        {
            return BasisPlayer.LoadModeNetworkDownloadable;
        }

        return fallbackMode;
    }

    private static bool IsRemoteUrl(string avatarLocation)
    {
        if (!Uri.TryCreate(avatarLocation, UriKind.Absolute, out Uri uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private void StartHealthEndpoint()
    {
        if (!HealthCheckEnabled)
        {
            StopHealthEndpoint();
            BasisHeadlessRuntimeStatus.SetHealthListenerRunning(false);
            return;
        }

        if (healthCheck != null)
        {
            return;
        }

        try
        {
            healthCheck = new BasisHeadlessHealthCheck(HealthCheckHost, HealthCheckPort, HealthPath);
            BasisDebug.Log($"Headless health check started at http://{HealthCheckHost}:{HealthCheckPort}{HealthPath}", BasisDebug.LogTag.Networking);
        }
        catch (Exception ex)
        {
            BasisHeadlessRuntimeStatus.SetHealthListenerRunning(false);
            BasisDebug.LogError($"Failed to start headless health check endpoint: {ex.Message}", BasisDebug.LogTag.Networking);
        }
    }

    private void StopHealthEndpoint()
    {
        healthCheck?.Dispose();
        healthCheck = null;
    }

    private void AttemptConnect()
    {
        if (isShuttingDown || BasisNetworkManagement.Instance == null)
        {
            return;
        }

        reconnectScheduled = false;
        BasisHeadlessRuntimeStatus.MarkConnecting();
        BasisNetworkManagement.Instance.Ip = Ip;
        BasisNetworkManagement.Instance.Password = Password;
        BasisNetworkManagement.Instance.IsHostMode = false;
        BasisNetworkManagement.Instance.Port = (ushort)Port;
        BasisNetworkManagement.Instance.Connect();
        BasisDebug.Log("connecting to default");
        BasisMainMenu.Close();
    }

    private void TriggerStrictMemoryCleanup(string reason)
    {
        if (!StrictMemoryCleanupEnabled || strictMemoryCleanupInProgress || isShuttingDown)
        {
            return;
        }

        strictMemoryCleanupInProgress = true;
        nextStrictMemoryCleanupTime = Time.unscaledTime + StrictMemoryCleanupIntervalSeconds;
        _ = RunStrictMemoryCleanupAsync(reason);
    }

    private async Task RunStrictMemoryCleanupAsync(string reason)
    {
        try
        {
            await Task.Yield();

            AsyncOperation unloadOperation = Resources.UnloadUnusedAssets();
            while (!unloadOperation.isDone)
            {
                await Task.Yield();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            BasisDebug.Log($"Headless strict memory cleanup completed after {reason}.", BasisDebug.LogTag.Device);
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"Headless strict memory cleanup failed during {reason}: {ex.Message}", BasisDebug.LogTag.Device);
        }
        finally
        {
            strictMemoryCleanupInProgress = false;
            nextStrictMemoryCleanupTime = Time.unscaledTime + StrictMemoryCleanupIntervalSeconds;
        }
    }

    private void OnDisconnectedAfterReboot(DisconnectInfo disconnectInfo)
    {
        string message = TryReadDisconnectMessage(disconnectInfo);
        BasisHeadlessRuntimeStatus.MarkDisconnected(disconnectInfo, message);

        if (!ShouldRetry(disconnectInfo))
        {
            return;
        }

        _ = ScheduleReconnectAsync(disconnectInfo);
    }

    private async Task ScheduleReconnectAsync(DisconnectInfo disconnectInfo)
    {
        if (reconnectCts == null || isShuttingDown)
        {
            return;
        }

        if (reconnectScheduled)
        {
            return;
        }

        reconnectScheduled = true;
        CancellationToken token = reconnectCts.Token;
        int nextAttempt = BasisHeadlessRuntimeStatus.CreateSnapshot().CurrentRetryAttempt + 1;
        if (nextAttempt > MaxReconnectAttempts)
        {
            reconnectScheduled = false;
            BasisHeadlessRuntimeStatus.MarkRetriesExhausted();
            BasisDebug.LogWarning("Headless reconnect attempts exhausted.", BasisDebug.LogTag.Networking);
            return;
        }

        BasisHeadlessRuntimeStatus.MarkReconnectScheduled(nextAttempt);
        BasisDebug.LogWarning($"Headless reconnect attempt {nextAttempt}/{MaxReconnectAttempts} scheduled in {ReconnectDelaySeconds}s after {disconnectInfo.Reason}.", BasisDebug.LogTag.Networking);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), token);
        }
        catch (TaskCanceledException)
        {
            reconnectScheduled = false;
            return;
        }

        if (token.IsCancellationRequested || isShuttingDown)
        {
            reconnectScheduled = false;
            return;
        }

        BasisDeviceManagement.EnqueueOnMainThread(() =>
        {
            if (!isShuttingDown)
            {
                AttemptConnect();
            }
        });
    }

    private void CancelReconnectLoop()
    {
        if (reconnectCts == null)
        {
            return;
        }

        reconnectCts.Cancel();
        reconnectCts.Dispose();
        reconnectCts = null;
        reconnectScheduled = false;
    }

    private bool ShouldRetry(DisconnectInfo disconnectInfo)
    {
        if (isShuttingDown || BasisNetworkConnection.HeadlessReconnectSuppressed)
        {
            return false;
        }

        if (!ReconnectEnabled || MaxReconnectAttempts <= 0)
        {
            return false;
        }

        switch (disconnectInfo.Reason)
        {
            case DisconnectReason.ConnectionFailed:
            case DisconnectReason.Timeout:
            case DisconnectReason.HostUnreachable:
            case DisconnectReason.NetworkUnreachable:
            case DisconnectReason.UnknownHost:
            case DisconnectReason.Reconnect:
            case DisconnectReason.PeerNotFound:
                return true;
            case DisconnectReason.RemoteConnectionClose:
                return !IsHardStopRemoteClose(TryReadDisconnectMessage(disconnectInfo));
            case DisconnectReason.DisconnectPeerCalled:
            case DisconnectReason.ConnectionRejected:
            case DisconnectReason.InvalidProtocol:
            case DisconnectReason.PeerToPeerConnection:
                return false;
            default:
                return false;
        }
    }

    private static bool IsHardStopRemoteClose(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("protocol", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string TryReadDisconnectMessage(DisconnectInfo disconnectInfo)
    {
        try
        {
            if (disconnectInfo.AdditionalData != null &&
                disconnectInfo.AdditionalData.TryGetString(out string message))
            {
                return message;
            }
        }
        catch
        {
        }

        return null;
    }

    /// <inheritdoc/>
    public override void StopSDK()
    {
        isShuttingDown = true;
        BasisNetworkConnection.HeadlessReconnectSuppressed = true;
        BasisNetworkConnection.OnConnectedAfterBootstrap -= OnConnectedAfterBootstrap;
        CancelReconnectLoop();
        StopHealthEndpoint();
        BasisHeadlessRuntimeStatus.MarkStopping();
        BasisDebug.Log(nameof(StopSDK), BasisDebug.LogTag.Device);
    }

    /// <inheritdoc/>
    public override bool IsDeviceBootable(string BootRequest)
    {
        if (BootRequest == "Headless")
        {
            return true;
        }
        return false;
    }

    public static string[] adjectives = { "Swift", "Brave", "Clever", "Fierce", "Nimble", "Silent", "Bold", "Lucky", "Strong", "Mighty", "Sneaky", "Fearless", "Wise", "Vicious", "Daring" };
    public static string[] nouns = { "Warrior", "Hunter", "Mage", "Rogue", "Paladin", "Shaman", "Knight", "Archer", "Monk", "Druid", "Assassin", "Sorcerer", "Ranger", "Guardian", "Berserker" };
    public static string[] titles = { "the Swift", "the Bold", "the Silent", "the Brave", "the Fierce", "the Wise", "the Protector", "the Shadow", "the Flame", "the Phantom" };
    public static string[] animals = { "Wolf", "Tiger", "Eagle", "Dragon", "Lion", "Bear", "Hawk", "Panther", "Raven", "Serpent", "Fox", "Falcon" };

    public static (string Name, string Hex)[] colors =
    {
        ("Red", "#FF0000"),
        ("Blue", "#0000FF"),
        ("Green", "#008000"),
        ("Yellow", "#FFFF00"),
        ("Black", "#000000"),
        ("White", "#FFFFFF"),
        ("Silver", "#C0C0C0"),
        ("Golden", "#FFD700"),
        ("Crimson", "#DC143C"),
        ("Azure", "#007FFF"),
        ("Emerald", "#50C878"),
        ("Amber", "#FFBF00")
    };

    /// <summary>
    /// Generates a flavored rich-text display name (not guaranteed globally unique).
    /// </summary>
    public static string GenerateRandomPlayerName()
    {
        System.Random random = new System.Random();

        string adjective = adjectives[random.Next(adjectives.Length)];
        string noun = nouns[random.Next(nouns.Length)];
        string title = titles[random.Next(titles.Length)];
        (string Name, string Hex) color = colors[random.Next(colors.Length)];
        string animal = animals[random.Next(animals.Length)];

        string colorText = $"<color={color.Hex}>{color.Name}</color>";
        string generatedName = $"{adjective}{noun} {title} of the {colorText} {animal}";

        return $"{generatedName}";
    }
}
#endif
