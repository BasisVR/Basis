using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using System;

[AutoStaticsCleanup]
public static partial class BasisHeadlessRuntimeStatus
{
    [NoAutoStaticsCleanup] private static readonly object sync = new object();

    public static bool IsHealthListenerRunning { get; private set; }
    public static bool IsConnected { get; private set; }
    public static bool IsConnecting { get; private set; }
    public static bool IsRetrying { get; private set; }
    public static bool RetryEnabled { get; private set; }
    public static int CurrentRetryAttempt { get; private set; }
    public static int MaxRetryAttempts { get; private set; }
    public static int RetryDelaySeconds { get; private set; }
    public static int TotalDisconnectCount { get; private set; }
    public static int TotalReconnectSuccessCount { get; private set; }
    public static string LastDisconnectReason { get; private set; }
    public static string LastDisconnectSocketError { get; private set; }
    public static string LastDisconnectMessage { get; private set; }
    public static string ConfiguredServerIp { get; private set; } = "localhost";
    public static int ConfiguredServerPort { get; private set; } = 4296;
    public static bool HealthCheckEnabled { get; private set; }
    public static string HealthCheckHost { get; private set; } = "0.0.0.0";
    public static int HealthCheckPort { get; private set; } = 10666;
    public static string HealthPath { get; private set; } = "/health";
    public static DateTimeOffset StartTimeUtc { get; private set; } = DateTimeOffset.UtcNow;
    public static DateTimeOffset? LastConnectAttemptUtc { get; private set; }
    public static DateTimeOffset? LastConnectedUtc { get; private set; }
    public static DateTimeOffset? LastDisconnectedUtc { get; private set; }
    public static DateTimeOffset LastHealthStateChangeUtc { get; private set; } = DateTimeOffset.UtcNow;
    public static BasisHeadlessConnectionState State { get; private set; } = BasisHeadlessConnectionState.Starting;

    // Memory counters, sampled on the main thread by BasisHeadlessMemoryProbe and read
    // from cache by the health listener thread.
    public static long MonoHeapBytes { get; private set; }
    public static long MonoUsedBytes { get; private set; }
    public static long TotalAllocatedBytes { get; private set; }
    public static long TotalReservedBytes { get; private set; }
    public static long TotalUnusedReservedBytes { get; private set; }
    public static long GcHeapBytes { get; private set; }
    public static long WorkingSetBytes { get; private set; }
    public static int RemotePlayerCount { get; private set; }
    public static DateTimeOffset? LastMemorySampleUtc { get; private set; }

    public static long TextureBytes { get; private set; }
    public static int TextureCount { get; private set; }
    public static long MeshBytes { get; private set; }
    public static int MeshCount { get; private set; }
    public static long AudioClipBytes { get; private set; }
    public static int AudioClipCount { get; private set; }
    public static DateTimeOffset? LastAssetSweepUtc { get; private set; }

    public static void Reset()
    {
        lock (sync)
        {
            IsHealthListenerRunning = false;
            IsConnected = false;
            IsConnecting = false;
            IsRetrying = false;
            RetryEnabled = false;
            CurrentRetryAttempt = 0;
            MaxRetryAttempts = 10;
            RetryDelaySeconds = 5;
            TotalDisconnectCount = 0;
            TotalReconnectSuccessCount = 0;
            LastDisconnectReason = null;
            LastDisconnectSocketError = null;
            LastDisconnectMessage = null;
            ConfiguredServerIp = "localhost";
            ConfiguredServerPort = 4296;
            HealthCheckEnabled = false;
            HealthCheckHost = "0.0.0.0";
            HealthCheckPort = 10666;
            HealthPath = "/health";
            StartTimeUtc = DateTimeOffset.UtcNow;
            LastConnectAttemptUtc = null;
            LastConnectedUtc = null;
            LastDisconnectedUtc = null;
            LastHealthStateChangeUtc = StartTimeUtc;
            State = BasisHeadlessConnectionState.Starting;
            MonoHeapBytes = 0;
            MonoUsedBytes = 0;
            TotalAllocatedBytes = 0;
            TotalReservedBytes = 0;
            TotalUnusedReservedBytes = 0;
            GcHeapBytes = 0;
            WorkingSetBytes = 0;
            RemotePlayerCount = 0;
            LastMemorySampleUtc = null;
            TextureBytes = 0;
            TextureCount = 0;
            MeshBytes = 0;
            MeshCount = 0;
            AudioClipBytes = 0;
            AudioClipCount = 0;
            LastAssetSweepUtc = null;
        }
    }

    /// <summary>
    /// Publishes a cheap counter sample. Called from the main thread only.
    /// </summary>
    public static void PublishMemoryCounters(
        long monoHeapBytes,
        long monoUsedBytes,
        long totalAllocatedBytes,
        long totalReservedBytes,
        long totalUnusedReservedBytes,
        long gcHeapBytes,
        long workingSetBytes,
        int remotePlayerCount)
    {
        lock (sync)
        {
            MonoHeapBytes = monoHeapBytes;
            MonoUsedBytes = monoUsedBytes;
            TotalAllocatedBytes = totalAllocatedBytes;
            TotalReservedBytes = totalReservedBytes;
            TotalUnusedReservedBytes = totalUnusedReservedBytes;
            GcHeapBytes = gcHeapBytes;
            WorkingSetBytes = workingSetBytes;
            RemotePlayerCount = remotePlayerCount;
            LastMemorySampleUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Publishes an asset-attribution sweep. Called from the main thread only, on a slow
    /// cadence because the sweep walks every loaded asset of each type.
    /// </summary>
    public static void PublishAssetMemory(
        long textureBytes,
        int textureCount,
        long meshBytes,
        int meshCount,
        long audioClipBytes,
        int audioClipCount)
    {
        lock (sync)
        {
            TextureBytes = textureBytes;
            TextureCount = textureCount;
            MeshBytes = meshBytes;
            MeshCount = meshCount;
            AudioClipBytes = audioClipBytes;
            AudioClipCount = audioClipCount;
            LastAssetSweepUtc = DateTimeOffset.UtcNow;
        }
    }

    public static void ApplyConfiguration(
        string configuredServerIp,
        int configuredServerPort,
        bool healthCheckEnabled,
        string healthCheckHost,
        int healthCheckPort,
        string healthPath,
        bool retryEnabled,
        int retryDelaySeconds,
        int maxRetryAttempts)
    {
        lock (sync)
        {
            ConfiguredServerIp = configuredServerIp;
            ConfiguredServerPort = configuredServerPort;
            HealthCheckEnabled = healthCheckEnabled;
            HealthCheckHost = healthCheckHost;
            HealthCheckPort = healthCheckPort;
            HealthPath = healthPath;
            RetryEnabled = retryEnabled;
            RetryDelaySeconds = retryDelaySeconds;
            MaxRetryAttempts = maxRetryAttempts;
        }
    }

    public static void SetHealthListenerRunning(bool isRunning)
    {
        lock (sync)
        {
            IsHealthListenerRunning = isRunning;
        }
    }

    public static void MarkConnecting()
    {
        lock (sync)
        {
            IsConnected = false;
            IsConnecting = true;
            IsRetrying = false;
            LastConnectAttemptUtc = DateTimeOffset.UtcNow;
            SetStateLocked(BasisHeadlessConnectionState.Connecting);
        }
    }

    public static void MarkConnected()
    {
        lock (sync)
        {
            if (CurrentRetryAttempt > 0)
            {
                TotalReconnectSuccessCount++;
            }

            IsConnected = true;
            IsConnecting = false;
            IsRetrying = false;
            CurrentRetryAttempt = 0;
            LastConnectedUtc = DateTimeOffset.UtcNow;
            SetStateLocked(BasisHeadlessConnectionState.Connected);
        }
    }

    public static void MarkReconnectScheduled(int attempt)
    {
        lock (sync)
        {
            IsConnected = false;
            IsConnecting = false;
            IsRetrying = true;
            CurrentRetryAttempt = attempt;
            SetStateLocked(BasisHeadlessConnectionState.RetryScheduled);
        }
    }

    public static void MarkDisconnected(DisconnectInfo disconnectInfo, string message)
    {
        lock (sync)
        {
            IsConnected = false;
            IsConnecting = false;
            IsRetrying = false;
            TotalDisconnectCount++;
            LastDisconnectedUtc = DateTimeOffset.UtcNow;
            LastDisconnectReason = disconnectInfo.Reason.ToString();
            LastDisconnectSocketError = disconnectInfo.SocketErrorCode.ToString();
            LastDisconnectMessage = message;
            SetStateLocked(BasisHeadlessConnectionState.Disconnected);
        }
    }

    public static void MarkRetriesExhausted()
    {
        lock (sync)
        {
            IsConnected = false;
            IsConnecting = false;
            IsRetrying = false;
            SetStateLocked(BasisHeadlessConnectionState.RetriesExhausted);
        }
    }

    public static void MarkStopping()
    {
        lock (sync)
        {
            IsConnected = false;
            IsConnecting = false;
            IsRetrying = false;
            SetStateLocked(BasisHeadlessConnectionState.Stopping);
        }
    }

    public static Snapshot CreateSnapshot()
    {
        lock (sync)
        {
            return new Snapshot
            {
                IsHealthListenerRunning = IsHealthListenerRunning,
                IsConnected = IsConnected,
                IsConnecting = IsConnecting,
                IsRetrying = IsRetrying,
                RetryEnabled = RetryEnabled,
                CurrentRetryAttempt = CurrentRetryAttempt,
                MaxRetryAttempts = MaxRetryAttempts,
                RetryDelaySeconds = RetryDelaySeconds,
                TotalDisconnectCount = TotalDisconnectCount,
                TotalReconnectSuccessCount = TotalReconnectSuccessCount,
                LastDisconnectReason = LastDisconnectReason,
                LastDisconnectSocketError = LastDisconnectSocketError,
                LastDisconnectMessage = LastDisconnectMessage,
                ConfiguredServerIp = ConfiguredServerIp,
                ConfiguredServerPort = ConfiguredServerPort,
                HealthCheckEnabled = HealthCheckEnabled,
                HealthCheckHost = HealthCheckHost,
                HealthCheckPort = HealthCheckPort,
                HealthPath = HealthPath,
                StartTimeUtc = StartTimeUtc,
                LastConnectAttemptUtc = LastConnectAttemptUtc,
                LastConnectedUtc = LastConnectedUtc,
                LastDisconnectedUtc = LastDisconnectedUtc,
                LastHealthStateChangeUtc = LastHealthStateChangeUtc,
                State = State,
                MonoHeapBytes = MonoHeapBytes,
                MonoUsedBytes = MonoUsedBytes,
                TotalAllocatedBytes = TotalAllocatedBytes,
                TotalReservedBytes = TotalReservedBytes,
                TotalUnusedReservedBytes = TotalUnusedReservedBytes,
                GcHeapBytes = GcHeapBytes,
                WorkingSetBytes = WorkingSetBytes,
                RemotePlayerCount = RemotePlayerCount,
                LastMemorySampleUtc = LastMemorySampleUtc,
                TextureBytes = TextureBytes,
                TextureCount = TextureCount,
                MeshBytes = MeshBytes,
                MeshCount = MeshCount,
                AudioClipBytes = AudioClipBytes,
                AudioClipCount = AudioClipCount,
                LastAssetSweepUtc = LastAssetSweepUtc
            };
        }
    }

    private static void SetStateLocked(BasisHeadlessConnectionState state)
    {
        State = state;
        LastHealthStateChangeUtc = DateTimeOffset.UtcNow;
    }

    public sealed class Snapshot
    {
        public bool IsHealthListenerRunning;
        public bool IsConnected;
        public bool IsConnecting;
        public bool IsRetrying;
        public bool RetryEnabled;
        public int CurrentRetryAttempt;
        public int MaxRetryAttempts;
        public int RetryDelaySeconds;
        public int TotalDisconnectCount;
        public int TotalReconnectSuccessCount;
        public string LastDisconnectReason;
        public string LastDisconnectSocketError;
        public string LastDisconnectMessage;
        public string ConfiguredServerIp;
        public int ConfiguredServerPort;
        public bool HealthCheckEnabled;
        public string HealthCheckHost;
        public int HealthCheckPort;
        public string HealthPath;
        public DateTimeOffset StartTimeUtc;
        public DateTimeOffset? LastConnectAttemptUtc;
        public DateTimeOffset? LastConnectedUtc;
        public DateTimeOffset? LastDisconnectedUtc;
        public DateTimeOffset LastHealthStateChangeUtc;
        public BasisHeadlessConnectionState State;
        public long MonoHeapBytes;
        public long MonoUsedBytes;
        public long TotalAllocatedBytes;
        public long TotalReservedBytes;
        public long TotalUnusedReservedBytes;
        public long GcHeapBytes;
        public long WorkingSetBytes;
        public int RemotePlayerCount;
        public DateTimeOffset? LastMemorySampleUtc;
        public long TextureBytes;
        public int TextureCount;
        public long MeshBytes;
        public int MeshCount;
        public long AudioClipBytes;
        public int AudioClipCount;
        public DateTimeOffset? LastAssetSweepUtc;
    }
}
