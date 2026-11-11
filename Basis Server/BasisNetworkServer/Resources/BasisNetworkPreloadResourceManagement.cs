using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static SerializableBasis;

/// <summary>
/// Server-side tracking for synchronized resource loads.
/// Tracks which clients have reported readiness and triggers the spawn
/// signal when all are ready or the timeout expires.
/// </summary>
public static class BasisNetworkPreloadResourceManagement
{
    /// <summary>
    /// Timeout for synchronized loads. After this duration the server sends
    /// the spawn signal regardless of how many clients have reported.
    /// </summary>
    public static readonly TimeSpan SynchronizedTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Active synchronized load sessions, keyed by LoadedNetID.
    /// </summary>
    public static readonly ConcurrentDictionary<string, SyncLoadSession> ActiveSessions = new();

    public class SyncLoadSession
    {
        public LocalLoadResource Resource;
        public HashSet<int> ReadyPeers = new();
        public HashSet<int> FailedPeers = new();
        public DateTime StartTimeUtc;
        public CancellationTokenSource TimeoutCts;

        /// <summary>
        /// Total number of connected peers when this session started.
        /// </summary>
        public int TotalPeerCount;
        public bool IsComplete => ReadyPeers.Count + FailedPeers.Count >= TotalPeerCount;
    }

    /// <summary>
    /// Called when the server receives a LoadResource with LoadStrategy = 2 (Synchronized).
    /// Broadcasts the preload request to all clients and starts tracking readiness.
    /// </summary>
    public static void StartSynchronizedLoad(LocalLoadResource resource)
    {
        string netId = resource.LoadedNetID;

        if (ActiveSessions.ContainsKey(netId))
        {
            BNL.LogError($"PreloadResourceManagement: Session already exists for {netId}");
            return;
        }

        if (!BasisNetworkResourceManagement.CanCreatorLoadMore(resource.UUIDOfCreator))
        {
            BNL.LogError($"PreloadResourceManagement: Creator {resource.UUIDOfCreator} reached the per-player loaded-object limit; dropping synchronized load {netId}.");
            return;
        }

        Basis.Network.Server.Generic.BasisContentKeyRelay.TakeCustody(ref resource);
        var peerSnapshot = NetworkServer.PeerSnapshot;
        int peerCount = peerSnapshot.Length;

        var session = new SyncLoadSession
        {
            Resource = resource,
            StartTimeUtc = DateTime.UtcNow,
            TotalPeerCount = peerCount,
            TimeoutCts = new CancellationTokenSource(),
        };

        if (!ActiveSessions.TryAdd(netId, session))
        {
            BNL.LogError($"PreloadResourceManagement: Failed to add session for {netId}");
            return;
        }

        BNL.Log($"PreloadResourceManagement: Starting synchronized load for {netId}, {peerCount} peers");
        BasisNetworkPredownloadManagement.Fulfil(resource.CombinedURL, resource.Mode);

        // Store in the main resource database too
        if (BasisNetworkResourceManagement.UshortNetworkDatabase.TryAdd(netId, resource))
        {
            BasisNetworkResourceManagement.NoteResourceAdded(resource.UUIDOfCreator);
        }

        // Broadcast the load resource to all clients (they will see LoadStrategy = 2
        // and handle it as a synchronized preload)
        NetDataWriter writer = NetworkServer.RentWriter();
        resource.Serialize(writer);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.LoadResourceChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);

        // No peers: complete immediately rather than waiting for the 5-minute timeout
        if (peerCount == 0)
        {
            BNL.Log($"PreloadResourceManagement: No peers connected, completing {netId} immediately");
            session.TimeoutCts.Cancel();
            BroadcastSpawnSignal(netId);
            return;
        }

        // Start timeout task
        _ = RunTimeoutAsync(netId, session);
    }

    /// <summary>
    /// Called when the server receives a PreloadReady message from a client.
    /// </summary>
    public static void HandleClientReady(string loadedNetId, int peerId, bool isReady)
    {
        if (!ActiveSessions.TryGetValue(loadedNetId, out SyncLoadSession session))
        {
            BNL.LogError($"PreloadResourceManagement: Received ready from peer {peerId} for unknown session {loadedNetId}");
            return;
        }

        if (isReady)
        {
            session.ReadyPeers.Add(peerId);
            BNL.Log($"PreloadResourceManagement: Peer {peerId} ready for {loadedNetId} ({session.ReadyPeers.Count + session.FailedPeers.Count}/{session.TotalPeerCount})");
        }
        else
        {
            session.FailedPeers.Add(peerId);
            BNL.Log($"PreloadResourceManagement: Peer {peerId} FAILED for {loadedNetId} ({session.ReadyPeers.Count + session.FailedPeers.Count}/{session.TotalPeerCount})");
        }

        if (session.IsComplete)
        {
            BNL.Log($"PreloadResourceManagement: All peers reported for {loadedNetId}, sending spawn signal");
            session.TimeoutCts.Cancel();
            BroadcastSpawnSignal(loadedNetId);
        }
    }

    /// <summary>
    /// Runs the timeout for a synchronized load session.
    /// If not all clients have reported by the timeout, sends the spawn signal anyway.
    /// </summary>
    private static async Task RunTimeoutAsync(string netId, SyncLoadSession session)
    {
        try
        {
            await Task.Delay(SynchronizedTimeout, session.TimeoutCts.Token);

            // Timeout reached - send spawn signal regardless
            BNL.Log($"PreloadResourceManagement: Timeout reached for {netId}. Ready: {session.ReadyPeers.Count}, Failed: {session.FailedPeers.Count}, Total: {session.TotalPeerCount}");
            BroadcastSpawnSignal(netId);
        }
        catch (TaskCanceledException)
        {
            // Session completed before timeout - normal
        }
    }

    /// <summary>
    /// Broadcasts the spawn signal to all connected clients for a synchronized load.
    /// Also broadcasts unload messages for existing scenes through the normal unload path
    /// so the server tracking stays consistent. The client-side HandleSpawnPreloaded
    /// also unloads scenes locally as a safety net against message ordering races.
    /// </summary>
    private static void BroadcastSpawnSignal(string loadedNetId)
    {
        if (!ActiveSessions.TryRemove(loadedNetId, out SyncLoadSession session))
        {
            return;
        }

        session.TimeoutCts.Dispose();

        var peerSnapshot = NetworkServer.PeerSnapshot;

        // Only unload existing scenes when the synchronized resource is itself a scene.
        // Props (Mode == 0) should never cause scene unloads.
        // Exclude loadedNetId — that is the scene being switched TO; removing it would
        // race against the SpawnPreloaded signal on the client.
        if (session.Resource.Mode == 1)
        {
            UnloadAllSceneResources(peerSnapshot, loadedNetId);
        }

        SpawnPreloadedMessage spawnMsg = new SpawnPreloadedMessage
        {
            LoadedNetID = loadedNetId,
        };

        NetDataWriter writer = NetworkServer.RentWriter();
        spawnMsg.Serialize(writer);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.SpawnPreloadedChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);

        BNL.Log($"PreloadResourceManagement: Spawn signal sent for {loadedNetId}");
    }

    /// <summary>
    /// Unloads all scene-type resources (Mode == 1) from the server database
    /// and broadcasts unload messages to all clients through the normal unload channel.
    /// </summary>
    private static void UnloadAllSceneResources(NetPeer[] peerSnapshot, string excludeNetId = null)
    {
        var sceneResources = BasisNetworkResourceManagement.UshortNetworkDatabase.Values
            .Where(r => r.Mode == 1 && r.LoadedNetID != excludeNetId)
            .ToArray();

        if (sceneResources.Length == 0) return;

        BNL.Log($"PreloadResourceManagement: Unloading {sceneResources.Length} existing scene(s) before synchronized spawn");

        NetDataWriter writer = NetworkServer.RentWriter();
        foreach (var scene in sceneResources)
        {
            if (BasisNetworkResourceManagement.UshortNetworkDatabase.TryRemove(scene.LoadedNetID, out _))
            {
                BasisNetworkResourceManagement.NoteResourceRemoved(scene.UUIDOfCreator);
            }

            UnLoadResource unload = new UnLoadResource
            {
                LoadedNetID = scene.LoadedNetID,
                Mode = 1,
            };
            writer.Reset();
            unload.Serialize(writer);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.UnloadResourceChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);

            BNL.Log($"PreloadResourceManagement: Unloaded scene {scene.LoadedNetID}");
        }
        NetworkServer.ReturnWriter(writer);
    }

    /// <summary>
    /// Removes a disconnected peer from all active synchronized load sessions.
    /// Decrements the expected peer count and triggers the spawn signal if
    /// all remaining peers have already reported.
    /// </summary>
    public static void RemovePeer(int peerId)
    {
        List<string> completedSessions = null;

        foreach (var kvp in ActiveSessions)
        {
            var session = kvp.Value;
            session.ReadyPeers.Remove(peerId);
            session.FailedPeers.Remove(peerId);

            if (session.TotalPeerCount > 0)
            {
                session.TotalPeerCount--;
            }

            if (session.TotalPeerCount <= 0)
            {
                // No peers left, just clean up
                session.TimeoutCts?.Cancel();
                session.TimeoutCts?.Dispose();
                ActiveSessions.TryRemove(kvp.Key, out _);
            }
            else if (session.IsComplete)
            {
                completedSessions ??= new List<string>();
                completedSessions.Add(kvp.Key);
            }
        }

        if (completedSessions != null)
        {
            foreach (var netId in completedSessions)
            {
                if (ActiveSessions.TryGetValue(netId, out var session))
                {
                    BNL.Log($"PreloadResourceManagement: All remaining peers reported for {netId} after peer {peerId} disconnected, sending spawn signal");
                    session.TimeoutCts.Cancel();
                    BroadcastSpawnSignal(netId);
                }
            }
        }
    }

    /// <summary>
    /// Cleans up all active sessions. Called on server reset.
    /// </summary>
    public static void Reset()
    {
        foreach (var kvp in ActiveSessions)
        {
            kvp.Value.TimeoutCts?.Cancel();
            kvp.Value.TimeoutCts?.Dispose();
        }
        ActiveSessions.Clear();
    }
}

public static class BasisNetworkPredownloadManagement
{
    public const int MaxSessions = 16;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);
    public static Func<DateTime> Clock = () => DateTime.UtcNow;
    sealed class Session
    {
        public LocalLoadResource Resource;
        public ushort Requester;
        public DateTime StartedUtc;
        public bool Dirty;
        public readonly HashSet<int> Pending = new HashSet<int>(), Ready = new HashSet<int>(), Failed = new HashSet<int>();
        public bool Has(int peerId) => Pending.Contains(peerId) || Ready.Contains(peerId) || Failed.Contains(peerId);
        public bool Drop(int peerId) => Pending.Remove(peerId) | Ready.Remove(peerId) | Failed.Remove(peerId);
    }
    struct Outgoing
    {
        public string NetId;
        public ushort Requester, Ready, Failed, Total;
        public bool Closed;
    }
    static readonly object gate = new object();
    static readonly List<Session> sessions = new List<Session>();
    static readonly List<Outgoing> closing = new List<Outgoing>();
    static ushort messageId;
    static bool registered;
    static Timer ticker;
    public static int SessionCount
    {
        get
        {
            lock (gate) return sessions.Count;
        }
    }
    public static void Register()
    {
        if (registered) return;
        registered = true;
        messageId = BasisServerMessageRegistry.RegisterServerPlugin(BasisPredownloadProtocol.MessageName, DeliveryMethod.ReliableOrdered, Handle);
        BasisServerMessageRegistry.PeerSubscribed += OnSubscribed;
        ticker ??= new Timer(_ => Flush(), null, 250, 250);
    }
    public static bool TryGetCounts(string loadedNetId, out int ready, out int failed, out int total)
    {
        lock (gate)
        {
            Session session = Find(loadedNetId);
            ready = session?.Ready.Count ?? 0;
            failed = session?.Failed.Count ?? 0;
            total = session == null ? 0 : ready + failed + session.Pending.Count;
            return session != null;
        }
    }
    public static void Start(LocalLoadResource resource, int requesterPeerId, NetPeer[] peers)
    {
        if (!registered || string.IsNullOrEmpty(resource.LoadedNetID)) return;
        Session session = new Session
        {
            Resource = resource,
            Requester = requesterPeerId < 0 ? BasisPredownloadProtocol.ServerRequester : (ushort)requesterPeerId,
            StartedUtc = Clock(),
            Dirty = true,
        };
        for (int index = 0; index < peers.Length; index++)
        {
            NetPeer peer = peers[index];
            if (peer != null && BasisServerMessageRegistry.HasSubscribed(peer.Id, messageId)) session.Pending.Add(peer.Id);
        }
        lock (gate)
        {
            for (int index = sessions.Count - 1; index >= 0; index--)
            {
                Session existing = sessions[index];
                if (existing.Resource.LoadedNetID == resource.LoadedNetID || (existing.Resource.Mode == resource.Mode && existing.Resource.CombinedURL == resource.CombinedURL)) Close(index);
            }
            while (sessions.Count >= MaxSessions) Close(0);
            sessions.Add(session);
        }
    }
    public static void Fulfil(string url, byte mode)
    {
        if (string.IsNullOrEmpty(url)) return;
        lock (gate)
        {
            for (int index = sessions.Count - 1; index >= 0; index--)
            {
                if (sessions[index].Resource.Mode == mode && sessions[index].Resource.CombinedURL == url) Close(index);
            }
        }
    }
    public static void Report(int peerId, string loadedNetId, bool ready)
    {
        lock (gate)
        {
            Session session = Find(loadedNetId);
            if (session == null || !session.Has(peerId)) return;
            session.Drop(peerId);
            (ready ? session.Ready : session.Failed).Add(peerId);
            session.Dirty = true;
        }
    }
    public static void RemovePeer(int peerId)
    {
        lock (gate)
        {
            for (int index = 0; index < sessions.Count; index++)
            {
                if (sessions[index].Drop(peerId)) sessions[index].Dirty = true;
            }
        }
    }
    public static void Reset()
    {
        lock (gate)
        {
            for (int index = sessions.Count - 1; index >= 0; index--) Close(index);
        }
        Flush();
    }
    public static void Flush()
    {
        List<Outgoing> outgoing = null;
        lock (gate)
        {
            DateTime now = Clock();
            for (int index = sessions.Count - 1; index >= 0; index--)
            {
                if (now - sessions[index].StartedUtc >= SessionLifetime) Close(index);
            }
            if (closing.Count > 0)
            {
                outgoing = new List<Outgoing>(closing);
                closing.Clear();
            }
            for (int index = 0; index < sessions.Count; index++)
            {
                Session session = sessions[index];
                if (!session.Dirty) continue;
                session.Dirty = false;
                outgoing ??= new List<Outgoing>();
                outgoing.Add(Snapshot(session));
            }
        }
        if (outgoing == null) return;
        NetPeer[] peers = NetworkServer.PeerSnapshot;
        NetDataWriter writer = NetworkServer.RentWriter();
        for (int index = 0; index < outgoing.Count; index++)
        {
            writer.Reset();
            Write(writer, outgoing[index]);
            for (int peerIndex = 0; peerIndex < peers.Length; peerIndex++)
            {
                NetPeer peer = peers[peerIndex];
                if (peer != null && BasisServerMessageRegistry.HasSubscribed(peer.Id, messageId)) NetworkServer.TrySend(peer, writer, BasisNetworkCommons.GetPluginChannelForDelivery(DeliveryMethod.ReliableOrdered), DeliveryMethod.ReliableOrdered);
            }
        }
        NetworkServer.ReturnWriter(writer);
    }
    static void OnSubscribed(NetPeer peer)
    {
        if (peer == null || !BasisServerMessageRegistry.HasSubscribed(peer.Id, messageId)) return;
        List<LocalLoadResource> replay = null;
        List<Outgoing> known = null;
        lock (gate)
        {
            for (int index = 0; index < sessions.Count; index++)
            {
                Session session = sessions[index];
                if (session.Has(peer.Id))
                {
                    known ??= new List<Outgoing>();
                    known.Add(Snapshot(session));
                    continue;
                }
                session.Pending.Add(peer.Id);
                session.Dirty = true;
                replay ??= new List<LocalLoadResource>();
                replay.Add(session.Resource);
            }
        }
        if (replay == null && known == null) return;
        NetDataWriter writer = NetworkServer.RentWriter();
        for (int index = 0; replay != null && index < replay.Count; index++)
        {
            writer.Reset();
            replay[index].Serialize(writer);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.LoadResourceChannel, DeliveryMethod.ReliableOrdered);
        }
        for (int index = 0; known != null && index < known.Count; index++)
        {
            writer.Reset();
            Write(writer, known[index]);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.GetPluginChannelForDelivery(DeliveryMethod.ReliableOrdered), DeliveryMethod.ReliableOrdered);
        }
        NetworkServer.ReturnWriter(writer);
    }
    static void Handle(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            if (reader.TryGetByte(out byte op) && op == BasisPredownloadProtocol.OpDone && reader.TryGetString(out string loadedNetId) && reader.TryGetBool(out bool ready)) Report(peer.Id, loadedNetId, ready);
        }
        finally
        {
            reader.Recycle();
        }
    }
    static Session Find(string loadedNetId)
    {
        for (int index = 0; index < sessions.Count; index++)
        {
            if (sessions[index].Resource.LoadedNetID == loadedNetId) return sessions[index];
        }
        return null;
    }
    static void Close(int index)
    {
        closing.Add(new Outgoing { NetId = sessions[index].Resource.LoadedNetID, Closed = true });
        sessions.RemoveAt(index);
    }
    static Outgoing Snapshot(Session session)
    {
        int ready = session.Ready.Count, failed = session.Failed.Count, total = ready + failed + session.Pending.Count;
        return new Outgoing { NetId = session.Resource.LoadedNetID, Requester = session.Requester, Ready = (ushort)Math.Min(ready, ushort.MaxValue), Failed = (ushort)Math.Min(failed, ushort.MaxValue), Total = (ushort)Math.Min(total, ushort.MaxValue) };
    }
    static void Write(NetDataWriter writer, Outgoing message)
    {
        writer.Put(messageId);
        writer.Put(message.Closed ? BasisPredownloadProtocol.OpClosed : BasisPredownloadProtocol.OpProgress);
        writer.Put(message.NetId);
        if (message.Closed) return;
        writer.Put(message.Requester);
        writer.Put(message.Ready);
        writer.Put(message.Failed);
        writer.Put(message.Total);
    }
}
