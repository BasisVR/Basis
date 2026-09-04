using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkCore;
using BasisNetworkCore.Pooling;
using BasisNetworkCore.Security;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisPermissions;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisServerHandle
{
    public static class BasisServerHandleEvents
    {
        [ThreadStatic] private static HashSet<int> _excludedSet;

        /// <summary>
        /// Coalesces "a player joined" notifications instead of fanning each one out inline.
        ///
        /// Announcing a join costs one send per already-connected peer, and that ran on the transport
        /// event thread — the same thread that dispatches auth responses. Measured on a 32-core box it
        /// is ~3us per peer, so a join into 2,500 players spent ~8ms there and a 3,000-player ramp
        /// burned ~20s of event-thread time, which is what pushed handshakes past their window.
        ///
        /// Joins are gathered here and flushed from a worker thread as one ServerReadyBatchMessage per
        /// peer, on the channel the client already uses for the initial player list. Two wins: the
        /// event thread is free again, and K joins inside a window cost one send per peer instead of K.
        ///
        /// Ordering is by join sequence. A peer only receives records newer than its own join, because
        /// everything older was already in the player list it got on arrival — that single rule covers
        /// both "don't spawn a player to itself" and "don't spawn anyone twice".
        /// </summary>
        internal static class JoinBroadcast
        {
            private readonly struct Record
            {
                public readonly long Seq;
                public readonly int PeerId;
                public readonly byte[] Payload;
                public Record(long seq, int peerId, byte[] payload) { Seq = seq; PeerId = peerId; Payload = payload; }
            }

            private static readonly List<Record> _pending = new List<Record>();
            private static readonly List<ushort> _pendingLeaves = new List<ushort>();
            private static readonly ConcurrentDictionary<int, long> _peerSeq = new ConcurrentDictionary<int, long>();
            private static readonly AutoResetEvent _signal = new AutoResetEvent(false);
            private static long _seq;
            private static Thread _worker;
            private static volatile bool _running;

            internal const int FlushIntervalMs = 50;

            public static long NextSeq() => Interlocked.Increment(ref _seq);

            public static void RegisterPeer(int peerId, long seq) => _peerSeq[peerId] = seq;

            public static long RegisteredSeqFor(int peerId) => _peerSeq.TryGetValue(peerId, out long s) ? s : NextSeq();

            public static void UnregisterPeer(int peerId) => _peerSeq.TryRemove(peerId, out _);

            public static void Start()
            {
                Stop();
                _running = true;
                _worker = new Thread(WorkerLoop) { Name = "JoinBroadcast", IsBackground = true };
                _worker.Start();
            }

            public static void Stop()
            {
                _running = false;
                _signal.Set();
                Thread thread = _worker;
                _worker = null;
                if (thread != null && thread != Thread.CurrentThread)
                {
                    thread.Join(500);
                }
                lock (_pending) { _pending.Clear(); }
                // Departures must be dropped too: a restarted server announcing the previous
                // session's leavers would tell clients to despawn players that never existed.
                lock (_pendingLeaves) { _pendingLeaves.Clear(); }
                _peerSeq.Clear();
            }

            public static void Enqueue(long seq, int peerId, byte[] payload)
            {
                lock (_pending) { _pending.Add(new Record(seq, peerId, payload)); }
                _signal.Set();
            }

            /// <summary>
            /// Departures are announced the same way joins are: one send per peer per flush instead of
            /// one per peer per departure. Same O(N) per event, and on a mass exit — shutdown, world
            /// change, cascade — that was the same O(N^2) stall the join path had.
            ///
            /// If the leaver's join is still sitting in this batch, both are dropped: nobody was ever
            /// told the player existed, so there is nothing to undo. That also removes the only
            /// ordering hazard batching introduces, where a "left" could otherwise overtake the
            /// matching "joined" on a different channel and strand a player who never despawns.
            /// </summary>
            public static void EnqueueLeave(int peerId)
            {
                lock (_pending)
                {
                    int pendingJoin = _pending.FindIndex(r => r.PeerId == peerId);
                    if (pendingJoin >= 0)
                    {
                        _pending.RemoveAt(pendingJoin);
                        return;
                    }
                }
                lock (_pendingLeaves) { _pendingLeaves.Add((ushort)peerId); }
                _signal.Set();
            }

            private static void WorkerLoop()
            {
                while (_running)
                {
                    _signal.WaitOne(FlushIntervalMs);
                    if (!_running) break;
                    try
                    {
                        Flush();
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"JoinBroadcast flush failed: {ex.Message}");
                    }
                }
            }

            internal static void Flush()
            {
                // The batch payload ceiling is enforced HERE, on the snapshot, not inside Frame.
                // Frame runs once per distinct receiver start-offset, so a per-Frame overflow
                // re-queue duplicated the overflowing record once per offset — a sustained join
                // ramp compounded that into unbounded growth of _pending (and duplicate spawn
                // announcements). Taking a bounded oldest-first prefix leaves the tail queued
                // exactly once and guarantees progress: at least one record per flush, and every
                // Frame suffix of the prefix fits the cap by construction.
                Record[] batch;
                lock (_pending)
                {
                    if (_pending.Count == 0)
                    {
                        batch = Array.Empty<Record>();
                    }
                    else
                    {
                        _pending.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
                        int take = 0;
                        long payloadBytes = 0;
                        while (take < _pending.Count)
                        {
                            payloadBytes += _pending[take].Payload.Length;
                            if (take > 0 && payloadBytes > ServerReadyBatchMessage.MaxPayloadBytes) break;
                            take++;
                        }
                        batch = new Record[take];
                        _pending.CopyTo(0, batch, 0, take);
                        _pending.RemoveRange(0, take);
                    }
                }
                ushort[] leaves;
                lock (_pendingLeaves)
                {
                    leaves = _pendingLeaves.Count == 0 ? Array.Empty<ushort>() : _pendingLeaves.ToArray();
                    _pendingLeaves.Clear();
                }
                if (batch.Length == 0 && leaves.Length == 0) return;

                NetPeer[] peers = NetworkServer.PeerSnapshot;
                if (peers == null || peers.Length == 0) return;

                // Peers that joined before this whole batch take the identical bytes, which is the
                // common case; only the joiners inside the batch need a trimmed copy of their own.
                // Each distinct start-offset costs a full serialize + Deflate, so with K joins in
                // the window the K+1 frames build in parallel; the fanout itself stays serial.
                List<(NetPeer peer, int start)> targets = new List<(NetPeer, int)>(peers.Length);
                List<int> distinctStarts = new List<int>();
                foreach (NetPeer peer in peers)
                {
                    if (peer == null) continue;
                    long peerSeq = _peerSeq.TryGetValue(peer.Id, out long s) ? s : 0;

                    int start = 0;
                    while (start < batch.Length && batch[start].Seq <= peerSeq) start++;
                    if (start >= batch.Length) continue;

                    targets.Add((peer, start));
                    if (!distinctStarts.Contains(start)) distinctStarts.Add(start);
                }

                Dictionary<int, byte[]> framedByStart = new Dictionary<int, byte[]>();
                if (distinctStarts.Count == 1)
                {
                    framedByStart[distinctStarts[0]] = Frame(batch, distinctStarts[0]);
                }
                else if (distinctStarts.Count > 1)
                {
                    byte[][] framedResults = new byte[distinctStarts.Count][];
                    Parallel.For(0, distinctStarts.Count, BasisServerReductionSystemEvents.SharedParallelOptions,
                        i => framedResults[i] = Frame(batch, distinctStarts[i]));
                    for (int i = 0; i < distinctStarts.Count; i++)
                    {
                        framedByStart[distinctStarts[i]] = framedResults[i];
                    }
                }

                long sent = 0, bytes = 0;
                for (int t = 0; t < targets.Count; t++)
                {
                    (NetPeer peer, int start) = targets[t];
                    byte[] framed = framedByStart[start];
                    try
                    {
                        peer.Send(framed, BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, DeliveryMethod.ReliableOrdered);
                        sent++;
                        bytes += framed.Length;
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"Failed to announce joins to peer {peer.Id}: {ex.Message}");
                    }
                }

                if (sent > 0)
                {
                    BasisNetworkStatistics.RecordOutboundBatch(BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, sent, bytes);
                }

                // Departures after arrivals, so a spawn always precedes any despawn in the same flush.
                FlushLeaves(peers, leaves);
            }

            private static void FlushLeaves(NetPeer[] peers, ushort[] leaves)
            {
                if (leaves.Length == 0) return;

                // The client reads departure ids until the buffer runs out, so a batch is just the
                // ids concatenated — no framing and no client change needed.
                NetDataWriter writer = NetworkServer.RentWriter();
                long sent = 0, bytes = 0;
                try
                {
                    for (int i = 0; i < leaves.Length; i++) writer.Put(leaves[i]);
                    if (!NetworkServer.CheckValidated(writer)) return;

                    foreach (NetPeer peer in peers)
                    {
                        if (peer == null) continue;
                        // A peer in this batch is already gone; skip rather than announce its own exit.
                        bool isLeaver = false;
                        for (int i = 0; i < leaves.Length; i++) { if (peer.Id == leaves[i]) { isLeaver = true; break; } }
                        if (isLeaver) continue;

                        try
                        {
                            peer.Send(writer, BasisNetworkCommons.DisconnectionChannel, DeliveryMethod.ReliableOrdered);
                            sent++;
                            bytes += writer.Length;
                        }
                        catch (Exception ex)
                        {
                            BNL.LogError($"Failed to announce departures to peer {peer.Id}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    NetworkServer.ReturnWriter(writer);
                }

                if (sent > 0)
                {
                    BasisNetworkStatistics.RecordOutboundBatch(BasisNetworkCommons.DisconnectionChannel, sent, bytes);
                }
            }

            private static byte[] Frame(Record[] batch, int start)
            {
                NetDataWriter payload = NetworkServer.RentWriter();
                NetDataWriter framed = NetworkServer.RentWriter();
                try
                {
                    ushort count = 0;
                    for (int i = start; i < batch.Length; i++)
                    {
                        payload.Put(batch[i].Payload);
                        count++;
                    }

                    ServerReadyBatchMessage message = new ServerReadyBatchMessage
                    {
                        Count = count,
                        Payload = payload.CopyData(),
                    };
                    message.Serialize(framed);
                    return framed.CopyData();
                }
                finally
                {
                    NetworkServer.ReturnWriter(payload);
                    NetworkServer.ReturnWriter(framed);
                }
            }
        }

        #region Server Events Setup
        public static void SubscribeServerEvents()
        {
            NetworkServer.Listener.ConnectionRequestEvent += HandleConnectionRequest;
            NetworkServer.Listener.PeerDisconnectedEvent += HandlePeerDisconnected;
            NetworkServer.Listener.NetworkReceiveEvent += BasisNetworkMessageProcessor.ProcessMessage;
            NetworkServer.Listener.NetworkErrorEvent += OnNetworkError;
            BasisServerInfoQuery.Subscribe();
            JoinBroadcast.Start();
        }

        public static void UnsubscribeServerEvents()
        {
            NetworkServer.Listener.ConnectionRequestEvent -= HandleConnectionRequest;
            NetworkServer.Listener.PeerDisconnectedEvent -= HandlePeerDisconnected;
            NetworkServer.Listener.NetworkReceiveEvent -= BasisNetworkMessageProcessor.ProcessMessage;
            NetworkServer.Listener.NetworkErrorEvent -= OnNetworkError;
            BasisServerInfoQuery.Unsubscribe();
        }

        public static void StopWorker()
        {
            JoinBroadcast.Stop();
            NetworkServer.Server?.Stop();
            BasisServerHandleEvents.UnsubscribeServerEvents();
        }
        #endregion

        #region Network Event Handlers

        public static void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            BNL.LogError($"Endpoint {endPoint.ToString()} was reported with error {socketError}");
        }
        #endregion

        #region Peer Connection and Disconnection

        /// <summary>
        /// Runs the idempotent per-peer subsystem cleanup shared by graceful
        /// disconnects and reconnect-collision eviction. Does NOT broadcast a
        /// disconnect to other peers and does NOT reset server-wide state — the
        /// caller decides whether either is appropriate.
        /// </summary>
        private static bool CleanupPeerSubsystems(NetPeer peer, int id)
        {
            // The auth-identity map is the primary UUID source, but it is empty when
            // UseAuthIdentity is off and can already be evicted on a reconnect collision. The
            // stored connect metadata carries the same server-computed UUID (OnNetworkAccepted
            // overwrites the client-supplied one before storing), and it is still present here —
            // BasisSavedState.RemovePlayer runs below. Without the fallback, every UUID-keyed
            // store in this block was orphaned for the life of the process in those modes.
            if (!NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) || string.IsNullOrEmpty(uuid))
            {
                if (BasisSavedState.GetLastPlayerMetaData(peer, out ClientMetaDataMessage meta) && !string.IsNullOrEmpty(meta.playerUUID))
                {
                    uuid = meta.playerUUID;
                }
            }

            NetworkServer.AuthIdentity.RemoveConnection(id, peer);

            // A predecessor's disconnect can land after a reconnect has already taken the same id.
            // Every teardown below is keyed by id alone, so running it for a peer that no longer
            // owns the slot dismantles the live peer's state instead — the "direct connect works,
            // then dies after a rejoin" symptom. An id held by nobody still cleans up, so a peer
            // rejected before auth completed keeps releasing whatever partial state it made.
            if (NetworkServer.AuthenticatedPeers.TryGetValue(id, out NetPeer holder) && !Equals(holder, peer))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(uuid))
            {
                PermissionIntegration.RemovePlayerMeta(uuid);
                PermissionIntegration.EvictPermissionCache(uuid);
                BasisNetworkHandleErrorReport.RemoveUser(uuid);
                BasisNetworkResourceManagement.RemovePeerResources(uuid);
            }

            BasisNetworkOwnership.RemovePlayerOwnership(id);
            BasisSavedState.RemovePlayer(id);
            BasisServerReductionSystemEvents.RemovePlayer(id);
            BasisNetworkPIPCamera.RemovePlayer(id);
            BasisNetworkContentShare.RemovePlayerSpheres(id);
            BasisNetworkImageCache.RemovePlayerImages(id);
            // Drops this peer's egress bucket and any replay still queued for it. Without this a
            // recycled player id would inherit the previous holder's spent budget.
            BasisImageBandwidthGovernor.RemovePeer(id);
            BasisNetworkPreloadResourceManagement.RemovePeer(id);
            BasisNetworkServer.Security.BasisUserOpusBitrateStateManager.ClearForPeer(id);
            BasisServerP2PBroker.RemovePeer(id);
            BasisNetworkIDDatabase.RemovePeer(id);
            Basis.Network.Server.Generic.BasisNetworkingGeneric.RemovePeerSceneEgress(id);
            BasisNetworkMessageProcessor.ClearPeerErrors(id);
            BasisServerMessageRegistry.ClearSubscription(id);
            JoinBroadcast.UnregisterPeer(id);

            // Value-matched, mirroring RejectWithReason(NetPeer): the guard above raced against a
            // reconnect that may have claimed the id since.
            return ((ICollection<KeyValuePair<int, NetPeer>>)NetworkServer.AuthenticatedPeers)
                .Remove(new KeyValuePair<int, NetPeer>(id, peer));
        }

        public static void HandlePeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            try
            {
                if(peer == null)
                {
                    BNL.LogError("Missing Peer this is a mistake!");
                    return;
                }
                int id = peer.Id;

                if (CleanupPeerSubsystems(peer, id))
                {
                    NetworkServer.RebuildPeerSnapshot();
                    BNL.Log($"Peer removed: {id}");
                }
                else
                {
                    BNL.Log($"Peer {id} was not in AuthenticatedPeers (likely rejected before auth completed).");
                }

                if (NetworkServer.AuthenticatedPeers.IsEmpty)
                {
                    BasisNetworkIDDatabase.Reset();
                    BasisNetworkResourceManagement.Reset();
                    BasisNetworkContentShare.Reset();
                }

                JoinBroadcast.EnqueueLeave(id);
            }
            catch (Exception e)
            {
                BNL.LogError($"{e.Message} {e.StackTrace}");
            }
        }
        #endregion

        #region Utility Methods
        public static void RejectWithReason(ConnectionRequest request, string reason)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(reason);
            request.Reject(writer);
            NetworkServer.ReturnWriter(writer);
            BNL.LogError($"Rejected for reason: {reason}");
        }

        /// <summary>
        /// Rejects a pending connection with a structured payload the client can branch on
        /// (see BasisNetworkCommons.RejectKind_*), e.g. to show a dedicated "Update Required" or
        /// "Server Full" screen. Older clients read it defensively as an (empty) string and fall back
        /// to a generic message, so this stays backward compatible.
        /// </summary>
        public static void RejectStructured(ConnectionRequest request, byte kind, ushort aux0, ushort aux1, string message)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(BasisNetworkCommons.RejectMagic);
            writer.Put(kind);
            writer.Put(aux0);
            writer.Put(aux1);
            writer.Put(message ?? string.Empty);
            request.Reject(writer);
            NetworkServer.ReturnWriter(writer);
            BNL.LogError($"Rejected (kind {kind}): {message}");
        }

        public static void RejectVersionMismatch(ConnectionRequest request, ushort serverVersion, ushort clientVersion)
        {
            string guidance = clientVersion < serverVersion
                ? "Update your Basis client to match the server."
                : "This server is running an older Basis build than your client.";
            RejectStructured(request, BasisNetworkCommons.RejectKind_VersionMismatch, serverVersion, clientVersion,
                $"This server needs client protocol v{serverVersion}; your client is v{clientVersion}. {guidance}");
        }
        public static void RejectWithReason(NetPeer request, string reason)
        {
            int id = request.Id;
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(reason ?? string.Empty);
            byte[] reasonBytes = writer.CopyData();
            NetworkServer.ReturnWriter(writer);
            // Key-value-matched remove: "Peer already exists" rejects the duplicate,
            // so only evict if the stored NetPeer is actually this one — otherwise
            // we'd silently kick the alive peer that owns the slot.
            var kvp = new KeyValuePair<int, NetPeer>(id, request);
            if (((ICollection<KeyValuePair<int, NetPeer>>)NetworkServer.AuthenticatedPeers).Remove(kvp))
            {
                NetworkServer.RebuildPeerSnapshot();
            }
            request.Disconnect(reasonBytes);
            BNL.LogError($"Rejected after accept with reason: {reason}");
        }

        public static bool IsHeadlessDisallowed(ClientMetaDataMessage metaData, out string reason)
        {
            if (!BasisHeadlessConnectionPolicyManager.HeadlessDisallowed ||
                !BasisHeadlessConnectionPolicyManager.IsHeadlessClient(metaData))
            {
                reason = null;
                return false;
            }

            reason = BasisHeadlessConnectionPolicyManager.DisallowedReason;
            return true;
        }
        #endregion

        #region Connection Handling
        public static void HandleConnectionRequest(ConnectionRequest ConReq)
        {
            try
            {
                if (BasisPlayerModeration.IsIpBanned(ConReq.RemoteEndPoint.Address.ToString()))
                {
                    RejectWithReason(ConReq, "Banned IP");
                    return;
                }
              //  BNL.Log("Processing Connection Request");
                int ServerCount = NetworkServer.Server.ConnectedPeersCount;

                if (ServerCount >= NetworkServer.Configuration.PeerLimit)
                {
                    RejectStructured(ConReq, BasisNetworkCommons.RejectKind_ServerFull, 0, 0,
                        $"This server is full ({ServerCount}/{NetworkServer.Configuration.PeerLimit}). Please try again later.");
                    return;
                }

                if (!ConReq.Data.TryGetUShort(out ushort ClientVersion))
                {
                    RejectWithReason(ConReq, "Invalid client data.");
                    return;
                }

                if (ClientVersion != BasisNetworkVersion.ServerVersion)
                {
                    RejectVersionMismatch(ConReq, BasisNetworkVersion.ServerVersion, ClientVersion);
                    return;
                }
                if (NetworkServer.Configuration.UseAuth)
                {
                    BytesMessage authMessage = new BytesMessage();
                    if (!authMessage.Deserialize(ConReq.Data, out byte[] AuthBytes))
                    {
                        RejectWithReason(ConReq, "Malformed auth payload");
                        return;
                    }
                    if (NetworkServer.Auth.IsAuthenticated(AuthBytes) == false)
                    {
                        RejectWithReason(ConReq, "Authentication failed, Auth rejected");
                        return;
                    }
                }
                else
                {
                    //we still want to read the data to move the needle along
                    BytesMessage authMessage = new BytesMessage();
                    authMessage.Deserialize(ConReq.Data, out byte[] UnusedBytes);
                }
                if (NetworkServer.Configuration.UseAuthIdentity)
                {
                    NetPeer newPeer = ConReq.Accept();//can do both way Communication from here on
                    NetworkServer.AuthIdentity.ProcessConnection(NetworkServer.Configuration, ConReq, newPeer);
                }
                else
                {
                    ReadyMessage readyMessage = new ReadyMessage();
                    readyMessage.Deserialize(ConReq.Data);

                    if (readyMessage.WasDeserializedCorrectly())
                    {
                        if (IsHeadlessDisallowed(readyMessage.playerMetaDataMessage, out string reason))
                        {
                            RejectWithReason(ConReq, reason);
                            return;
                        }
                    }

                    NetPeer newPeer = ConReq.Accept();//can do both way Communication from here on

                    if (readyMessage.WasDeserializedCorrectly())
                    {
                        OnNetworkAccepted(newPeer, readyMessage, readyMessage.playerMetaDataMessage.playerUUID);
                    }
                }
            }
            catch (Exception e)
            {
                RejectWithReason(ConReq, "Fatal Connection Issue stacktrace on server " + e.Message);
                BNL.LogError(e.StackTrace);
            }
        }
        public static void OnNetworkAccepted(NetPeer newPeer, ReadyMessage ReadyMessage, string UUID)
        {
            ushort PeerId = (ushort)newPeer.Id;

            // AllowList gate. Both auth paths (DID challenge + plain ReadyMessage) funnel
            // through here with a verified UUID, so this is the single point that enforces
            // BasisUserRestrictionMode.AllowList on entry. Banlist is enforced separately
            // at HandleConnectionRequest / BasisDIDAuthIdentity.ProcessConnection.
            if (NetworkServer.Configuration.BasisUserRestrictionMode == BasisUserRestrictionMode.AllowList
                && NetworkServer.AllowList != null
                && !NetworkServer.AllowList.IsAllowed(UUID))
            {
                BNL.Log($"Rejecting peer {PeerId} (UUID {UUID}) — not on allowlist.");
                RejectWithReason(newPeer, "You are not on the allowlist.");
                return;
            }

            if (NetworkServer.Configuration.BasisUserRestrictionMode == BasisUserRestrictionMode.BanList
                && NetworkServer.BanList != null
                && NetworkServer.BanList.IsBanned(UUID))
            {
                BNL.Log($"Rejecting peer {PeerId} (UUID {UUID}) — on banlist.");
                RejectWithReason(newPeer, "You are not permitted on this server.");
                return;
            }

            // Rejoin-only lockdown: only UUIDs captured when the mode was enabled may (re)connect.
            // Config-editor admins always bypass so an admin can't lock themselves out.
            if (NetworkServer.Configuration.BasisUserRestrictionMode == BasisUserRestrictionMode.RejoinOnly
                && !BasisRejoinLockManager.IsAllowed(UUID)
                && !PermissionIntegration.HasValidRequirement(UUID, PermNodes.ConfigurationEditor))
            {
                BNL.Log($"Rejecting peer {PeerId} (UUID {UUID}) — server locked to current players (rejoin-only).");
                RejectWithReason(newPeer, "The server is locked — only players already here may rejoin.");
                return;
            }

            string sanitizedDisplayName = BasisDisplayNameSanitizer.Sanitize(ReadyMessage.playerMetaDataMessage.playerDisplayName);
            if (string.IsNullOrEmpty(sanitizedDisplayName))
            {
                BNL.Log($"Rejecting peer {PeerId} (UUID {UUID}) — empty or invisible display name.");
                RejectWithReason(newPeer, "Choose a non-empty username.");
                return;
            }
            ReadyMessage.playerMetaDataMessage.playerDisplayName = sanitizedDisplayName;

            bool added = NetworkServer.AuthenticatedPeers.TryAdd(PeerId, newPeer);
            if (!added)
            {
                // Reconnect collision: LiteNetLib recycled this peer-id slot before the
                // previous disconnect's subsystem cleanup completed (or the original
                // PeerDisconnectedEvent has not yet been dispatched). The old entry is
                // stale because LNL will not hand us two live peers with the same Id —
                // evict it synchronously and retry the insert.
                if (NetworkServer.AuthenticatedPeers.TryGetValue(PeerId, out NetPeer stale) &&
                    !Equals(stale, newPeer))
                {
                    BNL.Log($"Reconnect collision on peer id {PeerId}; evicting stale entry and accepting new connection.");
                    CleanupPeerSubsystems(stale, PeerId);
                    added = NetworkServer.AuthenticatedPeers.TryAdd(PeerId, newPeer);
                }
            }

            if (added)
            {
                newPeer.Tag = NetworkServer.AuthenticatedPeerTag;
                NetworkServer.RebuildPeerSnapshot();
                // Claim this peer's place in the join order before anything is announced, so the
                // "only records newer than my own join" rule below has a value to compare against.
                JoinBroadcast.RegisterPeer(newPeer.Id, JoinBroadcast.NextSeq());
                BNL.Log($"Peer connected: {newPeer.Id}");
                //never ever assume the UUID provided by the user is good always recalc on the server.
                //this means that as long as they pass auth but locally have a bad UUID that only they locally are effected.
                //there is no way to force a user locally to be a certain UUID, that's not how the internet works.
                //instead we can make sure all additional clients have them correct.
                //this only occurs if the server is doing Auth checks.
                ReadyMessage.playerMetaDataMessage.playerUUID = UUID;
                PermissionIntegration.StorePlayerMeta(UUID, ReadyMessage.playerMetaDataMessage);

               Configuration Config = NetworkServer.Configuration;
                //lets dump to the local client there data after the server has had its way
                ServerMetaDataMessage ServerMetaDataMessage = new ServerMetaDataMessage
                {
                    ClientMetaDataMessage = ReadyMessage.playerMetaDataMessage,
                    SyncInterval = Config.BSRSMillisecondDefaultInterval,
                    BaseMultiplier = Config.BSRBaseMultiplier,
                    IncreaseRate = Config.BSRSIncreaseRate,
                    SlowestSendRate = Config.BSRSlowestSendRate,
                    PeerLimit = Config.PeerLimit,
                    UplinkDeltaEnabled = Config.EnableUplinkAvatarDelta,
                    ImageShareEgressMegabitsPerSecond = Config.ImageShareEgressMegabitsPerSecond,
                    ImagePickupRangeMeters = Math.Max(0f, Config.ImagePickupRangeMeters),
                };
                ServerMetaDataMessage.SetPermissions(PermissionIntegration.Manager.GetAllAllowedRules(UUID), PermissionIntegration.Manager.GetAllDeniedRules(UUID));
                NetDataWriter Writer = NetworkServer.RentWriter();
                ServerMetaDataMessage.Serialize(Writer);
                NetworkServer.TrySend(newPeer, Writer, BasisNetworkCommons.metaDataChannel, DeliveryMethod.ReliableOrdered);

                BasisServerMessageRegistry.SendSupplyTo(newPeer);

                if (BasisNetworkIDDatabase.GetAllNetworkID(out List<ServerNetIDMessage> ServerNetIDMessages))
                {
                    ServerUniqueIDMessages ServerUniqueIDMessageArray = new ServerUniqueIDMessages
                    {
                        Messages = ServerNetIDMessages.ToArray(),
                    };

                    Writer.Reset();
                    ServerUniqueIDMessageArray.Serialize(Writer);
                    //BNL.Log($"Sending out Network Id Count " + ServerUniqueIDMessageArray.Messages.Length);
                    NetworkServer.TrySend(newPeer, Writer, BasisNetworkCommons.NetIDAssignsChannel, DeliveryMethod.ReliableOrdered);
                }

                NetworkServer.ReturnWriter(Writer);

                SendRemoteSpawnMessage(newPeer, ReadyMessage);

                BasisNetworkResourceManagement.SendOutAllResources(newPeer);
                BasisNetworkServerLibrary.SendLibraryToPeer(newPeer);
                BasisNetworkOwnership.SendOutOwnershipInformation(newPeer);
                BasisNetworkPIPCamera.SendPIPStateToPeer(newPeer);
                BasisNetworkContentShare.SendAllSpheresToPeer(newPeer);
                BasisNetworkImageCache.OfferCachedImagesToPeer(newPeer);
                BasisNetworkServer.Security.BasisGlobalLockManager.SendLockStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisHeadlessAudioStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisHeadlessConnectionPolicyManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisOpusPacketLossStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisOpusFrameDurationStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisUserOpusBitrateStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisUserOpusBitrateStateManager.SendGlobalStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisCrashReportStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisAudioRangeLimitManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisAvatarScaleLimitManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisResourceLimitManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisPlayerModeration.SendReductionSettingsToPeer(newPeer);
                BasisNetworkServer.Security.BasisPlayerModeration.SendImageBandwidthToPeer(newPeer);
                BasisNetworkServer.Security.BasisPlayerModeration.SendPeerLimitToPeer(newPeer);
                BasisNetworkServer.Security.BasisPlayerMuteManager.SendStateToPeerIfMuted(newPeer);
                SendAnnounceStateToPeer(newPeer);
                SendShoutStateToPeer(newPeer);
            }
            else
            {
                RejectWithReason(newPeer, "Peer already exists.");
            }
        }
        #endregion
        // Define the delegate type
        public delegate void AuthEventHandler(NetPacketReader reader, NetPeer peer);

        // Declare an event of the delegate type
        public static event AuthEventHandler OnAuthReceived;
        public static void HandleAuth(NetPacketReader Reader, NetPeer Peer)
        {
            OnAuthReceived?.Invoke(Reader, Peer);
            Reader.Recycle();
        }
        public static ServerEventHandler OnServerReceived;
        public delegate void ServerEventHandler(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod);
        #region Avatar and Voice Handling
        public static void SendAvatarMessageToClients(NetPacketReader Reader, NetPeer Peer)
        {
            // Leading kind byte multiplexes this channel — see BasisNetworkCommons.AvatarChangeKind*.
            byte kind = Reader.GetByte();
            if (kind == BasisNetworkCommons.AvatarChangeKindBodyFit)
            {
                SendBodyFitMessageToClients(Reader, Peer);
                return;
            }

            ClientAvatarChangeMessage ClientAvatarChangeMessage = new ClientAvatarChangeMessage();
            ClientAvatarChangeMessage.Deserialize(Reader);
            Reader.Recycle();

            // Global avatar lock: drop the change outright — neither broadcast nor saved, so a
            // late joiner isn't handed an avatar the lock exists to keep out of the instance.
            if (BasisNetworkServer.Security.BasisGlobalLockManager.AvatarsLocked)
            {
                bool hasBypass = false;
                if (NetworkServer.AuthIdentity.NetIDToUUID(Peer, out string uuid))
                {
                    hasBypass = PermissionIntegration.HasValidRequirement(uuid, PermNodes.ResourceLockBypassAvatar);
                }

                if (!hasBypass)
                {
                    BNL.Log($"Avatar loading is globally disabled. Rejected avatar change from peer {Peer.Id}");
                    BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(Peer, "Avatar loading is currently disabled by an admin.");
                    return;
                }
            }

            ServerAvatarChangeMessage serverAvatarChangeMessage = new ServerAvatarChangeMessage
            {
                clientAvatarChangeMessage = ClientAvatarChangeMessage,
                uShortPlayerId = new PlayerIdMessage
                {
                    playerID = (ushort)Peer.Id
                }
            };
            BasisSavedState.AddLastData(Peer, ClientAvatarChangeMessage);
            NetDataWriter Writer = NetworkServer.RentWriter();
            Writer.Put(BasisNetworkCommons.AvatarChangeKindFull);
            serverAvatarChangeMessage.Serialize(Writer);

            NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.AvatarChangeMessageChannel, Peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(Writer);
        }

        /// <summary>
        /// Handles a body-fit-only update: merge it into this peer's saved avatar record (so a late
        /// joiner receives the current proportions with the avatar, not the authored ones) and relay it
        /// to everyone else. Deliberately not gated by the global avatar lock — nothing is being loaded,
        /// this only resizes segments of an avatar the peer is already wearing.
        /// </summary>
        private static void SendBodyFitMessageToClients(NetPacketReader Reader, NetPeer Peer)
        {
            ClientBodyFitMessage bodyFit = new ClientBodyFitMessage();
            bodyFit.Deserialize(Reader);
            Reader.Recycle();

            BasisSavedState.UpdateBodyFit(Peer, bodyFit);

            ServerBodyFitMessage serverBodyFitMessage = new ServerBodyFitMessage
            {
                bodyFit = bodyFit,
                uShortPlayerId = new PlayerIdMessage
                {
                    playerID = (ushort)Peer.Id
                }
            };

            NetDataWriter Writer = NetworkServer.RentWriter();
            Writer.Put(BasisNetworkCommons.AvatarChangeKindBodyFit);
            serverBodyFitMessage.Serialize(Writer);

            NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.AvatarChangeMessageChannel, Peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(Writer);
        }

        /// <summary>
        /// True when this peer may not transmit voice: the global voice lock is on and they lack
        /// basis.voice.lockbypass, or a moderator voice-muted them. Shared by the normal and
        /// announce voice paths.
        /// </summary>
        public static bool IsVoiceBlockedFor(NetPeer peer)
        {
            if (BasisNetworkServer.Security.BasisPlayerMuteManager.IsVoiceMutedFor(peer)) return true;
            return BasisNetworkServer.Security.BasisGlobalLockManager.VoiceChatLocked &&
                !PermissionIntegration.HasValidRequirement(peer, PermNodes.VoiceLockBypass);
        }

        /// <summary>
        /// UUID-keyed form of <see cref="IsVoiceBlockedFor(NetPeer)"/>, mirroring the two
        /// HasValidRequirement overloads.
        /// </summary>
        public static bool IsVoiceBlockedForUuid(string uuid)
        {
            if (BasisNetworkServer.Security.BasisPlayerMuteManager.IsVoiceMuted(uuid)) return true;
            return BasisNetworkServer.Security.BasisGlobalLockManager.VoiceChatLocked &&
                !PermissionIntegration.HasValidRequirement(uuid, PermNodes.VoiceLockBypass);
        }

        public static void HandleVoiceMessage(NetPacketReader reader, NetPeer peer)
        {
            if (IsVoiceBlockedFor(peer))
            {
                // Dropped silently — voice arrives ~50x/sec per speaker, so a reply or log line per
                // dropped packet would be a far worse amplification vector than the traffic itself.
                // Clients stop transmitting once they see the broadcast lock state; this is the
                // backstop for old and modified ones.
                reader.Recycle();
                return;
            }

            AudioSegmentDataMessage audioSegment = ThreadSafeMessagePool<AudioSegmentDataMessage>.Rent();
            audioSegment.Deserialize(reader);
            reader.Recycle();

            ServerAudioSegmentMessage serverAudio = new ServerAudioSegmentMessage
            {
                audioSegmentData = audioSegment,
            };

            SendVoiceMessageToClients(serverAudio, peer, DeliveryMethod.Unreliable);

            ThreadSafeMessagePool<AudioSegmentDataMessage>.Return(audioSegment);
        }

        /// <summary>
        /// Handles announce voice sent by a client on AnnounceVoiceChannel (channel 4).
        /// Only processes if the sender is authorized for announce mode.
        /// Broadcasts to ALL connected peers.
        /// </summary>
        public static void HandleAnnounceVoiceMessage(NetPacketReader reader, NetPeer peer)
        {
            if (!BasisSavedState.IsInAnnounceMode(peer.Id))
            {
                BNL.LogError($"Peer {peer.Id} sent announce voice but is not in announce mode. Ignoring.");
                reader.Recycle();
                return;
            }

            if (IsVoiceBlockedFor(peer))
            {
                reader.Recycle();
                return;
            }

            AudioSegmentDataMessage audioSegment = ThreadSafeMessagePool<AudioSegmentDataMessage>.Rent();
            audioSegment.Deserialize(reader);
            reader.Recycle();

            ServerAudioSegmentMessage serverAudio = new ServerAudioSegmentMessage
            {
                audioSegmentData = audioSegment,
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)peer.Id,
                },
            };

            // Serialize once, then send raw to each peer — skips N writer→packet copies.
            var writer = NetworkServer.RentWriter();
            serverAudio.Serialize(writer);
            int len = writer.Length;
            byte[] data = writer.Data;
            byte channel = BasisNetworkCommons.AnnounceVoiceChannel;
            int senderId = peer.Id;

            var clients = NetworkServer.PeerSnapshot;
            for (int i = 0; i < clients.Length; i++)
            {
                NetPeer client = clients[i];
                if (client.Id != senderId)
                {
                    client.SendUnreliableRawMerge(data, 0, len, channel);
                    BasisNetworkStatistics.RecordOutbound(channel, len);
                }
            }

            NetworkServer.ReturnWriter(writer);
            ThreadSafeMessagePool<AudioSegmentDataMessage>.Return(audioSegment);
        }

        /// <summary>
        /// Broadcasts an announce mode state change to all clients via the AdminChannel.
        /// </summary>
        public static void BroadcastAnnounceModeState(ushort targetPlayerId, bool enabled, ushort initiatorPlayerId)
        {
            var writer = NetworkServer.RentWriter();
            AdminRequestMode mode = enabled ? AdminRequestMode.EnableAnnounceMode : AdminRequestMode.DisableAnnounceMode;
            new AdminRequest().Serialize(writer, mode);
            writer.Put(targetPlayerId);
            writer.Put(initiatorPlayerId);

            NetPeer[] peers = NetworkServer.PeerSnapshot;
            foreach (var client in peers)
            {
                BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.AdminChannel, writer.Length);
                client.Send(writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }

            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Sends current announce mode states to a newly connected peer.
        /// </summary>
        public static void SendAnnounceStateToPeer(NetPeer newPeer)
        {
            int[] announcePlayers = BasisSavedState.GetAllAnnounceModePlayers();
            if (announcePlayers.Length == 0) return;

            var writer = NetworkServer.RentWriter();
            foreach (int peerId in announcePlayers)
            {
                writer.Reset();
                new AdminRequest().Serialize(writer, AdminRequestMode.EnableAnnounceMode);
                writer.Put((ushort)peerId);
                writer.Put((ushort)peerId);
                BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.AdminChannel, writer.Length);
                newPeer.Send(writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }
            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Broadcasts an admin-granted shout mode state change to all clients via the AdminChannel.
        /// </summary>
        public static void BroadcastShoutModeState(ushort targetPlayerId, bool enabled, ushort initiatorPlayerId)
        {
            var writer = NetworkServer.RentWriter();
            AdminRequestMode mode = enabled ? AdminRequestMode.EnableShoutMode : AdminRequestMode.DisableShoutMode;
            new AdminRequest().Serialize(writer, mode);
            writer.Put(targetPlayerId);
            writer.Put(initiatorPlayerId);

            NetPeer[] peers = NetworkServer.PeerSnapshot;
            foreach (var client in peers)
            {
                BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.AdminChannel, writer.Length);
                client.Send(writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }

            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Sends current admin-granted shout mode states to a newly connected peer.
        /// </summary>
        public static void SendShoutStateToPeer(NetPeer newPeer)
        {
            int[] shoutPlayers = BasisSavedState.GetAllShoutModePlayers();
            if (shoutPlayers.Length == 0) return;

            var writer = NetworkServer.RentWriter();
            foreach (int peerId in shoutPlayers)
            {
                writer.Reset();
                new AdminRequest().Serialize(writer, AdminRequestMode.EnableShoutMode);
                writer.Put((ushort)peerId);
                writer.Put((ushort)peerId);
                BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.AdminChannel, writer.Length);
                newPeer.Send(writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }
            NetworkServer.ReturnWriter(writer);
        }

        public static void SendVoiceMessageToClients(ServerAudioSegmentMessage audioSegment, NetPeer sender, DeliveryMethod method)
        {
            if (!BasisSavedState.GetResolvedVoicePeers(sender, out List<NetPeer> targetPeers) || targetPeers == null)
            {
                return;
            }

            // Snapshot under the list lock so a concurrent rebuild or RemovePlayer
            // can't race our indexer reads. Lock is short — just a ref-array copy.
            NetPeer[] snapshot;
            int snapshotCount;
            lock (targetPeers)
            {
                snapshotCount = targetPeers.Count;
                if (snapshotCount == 0) return;
                snapshot = ArrayPool<NetPeer>.Shared.Rent(snapshotCount);
                targetPeers.CopyTo(0, snapshot, 0, snapshotCount);
            }

            audioSegment.playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)sender.Id,
            };

            bool largeId = sender.Id > byte.MaxValue;
            byte channel = largeId ? BasisNetworkCommons.VoiceLargeChannel : BasisNetworkCommons.VoiceChannel;

            // Serialize once into a byte[], then send raw to each peer — skips N writer→packet copies.
            // try/finally: this runs per voice packet, so a single throw mid-fanout must not eat the
            // rented snapshot and writer — lost writers permanently drain the shared pool.
            var writer = NetworkServer.RentWriter();
            try
            {
                audioSegment.Serialize(writer, largeId);
                int len = writer.Length;
                byte[] data = writer.Data;

                for (int i = 0; i < snapshotCount; i++)
                {
                    NetPeer client = snapshot[i];
                    if (client == null) continue;
                    if (BasisNetworkServer.BasisServerP2PBroker.IsP2POffloaded(sender.Id, client.Id))
                    {
                        continue;
                    }
                    client.SendUnreliableRawMerge(data, 0, len, channel);
                    BasisNetworkStatistics.RecordOutbound(channel, len);
                }
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
                ArrayPool<NetPeer>.Shared.Return(snapshot, clearArray: true);
            }
        }
        public static void UpdateVoiceReceivers(NetPacketReader Reader, NetPeer Peer, bool largeCount)
        {
            VoiceReceiversMessage VoiceReceiversMessage = new VoiceReceiversMessage();
            VoiceReceiversMessage.Deserialize(Reader, largeCount);
            Reader.Recycle();
            BasisSavedState.AddLastData(Peer, VoiceReceiversMessage);
        }

        /// <summary>
        /// Inverted mode: the message contains IDs to EXCLUDE. Everyone else is a recipient.
        /// </summary>
        public static void UpdateVoiceReceiversInverted(NetPacketReader Reader, NetPeer Peer, bool largeCount)
        {
            VoiceReceiversMessage excluded = new VoiceReceiversMessage();
            excluded.Deserialize(Reader, largeCount);
            Reader.Recycle();

            int senderId = Peer.Id;
            var peers = BasisSavedState.GetOrCreateResolvedList(senderId);

            lock (peers)
            {
                peers.Clear();

                if (excluded.Users == null || excluded.UsersLength == 0)
                {
                    // No exclusions: everyone except sender is a recipient
                    foreach (var kvp in NetworkServer.AuthenticatedPeers)
                    {
                        if (kvp.Key != senderId)
                            peers.Add(kvp.Value);
                    }
                }
                else
                {
                    // Reuse thread-local set to avoid allocation
                    if (_excludedSet == null)
                        _excludedSet = new HashSet<int>(64);
                    else
                        _excludedSet.Clear();
                    for (int i = 0; i < excluded.UsersLength; i++)
                        _excludedSet.Add(excluded.Users[i]);

                    foreach (var kvp in NetworkServer.AuthenticatedPeers)
                    {
                        if (kvp.Key != senderId && !_excludedSet.Contains(kvp.Key))
                            peers.Add(kvp.Value);
                    }
                }
            }

            excluded.ReturnPool();
        }

        /// <summary>
        /// Bitfield mode: each set bit at position N means playerID N is a recipient.
        /// Wire format: [byteCount: ushort][bitfield bytes]
        /// </summary>
        public static void UpdateVoiceReceiversBitfield(NetPacketReader Reader, NetPeer Peer)
        {
            int senderId = Peer.Id;

            if (Reader.AvailableBytes < sizeof(ushort))
            {
                Reader.Recycle();
                return;
            }

            ushort byteCount = Reader.GetUShort();

            if (byteCount == 0 || Reader.AvailableBytes < byteCount)
            {
                Reader.Recycle();
                return;
            }

            var peers = BasisSavedState.GetOrCreateResolvedList(senderId);

            lock (peers)
            {
                peers.Clear();

                for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
                {
                    byte b = Reader.GetByte();
                    if (b == 0) continue;

                    int baseId = byteIdx * 8;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if ((b & (1 << bit)) != 0)
                        {
                            int playerId = baseId + bit;
                            if (playerId != senderId && NetworkServer.AuthenticatedPeers.TryGetValue(playerId, out NetPeer found))
                            {
                                peers.Add(found);
                            }
                        }
                    }
                }
            }

            Reader.Recycle();
        }
        #endregion

        #region Spawn and Client List Handling
        public static void SendRemoteSpawnMessage(NetPeer authClient, ReadyMessage readyMessage)
        {
            ServerReadyMessage serverReadyMessage = LoadInitialState(authClient, readyMessage);
            NotifyExistingClients(serverReadyMessage, authClient);
            SendClientListToNewClient(authClient, readyMessage.localAvatarSyncMessage);
        }

        public static ServerReadyMessage LoadInitialState(NetPeer authClient, ReadyMessage readyMessage)
        {
            ServerReadyMessage serverReadyMessage = new ServerReadyMessage
            {
                localReadyMessage = readyMessage,
                playerIdMessage = new PlayerIdMessage()
                {
                    playerID = (ushort)authClient.Id
                }
            };
            BasisServerReductionSystemEvents.AddMessage(authClient, readyMessage.localAvatarSyncMessage, 0);
            BasisSavedState.AddLastData(authClient, readyMessage);
            return serverReadyMessage;
        }
        /// <summary>
        /// notify existing clients about a new player
        /// </summary>
        /// <param name="serverSideSyncPlayerMessage"></param>
        /// <param name="authClient"></param>
        public static void NotifyExistingClients(ServerReadyMessage serverSideSyncPlayerMessage, NetPeer authClient)
        {
            NetDataWriter Writer = NetworkServer.RentWriter();
            try
            {
                serverSideSyncPlayerMessage.Serialize(Writer);
                if (!NetworkServer.CheckValidated(Writer))
                {
                    return;
                }
                JoinBroadcast.Enqueue(JoinBroadcast.RegisteredSeqFor(authClient.Id), authClient.Id, Writer.CopyData());
            }
            finally
            {
                NetworkServer.ReturnWriter(Writer);
            }
        }
        /// <summary>
        /// send everyone to the new client
        /// </summary>
        /// <param name="authClient"></param>
        /// <summary>
        /// Tells a joining client about every player already present, batched into compressed runs
        /// rather than one packet per player. See ServerReadyBatchMessage for why the compression sits
        /// at the batch level and not inside each avatar record.
        /// </summary>
        public static void SendClientListToNewClient(NetPeer authClient, LocalAvatarSyncMessage joinerPose)
        {
            try
            {
                // The joiner's own position, taken from the pose it just sent. Used to pick each
                // player's quality tier; a zero here simply means everyone is measured from the origin,
                // which is the same answer the reduction system would reach a tick later.
                Basis.Scripts.Networking.Compression.Vector3 viewerPosition = default;
                // Every quality tier carries the position in the same int24-millimetre form, so
                // this decodes correctly whatever tier the joiner's pose arrived on. A short/absent
                // payload falls back to the origin (and therefore to High for everyone).
                if (joinerPose.array != null
                    && joinerPose.array.Length >= Basis.Network.Core.Compression.BasisAvatarBitPacking.WritePosition)
                {
                    byte[] poseBytes = joinerPose.array;
                    viewerPosition = Basis.Network.Core.Compression.BasisNetworkCompressionExtensions.ReadPosition(ref poseBytes);
                }

                NetPeer[] peers = NetworkServer.PeerSnapshot;
                NetDataWriter batchBuffer = NetworkServer.RentWriter();
                NetDataWriter sendWriter = NetworkServer.RentWriter();
                ushort batched = 0;
                List<(ushort count, byte[] payload)> readyBatches = new List<(ushort, byte[])>();

                foreach (var peer in peers)
                {
                    if (peer == authClient)
                    {
                        continue;
                    }
                    if (!CreateServerReadyMessageForPeer(peer, viewerPosition, out ServerReadyMessage Message))
                    {
                        continue;
                    }

                    Message.Serialize(batchBuffer);
                    batched++;

                    if (batchBuffer.Length >= ServerReadyBatchMessage.MaxPayloadBytes)
                    {
                        CollectReadyBatch(readyBatches, batchBuffer, ref batched);
                    }
                }

                CollectReadyBatch(readyBatches, batchBuffer, ref batched);

                // At scale the Deflate(Optimal) passes dominate this path (~16 batches of 32 KB
                // at 2000 players, all previously serial on the accept thread). The batches are
                // independent, so compress them in parallel and keep only the ordered sends serial.
                int batchTotal = readyBatches.Count;
                if (batchTotal == 1)
                {
                    SendReadyBatch(authClient, sendWriter, readyBatches[0].count,
                        ServerReadyBatchMessage.Compress(readyBatches[0].payload, out bool compressed), compressed);
                }
                else if (batchTotal > 1)
                {
                    byte[][] framed = new byte[batchTotal][];
                    bool[] framedCompressed = new bool[batchTotal];
                    Parallel.For(0, batchTotal, BasisServerReductionSystemEvents.SharedParallelOptions, i =>
                    {
                        framed[i] = ServerReadyBatchMessage.Compress(readyBatches[i].payload, out bool compressed);
                        framedCompressed[i] = compressed;
                    });
                    for (int i = 0; i < batchTotal; i++)
                    {
                        SendReadyBatch(authClient, sendWriter, readyBatches[i].count, framed[i], framedCompressed[i]);
                    }
                }

                NetworkServer.ReturnWriter(sendWriter);
                NetworkServer.ReturnWriter(batchBuffer);
            }
            catch (Exception ex)
            {
                BNL.LogError($"Failed to send client list: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void CollectReadyBatch(List<(ushort count, byte[] payload)> readyBatches, NetDataWriter batchBuffer, ref ushort batched)
        {
            if (batched == 0)
            {
                return;
            }
            readyBatches.Add((batched, batchBuffer.CopyData()));
            batchBuffer.Reset();
            batched = 0;
        }

        private static void SendReadyBatch(NetPeer authClient, NetDataWriter sendWriter, ushort count, byte[] framed, bool compressed)
        {
            ServerReadyBatchMessage batch = new ServerReadyBatchMessage { Count = count };
            sendWriter.Reset();
            batch.SerializePreCompressed(sendWriter, framed, compressed);
            NetworkServer.TrySend(authClient, sendWriter, BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, DeliveryMethod.ReliableOrdered);
        }
        /// <param name="viewerPosition">Where the joining player is. Selects the quality tier for
        /// <paramref name="peer"/>, exactly as the steady-state send loop would.</param>
        private static bool CreateServerReadyMessageForPeer(NetPeer peer, Basis.Scripts.Networking.Compression.Vector3 viewerPosition, out ServerReadyMessage ServerReadyMessage)
        {
            try
            {
                ClientAvatarChangeMessage changeState;
                bool haveRecord = BasisSavedState.GetLastAvatarChangeState(peer, out changeState);
                bool haveAvatar = haveRecord && changeState.byteArray != null;
                if (!haveAvatar)
                {
                    BNL.Log($"No avatar state yet for peer {peer.Id}; sending placeholder spawn so the remote player is created on the joining client.");
                    changeState = new ClientAvatarChangeMessage
                    {
                        loadMode = 0,
                        byteArray = null,
                        LocalAvatarIndex = 0,
                        // Carry the fit through even with no avatar yet: a body-fit update can land
                        // before the avatar change (recalibration mid-load), and dropping it here would
                        // leave this joiner rendering authored proportions until the next recalibration.
                        ArmScale = haveRecord ? changeState.ArmScale : 1f,
                        LegScale = haveRecord ? changeState.LegScale : 1f,
                        TorsoScale = haveRecord ? changeState.TorsoScale : 1f,
                    };
                }

                int id = peer.Id;
                LocalAvatarSyncMessage syncState;
                // Distance-tiered: a joiner gets the same quality for this player that the reduction
                // system would pick on its next tick, instead of a full High payload for everyone in
                // the instance. At crowd scale almost everyone is past the VeryLow threshold.
                if (BasisServerReductionSystemEvents.TryGetJoinSnapshot(viewerPosition, id, out LocalAvatarSyncMessage tiered))
                {
                    syncState = tiered;
                }
                else
                {
                    syncState = new LocalAvatarSyncMessage
                    {
                        DataQualityLevel = (byte)Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality.High,
                        array = new byte[NetworkServer.HighQualityLength],
                        AdditionalAvatarDatas = null,
                        AdditionalAvatarDataSize = 0,
                        LinkedAvatarIndex = 0
                    };
                    // Optionally log fallback
                    // BNL.LogError("Unable to get Last Player Avatar Data! Using Error Fallback");
                }
                // Meta Data
                if (!BasisSavedState.GetLastPlayerMetaData(peer, out var metaData))
                {
                    metaData = new ClientMetaDataMessage
                    {
                        playerDisplayName = "Error",
                        playerUUID = string.Empty,
                        playerPlatform = string.Empty
                    };
                    BNL.LogError("Unable to get Last Player Meta Data! Using Error Fallback");
                }

                // Construct ServerReadyMessage
                ServerReadyMessage = new ServerReadyMessage
                {
                    localReadyMessage = new ReadyMessage
                    {
                        localAvatarSyncMessage = syncState,
                        clientAvatarChangeMessage = changeState,
                        playerMetaDataMessage = metaData
                    },
                    playerIdMessage = new PlayerIdMessage
                    {
                        playerID = (ushort)peer.Id
                    }
                };

                return true;
            }
            catch (Exception ex)
            {
                BNL.LogError($"Failed to create ServerReadyMessage for peer {peer.Id}: {ex.Message}");
                ServerReadyMessage = new ServerReadyMessage();
                return false;
            }
        }
        #endregion
        #region Network ID Generation
        public static void NetIDAssign(NetPacketReader Reader, NetPeer Peer)
        {
            NetIDMessage ServerUniqueIDMessage = new NetIDMessage();
            ServerUniqueIDMessage.Deserialize(Reader);
            Reader.Recycle();
            //returns a message with the ushort back to the client, or it sends it to everyone if its new.
            BasisNetworkIDDatabase.AddOrFindNetworkID(Peer, ServerUniqueIDMessage.playerID);
            //we need to convert the string int a  ushort.
        }
        public static void LoadResource(NetPacketReader Reader, NetPeer Peer,string UUID)
        {
            LocalLoadResource LocalLoadResource = new LocalLoadResource();

            if (NetworkServer.AuthIdentity.NetIDToUUID(Peer, out string uuid) == false)
            {
                BNL.LogError($"User UUID not found for peer: {Peer}");
                return;
            }
            LocalLoadResource.Deserialize(Reader);
            bool isPrivileged = PermissionIntegration.HasValidRequirement(Peer, PermNodes.protection);
            LocalLoadResource.IsAdminLocked = isPrivileged;
            LocalLoadResource.UUIDOfCreator = UUID;
            if (!isPrivileged)
            {
                LocalLoadResource.Persist = false;
                LocalLoadResource.Static = false;
                LocalLoadResource.StaticAdminLocked = false;
            }
            Reader.Recycle();

            switch (LocalLoadResource.Mode)
            {
                case 0:
                    if (BasisNetworkServer.Security.BasisGlobalLockManager.PropsLocked &&
                        !PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLockBypassProp))
                    {
                        BNL.Log($"Prop loading is globally disabled. Rejected request from {UUID}");
                        BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(Peer, "Prop loading is currently disabled by an admin.");
                        return;
                    }
                    if (PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLoadProp) == false)
                    {
                        BNL.LogError($"Invalid Request To Load Gameobject From {UUID}");
                        return;
                    }
                    break;
                case 1:
                    if (BasisNetworkServer.Security.BasisGlobalLockManager.WorldsLocked &&
                        !PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLockBypassWorld))
                    {
                        BNL.Log($"World loading is globally disabled. Rejected request from {UUID}");
                        BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(Peer, "World loading is currently disabled by an admin.");
                        return;
                    }
                    if (PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLoadWorld) == false)
                    {
                        BNL.LogError($"Invalid Request To Load Scene From {UUID}");
                        return;
                    }
                    break;
                default:
                    BNL.LogError($"Missing Mode {LocalLoadResource.Mode}");
                    return;
            }
            // Route based on load strategy
            switch (LocalLoadResource.LoadStrategy)
            {
                case 0:
                    BasisNetworkResourceManagement.LoadResource(LocalLoadResource);
                    break;
                case 2: // Synchronized
                    BasisNetworkPreloadResourceManagement.StartSynchronizedLoad(LocalLoadResource);
                    break;
                case 3: // Predownload only - tell everyone to cache it; do not register or spawn
                    BasisNetworkResourceManagement.PredownloadResource(LocalLoadResource);
                    break;
                default:
                    BNL.LogError("Falling Back to Resource Load, Unsupported Load Strategy");
                    BasisNetworkResourceManagement.LoadResource(LocalLoadResource);
                    break;
            }
        }
        public static void HandlePreloadReady(NetPacketReader Reader, NetPeer Peer)
        {
            PreloadReadyMessage readyMsg = new PreloadReadyMessage();
            readyMsg.Deserialize(Reader);
            Reader.Recycle();
            BasisNetworkPreloadResourceManagement.HandleClientReady(readyMsg.LoadedNetID, Peer.Id, readyMsg.IsReady);
        }
        public static void UnloadResource(NetPacketReader Reader, NetPeer Peer)
        {
            UnLoadResource UnLoadResource = new UnLoadResource();
            UnLoadResource.Deserialize(Reader);
            Reader.Recycle();

            // Tier comes from the stored record, not the packet: Mode is client-supplied and is
            // never compared against the target, so a user denied world-unload could send Mode 0
            // and have the prop permission checked instead.
            if (!BasisNetworkResourceManagement.UshortNetworkDatabase.TryGetValue(UnLoadResource.LoadedNetID, out LocalLoadResource TargetResource))
            {
                BNL.LogError($"Trying to unload an object that does not exist! ID Provided was [{UnLoadResource.LoadedNetID}]");
                return;
            }

            switch (TargetResource.Mode)
            {
                case 0:
                    if (PermissionIntegration.HasValidRequirement(Peer, PermNodes.ResourceUnloadProp) == false)
                    {
                        return;
                    }
                    break;
                case 1:
                    if (PermissionIntegration.HasValidRequirement(Peer, PermNodes.ResourceUnloadWorld) == false)
                    {
                        return;
                    }
                    break;
                default:
                    BNL.LogError($"Missing Mode {UnLoadResource.Mode}");
                    return;
            }

            //returns a message with the ushort back to the client, or it sends it to everyone if its new.
            BasisNetworkResourceManagement.UnloadResource(UnLoadResource, Peer);
            //we need to convert the string int a  ushort.
        }
        public static void HandleModifyResource(NetPacketReader Reader, NetPeer Peer)
        {
            ModifyResource modifyResource = new ModifyResource();
            modifyResource.Deserialize(Reader);
            Reader.Recycle();
            // Authorization (creator or moderator) is enforced inside SetStatic.
            BasisNetworkResourceManagement.SetStatic(modifyResource, Peer);
        }
        #endregion
    }
}
