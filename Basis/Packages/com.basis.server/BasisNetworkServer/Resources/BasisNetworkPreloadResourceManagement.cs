using Basis.Network.Core;
using BasisPermissions;
using static BasisPermissions.PermissionManager;
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
    public static readonly TimeSpan SynchronizedTimeout = TimeSpan.FromMinutes(5);
    public static TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    public static Func<DateTime> Clock = () => DateTime.UtcNow;
    public sealed class SyncLoadSession
    {
        public LocalLoadResource Resource;
        public ushort Requester;
        public byte Swap;
        public DateTime StartTimeUtc;
        public bool Dirty;
        public readonly Dictionary<int, DateTime> Waiting = new Dictionary<int, DateTime>();
        public readonly HashSet<int> ReadyPeers = new HashSet<int>(), FailedPeers = new HashSet<int>(), SkippedPeers = new HashSet<int>();
        public int Tracked => Waiting.Count + ReadyPeers.Count + FailedPeers.Count + SkippedPeers.Count;
        public bool Drop(int peerId) => Waiting.Remove(peerId) | ReadyPeers.Remove(peerId) | FailedPeers.Remove(peerId) | SkippedPeers.Remove(peerId);
    }
    static readonly object gate = new object();
    static readonly Dictionary<string, SyncLoadSession> sessions = new Dictionary<string, SyncLoadSession>();
    public static bool IsActive(string loadedNetId)
    {
        lock (gate) return sessions.ContainsKey(loadedNetId);
    }
    public static bool TryGetCounts(string loadedNetId, out int ready, out int failed, out int waiting, out int skipped)
    {
        lock (gate)
        {
            bool found = sessions.TryGetValue(loadedNetId, out SyncLoadSession session);
            ready = found ? session.ReadyPeers.Count : 0;
            failed = found ? session.FailedPeers.Count : 0;
            waiting = found ? session.Waiting.Count : 0;
            skipped = found ? session.SkippedPeers.Count : 0;
            return found;
        }
    }
    public static void StartSynchronizedLoad(LocalLoadResource resource, int requesterPeerId = -1, byte swap = BasisPredownloadProtocol.SwapReplaceWorlds)
    {
        string netId = resource.LoadedNetID;
        if (IsActive(netId))
        {
            BNL.LogError($"PreloadResourceManagement: Session already exists for {netId}");
            return;
        }
        if (!BasisNetworkResourceManagement.CanCreatorLoadMore(resource.UUIDOfCreator))
        {
            BNL.LogError($"PreloadResourceManagement: Creator {resource.UUIDOfCreator} reached the per-player loaded-object limit; dropping synchronized load {netId}.");
            return;
        }
        resource.LoadStrategy = 2;
        Basis.Network.Server.Generic.BasisContentKeyRelay.TakeCustody(ref resource);
        NetPeer[] peerSnapshot = NetworkServer.PeerSnapshot;
        DateTime now = Clock();
        SyncLoadSession session = new SyncLoadSession
        {
            Resource = resource,
            Requester = requesterPeerId < 0 ? BasisPredownloadProtocol.ServerRequester : (ushort)requesterPeerId,
            Swap = swap,
            StartTimeUtc = now,
            Dirty = true,
        };
        for (int index = 0; index < peerSnapshot.Length; index++)
        {
            if (peerSnapshot[index] != null) session.Waiting[peerSnapshot[index].Id] = now;
        }
        lock (gate)
        {
            if (!sessions.TryAdd(netId, session))
            {
                BNL.LogError($"PreloadResourceManagement: Failed to add session for {netId}");
                return;
            }
        }
        BNL.Log($"PreloadResourceManagement: Starting synchronized load for {netId}, {session.Waiting.Count} peers");
        BasisNetworkPredownloadManagement.Fulfil(resource.CombinedURL, resource.Mode);
        if (BasisNetworkResourceManagement.UshortNetworkDatabase.TryAdd(netId, resource))
        {
            BasisNetworkResourceManagement.NoteResourceAdded(resource.UUIDOfCreator);
        }
        NetDataWriter writer = NetworkServer.RentWriter();
        resource.Serialize(writer);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.LoadResourceChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
        if (session.Waiting.Count == 0) Finish(netId, "no peers connected");
    }
    public static bool AddLateJoiner(string loadedNetId, int peerId)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(loadedNetId, out SyncLoadSession session)) return false;
            session.Drop(peerId);
            session.Waiting[peerId] = Clock();
            session.Dirty = true;
            return true;
        }
    }
    public static void HandleClientReady(string loadedNetId, int peerId, bool isReady)
    {
        bool complete;
        lock (gate)
        {
            if (!sessions.TryGetValue(loadedNetId, out SyncLoadSession session)) return;
            session.Drop(peerId);
            (isReady ? session.ReadyPeers : session.FailedPeers).Add(peerId);
            session.Dirty = true;
            complete = session.Waiting.Count == 0;
            BNL.Log($"PreloadResourceManagement: Peer {peerId} {(isReady ? "ready" : "FAILED")} for {loadedNetId} ({session.ReadyPeers.Count} ready, {session.FailedPeers.Count} failed, {session.Waiting.Count} waiting, {session.SkippedPeers.Count} skipped)");
        }
        if (complete) Finish(loadedNetId, "everyone still required has reported");
    }
    public static void HandleStatus(int peerId, string loadedNetId)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(loadedNetId, out SyncLoadSession session)) return;
            if (session.SkippedPeers.Remove(peerId))
            {
                session.Dirty = true;
                BNL.Log($"PreloadResourceManagement: Peer {peerId} is responding again for {loadedNetId}, waiting for them");
            }
            else if (!session.Waiting.ContainsKey(peerId)) return;
            session.Waiting[peerId] = Clock();
        }
    }
    public static void Touch(int peerId)
    {
        lock (gate)
        {
            DateTime now = Clock();
            foreach (SyncLoadSession session in sessions.Values)
            {
                if (session.Waiting.ContainsKey(peerId)) session.Waiting[peerId] = now;
            }
        }
    }
    public static void Tick()
    {
        List<(string netId, string reason)> finished = null;
        lock (gate)
        {
            if (sessions.Count == 0) return;
            DateTime now = Clock();
            List<int> silent = null;
            foreach (KeyValuePair<string, SyncLoadSession> pair in sessions)
            {
                SyncLoadSession session = pair.Value;
                if (now - session.StartTimeUtc >= SynchronizedTimeout)
                {
                    finished ??= new List<(string, string)>();
                    finished.Add((pair.Key, "timeout reached"));
                    continue;
                }
                silent?.Clear();
                foreach (KeyValuePair<int, DateTime> waiting in session.Waiting)
                {
                    if (now - waiting.Value < StatusTimeout || !BasisNetworkPredownloadManagement.Supports(waiting.Key)) continue;
                    silent ??= new List<int>();
                    silent.Add(waiting.Key);
                }
                for (int index = 0; silent != null && index < silent.Count; index++)
                {
                    session.Waiting.Remove(silent[index]);
                    session.SkippedPeers.Add(silent[index]);
                    session.Dirty = true;
                    BNL.Log($"PreloadResourceManagement: Peer {silent[index]} sent no status for {StatusTimeout.TotalSeconds:0}s, no longer waiting for them on {pair.Key}");
                }
                if (session.Waiting.Count == 0)
                {
                    finished ??= new List<(string, string)>();
                    finished.Add((pair.Key, "everyone still required has reported"));
                }
                else if (session.Dirty)
                {
                    session.Dirty = false;
                    BasisNetworkPredownloadManagement.Enqueue(Snapshot(session));
                }
            }
        }
        for (int index = 0; finished != null && index < finished.Count; index++) Finish(finished[index].netId, finished[index].reason);
    }
    public static void Cancel(string loadedNetId)
    {
        lock (gate)
        {
            if (!sessions.Remove(loadedNetId)) return;
            BasisNetworkPredownloadManagement.Enqueue(new BasisNetworkPredownloadManagement.Outgoing { NetId = loadedNetId, Closed = true });
        }
        BNL.Log($"PreloadResourceManagement: Synchronized load {loadedNetId} was removed before it spawned");
    }
    static void Finish(string loadedNetId, string reason)
    {
        SyncLoadSession session;
        lock (gate)
        {
            if (!sessions.Remove(loadedNetId, out session)) return;
            session.SkippedPeers.UnionWith(session.Waiting.Keys);
            session.Waiting.Clear();
            BasisNetworkPredownloadManagement.Enqueue(Snapshot(session));
            BasisNetworkPredownloadManagement.Enqueue(new BasisNetworkPredownloadManagement.Outgoing { NetId = loadedNetId, Closed = true });
        }
        BNL.Log($"PreloadResourceManagement: Spawning {loadedNetId}, {reason}. Ready: {session.ReadyPeers.Count}, Failed: {session.FailedPeers.Count}, Skipped: {session.SkippedPeers.Count}");
        BroadcastSpawnSignal(session);
    }
    static BasisNetworkPredownloadManagement.Outgoing Snapshot(SyncLoadSession session)
    {
        int ready = session.ReadyPeers.Count, failed = session.FailedPeers.Count;
        return new BasisNetworkPredownloadManagement.Outgoing
        {
            NetId = session.Resource.LoadedNetID,
            Requester = session.Requester,
            Ready = (ushort)Math.Min(ready, ushort.MaxValue),
            Failed = (ushort)Math.Min(failed, ushort.MaxValue),
            Total = (ushort)Math.Min(ready + failed + session.Waiting.Count, ushort.MaxValue),
            Skipped = (ushort)Math.Min(session.SkippedPeers.Count, ushort.MaxValue),
        };
    }
    static void BroadcastSpawnSignal(SyncLoadSession session)
    {
        string loadedNetId = session.Resource.LoadedNetID;
        NetPeer[] peerSnapshot = NetworkServer.PeerSnapshot;
        if (session.Swap == BasisPredownloadProtocol.SwapClearAll)
        {
            UnloadAllContent(session);
        }
        else if (session.Swap == BasisPredownloadProtocol.SwapReplaceWorlds && session.Resource.Mode == 1)
        {
            UnloadAllSceneResources(peerSnapshot, loadedNetId);
        }
        SpawnPreloadedMessage spawnMsg = new SpawnPreloadedMessage
        {
            LoadedNetID = loadedNetId,
            Swap = session.Swap,
        };
        NetDataWriter writer = NetworkServer.RentWriter();
        spawnMsg.Serialize(writer);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.SpawnPreloadedChannel, peerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
        BNL.Log($"PreloadResourceManagement: Spawn signal sent for {loadedNetId}");
    }
    static void UnloadAllContent(SyncLoadSession session)
    {
        string requester = session.Resource.UUIDOfCreator;
        bool moderator = !string.IsNullOrEmpty(requester) && PermissionIntegration.HasValidRequirement(requester, PermNodes.protection);
        bool removalLocked = BasisNetworkServer.Security.BasisGlobalLockManager.ContentRemovalLocked;
        int removed = 0, kept = 0;
        foreach (LocalLoadResource resource in BasisNetworkResourceManagement.UshortNetworkDatabase.Values.ToArray())
        {
            if (resource.Mode > 1 || resource.LoadedNetID == session.Resource.LoadedNetID) continue;
            if (!moderator && (resource.IsAdminLocked || (removalLocked && resource.UUIDOfCreator != requester)))
            {
                kept++;
                continue;
            }
            if (BasisNetworkResourceManagement.UnloadResource(new UnLoadResource { LoadedNetID = resource.LoadedNetID, Mode = resource.Mode })) removed++;
        }
        BNL.Log($"PreloadResourceManagement: Cleared {removed} loaded item(s) before spawning {session.Resource.LoadedNetID}, {kept} protected item(s) kept");
    }
    static void UnloadAllSceneResources(NetPeer[] peerSnapshot, string excludeNetId = null)
    {
        LocalLoadResource[] sceneResources = BasisNetworkResourceManagement.UshortNetworkDatabase.Values.Where(r => r.Mode == 1 && r.LoadedNetID != excludeNetId).ToArray();
        if (sceneResources.Length == 0) return;
        BNL.Log($"PreloadResourceManagement: Unloading {sceneResources.Length} existing scene(s) before synchronized spawn");
        NetDataWriter writer = NetworkServer.RentWriter();
        foreach (LocalLoadResource scene in sceneResources)
        {
            if (BasisNetworkResourceManagement.UshortNetworkDatabase.TryRemove(scene.LoadedNetID, out _))
            {
                BasisNetworkResourceManagement.NoteResourceRemoved(scene.UUIDOfCreator);
            }
            Cancel(scene.LoadedNetID);
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
    public static void RemovePeer(int peerId)
    {
        List<string> completed = null;
        lock (gate)
        {
            List<string> empty = null;
            foreach (KeyValuePair<string, SyncLoadSession> pair in sessions)
            {
                SyncLoadSession session = pair.Value;
                if (!session.Drop(peerId)) continue;
                session.Dirty = true;
                if (session.Tracked == 0)
                {
                    empty ??= new List<string>();
                    empty.Add(pair.Key);
                }
                else if (session.Waiting.Count == 0)
                {
                    completed ??= new List<string>();
                    completed.Add(pair.Key);
                }
            }
            for (int index = 0; empty != null && index < empty.Count; index++) sessions.Remove(empty[index]);
        }
        for (int index = 0; completed != null && index < completed.Count; index++) Finish(completed[index], $"everyone still required has reported after peer {peerId} left");
    }
    public static void Reset()
    {
        lock (gate) sessions.Clear();
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
    internal struct Outgoing
    {
        public string NetId;
        public ushort Requester, Ready, Failed, Total, Skipped;
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
    internal static bool Supports(int peerId) => registered && BasisServerMessageRegistry.HasSubscribed(peerId, messageId);
    internal static void Enqueue(Outgoing message)
    {
        lock (gate) closing.Add(message);
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
        BasisNetworkPreloadResourceManagement.Tick();
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
        BasisNetworkPreloadResourceManagement.Touch(peer.Id);
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
            if (!reader.TryGetByte(out byte op) || !reader.TryGetString(out string loadedNetId)) return;
            if (op == BasisPredownloadProtocol.OpStatus) BasisNetworkPreloadResourceManagement.HandleStatus(peer.Id, loadedNetId);
            else if (op == BasisPredownloadProtocol.OpDone && reader.TryGetBool(out bool ready)) Report(peer.Id, loadedNetId, ready);
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
        writer.Put(message.Skipped);
    }
}
