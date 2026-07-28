using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using static SerializableBasis;
using LiteNetManager = LiteNetLib.NetManager;
using LiteNetDataWriter = LiteNetLib.Utils.NetDataWriter;
using LiteNatPunchListener = LiteNetLib.EventBasedNatPunchListener;
using LiteNatAddressType = LiteNetLib.NatAddressType;

namespace Basis.Scripts.Networking
{
    public static class BasisP2PManager
    {
        public enum P2PSessionState : byte
        {
            Idle,
            OutgoingRequested,
            OutgoingArmed,
            IncomingPending,
            Punching,
            Connected,
            Reconnecting,
            Failed,
            // The direct link is not currently carrying this peer — the server relay is.
            // Reached when a Connected link goes stale/one-way, or when re-establishing it
            // gives up after having connected at least once. Everything falls back to the
            // server automatically (all send/relay sites key off Connected); a bounded
            // auto-retry, then a manual "Try Again", re-punches the link.
            PartialConnection,
        }

        private const int MaxPunchAttempts = 5;
        private const int PunchRequestSends = 4;
        private const int PunchRequestIntervalMs = 300;

        // --- Direct-link health / partial-connection detection ---
        // The server stops relaying a pair once both sides report LinkUp. If the UDP data
        // path then dies in one direction while LiteNetLib's tiny keepalive pings survive,
        // no disconnect fires and the starved side is frozen with no server fallback. A
        // lightweight watchdog watches for that (no inbound P2P while Connected), for a peer
        // that never confirmed the offload, and for punches that never complete, and demotes
        // the session to PartialConnection — which restores the server path for free.
        private const int HealthCheckIntervalMs = 500;
        private const long ConnectedGracePeriodMs = 2500;   // settle window before liveness checks
        private const long StaleTimeoutMs = 1500;           // no inbound P2P while Connected => dead (>> the >=20Hz avatar cadence)
        private const long ConfirmTimeoutMs = 4000;         // Connected but server never confirmed offload => peer never came up
        private const long PunchTimeoutMs = 6000;           // Punching/Reconnecting stuck this long => retry
        private const long HealthyDwellResetMs = 30000;     // continuous-Connected time that clears the flap counter
        private const int MaxPartialAutoRetries = 2;        // connect->die->reconnect flaps before we rest for manual retry
        private static readonly int[] PartialBackoffMs = { 0, 3000 };
        // Stopwatch is monotonic and available on every runtime; health timestamps are stored
        // as Stopwatch ticks and converted to ms for the timeouts above.
        private static readonly double StopwatchTicksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        public const float MinAvatarSyncHz = 20f;
        public const float MaxAvatarSyncHz = 250f;
        public const float DefaultAvatarSyncHz = 60f;

        public static float FastAvatarIntervalSeconds
        {
            get
            {
                float hz = BasisSettingsDefaults.P2PAvatarSyncRate != null
                    ? BasisSettingsDefaults.P2PAvatarSyncRate.RawValue
                    : DefaultAvatarSyncHz;
                hz = Math.Clamp(hz, MinAvatarSyncHz, MaxAvatarSyncHz);
                return 1f / hz;
            }
        }

        private sealed class Session
        {
            public string Token;
            public ushort OtherPlayerId;
            public bool LocalIsInitiator;
            public P2PSessionState State;
            public NetPeer P2PPeer;
            public IPAddress ExpectedRemoteAddress;
            public int PunchAttempts;
            public LiteNatAddressType ConnectionType;
            public bool ConnectIssued;
            public byte[] LocalEphemeralPrivate;
            public byte[] LocalEphemeralPublic;
            public byte[] SendKey;
            public byte[] RecvKey;
            public IPEndPoint CryptoEndpoint;
            public long CryptoCounterBase;

            // Health / partial-connection bookkeeping (monotonic Stopwatch ticks; ms via StopwatchTicksToMs).
            public long LastInboundTicks;     // last inbound P2P packet on this peer's socket
            public long ConnectedSinceTicks;  // when we entered Connected (for grace + dwell)
            public long PunchStartedTicks;     // when the current punch cycle began
            public int PartialRecoveryAttempts; // connect->die->reconnect flaps (NOT reset by a successful connect)
            public bool WasEverConnected;      // ever reached Connected — decides Partial-vs-Failed on give-up
            public volatile bool OffloadConfirmed; // server confirmed both sides are up (P2PSub_Offloaded)

            // Serializes state transitions that enter/leave Connected so the health tick
            // (threadpool) and the net thread can't both cross the Connected boundary for
            // the same session and double-count _connectedSessionCount.
            public readonly object Gate = new object();
        }

        private static readonly ConcurrentDictionary<string, Session> _sessionsByToken = new();
        private static readonly ConcurrentDictionary<ushort, Session> _sessionsByOtherId = new();
        private static int _connectedSessionCount;

        // Direct-link health watchdog (see the Health*/Partial* constants above).
        private static Timer _healthTimer;
        private static int _healthTickRunning;

        private static LiteNetManager _p2pManager;
        private static EventBasedNetListener _p2pListener;
        private static LiteNatPunchListener _natListener;
        private static BasisCryptoLayer _p2pCryptoLayer;
        private static readonly object _initLock = new object();

        // Direct connections are always encrypted. When the link re-punches we reuse the
        // same derived keys, so the nonce counter is advanced by this gap on every
        // re-install to guarantee a (key, nonce) pair is never reused.
        private const long P2PReconnectNonceGap = 1L << 40;

        public static string ServerHost { get; set; }
        public static int ServerPort { get; set; }

        public static event Action<ushort, string> OnIncomingRequest;
        public static event Action<ushort, P2PSessionState> OnSessionStateChanged;

        public static void StampServerEndpoint(string host, int port)
        {
            ServerHost = host;
            ServerPort = port;
            BasisNetworkProfiler.ConnectedSessionsProvider = GetConnectedSessionCount;

            BasisNetworkPlayer.OnRemotePlayerLeft -= HandleRemotePlayerLeft;
            BasisNetworkPlayer.OnRemotePlayerLeft += HandleRemotePlayerLeft;
        }

        private static void HandleRemotePlayerLeft(BasisNetworkPlayer player, Basis.Scripts.BasisSdk.Players.BasisRemotePlayer remotePlayer)
        {
            if (player == null) return;
            if (_sessionsByOtherId.TryGetValue(player.playerId, out Session s))
            {
                BasisDebug.Log($"[P2P] Player {player.playerId} left the server; dropping direct session (state {s.State}, token {Preview(s.Token)}).");
                DropSession(s, P2PSessionState.Idle);
            }
        }

        public static int GetConnectedSessionCount()
        {
            return Volatile.Read(ref _connectedSessionCount);
        }

        public static void Shutdown()
        {
            BasisNetworkPlayer.OnRemotePlayerLeft -= HandleRemotePlayerLeft;

            foreach (var kvp in _sessionsByOtherId)
            {
                ApplyState(kvp.Value, P2PSessionState.Idle);
                NotifyStateChanged(kvp.Value.OtherPlayerId, P2PSessionState.Idle);
            }
            _sessionsByOtherId.Clear();
            _sessionsByToken.Clear();

            lock (_initLock)
            {
                _healthTimer?.Dispose();
                _healthTimer = null;

                if (_p2pManager != null)
                {
                    try { _p2pManager.Stop(); }
                    catch (Exception ex) { BasisDebug.LogError($"[P2P] Stop failed: {ex.Message}"); }
                    _p2pManager = null;
                    _p2pListener = null;
                    _natListener = null;
                    _p2pCryptoLayer = null;
                }
            }
        }

        public static string SendRequest(ushort targetPlayerId)
        {
            if (BasisNetworkConnection.LocalPlayerPeer == null)
            {
                BasisDebug.LogError("[P2P] Not connected to server.");
                return null;
            }
            if (!BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId) || localId == targetPlayerId)
            {
                BasisDebug.LogError("[P2P] Cannot request a session with yourself.");
                return null;
            }
            if (_sessionsByOtherId.ContainsKey(targetPlayerId))
            {
                BasisDebug.LogWarning($"[P2P] Already have a session for player {targetPlayerId}.");
                return null;
            }

            string token = Guid.NewGuid().ToString("N");
            var session = new Session
            {
                Token = token,
                OtherPlayerId = targetPlayerId,
                LocalIsInitiator = true,
                State = P2PSessionState.OutgoingRequested,
            };
            BasisCryptoHandshake.GenerateKeyPair(out byte[] ephemeralPrivate, out byte[] ephemeralPublic);
            session.LocalEphemeralPrivate = ephemeralPrivate;
            session.LocalEphemeralPublic = ephemeralPublic;
            _sessionsByToken[token] = session;
            _sessionsByOtherId[targetPlayerId] = session;

            BasisDebug.Log($"[P2P] SendRequest → player {targetPlayerId} (token {Preview(token)}).");
            SendSubToServer(BasisNetworkCommons.P2PSub_Request, targetPlayerId, token, session.LocalEphemeralPublic);
            NotifyStateChanged(targetPlayerId, session.State);
            return token;
        }

        private static string Preview(string token)
        {
            if (string.IsNullOrEmpty(token)) return "(empty)";
            return token.Length <= 8 ? token : token.Substring(0, 8);
        }

        public static void CancelSession(ushort otherPlayerId)
        {
            if (!_sessionsByOtherId.TryGetValue(otherPlayerId, out Session s)) return;

            BasisDebug.Log($"[P2P] CancelSession with player {otherPlayerId} (state was {s.State}, token {Preview(s.Token)}).");
            SendSubToServer(BasisNetworkCommons.P2PSub_Cancel, otherPlayerId, s.Token);
            DropSession(s, P2PSessionState.Idle);
        }

        public static void AcceptIncoming(ushort senderPlayerId, string token)
        {
            if (!_sessionsByToken.TryGetValue(token, out Session s))
            {
                BasisDebug.LogError($"[P2P] AcceptIncoming for unknown token {Preview(token)}.");
                return;
            }
            if (s.State != P2PSessionState.IncomingPending)
            {
                BasisDebug.LogWarning($"[P2P] AcceptIncoming in unexpected state {s.State}.");
                return;
            }
            BasisDebug.Log($"[P2P] AcceptIncoming → player {senderPlayerId} (token {Preview(token)}); sending Accept and starting NAT punch.");
            SendSubToServer(BasisNetworkCommons.P2PSub_Accept, senderPlayerId, token, s.LocalEphemeralPublic);
            StartPunch(s);
        }

        public static void DeclineIncoming(ushort senderPlayerId, string token)
        {
            BasisDebug.Log($"[P2P] DeclineIncoming → player {senderPlayerId} (token {Preview(token)}).");
            SendSubToServer(BasisNetworkCommons.P2PSub_Decline, senderPlayerId, token);
            if (_sessionsByToken.TryGetValue(token, out Session s))
            {
                DropSession(s, P2PSessionState.Idle);
            }
        }

        public static P2PSessionState GetSessionState(ushort otherPlayerId)
        {
            return _sessionsByOtherId.TryGetValue(otherPlayerId, out Session s) ? s.State : P2PSessionState.Idle;
        }

        public static bool TryGetP2PPeer(ushort otherPlayerId, out NetPeer peer)
        {
            peer = null;
            if (!_sessionsByOtherId.TryGetValue(otherPlayerId, out Session s)) return false;
            if (s.State != P2PSessionState.Connected) return false;
            peer = s.P2PPeer;
            return peer != null;
        }

        public static bool IsP2PSessionLocal(ushort otherPlayerId)
        {
            if (!_sessionsByOtherId.TryGetValue(otherPlayerId, out Session s)) return false;
            if (s.State != P2PSessionState.Connected) return false;
            return s.ConnectionType == LiteNatAddressType.Internal;
        }

        // Round-trip time in milliseconds for the P2P link to the given player.
        // Returns false (rttMs = 0) unless the session is Connected with a live peer.
        public static bool TryGetP2PRoundTripTime(ushort otherPlayerId, out int rttMs)
        {
            rttMs = 0;
            if (!_sessionsByOtherId.TryGetValue(otherPlayerId, out Session s)) return false;
            if (s.State != P2PSessionState.Connected) return false;
            var peer = s.P2PPeer;
            if (peer == null) return false;
            rttMs = peer.RoundTripTime;
            return true;
        }

        public static bool HasAnyConnectedSession()
        {
            return Volatile.Read(ref _connectedSessionCount) > 0;
        }

        [ThreadStatic] private static NetDataWriter _p2pVoiceWriter;
        [ThreadStatic] private static NetDataWriter _p2pAvatarWriter;

        private static int SendToAllConnected(NetDataWriter writer, byte channel, DeliveryMethod deliveryMethod)
        {
            if (writer == null || writer.Length == 0) return 0;
            int sent = 0;
            foreach (var kvp in _sessionsByOtherId)
            {
                var s = kvp.Value;
                if (s.State != P2PSessionState.Connected) continue;
                var peer = s.P2PPeer;
                if (peer == null) continue;
                try
                {
                    peer.Send(writer, channel, deliveryMethod);
                    sent++;
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"[P2P] Send on channel {channel} to player {s.OtherPlayerId} failed: {ex.Message}");
                }
            }
            return sent;
        }

        public static int SendDirectToAllConnected(NetDataWriter writer, byte channel, DeliveryMethod deliveryMethod)
        {
            return SendToAllConnected(writer, channel, deliveryMethod);
        }

        public static bool SendDirectTo(ushort otherPlayerId, NetDataWriter writer, byte channel, DeliveryMethod deliveryMethod)
        {
            if (writer == null || writer.Length == 0) return false;
            if (!TryGetP2PPeer(otherPlayerId, out NetPeer peer)) return false;
            try
            {
                peer.Send(writer, channel, deliveryMethod);
                return true;
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[P2P] Direct send on channel {channel} to player {otherPlayerId} failed: {ex.Message}");
                return false;
            }
        }

        public static void PartitionRecipients(ushort[] recipients, out System.Collections.Generic.List<ushort> directIds, out System.Collections.Generic.List<ushort> relayIds)
        {
            directIds = new System.Collections.Generic.List<ushort>();
            relayIds = new System.Collections.Generic.List<ushort>();

            if (recipients == null)
            {
                foreach (var kvp in _sessionsByOtherId)
                {
                    if (kvp.Value.State == P2PSessionState.Connected)
                    {
                        directIds.Add(kvp.Value.OtherPlayerId);
                    }
                }
                if (!BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId))
                {
                    localId = ushort.MaxValue;
                }
                foreach (var kvp in BasisNetworkPlayers.Players)
                {
                    ushort id = kvp.Key;
                    if (id == localId) continue;
                    if (GetSessionState(id) == P2PSessionState.Connected) continue;
                    relayIds.Add(id);
                }
            }
            else
            {
                for (int Index = 0; Index < recipients.Length; Index++)
                {
                    ushort id = recipients[Index];
                    if (GetSessionState(id) == P2PSessionState.Connected)
                    {
                        directIds.Add(id);
                    }
                    else
                    {
                        relayIds.Add(id);
                    }
                }
            }
        }

        public static void BroadcastVoiceViaP2P(NetDataWriter clientFormatWriter)
        {
            if (clientFormatWriter == null || clientFormatWriter.Length == 0) return;
            if (!HasAnyConnectedSession()) return;
            if (!BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId)) return;

            bool largeId = localId > byte.MaxValue;
            byte channel = largeId ? BasisNetworkCommons.VoiceLargeChannel : BasisNetworkCommons.VoiceChannel;

            var w = _p2pVoiceWriter ??= new NetDataWriter();
            w.Reset();
            if (largeId) w.Put(localId);
            else w.Put((byte)localId);
            w.Put(clientFormatWriter.Data, 0, clientFormatWriter.Length);

            // Allowlist talk modes (Private / 1-on-1) only deliver to P2P peers that are
            // actually recipients, so a direct link with a non-member doesn't leak voice.
            bool restricted = BasisTalkModeManager.CurrentMode == BasisTalkMode.Private
                           || BasisTalkModeManager.CurrentMode == BasisTalkMode.ThisPerson;

            foreach (var kvp in _sessionsByOtherId)
            {
                var s = kvp.Value;
                if (s.State != P2PSessionState.Connected) continue;
                if (restricted && !BasisTalkModeManager.IsRecipient(s.OtherPlayerId)) continue;
                var peer = s.P2PPeer;
                if (peer == null) continue;
                try
                {
                    peer.Send(w, channel, DeliveryMethod.Unreliable);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"[P2P] Voice send to player {s.OtherPlayerId} failed: {ex.Message}");
                }
            }
        }

        public static void BroadcastAvatarViaP2P(NetDataWriter clientFormatWriter, byte smallChannel)
        {
            if (clientFormatWriter == null || clientFormatWriter.Length == 0) return;
            if (!HasAnyConnectedSession()) return;
            if (!BasisNetworkConnection.TryGetLocalPlayerID(out ushort localId)) return;

            bool largeId = localId > byte.MaxValue;

            var w = _p2pAvatarWriter ??= new NetDataWriter();
            w.Reset();

            byte channel;
            if (smallChannel == BasisNetworkCommons.DeltaAvatarChannel)
            {
                // Uplink delta [hdr][seq][baseSeq][body][additional?] → the standard delta frame
                // the receive path already decodes: [hdr(+largeId)][playerId][interval][seq][baseSeq][body].
                // Interval 0 decodes to the base cadence; P2P interp uses the announce cache anyway.
                channel = BasisNetworkCommons.DeltaAvatarChannel;
                byte hdr = clientFormatWriter.Data[0];
                if (largeId) hdr |= 0x8;
                w.Put(hdr);
                if (largeId) w.Put(localId);
                else w.Put((byte)localId);
                w.Put((byte)0);
                w.Put(clientFormatWriter.Data, 1, clientFormatWriter.Length - 1);
            }
            else
            {
                channel = largeId
                    ? (byte)(BasisNetworkCommons.PlayerAvatarVeryLowLargeChannel
                        + (smallChannel - BasisNetworkCommons.PlayerAvatarVeryLowChannel))
                    : smallChannel;
                if (largeId) w.Put(localId);
                else w.Put((byte)localId);
                w.Put((byte)0);
                w.Put(clientFormatWriter.Data, 0, clientFormatWriter.Length);
            }

            int sent = SendToAllConnected(w, channel, DeliveryMethod.Unreliable);
            if (sent > 0)
            {
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.OutboundAvatarP2P, (long)w.Length * sent);
            }
        }

        public static void StripP2PConnectedFromRecipients(System.Collections.Generic.List<ushort> recipients)
        {
            if (recipients == null || recipients.Count == 0) return;
            if (!HasAnyConnectedSession()) return;
            for (int i = recipients.Count - 1; i >= 0; i--)
            {
                if (_sessionsByOtherId.TryGetValue(recipients[i], out Session s) &&
                    s.State == P2PSessionState.Connected)
                {
                    recipients.RemoveAt(i);
                }
            }
        }

        public static void AddP2PConnectedToExcluded(System.Collections.Generic.List<ushort> excluded)
        {
            if (excluded == null) return;
            if (!HasAnyConnectedSession()) return;
            foreach (var kvp in _sessionsByOtherId)
            {
                var s = kvp.Value;
                if (s.State != P2PSessionState.Connected) continue;
                if (!excluded.Contains(s.OtherPlayerId))
                {
                    excluded.Add(s.OtherPlayerId);
                }
            }
        }

        public static void HandleServerMessage(NetPacketReader reader)
        {
            byte sub = reader.GetByte();
            BasisP2PSignalMessage msg = default;
            msg.Deserialize(reader);
            reader.Recycle();

            switch (sub)
            {
                case BasisNetworkCommons.P2PSub_Request:
                    OnInboundRequest(msg);
                    break;
                case BasisNetworkCommons.P2PSub_Accept:
                    OnInboundAccept(msg);
                    break;
                case BasisNetworkCommons.P2PSub_Decline:
                case BasisNetworkCommons.P2PSub_Cancel:
                    if (_sessionsByToken.TryGetValue(msg.sessionToken, out Session s1))
                    {
                        BasisDebug.Log($"[P2P] Remote sent {(sub == BasisNetworkCommons.P2PSub_Decline ? "Decline" : "Cancel")} for player {s1.OtherPlayerId}; tearing down session (token {Preview(msg.sessionToken)}).");
                        DropSession(s1, P2PSessionState.Idle);
                    }
                    break;
                case BasisNetworkCommons.P2PSub_LinkLost:
                    if (_sessionsByToken.TryGetValue(msg.sessionToken, out Session s2))
                    {
                        BasisDebug.Log($"[P2P] Remote reported LinkLost for player {s2.OtherPlayerId} (token {Preview(msg.sessionToken)}); scheduling reconnect.");
                        ScheduleReconnect(s2);
                    }
                    break;
                case BasisNetworkCommons.P2PSub_ServerArmed:
                    if (_sessionsByToken.TryGetValue(msg.sessionToken, out Session s3) &&
                        s3.State == P2PSessionState.OutgoingRequested)
                    {
                        BasisDebug.Log($"[P2P] Server armed our request for player {s3.OtherPlayerId} (token {Preview(msg.sessionToken)}); waiting on Accept.");
                        ApplyState(s3, P2PSessionState.OutgoingArmed);
                        NotifyStateChanged(s3.OtherPlayerId, s3.State);
                    }
                    break;
                case BasisNetworkCommons.P2PSub_Offloaded:
                    // Server confirms BOTH sides reported LinkUp and it is now skipping the
                    // relay for this pair. Positive signal that the direct link is fully up;
                    // its absence (while Connected) is what the confirm-timeout watches for.
                    if (_sessionsByToken.TryGetValue(msg.sessionToken, out Session s4))
                    {
                        s4.OffloadConfirmed = true;
                        BasisDebug.Log($"[P2P] Server confirmed direct offload for player {s4.OtherPlayerId} (token {Preview(msg.sessionToken)}).");
                    }
                    break;
            }
        }

        private static void OnInboundRequest(BasisP2PSignalMessage msg)
        {
            BasisDebug.Log($"[P2P] Server forwarded a direct-connection Request from player {msg.otherPlayerId} (token {msg.sessionToken}).");
            if (_sessionsByOtherId.ContainsKey(msg.otherPlayerId))
            {
                BasisDebug.LogWarning($"[P2P] Auto-declining: already have a session with player {msg.otherPlayerId}.");
                SendSubToServer(BasisNetworkCommons.P2PSub_Decline, msg.otherPlayerId, msg.sessionToken);
                return;
            }

            var session = new Session
            {
                Token = msg.sessionToken,
                OtherPlayerId = msg.otherPlayerId,
                LocalIsInitiator = false,
                State = P2PSessionState.IncomingPending,
            };
            BasisCryptoHandshake.GenerateKeyPair(out byte[] ephemeralPrivate, out byte[] ephemeralPublic);
            session.LocalEphemeralPrivate = ephemeralPrivate;
            session.LocalEphemeralPublic = ephemeralPublic;
            DeriveSessionKeys(session, msg.ephemeralPublicKey);
            _sessionsByToken[msg.sessionToken] = session;
            _sessionsByOtherId[msg.otherPlayerId] = session;
            NotifyStateChanged(msg.otherPlayerId, session.State);

            OnIncomingRequest?.Invoke(msg.otherPlayerId, msg.sessionToken);
        }

        private static void OnInboundAccept(BasisP2PSignalMessage msg)
        {
            if (!_sessionsByToken.TryGetValue(msg.sessionToken, out Session s))
            {
                BasisDebug.LogWarning($"[P2P] Accept for unknown token {Preview(msg.sessionToken)} — dropping.");
                return;
            }
            if (s.State != P2PSessionState.OutgoingArmed && s.State != P2PSessionState.OutgoingRequested)
            {
                BasisDebug.LogWarning($"[P2P] Accept arrived in unexpected state {s.State} for player {s.OtherPlayerId}; ignoring.");
                return;
            }
            BasisDebug.Log($"[P2P] Player {s.OtherPlayerId} accepted our request (token {Preview(msg.sessionToken)}); starting NAT punch.");
            DeriveSessionKeys(s, msg.ephemeralPublicKey);
            StartPunch(s);
        }

        private static void StartPunch(Session s)
        {
            EnsureP2PNetManager();
            ApplyState(s, P2PSessionState.Punching);
            s.PunchAttempts++;
            Interlocked.Exchange(ref s.PunchStartedTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            NotifyStateChanged(s.OtherPlayerId, s.State);

            if (_p2pManager == null || string.IsNullOrEmpty(ServerHost) || ServerPort == 0)
            {
                BasisDebug.LogError("[P2P] P2P NetManager or server endpoint missing — cannot punch.");
                DropSession(s, P2PSessionState.Failed);
                return;
            }

            BasisDebug.Log($"[P2P] StartPunch token={Preview(s.Token)} player={s.OtherPlayerId} attempt={s.PunchAttempts}/{MaxPunchAttempts} initiator={s.LocalIsInitiator} → sending {PunchRequestSends} NatIntroduceRequest(s) to {ServerHost}:{ServerPort} from P2P port {_p2pManager.LocalPort}.");
            SendIntroduceBurst(s.Token);
        }

        // Re-send the introduce request a few times across the punch window: a dropped
        // request or a slightly-late peer shouldn't abort the punch, and repeating widens
        // the interval where both NATs have an open mapping.
        private static void SendIntroduceBurst(string token)
        {
            if (!SendOneIntroduce(token) || PunchRequestSends <= 1) return;

            int remaining = PunchRequestSends - 1;
            Timer timer = null;
            timer = new Timer(_ =>
            {
                if (!SendOneIntroduce(token) || Interlocked.Decrement(ref remaining) <= 0)
                {
                    timer?.Dispose();
                }
            }, null, PunchRequestIntervalMs, PunchRequestIntervalMs);
        }

        // Returns false once the session is gone or has left the Punching state, so the
        // burst timer stops resending.
        private static bool SendOneIntroduce(string token)
        {
            if (!_sessionsByToken.TryGetValue(token, out Session s) || s.State != P2PSessionState.Punching)
                return false;
            var mgr = _p2pManager;
            if (mgr == null) return false;
            try
            {
                mgr.NatPunchModule.SendNatIntroduceRequest(ServerHost, ServerPort, token);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[P2P] SendNatIntroduceRequest failed: {ex.Message}");
            }
            return true;
        }

        private static void EnsureP2PNetManager()
        {
            lock (_initLock)
            {
                if (_p2pManager != null) return;

                _p2pListener = new EventBasedNetListener();
                _p2pListener.ConnectionRequestEvent += OnP2PConnectionRequest;
                _p2pListener.PeerConnectedEvent += OnP2PPeerConnected;
                _p2pListener.PeerDisconnectedEvent += OnP2PPeerDisconnected;
                _p2pListener.NetworkReceiveEvent += OnP2PNetworkReceive;

                _natListener = new LiteNatPunchListener();
                _natListener.NatIntroductionSuccess += OnNatIntroductionSuccess;

                _p2pCryptoLayer = new BasisCryptoLayer();
                _p2pManager = new LiteNetManager(_p2pListener, _p2pCryptoLayer)
                {
                    NatPunchEnabled = true,
                    UnsyncedEvents = true,
                    AutoRecycle = false,
                    ChannelsCount = BasisNetworkCommons.TotalChannels,
                    UpdateTime = BasisNetworkCommons.NetworkIntervalPoll,
                };
                _p2pManager.NatPunchModule.Init(_natListener);
                _p2pManager.NatPunchModule.UnsyncedEvents = true;

                if (!_p2pManager.Start())
                {
                    BasisDebug.LogError("[P2P] Failed to start P2P NetManager.");
                    _p2pManager = null;
                    _p2pListener = null;
                    _natListener = null;
                    return;
                }

                BasisDebug.Log($"[P2P] P2P NetManager listening on port {_p2pManager.LocalPort}.");
                _healthTimer ??= new Timer(HealthTick, null, HealthCheckIntervalMs, HealthCheckIntervalMs);
            }
        }

        private static void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, LiteNatAddressType type, string token)
        {
            if (!_sessionsByToken.TryGetValue(token, out Session s))
            {
                BasisDebug.LogWarning($"[P2P] NatIntroductionSuccess for unknown token {Preview(token)}.");
                return;
            }
            if (s.State == P2PSessionState.Connected)
            {
                return;
            }

            // First success wins — Internal arrives before External on same-LAN pairs.
            bool firstSuccess = s.ExpectedRemoteAddress == null;
            if (firstSuccess)
            {
                s.ExpectedRemoteAddress = targetEndPoint.Address;
                s.ConnectionType = type;
                BasisDebug.Log($"[P2P] NatIntroductionSuccess: player {s.OtherPlayerId} reachable ({type}{(type == LiteNatAddressType.Internal ? " — same LAN" : "")}).");
            }

            // Both sides connect to the discovered endpoint; LiteNetLib's simultaneous-open
            // tiebreak (P2PLose) collapses the two attempts into one link. A symmetric-NAT
            // peer's real port is only knowable here, so it must connect too — not just wait.
            if (s.ConnectIssued) return;
            s.ConnectIssued = true;

            if (!InstallSessionKeys(s, targetEndPoint))
            {
                BasisDebug.LogError($"[P2P] Refusing to open an unencrypted direct link to player {s.OtherPlayerId}.");
                s.ConnectIssued = false;
                DropSession(s, P2PSessionState.Failed);
                return;
            }

            try
            {
                // Give the handshake its own timeout window (distinct from the introduce burst).
                Interlocked.Exchange(ref s.PunchStartedTicks, System.Diagnostics.Stopwatch.GetTimestamp());
                var connectData = new LiteNetDataWriter();
                connectData.Put(token);
                _p2pManager.Connect(targetEndPoint, connectData);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[P2P] Connect to player {s.OtherPlayerId} failed: {ex.Message}");
                s.ConnectIssued = false;
            }
        }

        private static void OnP2PConnectionRequest(ConnectionRequest request)
        {
            try
            {
                string token = request.Data.GetString(BasisP2PSignalMessage.MaxTokenLength);
                if (_sessionsByToken.TryGetValue(token, out Session s) &&
                    s.State == P2PSessionState.Punching)
                {
                    if (!InstallSessionKeys(s, request.RemoteEndPoint))
                    {
                        BasisDebug.LogError($"[P2P] Refusing unencrypted inbound direct link from player {s.OtherPlayerId}.");
                        request.Reject(new NetDataWriter());
                        return;
                    }
                    BasisDebug.Log($"[P2P] Inbound P2P connect for token {Preview(token)} (player {s.OtherPlayerId}) — accepting.");
                    s.P2PPeer = request.Accept();
                }
                else
                {
                    BasisDebug.LogWarning($"[P2P] Rejecting inbound P2P connect — unknown or wrong-state token {Preview(token)}.");
                    request.Reject(new NetDataWriter());
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[P2P] Bad connect data: {ex.Message}");
                request.Reject(new NetDataWriter());
            }
        }

        private static void OnP2PPeerConnected(NetPeer peer)
        {
            // Inbound match: P2PPeer already set in OnP2PConnectionRequest, fresh wrapper here equals
            // by underlying LiteNetLib peer. Outbound match: compare against ExpectedRemoteAddress.
            Session matched = null;
            foreach (var kvp in _sessionsByToken)
            {
                var s = kvp.Value;
                if (s.P2PPeer != null && s.P2PPeer.Equals(peer))
                {
                    s.P2PPeer = peer;
                    matched = s;
                    break;
                }
                if (s.P2PPeer == null &&
                    s.State == P2PSessionState.Punching &&
                    s.ExpectedRemoteAddress != null &&
                    peer.Address != null &&
                    peer.Address.Equals(s.ExpectedRemoteAddress))
                {
                    s.P2PPeer = peer;
                    matched = s;
                    break;
                }
            }

            if (matched == null)
            {
                BasisDebug.LogWarning($"[P2P] PeerConnected did not match any pending session.");
                return;
            }

            // Seed the health timestamps BEFORE flipping to Connected so the watchdog, once
            // it observes Connected, always sees a fresh window (never an instant stale).
            long connectedNow = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref matched.ConnectedSinceTicks, connectedNow);
            Interlocked.Exchange(ref matched.LastInboundTicks, connectedNow);
            matched.WasEverConnected = true;
            matched.OffloadConfirmed = false; // fresh link; server re-confirms once both are up

            ApplyState(matched, P2PSessionState.Connected);
            matched.PunchAttempts = 0;
            NotifyStateChanged(matched.OtherPlayerId, matched.State);
            BasisDebug.Log($"[P2P] CONNECTED to player {matched.OtherPlayerId} via {(matched.ConnectionType == LiteNatAddressType.Internal ? "LAN" : "Internet")} (token {Preview(matched.Token)}); sending LinkUp to server.");

            SendSubToServer(BasisNetworkCommons.P2PSub_LinkUp, matched.OtherPlayerId, matched.Token);
            BasisAvatarRateRegistry.ForceNextAnnouncement();
        }

        private static void OnP2PPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            foreach (var kvp in _sessionsByToken)
            {
                var s = kvp.Value;
                if (s.P2PPeer == null || !s.P2PPeer.Equals(peer)) continue;
                BasisDebug.Log($"[P2P] P2P peer for player {s.OtherPlayerId} disconnected: {info.Reason}");
                SendSubToServer(BasisNetworkCommons.P2PSub_LinkLost, s.OtherPlayerId, s.Token);
                ScheduleReconnect(s);
                return;
            }
        }

        private static void OnP2PNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            if (!IsAllowedP2PChannel(channel))
            {
                BasisDebug.LogWarning($"[P2P] Dropping packet on unexpected channel {channel} from P2P peer.");
                reader.Recycle();
                return;
            }

            // Spoof guard — embedded player id must match the session this socket belongs to.
            if (!TryFindSessionByPeer(peer, out Session boundSession, out ushort expectedOtherId))
            {
                BasisDebug.LogWarning("[P2P] Packet from a P2P peer not bound to any session.");
                reader.Recycle();
                return;
            }

            // Any inbound packet on this authenticated socket proves the direct link is
            // alive (LiteNetLib keepalives don't surface here) — refresh the liveness clock
            // so the health watchdog only trips on a genuinely dead/one-way path.
            Interlocked.Exchange(ref boundSession.LastInboundTicks, System.Diagnostics.Stopwatch.GetTimestamp());

            if (channel == BasisNetworkCommons.DirectSceneChannel || channel == BasisNetworkCommons.DirectAvatarChannel)
            {
                HandleDirectP2PPacket(channel, reader, expectedOtherId, deliveryMethod);
                reader.Recycle();
                return;
            }

            if (channel == BasisNetworkCommons.DeltaAvatarChannel)
            {
                HandleP2PDeltaFrame(reader, expectedOtherId);
                reader.Recycle();
                return;
            }

            bool largeId = BasisNetworkCommons.IsLargePlayerIdChannel(channel);
            int origPos = reader.Position;
            if (reader.AvailableBytes < (largeId ? 2 : 1))
            {
                BasisDebug.LogWarning($"[P2P] Truncated packet on channel {channel} from player {expectedOtherId}.");
                reader.Recycle();
                return;
            }
            ushort embeddedId = largeId ? reader.GetUShort() : (ushort)reader.GetByte();
            if (embeddedId != expectedOtherId)
            {
                BasisDebug.LogWarning($"[P2P] Embedded player id {embeddedId} doesn't match session {expectedOtherId} — dropping spoofed packet.");
                reader.Recycle();
                return;
            }
            reader.SetPosition(origPos);

            switch (channel)
            {
                case BasisNetworkCommons.VoiceChannel:
                    _ = BasisNetworkHandleVoice.HandleAudioUpdate(reader, false);
                    break;
                case BasisNetworkCommons.VoiceLargeChannel:
                    _ = BasisNetworkHandleVoice.HandleAudioUpdate(reader, true);
                    break;
                default:
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.InboundAvatarP2P, reader.AvailableBytes);
                    BasisNetworkHandleAvatar.HandleAvatarUpdate(reader, channel);
                    reader.Recycle();
                    break;
            }
        }

        /// <summary>
        /// Inbound P2P DeltaAvatarChannel frame: either a control frame (a peer asking us for a
        /// fresh uplink keyframe) or a peer's avatar delta in the standard delta layout, which the
        /// normal delta decoder handles after the embedded-id spoof check.
        /// </summary>
        private static void HandleP2PDeltaFrame(NetPacketReader reader, ushort expectedOtherId)
        {
            if (reader.AvailableBytes < 1) return;
            int origPos = reader.Position;
            byte header = reader.GetByte();

            if (BasisNetworkCommons.IsDeltaControlHeader(header))
            {
                if (header == BasisNetworkCommons.DeltaControlUplinkKeyframeRequest)
                {
                    Basis.Scripts.Networking.NetworkedAvatar.BasisNetworkAvatarCompressor.ForceUplinkKeyframe();
                }
                return;
            }

            bool largeId = BasisNetworkCommons.DeltaHeaderLargeId(header);
            if (reader.AvailableBytes < (largeId ? 2 : 1)) return;
            ushort embeddedId = largeId ? reader.GetUShort() : reader.GetByte();
            if (embeddedId != expectedOtherId)
            {
                BasisDebug.LogWarning($"[P2P] Delta frame id {embeddedId} doesn't match session {expectedOtherId} — dropping spoofed packet.");
                return;
            }
            reader.SetPosition(origPos);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.InboundAvatarP2P, reader.AvailableBytes);
            BasisNetworkHandleAvatarDelta.Handle(reader);
        }

        private static void HandleDirectP2PPacket(byte channel, NetPacketReader reader, ushort senderPlayerId, DeliveryMethod deliveryMethod)
        {
            if (channel == BasisNetworkCommons.DirectSceneChannel)
            {
                if (reader.AvailableBytes < sizeof(ushort)) return;
                ushort messageIndex = reader.GetUShort();
                byte[] payload = reader.AvailableBytes > 0 ? reader.GetRemainingBytes() : Array.Empty<byte>();
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                    BasisNetworkGenericMessages.HandleDirectP2PSceneMessage(senderPlayerId, messageIndex, payload, deliveryMethod));
            }
            else
            {
                if (reader.AvailableBytes < 2) return;
                byte messageIndex = reader.GetByte();
                byte avatarLinkIndex = reader.GetByte();
                byte[] payload = reader.AvailableBytes > 0 ? reader.GetRemainingBytes() : Array.Empty<byte>();
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                    BasisNetworkGenericMessages.HandleDirectP2PAvatarMessage(senderPlayerId, messageIndex, avatarLinkIndex, payload, deliveryMethod));
            }
        }

        private static bool IsAllowedP2PChannel(byte channel)
        {
            if (channel == BasisNetworkCommons.VoiceChannel ||
                channel == BasisNetworkCommons.VoiceLargeChannel ||
                channel == BasisNetworkCommons.DirectSceneChannel ||
                channel == BasisNetworkCommons.DirectAvatarChannel ||
                channel == BasisNetworkCommons.DeltaAvatarChannel)
                return true;
            return (channel >= BasisNetworkCommons.PlayerAvatarVeryLowChannel &&
                    channel <= BasisNetworkCommons.PlayerAvatarHighAdditionalChannel) ||
                   (channel >= BasisNetworkCommons.PlayerAvatarVeryLowLargeChannel &&
                    channel <= BasisNetworkCommons.PlayerAvatarHighAdditionalLargeChannel);
        }

        private static bool TryFindSessionByPeer(NetPeer peer, out Session session, out ushort otherPlayerId)
        {
            foreach (var kvp in _sessionsByToken)
            {
                var s = kvp.Value;
                if (s.P2PPeer != null && s.P2PPeer.Equals(peer))
                {
                    session = s;
                    otherPlayerId = s.OtherPlayerId;
                    return true;
                }
            }
            session = null;
            otherPlayerId = 0;
            return false;
        }

        private static void ScheduleReconnect(Session s)
        {
            // Leaving Connected (if we were) decrements the counter via ApplyState under s.Gate.
            ApplyState(s, P2PSessionState.Reconnecting);
            s.P2PPeer = null;
            s.ExpectedRemoteAddress = null;
            s.ConnectIssued = false;
            s.OffloadConfirmed = false;

            if (s.PunchAttempts >= MaxPunchAttempts)
            {
                if (s.WasEverConnected)
                {
                    // The link worked before, so keep the session and rest on the server
                    // relay: a manual Try Again (or the peer's own punch) can still
                    // re-establish it, and both sides stay symmetric — neither is torn down,
                    // so a later request can't be auto-declined as "already have a session".
                    BasisDebug.LogWarning($"[P2P] Could not re-establish the direct link to player {s.OtherPlayerId} after {s.PunchAttempts} attempts; resting on server relay (Partial).");
                    RestInPartial(s);
                }
                else
                {
                    BasisDebug.LogWarning($"[P2P] Giving up on player {s.OtherPlayerId} after {s.PunchAttempts} punch attempts (never connected).");
                    DropSession(s, P2PSessionState.Failed);
                }
                return;
            }
            NotifyStateChanged(s.OtherPlayerId, s.State);
            StartPunch(s);
        }

        // Connected -> PartialConnection: the direct link died / went one-way while we were
        // carrying this peer over it. Fall back to the server (the state change alone does
        // that), tell the server to stop offloading (which also forwards LinkLost to the
        // peer so it falls back too), then auto-retry a bounded number of times.
        private static void EnterPartial(Session s, string reason)
        {
            NetPeer deadPeer;
            lock (s.Gate)
            {
                if (s.State != P2PSessionState.Connected) return; // lost the race to a real disconnect
                ApplyState(s, P2PSessionState.PartialConnection);
                deadPeer = s.P2PPeer;
                s.P2PPeer = null;
                s.ExpectedRemoteAddress = null;
                s.ConnectIssued = false;
                s.OffloadConfirmed = false;
            }

            BasisDebug.LogWarning($"[P2P] Direct link to player {s.OtherPlayerId} degraded ({reason}); falling back to the server relay (token {Preview(s.Token)}).");
            RemoveSessionKeys(s);
            if (deadPeer != null) { try { deadPeer.Disconnect(); } catch { } }

            // Clears the server offload (and forwards LinkLost to the peer, dropping it out
            // of Connected too) so BOTH sides resume the server relay.
            SendSubToServer(BasisNetworkCommons.P2PSub_LinkLost, s.OtherPlayerId, s.Token);
            BasisAvatarRateRegistry.ForceNextAnnouncement();
            BasisTransmissionResults.ForceVoiceRecipientResend = true;
            NotifyStateChanged(s.OtherPlayerId, P2PSessionState.PartialConnection);

            int attempt = s.PartialRecoveryAttempts;
            if (attempt < MaxPartialAutoRetries)
            {
                s.PartialRecoveryAttempts = attempt + 1;
                int backoff = PartialBackoffMs[Math.Min(attempt, PartialBackoffMs.Length - 1)];
                BasisDebug.Log($"[P2P] Auto-retrying direct link to player {s.OtherPlayerId} in {backoff}ms (attempt {attempt + 1}/{MaxPartialAutoRetries}).");
                ArmPartialRetry(s, backoff);
            }
            else
            {
                BasisDebug.Log($"[P2P] Direct link to player {s.OtherPlayerId} staying on the server relay after {attempt} auto-retries; awaiting manual Try Again.");
            }
        }

        // Rest in PartialConnection without auto-retrying (used when re-establishment already
        // exhausted its punch attempts). The server offload was cleared when the link first
        // dropped, so the server is already relaying; just make sure voice recipients recompute.
        private static void RestInPartial(Session s)
        {
            // Reached from ScheduleReconnect after ApplyState(Reconnecting): Reconnecting is
            // not Connected, so this transition does not touch the connected counter.
            ApplyState(s, P2PSessionState.PartialConnection);
            s.P2PPeer = null;
            s.ExpectedRemoteAddress = null;
            s.ConnectIssued = false;
            s.OffloadConfirmed = false;
            RemoveSessionKeys(s);
            BasisTransmissionResults.ForceVoiceRecipientResend = true;
            NotifyStateChanged(s.OtherPlayerId, P2PSessionState.PartialConnection);
        }

        // Arm a one-shot re-punch after a backoff. Only fires if the session is still the
        // same live PartialConnection (not cancelled, recovered, or replaced).
        private static void ArmPartialRetry(Session s, int delayMs)
        {
            Timer t = null;
            t = new Timer(_ =>
            {
                t?.Dispose();
                if (!_sessionsByToken.TryGetValue(s.Token, out var cur) || !ReferenceEquals(cur, s)) return;
                if (s.State != P2PSessionState.PartialConnection) return;
                BasisDebug.Log($"[P2P] Re-punching direct link to player {s.OtherPlayerId} (token {Preview(s.Token)}).");
                s.PunchAttempts = 0;
                s.ExpectedRemoteAddress = null;
                s.ConnectIssued = false;
                s.OffloadConfirmed = false;
                StartPunch(s);
            }, null, delayMs, Timeout.Infinite);
        }

        // Manual "Try Again" from the individual-player panel. Re-punches an existing (Partial)
        // session — reusing its token/keys and re-arming the broker + peer — or sends a fresh
        // request if the session was already torn down (never-connected Failed path).
        public static void RetryDirect(ushort otherPlayerId)
        {
            if (_sessionsByOtherId.TryGetValue(otherPlayerId, out Session s))
            {
                BasisDebug.Log($"[P2P] Manual retry for player {otherPlayerId} (state {s.State}, token {Preview(s.Token)}).");
                s.PartialRecoveryAttempts = 0;
                s.PunchAttempts = 0;
                s.ExpectedRemoteAddress = null;
                s.ConnectIssued = false;
                s.OffloadConfirmed = false;
                // Re-arm the broker session + poke the peer to re-punch, then punch ourselves.
                SendSubToServer(BasisNetworkCommons.P2PSub_LinkLost, s.OtherPlayerId, s.Token);
                StartPunch(s);
            }
            else
            {
                BasisDebug.Log($"[P2P] Manual retry for player {otherPlayerId}: no existing session, sending a fresh request.");
                SendRequest(otherPlayerId);
            }
        }

        // Periodic direct-link health check (threadpool). Demotes half-dead / unconfirmed
        // Connected sessions to PartialConnection, retries stalled punches, and clears the
        // flap counter once a link has been healthy for a while.
        private static void HealthTick(object _)
        {
            if (Interlocked.CompareExchange(ref _healthTickRunning, 1, 0) != 0) return;
            try
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (var kvp in _sessionsByToken)
                {
                    var s = kvp.Value;
                    switch (s.State)
                    {
                        case P2PSessionState.Connected:
                        {
                            long connectedAge = (long)((now - Interlocked.Read(ref s.ConnectedSinceTicks)) * StopwatchTicksToMs);
                            long sinceInbound = (long)((now - Interlocked.Read(ref s.LastInboundTicks)) * StopwatchTicksToMs);
                            // Decision logic lives in BasisNetworkCore (BasisP2PLinkHealth) so it can be
                            // unit-tested without a live client; this switch just carries out the verdict.
                            switch (BasisP2PLinkHealth.EvaluateConnected(
                                        connectedAge, sinceInbound, s.OffloadConfirmed, s.PartialRecoveryAttempts != 0,
                                        ConnectedGracePeriodMs, StaleTimeoutMs, ConfirmTimeoutMs, HealthyDwellResetMs))
                            {
                                case BasisP2PLinkHealth.ConnectedVerdict.DemoteStale:
                                    EnterPartial(s, $"no inbound for {sinceInbound}ms");
                                    break;
                                case BasisP2PLinkHealth.ConnectedVerdict.DemoteUnconfirmed:
                                    EnterPartial(s, "peer never confirmed");
                                    break;
                                case BasisP2PLinkHealth.ConnectedVerdict.ClearFlapCounter:
                                    s.PartialRecoveryAttempts = 0;
                                    break;
                            }
                            break;
                        }
                        case P2PSessionState.Punching:
                        case P2PSessionState.Reconnecting:
                        {
                            long punchAge = (long)((now - Interlocked.Read(ref s.PunchStartedTicks)) * StopwatchTicksToMs);
                            if (BasisP2PLinkHealth.PunchStalled(punchAge, PunchTimeoutMs))
                            {
                                BasisDebug.LogWarning($"[P2P] Punch to player {s.OtherPlayerId} stalled {punchAge}ms (token {Preview(s.Token)}); retrying.");
                                ScheduleReconnect(s);
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[P2P] Health tick failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _healthTickRunning, 0);
            }
        }

        private static void DeriveSessionKeys(Session s, byte[] remotePublicKey)
        {
            if (s.LocalEphemeralPrivate == null || s.LocalEphemeralPublic == null ||
                remotePublicKey == null || remotePublicKey.Length != BasisCryptoHandshake.PublicKeySize)
            {
                BasisDebug.LogError($"[P2P] Missing key material for player {s.OtherPlayerId}; direct link cannot be encrypted.");
                return;
            }
            if (BasisCryptoHandshake.DerivePeerKeys(s.LocalEphemeralPrivate, s.LocalEphemeralPublic, remotePublicKey,
                    out byte[] sendKey, out byte[] recvKey))
            {
                s.SendKey = sendKey;
                s.RecvKey = recvKey;
            }
            else
            {
                BasisDebug.LogError($"[P2P] Key derivation failed for player {s.OtherPlayerId}.");
            }
        }

        private static bool InstallSessionKeys(Session s, IPEndPoint endpoint)
        {
            if (_p2pCryptoLayer == null || endpoint == null || s.SendKey == null || s.RecvKey == null) return false;
            if (s.CryptoEndpoint != null) _p2pCryptoLayer.RemoveEndpoint(s.CryptoEndpoint);
            _p2pCryptoLayer.SetEndpointKeys(endpoint, s.SendKey, s.RecvKey, s.CryptoCounterBase);
            s.CryptoEndpoint = endpoint;
            s.CryptoCounterBase += P2PReconnectNonceGap;
            return true;
        }

        private static void RemoveSessionKeys(Session s)
        {
            if (_p2pCryptoLayer != null && s.CryptoEndpoint != null)
            {
                _p2pCryptoLayer.RemoveEndpoint(s.CryptoEndpoint);
                s.CryptoEndpoint = null;
            }
        }

        private static void DropSession(Session s, P2PSessionState finalState)
        {
            // A Connected session strips its peer from the server voice relay
            // (StripP2PConnectedFromRecipients / AddP2PConnectedToExcluded). Capture that before the
            // state changes so we can restore the server path once the session is gone.
            bool wasConnected = s.State == P2PSessionState.Connected;

            ApplyState(s, finalState);
            RemoveSessionKeys(s);
            _sessionsByToken.TryRemove(s.Token, out _);
            System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<ushort, Session>> byId = _sessionsByOtherId;
            byId.Remove(new System.Collections.Generic.KeyValuePair<ushort, Session>(s.OtherPlayerId, s));
            if (s.P2PPeer != null)
            {
                try { s.P2PPeer.Disconnect(); } catch { }
                s.P2PPeer = null;
            }
            NotifyStateChanged(s.OtherPlayerId, finalState);

            // Force the voice recipient list to be recomputed so the peer is put back onto the server
            // relay immediately. Without this, a teardown that doesn't coincide with a player-list
            // change (IndexChanged) — e.g. a Cancel or a direct-link drop while the peer stays in the
            // instance — could leave a stale server-side exclusion, and the peer goes silent. EnterPartial/
            // RestInPartial already do this for the degrade path; this covers the full-teardown path.
            if (wasConnected)
            {
                BasisTransmissionResults.ForceVoiceRecipientResend = true;
            }
        }

        private static void ApplyState(Session s, P2PSessionState newState)
        {
            bool crossedToZero = false;
            bool crossedToOne = false;
            // Serialize the state read-modify-write + counter under the session gate so the
            // health tick and the net thread can't both cross the Connected boundary and
            // double-count (which would leave _connectedSessionCount permanently wrong).
            lock (s.Gate)
            {
                P2PSessionState previous = s.State;
                if (previous == newState) return;
                s.State = newState;
                if (previous == P2PSessionState.Connected)
                    crossedToZero = Interlocked.Decrement(ref _connectedSessionCount) == 0;
                else if (newState == P2PSessionState.Connected)
                    crossedToOne = Interlocked.Increment(ref _connectedSessionCount) == 1;
            }
            // Outside the lock — it calls into the opus subsystem; don't hold a P2P lock across it.
            if (crossedToZero || crossedToOne)
                LocalOpusSettings.ReevaluateEffectiveBitrate();
        }

        private static void NotifyStateChanged(ushort otherPlayerId, P2PSessionState state)
        {
            try { OnSessionStateChanged?.Invoke(otherPlayerId, state); }
            catch (Exception ex) { BasisDebug.LogError($"[P2P] State change handler threw: {ex.Message}"); }
        }

        private static void SendSubToServer(byte sub, ushort otherPlayerId, string token, byte[] ephemeralPublicKey = null)
        {
            var peer = BasisNetworkConnection.LocalPlayerPeer;
            if (peer == null) return;

            var writer = new NetDataWriter();
            writer.Put(sub);
            var body = new BasisP2PSignalMessage
            {
                otherPlayerId = otherPlayerId,
                sessionToken = token ?? string.Empty,
                ephemeralPublicKey = ephemeralPublicKey,
            };
            body.Serialize(writer);
            peer.Send(writer, BasisNetworkCommons.P2PChannel, DeliveryMethod.ReliableOrdered);
        }
    }
}
