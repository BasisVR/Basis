using Basis.Network.Core;
using System;

public enum BasisHeadlessConnectionState
{
    Starting,
    Connecting,
    Connected,
    Disconnected,
    RetryScheduled,
    RetriesExhausted,
    Stopping
}

public static class BasisHeadlessRuntimeStatus
{
    private static readonly object sync = new object();

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
                State = State
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
    }
}
