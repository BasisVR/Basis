using Basis.Network.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static SerializableBasis;
using LiteNatPunchListener = LiteNetLib.EventBasedNatPunchListener;

namespace BasisNetworkServer
{
    public static class BasisServerP2PBroker
    {
        private enum SessionState : byte { Awaiting, ReadyForPunch, Punched }

        private sealed class Session
        {
            public string Token;
            public int InitiatorPeerId;
            public int TargetPeerId;
            public SessionState State;

            public IPEndPoint EndpointA_Internal;
            public IPEndPoint EndpointA_External;
            public IPEndPoint EndpointB_Internal;
            public IPEndPoint EndpointB_External;
            public bool HasA;
            public bool HasB;

            public bool InitiatorLinkUp;
            public bool TargetLinkUp;
        }

        private static readonly ConcurrentDictionary<string, Session> _sessions = new();
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _peerSessions = new();
        private static readonly ConcurrentDictionary<long, byte> _offloadedPairs = new();

        private static long PackPair(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        /// <summary>
        /// False when no pair is offloaded at all, which is the overwhelmingly common case. The avatar
        /// send loop tests this before <see cref="IsP2POffloaded"/> so a server with no NAT-punched
        /// sessions pays a register compare per pair instead of a ConcurrentDictionary lookup — at 1000
        /// players that is a million hash lookups per tick that no longer happen.
        /// </summary>
        public static bool HasOffloadedPairs => Volatile.Read(ref _offloadedPairCount) != 0;

        private static int _offloadedPairCount;

        public static bool IsP2POffloaded(int a, int b)
        {
            if (a == b) return false;
            if (Volatile.Read(ref _offloadedPairCount) == 0) return false;
            return _offloadedPairs.ContainsKey(PackPair(a, b));
        }

        private static LiteNatPunchListener _natListener;

        public static void Initialize()
        {
            if (_natListener != null) return;

            var manager = (NetworkServer.Server as LNLNetManager)?.manager;
            if (manager == null)
            {
                BNL.LogError("[P2P] NetManager not initialised or active stack is not LiteNetLib, cannot start P2P broker.");
                return;
            }

            if (!manager.NatPunchEnabled)
            {
                BNL.LogWarning("[P2P] NatPunchEnabled=false in server config — direct peer connections will not work. Set NatPunchEnabled=true to enable.");
            }

            _natListener = new LiteNatPunchListener();
            _natListener.NatIntroductionRequest += OnNatIntroductionRequest;
            manager.NatPunchModule.Init(_natListener);
            manager.NatPunchModule.UnsyncedEvents = true;

            BNL.Log("[P2P] Broker initialised.");
        }

        public static void HandleP2PMessage(NetPacketReader reader, NetPeer peer)
        {
            byte sub = reader.GetByte();
            BasisP2PSignalMessage msg = default;
            msg.Deserialize(reader);
            reader.Recycle();

            switch (sub)
            {
                case BasisNetworkCommons.P2PSub_Request:
                    HandleRequest(peer, msg);
                    break;
                case BasisNetworkCommons.P2PSub_Accept:
                    HandleAccept(peer, msg);
                    break;
                case BasisNetworkCommons.P2PSub_Decline:
                    ForwardAndDrop(peer, msg, BasisNetworkCommons.P2PSub_Decline);
                    break;
                case BasisNetworkCommons.P2PSub_Cancel:
                    ForwardAndDrop(peer, msg, BasisNetworkCommons.P2PSub_Cancel);
                    break;
                case BasisNetworkCommons.P2PSub_LinkLost:
                    HandleLinkLost(peer, msg);
                    break;
                case BasisNetworkCommons.P2PSub_LinkUp:
                    HandleLinkUp(peer, msg);
                    break;
                default:
                    BNL.LogError($"[P2P] Unknown sub-type {sub} from peer {peer.Id}.");
                    break;
            }
        }

        private static void HandleLinkUp(NetPeer sender, BasisP2PSignalMessage msg)
            => ApplyLinkUp(sender.Id, msg.sessionToken);

        // Core LinkUp handling, keyed by peer id (the NetPeer entry point only ever needs
        // sender.Id). Exposed to tests so the offload lifecycle can be exercised without
        // constructing LiteNetLib peers.
        internal static void ApplyLinkUp(int senderId, string sessionToken)
        {
            if (!_sessions.TryGetValue(sessionToken, out Session s)) return;

            if (senderId == s.InitiatorPeerId) s.InitiatorLinkUp = true;
            else if (senderId == s.TargetPeerId) s.TargetLinkUp = true;
            else return;

            BNL.Log($"[P2P] LinkUp from peer {senderId} (token {Preview(s.Token)}); flags InitiatorUp={s.InitiatorLinkUp} TargetUp={s.TargetLinkUp}.");
            if (s.InitiatorLinkUp && s.TargetLinkUp)
            {
                if (_offloadedPairs.TryAdd(PackPair(s.InitiatorPeerId, s.TargetPeerId), 0))
                {
                    Interlocked.Increment(ref _offloadedPairCount);
                }
                BNL.Log($"[P2P] OFFLOADED pair ({s.InitiatorPeerId},{s.TargetPeerId}) — server will skip relaying voice + avatar between them.");

                // Positive confirmation to BOTH peers that the pair is fully direct now. A
                // client that reached Connected but never sees this treats its link as partial
                // and falls back to the server relay (see BasisP2PManager confirm-timeout).
                if (NetworkServer.AuthenticatedPeers.TryGetValue(s.InitiatorPeerId, out NetPeer initiatorPeer))
                    SendSub(initiatorPeer, BasisNetworkCommons.P2PSub_Offloaded, s.Token, (ushort)s.TargetPeerId);
                if (NetworkServer.AuthenticatedPeers.TryGetValue(s.TargetPeerId, out NetPeer targetPeer))
                    SendSub(targetPeer, BasisNetworkCommons.P2PSub_Offloaded, s.Token, (ushort)s.InitiatorPeerId);
            }
        }

        private static void HandleRequest(NetPeer sender, BasisP2PSignalMessage msg)
        {
            if (string.IsNullOrEmpty(msg.sessionToken))
            {
                BNL.LogError($"[P2P] Empty session token from peer {sender.Id}, dropping Request.");
                return;
            }
            // Admin-controlled instance lockout: non-admins may not establish direct (P2P) connections.
            // Admins (basis.moderation.globallock) are exempt so they can still connect for moderation.
            if (BasisNetworkServer.Security.BasisGlobalLockManager.DirectConnectLocked &&
                !BasisPermissions.PermissionManager.PermissionIntegration.HasValidRequirement(sender, BasisPermissions.PermNodes.ModerationGlobalLock))
            {
                BNL.Log($"[P2P] DirectConnectLocked: rejecting Request from non-admin peer {sender.Id}.");
                SendSub(sender, BasisNetworkCommons.P2PSub_Cancel, msg.sessionToken, msg.otherPlayerId);
                return;
            }
            if (msg.otherPlayerId == sender.Id)
            {
                BNL.LogError($"[P2P] Peer {sender.Id} tried to request a session with itself.");
                return;
            }
            if (!NetworkServer.AuthenticatedPeers.TryGetValue(msg.otherPlayerId, out NetPeer target))
            {
                SendSub(sender, BasisNetworkCommons.P2PSub_Cancel, msg.sessionToken, msg.otherPlayerId);
                return;
            }

            var session = new Session
            {
                Token = msg.sessionToken,
                InitiatorPeerId = sender.Id,
                TargetPeerId = msg.otherPlayerId,
                State = SessionState.Awaiting,
            };
            _sessions[msg.sessionToken] = session;
            TrackPeerSession(sender.Id, msg.sessionToken);
            TrackPeerSession(msg.otherPlayerId, msg.sessionToken);

            BNL.Log($"[P2P] Forwarding Request from peer {sender.Id} to peer {msg.otherPlayerId} (token {Preview(msg.sessionToken)}).");
            SendSub(target, BasisNetworkCommons.P2PSub_Request, msg.sessionToken, (ushort)sender.Id, msg.ephemeralPublicKey);

            // ServerArmed confirms registration before either side starts punching, avoiding a race.
            SendSub(sender, BasisNetworkCommons.P2PSub_ServerArmed, msg.sessionToken, msg.otherPlayerId);
        }

        private static void HandleAccept(NetPeer sender, BasisP2PSignalMessage msg)
        {
            if (!_sessions.TryGetValue(msg.sessionToken, out Session s))
            {
                BNL.LogError($"[P2P] Accept for unknown token from peer {sender.Id}.");
                return;
            }
            if (s.TargetPeerId != sender.Id || s.InitiatorPeerId != msg.otherPlayerId)
            {
                BNL.LogError($"[P2P] Accept from peer {sender.Id} doesn't match session pair ({s.InitiatorPeerId},{s.TargetPeerId}).");
                return;
            }

            s.State = SessionState.ReadyForPunch;

            if (NetworkServer.AuthenticatedPeers.TryGetValue(s.InitiatorPeerId, out NetPeer initiator))
            {
                BNL.Log($"[P2P] Accept from peer {sender.Id} (token {Preview(s.Token)}); session armed, forwarding to initiator {s.InitiatorPeerId}.");
                SendSub(initiator, BasisNetworkCommons.P2PSub_Accept, s.Token, (ushort)sender.Id, msg.ephemeralPublicKey);
            }
            else
            {
                BNL.LogWarning($"[P2P] Accept arrived but initiator {s.InitiatorPeerId} already gone; dropping session {Preview(s.Token)}.");
                RemoveSession(s.Token);
            }
        }

        private static void HandleLinkLost(NetPeer sender, BasisP2PSignalMessage msg)
            => ApplyLinkLost(sender.Id, msg.sessionToken, msg.otherPlayerId);

        // Core LinkLost handling, keyed by peer id. Re-arms the session + clears offload so the
        // relay resumes during the re-punch window, then forwards LinkLost to the other peer.
        // Exposed to tests (the NetPeer entry point only needs sender.Id + msg fields).
        internal static void ApplyLinkLost(int senderId, string sessionToken, int otherPlayerId)
        {
            // Re-arm session + clear offload so relay resumes during re-punch window.
            if (_sessions.TryGetValue(sessionToken, out Session s))
            {
                bool wasOffloaded = _offloadedPairs.ContainsKey(PackPair(s.InitiatorPeerId, s.TargetPeerId));
                s.HasA = false;
                s.HasB = false;
                s.InitiatorLinkUp = false;
                s.TargetLinkUp = false;
                s.State = SessionState.ReadyForPunch;
                if (_offloadedPairs.TryRemove(PackPair(s.InitiatorPeerId, s.TargetPeerId), out _))
                {
                    Interlocked.Decrement(ref _offloadedPairCount);
                }
                BNL.Log($"[P2P] LinkLost from peer {senderId} (token {Preview(s.Token)}); re-armed for punch, offload {(wasOffloaded ? "cleared (relay resumed)" : "already cleared")}.");
            }
            // Forward to the other peer (guarded) without dropping the session (re-armed above).
            if (NetworkServer.AuthenticatedPeers.TryGetValue(otherPlayerId, out NetPeer other))
                SendSub(other, BasisNetworkCommons.P2PSub_LinkLost, sessionToken, (ushort)senderId);
        }

        private static void ForwardAndDrop(NetPeer sender, BasisP2PSignalMessage msg, byte sub, bool dropSession = true)
        {
            if (NetworkServer.AuthenticatedPeers.TryGetValue(msg.otherPlayerId, out NetPeer other))
            {
                SendSub(other, sub, msg.sessionToken, (ushort)sender.Id);
            }
            if (dropSession && !string.IsNullOrEmpty(msg.sessionToken))
            {
                RemoveSession(msg.sessionToken);
            }
        }

        private static void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            if (!_sessions.TryGetValue(token, out Session s))
            {
                BNL.LogWarning($"[P2P] NatIntroduceRequest with unknown token {Preview(token)} — dropping.");
                return;
            }
            if (s.State < SessionState.ReadyForPunch)
            {
                BNL.LogWarning($"[P2P] NatIntroduceRequest for token {Preview(token)} in state {s.State} — not ready, dropping.");
                return;
            }
            BNL.Log($"[P2P] NatIntroduceRequest token={Preview(token)}; HasA={s.HasA} HasB={s.HasB}.");

            // Arrival order labels the slots; NatIntroduce is symmetric so it doesn't matter which is which.
            lock (s)
            {
                if (!s.HasA)
                {
                    s.EndpointA_Internal = localEndPoint;
                    s.EndpointA_External = remoteEndPoint;
                    s.HasA = true;
                }
                else if (!s.HasB)
                {
                    s.EndpointB_Internal = localEndPoint;
                    s.EndpointB_External = remoteEndPoint;
                    s.HasB = true;
                }

                if (s.HasA && s.HasB)
                {
                    bool firstFire = s.State != SessionState.Punched;
                    bool sameNat = s.EndpointA_External != null &&
                                   s.EndpointB_External != null &&
                                   s.EndpointA_External.Address.Equals(s.EndpointB_External.Address);
                    string lanTag = sameNat ? " [SAME-NETWORK]" : "";

                    // Spray predicted ports on both sides (A/B are arrival-ordered, not
                    // mapped to a specific peer), except on a same-network pair where the
                    // internal punch already handles it.
                    int spray = (firstFire && !sameNat) ? GetPredictionRange() : 0;

                    // Two clients on the SAME host advertise the SAME internal (LAN) IP.
                    // Punching/connecting to a machine's own external-facing LAN IP is often
                    // dropped by the OS/NIC (weak-host-model / firewall), so two same-PC clients
                    // never establish a direct link even though their internal endpoints look
                    // correct. Loopback (127.0.0.1) always routes locally, so rewrite the
                    // internal endpoints to it for a same-host pair. Gated on the same external
                    // IP too, so two different machines that merely share a private IP behind
                    // separate NATs are never rewritten (they keep the real internal punch).
                    IPEndPoint aInternal = s.EndpointA_Internal;
                    IPEndPoint bInternal = s.EndpointB_Internal;
                    bool sameHost = sameNat && aInternal != null && bInternal != null &&
                                    aInternal.Address.Equals(bInternal.Address);
                    if (sameHost)
                    {
                        aInternal = new IPEndPoint(IPAddress.Loopback, aInternal.Port);
                        bInternal = new IPEndPoint(IPAddress.Loopback, bInternal.Port);
                        BNL.Log($"[P2P] SAME-HOST pair for token {Preview(token)} — rewriting internal endpoints to loopback so the local punch lands.");
                    }

                    BNL.Log($"[P2P] Both NAT endpoints collected for token {Preview(token)}. Firing NatIntroduce (spray={spray}).{lanTag}");
                    LiteNetLib.NetManager lnlManager = (NetworkServer.Server as LNLNetManager)?.manager;
                    if (lnlManager == null) return;
                    lnlManager.NatPunchModule.NatIntroduce(
                        aInternal,
                        s.EndpointA_External,
                        spray,
                        bInternal,
                        s.EndpointB_External,
                        spray,
                        token);
                    s.State = SessionState.Punched;
                }
            }
        }

        private static string Preview(string token)
        {
            if (string.IsNullOrEmpty(token)) return "(empty)";
            return token.Length <= 8 ? token : token.Substring(0, 8);
        }

        private static int GetPredictionRange()
        {
            try
            {
                var cfg = BasisTransportConfigStore.Get<LNLTransportConfig>(BasisNetworkStackRegistry.LiteNetLibId);
                return cfg != null ? cfg.NatPortPredictionRange : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static void RemovePeer(int peerId)
        {
            if (!_peerSessions.TryRemove(peerId, out var tokens)) return;
            BNL.Log($"[P2P] Peer {peerId} disconnected; closing out {tokens.Count} P2P session(s).");
            foreach (var kv in tokens)
            {
                string token = kv.Key;
                if (!_sessions.TryGetValue(token, out Session s)) continue;
                int otherId = s.InitiatorPeerId == peerId ? s.TargetPeerId : s.InitiatorPeerId;
                if (NetworkServer.AuthenticatedPeers.TryGetValue(otherId, out NetPeer other))
                {
                    BNL.Log($"[P2P] Notifying peer {otherId} via Cancel that peer {peerId} is gone (token {Preview(token)}).");
                    SendSub(other, BasisNetworkCommons.P2PSub_Cancel, token, (ushort)peerId);
                }
                RemoveSession(token);
            }
        }

        private static void RemoveSession(string token)
        {
            if (!_sessions.TryRemove(token, out Session s)) return;
            UntrackPeerSession(s.InitiatorPeerId, token);
            UntrackPeerSession(s.TargetPeerId, token);
            if (_offloadedPairs.TryRemove(PackPair(s.InitiatorPeerId, s.TargetPeerId), out _))
            {
                Interlocked.Decrement(ref _offloadedPairCount);
            }
        }

        private static void TrackPeerSession(int peerId, string token)
        {
            var inner = _peerSessions.GetOrAdd(peerId, _ => new ConcurrentDictionary<string, byte>());
            inner[token] = 0;
        }

        private static void UntrackPeerSession(int peerId, string token)
        {
            if (_peerSessions.TryGetValue(peerId, out var inner))
            {
                inner.TryRemove(token, out _);
            }
        }

        private static void SendSub(NetPeer to, byte sub, string token, ushort otherPlayerId, byte[] ephemeralPublicKey = null)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(sub);
            var body = new BasisP2PSignalMessage
            {
                otherPlayerId = otherPlayerId,
                sessionToken = token ?? string.Empty,
                ephemeralPublicKey = ephemeralPublicKey,
            };
            body.Serialize(writer);
            NetworkServer.TrySend(to, writer, BasisNetworkCommons.P2PChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        // --- Test seams (InternalsVisibleTo BasisServerTests) -------------------------------
        // The NetPeer-based entry points (HandleRequest/HandleAccept) can't be driven from a
        // unit test because NetPeer has only internal constructors that need a live NetManager.
        // These let a test set up a session and clear state directly, keyed by peer id, so the
        // offload lifecycle (establish -> disconnect -> rejoin with a reused id) is testable.

        // Clears all broker state so each test starts from a clean slate (the dictionaries are
        // static and otherwise persist across tests in the same run).
        internal static void ResetForTests()
        {
            _sessions.Clear();
            _peerSessions.Clear();
            _offloadedPairs.Clear();
            Volatile.Write(ref _offloadedPairCount, 0);
        }

        // Registers a session the way HandleRequest would (session record + per-peer tracking),
        // without needing NetPeers. State starts past Awaiting (as it would be after Accept).
        internal static void RegisterSessionForTests(string token, int initiatorId, int targetId)
        {
            var session = new Session
            {
                Token = token,
                InitiatorPeerId = initiatorId,
                TargetPeerId = targetId,
                State = SessionState.ReadyForPunch,
            };
            _sessions[token] = session;
            TrackPeerSession(initiatorId, token);
            TrackPeerSession(targetId, token);
        }

        // True if the broker currently holds a session under this token (used by tests to assert
        // that disconnect/cancel fully tore the session down, not just the offload flag).
        internal static bool HasSessionForTests(string token) => _sessions.ContainsKey(token);
    }
}
