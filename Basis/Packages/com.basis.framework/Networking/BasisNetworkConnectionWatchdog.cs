using Basis.BasisUI;
using Basis.Network.Core;
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    public enum BasisConnectionHealth
    {
        Idle,
        Connecting,
        Connected,
        Stalled,
        Reconnecting,
    }

    /// <summary>
    /// Watches the live server link so the player is never left staring at a frozen world while
    /// the transport silently counts down to its disconnect timeout. Owns the loading bar for the
    /// connect handshake, for a stalled link, and for the reconnect countdown, and re-issues the
    /// connection automatically when the failure came from the server side rather than the user.
    /// Ticked once per frame from the event driver; every entry point is main-thread only.
    /// </summary>
    public static class BasisNetworkConnectionWatchdog
    {
        /// <summary>
        /// Headless builds run their own reconnect loop in BasisHeadlessManagement and have no
        /// loading bar, so the watchdog compiles to a no-op there.
        /// </summary>
        public static readonly bool Supported =
#if UNITY_SERVER
            false;
#else
            true;
#endif

        public static bool AutoReconnectEnabled = true;

        /// <summary>Silence (ms) tolerated before the link is called stalled. Must clear the transport ping interval.</summary>
        public const float StallGraceMs = 3000f;
        /// <summary>Silence (ms) at or below which a stalled link counts as recovered.</summary>
        public const float RecoveredMs = 1000f;
        /// <summary>Seconds allowed for transport connect plus authentication before the attempt is abandoned.</summary>
        public const float HandshakeTimeoutSeconds = 25f;
        /// <summary>Seconds a link must hold before the reconnect attempt counter is forgiven.</summary>
        public const float StableConnectionSeconds = 30f;
        public const int MaxReconnectAttempts = 5;

        private const float DefaultDisconnectTimeoutMs = 30000f;
        private static readonly float[] RetryDelaysSeconds = { 3f, 5f, 10f, 20f, 30f };

        public static BasisConnectionHealth Health { get; private set; } = BasisConnectionHealth.Idle;
        public static event Action<BasisConnectionHealth> OnHealthChanged;

        /// <summary>Reconnect attempts spent on the current outage; 0 while the link is healthy.</summary>
        public static int ReconnectAttempt => _attempt;

        /// <summary>
        /// True while the watchdog owns the outage. BasisNetworkEvents checks this so the generic
        /// "Server Disconnected" dialogue does not fight the reconnect progress bar.
        /// </summary>
        public static bool SuppressDisconnectDialogue => _suppressDialogue;

        private static float _handshakeElapsed;
        private static float _connectedSeconds;
        private static float _retryCountdown;
        private static float _retryTotal;
        private static float _disconnectTimeoutMs = DefaultDisconnectTimeoutMs;
        private static int _attempt;
        private static bool _suppressDialogue;
        private static bool _retryPending;
        private static bool _retryExhausted;
        private static bool _retryDriving;

        public static void Tick(float unscaledDeltaTime)
        {
            if (!Supported)
            {
                return;
            }

            switch (Health)
            {
                case BasisConnectionHealth.Idle:
                    if (BasisNetworkConnection.LocalPlayerIsConnected)
                    {
                        EnterConnected();
                    }
                    break;
                case BasisConnectionHealth.Connecting:
                    TickConnecting(unscaledDeltaTime);
                    break;
                case BasisConnectionHealth.Connected:
                case BasisConnectionHealth.Stalled:
                    TickLink(unscaledDeltaTime);
                    break;
                case BasisConnectionHealth.Reconnecting:
                    TickReconnecting(unscaledDeltaTime);
                    break;
            }
        }

        /// <summary>
        /// Called by <see cref="BasisConnectionService.ConnectAsync"/> once a user-driven attempt is
        /// accepted. Clears any outage the watchdog was nursing; ignored while it is driving its own retry.
        /// </summary>
        public static void NotifyConnectStarting()
        {
            if (!Supported || _retryDriving)
            {
                return;
            }
            _attempt = 0;
            _retryPending = false;
            _retryExhausted = false;
            _suppressDialogue = false;
            _retryCountdown = 0f;
            _retryTotal = 0f;
            SetHealth(BasisConnectionHealth.Idle);
        }

        /// <summary>Called once the client task has been handed the connect request.</summary>
        public static void NotifyHandshakeStarted()
        {
            if (!Supported)
            {
                return;
            }
            _handshakeElapsed = 0f;
            SetHealth(BasisConnectionHealth.Connecting);
        }

        /// <summary>Called when a connect attempt fails before it ever reaches the transport.</summary>
        public static void NotifyConnectAborted()
        {
            if (!Supported)
            {
                return;
            }
            _handshakeElapsed = 0f;
            if (Health == BasisConnectionHealth.Connecting)
            {
                SetHealth(BasisConnectionHealth.Idle);
            }
        }

        /// <summary>
        /// Records a disconnect before the network layer reboots. Returns true when the watchdog will
        /// re-issue the connection, in which case the disconnect dialogue is suppressed.
        /// </summary>
        public static bool NotifyDisconnected(DisconnectInfo disconnectInfo)
        {
            if (!Supported)
            {
                return false;
            }
            if (_retryPending || _retryExhausted || Health == BasisConnectionHealth.Reconnecting)
            {
                return _retryPending;
            }

            bool serverSide = IsServerSideFailure(disconnectInfo.Reason)
                && AutoReconnectEnabled
                && BasisConnectionService.HasReconnectTarget;

            _retryExhausted = serverSide && _attempt >= MaxReconnectAttempts;
            _retryPending = serverSide && !_retryExhausted;
            _suppressDialogue = _retryPending || _retryExhausted;

            if (_suppressDialogue)
            {
                BasisDebug.LogWarning($"Server link lost [{disconnectInfo.Reason}]; connection watchdog taking over.");
            }
            if (_retryPending)
            {
                _retryCountdown = 0f;
                _retryTotal = 0f;
                SetHealth(BasisConnectionHealth.Reconnecting);
            }
            return _retryPending;
        }

        /// <summary>Called after the network layer has finished tearing the old session down.</summary>
        public static void NotifyRebootComplete()
        {
            if (!Supported)
            {
                return;
            }
            _suppressDialogue = false;

            if (_retryExhausted)
            {
                GiveUp();
                return;
            }
            if (!_retryPending)
            {
                if (Health != BasisConnectionHealth.Reconnecting)
                {
                    SetHealth(BasisConnectionHealth.Idle);
                }
                return;
            }

            _retryPending = false;
            ArmRetry();
        }

        /// <summary>Drops all outage state. Called when the network layer is fully shut down.</summary>
        public static void Reset()
        {
            if (!Supported)
            {
                return;
            }
            if (Health != BasisConnectionHealth.Idle && Health != BasisConnectionHealth.Connected)
            {
                BasisConnectionService.CompleteConnectionProgress();
            }
            _handshakeElapsed = 0f;
            _connectedSeconds = 0f;
            _retryCountdown = 0f;
            _retryTotal = 0f;
            _attempt = 0;
            _suppressDialogue = false;
            _retryPending = false;
            _retryExhausted = false;
            SetHealth(BasisConnectionHealth.Idle);
        }

        private static void TickConnecting(float deltaTime)
        {
            if (BasisNetworkConnection.LocalPlayerIsConnected)
            {
                BasisConnectionService.CompleteConnectionProgress();
                EnterConnected();
                return;
            }

            _handshakeElapsed += deltaTime;
            if (_handshakeElapsed >= HandshakeTimeoutSeconds)
            {
                FailHandshake();
                return;
            }

            int remaining = Mathf.Max(1, Mathf.CeilToInt(HandshakeTimeoutSeconds - _handshakeElapsed));
            BasisConnectionService.ReportConnectionProgress(
                Mathf.Lerp(88f, 99f, _handshakeElapsed / HandshakeTimeoutSeconds),
                BasisLocalization.Get("menu.servers.status.awaitingResponse", remaining));
        }

        private static void TickLink(float deltaTime)
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected)
            {
                if (Health == BasisConnectionHealth.Stalled)
                {
                    BasisConnectionService.CompleteConnectionProgress();
                }
                SetHealth(BasisConnectionHealth.Idle);
                return;
            }

            float silentMs = BasisNetworkManagement.TimeSinceLastPacket();

            if (Health == BasisConnectionHealth.Connected)
            {
                _connectedSeconds += deltaTime;
                if (_attempt != 0 && _connectedSeconds >= StableConnectionSeconds)
                {
                    _attempt = 0;
                }
                if (silentMs >= StallGraceMs)
                {
                    BasisDebug.LogWarning($"No traffic from the server for {silentMs / 1000f:F1}s; waiting on the transport timeout.");
                    SetHealth(BasisConnectionHealth.Stalled);
                }
                return;
            }

            if (silentMs <= RecoveredMs)
            {
                BasisConnectionService.CompleteConnectionProgress();
                BasisDebug.Log("Server link recovered before the transport timed out.", BasisDebug.LogTag.Networking);
                SetHealth(BasisConnectionHealth.Connected);
                return;
            }

            float timeoutMs = Mathf.Max(_disconnectTimeoutMs, StallGraceMs + 1000f);
            int remaining = Mathf.Max(0, Mathf.CeilToInt((timeoutMs - silentMs) / 1000f));
            BasisConnectionService.ReportConnectionProgress(
                Mathf.Clamp(silentMs / timeoutMs * 100f, 1f, 99f),
                BasisLocalization.Get("menu.servers.status.connectionLost", remaining));
        }

        private static void TickReconnecting(float deltaTime)
        {
            if (BasisNetworkConnection.LocalPlayerIsConnected)
            {
                BasisConnectionService.CompleteConnectionProgress();
                EnterConnected();
                return;
            }

            if (_retryPending || _retryDriving)
            {
                BasisConnectionService.ReportConnectionProgress(70f,
                    BasisLocalization.Get("menu.servers.status.reconnectAttempt",
                        Mathf.Max(1, _attempt), MaxReconnectAttempts));
                return;
            }

            _retryCountdown -= deltaTime;
            if (_retryCountdown > 0f)
            {
                float span = Mathf.Max(_retryTotal, 0.001f);
                BasisConnectionService.ReportConnectionProgress(
                    Mathf.Clamp((1f - (_retryCountdown / span)) * 100f, 5f, 95f),
                    BasisLocalization.Get("menu.servers.status.reconnectingIn",
                        Mathf.Max(1, Mathf.CeilToInt(_retryCountdown)), _attempt, MaxReconnectAttempts));
                return;
            }

            _retryDriving = true;
            _ = DriveReconnect();
        }

        private static async Task DriveReconnect()
        {
            int attemptAtStart = _attempt;
            BasisDebug.Log($"Automatic reconnect attempt {_attempt} of {MaxReconnectAttempts}.", BasisDebug.LogTag.Networking);
            try
            {
                await BasisConnectionService.ReconnectAsync();
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Automatic reconnect attempt failed: {ex}", BasisDebug.LogTag.Networking);
            }
            finally
            {
                _retryDriving = false;
                bool alreadyRearmed = _retryPending || _attempt != attemptAtStart;
                if (Health == BasisConnectionHealth.Reconnecting && !alreadyRearmed)
                {
                    ArmRetry();
                }
            }
        }

        private static void ArmRetry()
        {
            _attempt++;
            if (_attempt > MaxReconnectAttempts)
            {
                GiveUp();
                return;
            }
            _retryTotal = RetryDelaysSeconds[Mathf.Clamp(_attempt - 1, 0, RetryDelaysSeconds.Length - 1)];
            _retryCountdown = _retryTotal;
            SetHealth(BasisConnectionHealth.Reconnecting);
        }

        private static void GiveUp()
        {
            _attempt = 0;
            _retryPending = false;
            _retryExhausted = false;
            _retryCountdown = 0f;
            _retryTotal = 0f;
            SetHealth(BasisConnectionHealth.Idle);
            BasisConnectionService.ReportConnectionError(
                BasisLocalization.Get("menu.servers.error.reconnectFailed", MaxReconnectAttempts));
        }

        private static void FailHandshake()
        {
            _handshakeElapsed = 0f;
            SetHealth(BasisConnectionHealth.Idle);
            BasisDebug.LogWarning($"Server never completed the handshake within {HandshakeTimeoutSeconds:F0}s; treating it as a timeout.");
            BasisNetworkConnection.HandleDisconnection(null, new DisconnectInfo
            {
                Reason = DisconnectReason.Timeout
            });
        }

        private static void EnterConnected()
        {
            _handshakeElapsed = 0f;
            _connectedSeconds = 0f;
            _retryCountdown = 0f;
            _retryTotal = 0f;
            _retryPending = false;
            _retryExhausted = false;
            _disconnectTimeoutMs = ReadDisconnectTimeoutMs();
            SetHealth(BasisConnectionHealth.Connected);
        }

        private static bool IsServerSideFailure(DisconnectReason reason)
        {
            switch (reason)
            {
                case DisconnectReason.Timeout:
                case DisconnectReason.ConnectionFailed:
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable:
                    return true;
                default:
                    return false;
            }
        }

        private static float ReadDisconnectTimeoutMs()
        {
            try
            {
                LNLTransportConfig config = BasisTransportConfigStore.Get<LNLTransportConfig>(BasisNetworkStackRegistry.LiteNetLibId);
                if (config != null && config.DisconnectTimeout > 0)
                {
                    return config.DisconnectTimeout;
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Could not read the transport disconnect timeout: {ex.Message}");
            }
            return DefaultDisconnectTimeoutMs;
        }

        private static void SetHealth(BasisConnectionHealth health)
        {
            if (Health == health)
            {
                return;
            }
            Health = health;
            try
            {
                OnHealthChanged?.Invoke(health);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"OnHealthChanged handler threw: {ex}", BasisDebug.LogTag.Networking);
            }
        }
    }
}
