using Basis.Network.Core;
using BasisNetworkCore;
using BasisNetworkServer.Security;
using BasisPermissions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace Basis.Network.Server.Generic
{
    public static class BasisNetworkClipCache
    {
        public const string ClipShareIdentifier = "BasisMediaClipShare";
        public const byte OpOffer = 1;
        public const byte OpChunk = 2;
        public const byte OpRemove = 3;
        public const byte OpPose = 4;
        public const byte OpNeed = 5;
        public const byte OpServerCacheState = 6;
        public const byte OpServerCacheOffer = 7;
        public const byte OpServerCacheRequest = 8;
        public const byte OpServerCacheCatalog = 9;
        public const byte OpServerHandover = 10;
        public const byte OpPersistence = 11;
        public const byte StateNotHeld = 0;
        public const byte StateUpload = 1;
        public const byte StateHeld = 2;
        public const int GuidBytes = 16;
        public const int ChunkBytes = 16 * 1024;
        public const int ChunkHeaderBytes = 1 + GuidBytes + sizeof(int);
        public const int HardMaxClipBytes = 1024 * 1024 * 1024;
        public const int MaxNeedIndices = 512;
        private const int PoseFloatBytes = 8 * sizeof(float);
        private const int OfferPoseOffset = 1 + GuidBytes + sizeof(int) + 2;
        private const int OfferFixedBytes = OfferPoseOffset + PoseFloatBytes + sizeof(ushort);
        private const int PoseBytes = 1 + GuidBytes + PoseFloatBytes;
        private const int MaxNameBytes = 160;
        private const byte FlagProtected = 1;
        private const byte KnownFlags = 3;
        private const int MaxQueuedPackets = 256;
        private const int PumpIntervalMs = 10;
        private const double BurstSeconds = 0.25;
        private const long BytesPerMegabyte = 1024L * 1024L;

        private sealed class Entry
        {
            public Guid Id;
            public ushort OwnerId;
            public long Sequence;
            public int TotalBytes;
            public int TotalChunks;
            public bool Protected;
            public byte[] Offer;
            public bool[] Held;
            public int HeldCount;
            public FileStream File;
            public string Path;
            public bool Complete => HeldCount == TotalChunks;
        }

        private sealed class Delivery
        {
            public ushort PeerId;
            public Entry Entry;
            public int Next = int.MaxValue;
            public readonly Queue<int> Explicit = new Queue<int>();
            public bool Done => Next >= Entry.TotalChunks && Explicit.Count == 0;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<Guid, Entry> Entries = new Dictionary<Guid, Entry>();
        private static readonly List<Delivery> Deliveries = new List<Delivery>();
        private static readonly HashSet<ushort> ReadyPeers = new HashSet<ushort>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static long _totalBytes;
        private static long _sequence;
        private static int _clipNetId = -1;
        private static string _directory;
        private static Timer _pump;
        private static int _pumping;
        private static double _tokens;
        private static double _lastPumpSeconds;
        private static int _deliveryCursor;
        private static byte[] _readScratch;

        public static long TotalBytes => Interlocked.Read(ref _totalBytes);

        public static int Count
        {
            get
            {
                lock (Gate) return Entries.Count;
            }
        }

        public static int CompleteCount
        {
            get
            {
                lock (Gate)
                {
                    int complete = 0;
                    foreach (Entry entry in Entries.Values)
                    {
                        if (entry.Complete) complete++;
                    }
                    return complete;
                }
            }
        }

        public static int PendingDeliveries
        {
            get
            {
                lock (Gate) return Deliveries.Count;
            }
        }

        private static bool Enabled
        {
            get
            {
                Configuration config = NetworkServer.Configuration;
                return config != null && config.VideoClipCacheEnabled && config.VideoClipCacheMaxMegabytes > 0;
            }
        }

        private static long MaxBytes => Math.Max(0, NetworkServer.Configuration?.VideoClipCacheMaxMegabytes ?? 0) * BytesPerMegabyte;

        private static long LargestClipBytes => Math.Min(HardMaxClipBytes, BasisGlobalLockManager.VideoClipMaxMegabytes * BytesPerMegabyte);

        private static double ServeBytesPerSecond => Math.Max(0, NetworkServer.Configuration?.VideoClipCacheServeMegabytesPerSecond ?? 0) * (double)BytesPerMegabyte;

        public static bool IsClipTraffic(ushort messageIndex)
        {
            int known = Volatile.Read(ref _clipNetId);
            if (known >= 0) return messageIndex == known;
            if (BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue(ClipShareIdentifier, out ushort resolved))
            {
                Volatile.Write(ref _clipNetId, resolved);
                return messageIndex == resolved;
            }
            return false;
        }

        public static bool IsAddressedToServer(ushort senderId, ushort[] recipients, int recipientsSize)
        {
            if (recipients == null || recipientsSize <= 0) return false;
            int count = Math.Min(recipientsSize, recipients.Length);
            for (int index = 0; index < count; index++)
            {
                if (recipients[index] != senderId) return false;
            }
            return count > 0;
        }

        public static void Observe(ushort senderId, byte[] payload, int payloadLength, bool toServer)
        {
            if (payload == null || payloadLength < 1 + GuidBytes) return;
            try
            {
                switch (payload[0])
                {
                    case OpOffer:
                        ObserveOffer(senderId, payload, payloadLength);
                        break;
                    case OpChunk:
                        ObserveChunk(senderId, payload, payloadLength);
                        break;
                    case OpPose:
                        ObservePose(payload, payloadLength);
                        break;
                    case OpRemove:
                        ObserveRemove(senderId, payload);
                        break;
                    case OpNeed:
                        if (toServer) ServeNeed(senderId, payload, payloadLength);
                        break;
                    case OpServerCacheRequest:
                        if (toServer) ServeRequest(senderId, ReadGuid(payload, 1));
                        break;
                    case OpServerCacheCatalog:
                        if (toServer) SendCatalog(senderId);
                        break;
                    case OpPersistence:
                        if (toServer && payloadLength > 1 + GuidBytes) BasisMediaPersistenceRegistry.Set(senderId, payload[1 + GuidBytes] != 0);
                        break;
                }
            }
            catch (Exception exception)
            {
                BNL.LogError($"Clip cache ignored a malformed payload from {senderId}: {exception.Message}");
            }
        }

        private static void ObserveOffer(ushort senderId, byte[] payload, int payloadLength)
        {
            if (!Enabled || payloadLength < OfferFixedBytes) return;
            Guid id = ReadGuid(payload, 1);
            int totalBytes = BitConverter.ToInt32(payload, 1 + GuidBytes);
            byte flags = payload[1 + GuidBytes + sizeof(int) + 1];
            int nameLength = BitConverter.ToUInt16(payload, OfferFixedBytes - sizeof(ushort));
            if (id == Guid.Empty || (flags & ~KnownFlags) != 0 || nameLength > MaxNameBytes || OfferFixedBytes + nameLength != payloadLength) return;
            if (totalBytes <= 0 || totalBytes > LargestClipBytes) return;
            int maxPerOwner = Math.Max(1, (int)BasisGlobalLockManager.VideoClipMaxPerPlayer);
            lock (Gate)
            {
                if (Entries.ContainsKey(id)) return;
                int owned = 0;
                foreach (Entry existing in Entries.Values)
                {
                    if (existing.OwnerId == senderId) owned++;
                }
                if (owned >= maxPerOwner || !TryReserveLocked(totalBytes)) return;
                string directory = EnsureDirectoryLocked();
                if (directory == null) return;
                string path = Path.Combine(directory, id.ToString("N") + ".clip");
                FileStream file;
                try
                {
                    file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess);
                }
                catch (Exception exception)
                {
                    BNL.LogWarning($"Clip cache could not create {path}: {exception.Message}");
                    return;
                }
                int totalChunks = (totalBytes + ChunkBytes - 1) / ChunkBytes;
                Entries[id] = new Entry
                {
                    Id = id,
                    OwnerId = senderId,
                    Sequence = ++_sequence,
                    TotalBytes = totalBytes,
                    TotalChunks = totalChunks,
                    Protected = (flags & FlagProtected) != 0,
                    Offer = Copy(payload, payloadLength),
                    Held = new bool[totalChunks],
                    File = file,
                    Path = path,
                };
                Interlocked.Add(ref _totalBytes, totalBytes);
            }
            NotifyOwner(senderId, id, StateUpload);
        }

        private static void ObserveChunk(ushort senderId, byte[] payload, int payloadLength)
        {
            if (payloadLength <= ChunkHeaderBytes) return;
            Guid id = ReadGuid(payload, 1);
            int index = BitConverter.ToInt32(payload, 1 + GuidBytes);
            int length = payloadLength - ChunkHeaderBytes;
            Entry completed = null;
            lock (Gate)
            {
                if (!Entries.TryGetValue(id, out Entry entry) || entry.OwnerId != senderId) return;
                if (index < 0 || index >= entry.TotalChunks || entry.Held[index] || length != ChunkLength(entry, index)) return;
                try
                {
                    entry.File.Seek((long)index * ChunkBytes, SeekOrigin.Begin);
                    entry.File.Write(payload, ChunkHeaderBytes, length);
                }
                catch (Exception exception)
                {
                    BNL.LogWarning($"Clip cache dropped {id:N}: {exception.Message}");
                    DropLocked(entry, notifyOwner: true);
                    return;
                }
                entry.Held[index] = true;
                entry.HeldCount++;
                if (entry.Complete)
                {
                    entry.File.Flush();
                    completed = entry;
                }
            }
            if (completed == null) return;
            BNL.Log($"Clip cache holds {id:N} ({completed.TotalBytes / (double)BytesPerMegabyte:0.#} MB) from {senderId}.");
            NotifyOwner(senderId, id, StateHeld);
            OfferToReadyPeers(completed);
        }

        private static void ObservePose(byte[] payload, int payloadLength)
        {
            if (payloadLength != PoseBytes) return;
            Guid id = ReadGuid(payload, 1);
            lock (Gate)
            {
                if (Entries.TryGetValue(id, out Entry entry)) Buffer.BlockCopy(payload, 1 + GuidBytes, entry.Offer, OfferPoseOffset, PoseFloatBytes);
            }
        }

        private static void ObserveRemove(ushort senderId, byte[] payload)
        {
            Guid id = ReadGuid(payload, 1);
            lock (Gate)
            {
                if (!Entries.TryGetValue(id, out Entry entry)) return;
                if (entry.OwnerId != senderId && (entry.Protected || BasisGlobalLockManager.ContentRemovalLocked)) return;
                DropLocked(entry, notifyOwner: false);
            }
        }

        private static void SendCatalog(ushort requesterId)
        {
            if (!NetworkServer.AuthenticatedPeers.TryGetValue(requesterId, out NetPeer peer) || peer == null) return;
            List<(ushort Owner, byte[] Offer)> offers = new List<(ushort, byte[])>();
            lock (Gate)
            {
                ReadyPeers.Add(requesterId);
                if (!Enabled) return;
                List<Entry> ordered = new List<Entry>(Entries.Values);
                ordered.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
                foreach (Entry entry in ordered)
                {
                    if (entry.Complete && entry.OwnerId != requesterId && OwnerMayShare(entry.OwnerId)) offers.Add((entry.OwnerId, BuildOffer(entry)));
                }
            }
            foreach ((ushort owner, byte[] offer) in offers) SendPayload(peer, owner, offer, offer.Length);
            if (offers.Count > 0) BNL.Log($"Clip cache offered {offers.Count} clip(s) to peer {requesterId}.");
        }

        private static void OfferToReadyPeers(Entry entry)
        {
            if (!OwnerMayShare(entry.OwnerId)) return;
            List<ushort> targets = new List<ushort>();
            byte[] offer;
            lock (Gate)
            {
                if (!Entries.ContainsKey(entry.Id)) return;
                foreach (ushort peerId in ReadyPeers)
                {
                    if (peerId != entry.OwnerId) targets.Add(peerId);
                }
                offer = BuildOffer(entry);
            }
            foreach (ushort target in targets)
            {
                if (NetworkServer.AuthenticatedPeers.TryGetValue(target, out NetPeer peer) && peer != null) SendPayload(peer, entry.OwnerId, offer, offer.Length);
            }
        }

        private static void ServeRequest(ushort requesterId, Guid id)
        {
            if (!Enabled) return;
            lock (Gate)
            {
                if (!Entries.TryGetValue(id, out Entry entry) || !entry.Complete || entry.OwnerId == requesterId || !OwnerMayShare(entry.OwnerId)) return;
                Delivery delivery = GetOrAddDeliveryLocked(requesterId, entry);
                if (delivery.Next >= entry.TotalChunks) delivery.Next = 0;
            }
            EnsurePump();
        }

        private static void ServeNeed(ushort requesterId, byte[] payload, int payloadLength)
        {
            const int FixedBytes = 1 + GuidBytes + sizeof(ushort);
            if (!Enabled || payloadLength < FixedBytes) return;
            Guid id = ReadGuid(payload, 1);
            int count = BitConverter.ToUInt16(payload, 1 + GuidBytes);
            if (count > MaxNeedIndices || payloadLength != FixedBytes + count * sizeof(int)) return;
            lock (Gate)
            {
                if (!Entries.TryGetValue(id, out Entry entry) || entry.OwnerId == requesterId || !OwnerMayShare(entry.OwnerId)) return;
                Delivery delivery = GetOrAddDeliveryLocked(requesterId, entry);
                for (int i = 0; i < count && delivery.Explicit.Count < entry.TotalChunks; i++)
                {
                    int index = BitConverter.ToInt32(payload, FixedBytes + i * sizeof(int));
                    if (index >= 0 && index < entry.TotalChunks && entry.Held[index]) delivery.Explicit.Enqueue(index);
                }
                if (delivery.Done) Deliveries.Remove(delivery);
            }
            EnsurePump();
        }

        private static Delivery GetOrAddDeliveryLocked(ushort peerId, Entry entry)
        {
            foreach (Delivery existing in Deliveries)
            {
                if (existing.PeerId == peerId && existing.Entry == entry) return existing;
            }
            Delivery delivery = new Delivery { PeerId = peerId, Entry = entry };
            Deliveries.Add(delivery);
            return delivery;
        }

        private static bool OwnerMayShare(ushort ownerId)
        {
            if (!BasisGlobalLockManager.VideoClipsLocked) return true;
            if (!NetworkServer.AuthenticatedPeers.TryGetValue(ownerId, out NetPeer owner) || owner == null || NetworkServer.AuthIdentity == null) return false;
            return NetworkServer.AuthIdentity.NetIDToUUID(owner, out string uuid)
                && !string.IsNullOrEmpty(uuid)
                && PermissionIntegration.HasValidRequirement(uuid, PermNodes.ModerationGlobalLock);
        }

        private static bool TryReserveLocked(long bytes)
        {
            long cap = MaxBytes;
            if (cap <= 0 || bytes > cap) return false;
            while (Interlocked.Read(ref _totalBytes) + bytes > cap)
            {
                Entry victim = PickVictimLocked();
                if (victim == null) return false;
                DropLocked(victim, notifyOwner: true);
            }
            return true;
        }

        private static Entry PickVictimLocked()
        {
            Dictionary<ushort, long> byOwner = new Dictionary<ushort, long>();
            foreach (Entry entry in Entries.Values)
            {
                byOwner.TryGetValue(entry.OwnerId, out long held);
                byOwner[entry.OwnerId] = held + entry.TotalBytes;
            }
            ushort heaviest = 0;
            long heaviestBytes = -1;
            foreach (KeyValuePair<ushort, long> pair in byOwner)
            {
                if (pair.Value > heaviestBytes)
                {
                    heaviestBytes = pair.Value;
                    heaviest = pair.Key;
                }
            }
            Entry oldest = null;
            foreach (Entry entry in Entries.Values)
            {
                if (entry.OwnerId == heaviest && (oldest == null || entry.Sequence < oldest.Sequence)) oldest = entry;
            }
            return oldest;
        }

        private static void DropLocked(Entry entry, bool notifyOwner)
        {
            if (!Entries.Remove(entry.Id)) return;
            Interlocked.Add(ref _totalBytes, -entry.TotalBytes);
            for (int i = Deliveries.Count - 1; i >= 0; i--)
            {
                if (Deliveries[i].Entry == entry) Deliveries.RemoveAt(i);
            }
            CloseFile(entry);
            if (notifyOwner) NotifyOwner(entry.OwnerId, entry.Id, StateNotHeld);
        }

        private static void CloseFile(Entry entry)
        {
            try
            {
                entry.File?.Dispose();
            }
            catch (Exception)
            {
            }
            entry.File = null;
            try
            {
                if (!string.IsNullOrEmpty(entry.Path) && File.Exists(entry.Path)) File.Delete(entry.Path);
            }
            catch (Exception exception)
            {
                BNL.LogWarning($"Clip cache could not delete {entry.Path}: {exception.Message}");
            }
        }

        public static bool Remove(Guid id)
        {
            lock (Gate)
            {
                if (!Entries.TryGetValue(id, out Entry entry)) return false;
                DropLocked(entry, notifyOwner: true);
                return true;
            }
        }

        public static void RemovePlayer(int peerId)
        {
            if (peerId < 0 || peerId > ushort.MaxValue) return;
            ushort id = (ushort)peerId;
            bool found = Basis.Network.Server.Ownership.BasisNetworkOwnership.TrySelectSuccessor(peerId, out ushort successor, out List<NetPeer> recipients);
            bool handOver = found && BasisMediaPersistenceRegistry.ShouldKeep(peerId);
            List<Entry> handed = new List<Entry>();
            lock (Gate)
            {
                List<Entry> doomed = new List<Entry>();
                foreach (Entry entry in Entries.Values)
                {
                    if (entry.OwnerId != id) continue;
                    if (handOver && entry.Complete)
                    {
                        entry.OwnerId = successor;
                        handed.Add(entry);
                    }
                    else doomed.Add(entry);
                }
                foreach (Entry entry in doomed) DropLocked(entry, notifyOwner: false);
                Deliveries.RemoveAll(delivery => delivery.PeerId == id);
                ReadyPeers.Remove(id);
            }
            if (!handOver) successor = BasisMediaPersistenceRegistry.NoSuccessor;
            byte[] notice = new byte[1 + sizeof(ushort) * 2];
            notice[0] = OpServerHandover;
            BitConverter.TryWriteBytes(new Span<byte>(notice, 1, sizeof(ushort)), id);
            BitConverter.TryWriteBytes(new Span<byte>(notice, 1 + sizeof(ushort), sizeof(ushort)), successor);
            if (Volatile.Read(ref _clipNetId) < 0 && BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue(ClipShareIdentifier, out ushort resolved)) Volatile.Write(ref _clipNetId, resolved);
            foreach (NetPeer peer in recipients)
            {
                if (peer != null && peer.Id >= 0 && peer.Id <= ushort.MaxValue) SendPayload(peer, (ushort)peer.Id, notice, notice.Length);
            }
            foreach (Entry entry in handed)
            {
                NotifyOwner(successor, entry.Id, StateHeld);
                OfferToReadyPeers(entry);
            }
            if (handOver) BNL.Log($"Player {id} left; their shared clips now belong to player {successor} ({handed.Count} held by the server).");
        }

        public static void Reset()
        {
            lock (Gate)
            {
                foreach (Entry entry in Entries.Values) CloseFile(entry);
                Entries.Clear();
                Deliveries.Clear();
                ReadyPeers.Clear();
                Interlocked.Exchange(ref _totalBytes, 0);
                _sequence = 0;
                _clipNetId = -1;
                _tokens = 0;
                _pump?.Dispose();
                _pump = null;
                if (_directory != null)
                {
                    try
                    {
                        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
                    }
                    catch (Exception)
                    {
                    }
                    _directory = null;
                }
            }
        }

        private static string EnsureDirectoryLocked()
        {
            if (_directory != null && Directory.Exists(_directory)) return _directory;
            string configured = NetworkServer.Configuration?.VideoClipCacheDirectory;
            int processId = 0;
            try
            {
                processId = Process.GetCurrentProcess().Id;
            }
            catch (Exception)
            {
            }
            string root = string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "BasisClipCache") : configured;
            try
            {
                Directory.CreateDirectory(root);
                ClearStaleDirectories(root, processId);
                string directory = Path.Combine(root, processId.ToString());
                Directory.CreateDirectory(directory);
                _directory = directory;
                return directory;
            }
            catch (Exception exception)
            {
                BNL.LogWarning($"Clip cache could not use {root}: {exception.Message}");
                return null;
            }
        }

        private static void ClearStaleDirectories(string root, int processId)
        {
            foreach (string directory in Directory.GetDirectories(root))
            {
                if (!int.TryParse(Path.GetFileName(directory), out int owner) || owner == processId || IsProcessAlive(owner)) continue;
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (Exception)
                {
                }
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void EnsurePump()
        {
            lock (Gate)
            {
                if (Deliveries.Count == 0) return;
                if (_pump == null)
                {
                    _lastPumpSeconds = Clock.Elapsed.TotalSeconds;
                    _pump = new Timer(_ => Pump(), null, 0, PumpIntervalMs);
                }
            }
        }

        internal static void Pump()
        {
            if (Interlocked.Exchange(ref _pumping, 1) == 1) return;
            try
            {
                lock (Gate)
                {
                    PumpLocked();
                    if (Deliveries.Count == 0 && _pump != null)
                    {
                        _pump.Dispose();
                        _pump = null;
                    }
                }
            }
            catch (Exception exception)
            {
                BNL.LogError($"Clip cache pump failed: {exception.Message}");
            }
            finally
            {
                Volatile.Write(ref _pumping, 0);
            }
        }

        private static void PumpLocked()
        {
            double now = Clock.Elapsed.TotalSeconds;
            double rate = ServeBytesPerSecond;
            double elapsed = Math.Max(0, now - _lastPumpSeconds);
            _lastPumpSeconds = now;
            _tokens = rate <= 0 ? double.MaxValue : Math.Min(_tokens + rate * elapsed, rate * BurstSeconds);
            int idle = 0;
            while (Deliveries.Count > 0 && idle < Deliveries.Count && _tokens > 0)
            {
                if (_deliveryCursor >= Deliveries.Count) _deliveryCursor = 0;
                Delivery delivery = Deliveries[_deliveryCursor];
                if (delivery.Done || !NetworkServer.AuthenticatedPeers.TryGetValue(delivery.PeerId, out NetPeer peer) || peer == null)
                {
                    Deliveries.RemoveAt(_deliveryCursor);
                    continue;
                }
                if (peer.GetPacketsCountInQueue(BasisNetworkCommons.DirectSceneServerChannel, DeliveryMethod.ReliableOrdered) >= MaxQueuedPackets)
                {
                    idle++;
                    _deliveryCursor++;
                    continue;
                }
                idle = 0;
                int index = delivery.Explicit.Count > 0 ? delivery.Explicit.Dequeue() : delivery.Next++;
                Entry entry = delivery.Entry;
                int length = ChunkLength(entry, index);
                if (length <= 0 || !entry.Held[index])
                {
                    continue;
                }
                byte[] packet = _readScratch ??= new byte[ChunkHeaderBytes + ChunkBytes];
                packet[0] = OpChunk;
                Buffer.BlockCopy(entry.Id.ToByteArray(), 0, packet, 1, GuidBytes);
                Buffer.BlockCopy(BitConverter.GetBytes(index), 0, packet, 1 + GuidBytes, sizeof(int));
                try
                {
                    entry.File.Seek((long)index * ChunkBytes, SeekOrigin.Begin);
                    int read = 0;
                    while (read < length)
                    {
                        int got = entry.File.Read(packet, ChunkHeaderBytes + read, length - read);
                        if (got <= 0) throw new EndOfStreamException();
                        read += got;
                    }
                }
                catch (Exception exception)
                {
                    BNL.LogWarning($"Clip cache could not read {entry.Id:N}: {exception.Message}");
                    DropLocked(entry, notifyOwner: true);
                    continue;
                }
                SendPayload(peer, entry.OwnerId, packet, ChunkHeaderBytes + length);
                _tokens -= ChunkHeaderBytes + length;
                _deliveryCursor++;
            }
        }

        private static void NotifyOwner(ushort ownerId, Guid id, byte state)
        {
            if (!NetworkServer.AuthenticatedPeers.TryGetValue(ownerId, out NetPeer owner) || owner == null) return;
            byte[] payload = new byte[1 + GuidBytes + 1];
            payload[0] = OpServerCacheState;
            Buffer.BlockCopy(id.ToByteArray(), 0, payload, 1, GuidBytes);
            payload[1 + GuidBytes] = state;
            SendPayload(owner, ownerId, payload, payload.Length);
        }

        private static void SendPayload(NetPeer peer, ushort stampedId, byte[] payload, int payloadLength)
        {
            int netId = Volatile.Read(ref _clipNetId);
            if (netId < 0 || peer == null) return;
            ServerSceneDataMessage message = new ServerSceneDataMessage
            {
                sceneDataMessage = new RemoteSceneDataMessage
                {
                    messageIndex = (ushort)netId,
                    payload = payload,
                    payloadLength = payloadLength,
                },
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = stampedId,
                },
            };
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                message.Serialize(writer);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.DirectSceneServerChannel, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        private static byte[] BuildOffer(Entry entry)
        {
            byte[] offer = Copy(entry.Offer, entry.Offer.Length);
            offer[0] = OpServerCacheOffer;
            return offer;
        }

        private static int ChunkLength(Entry entry, int index)
        {
            if (index < 0 || index >= entry.TotalChunks) return 0;
            return index == entry.TotalChunks - 1 ? entry.TotalBytes - index * ChunkBytes : ChunkBytes;
        }

        private static Guid ReadGuid(byte[] payload, int offset)
        {
            byte[] raw = new byte[GuidBytes];
            Buffer.BlockCopy(payload, offset, raw, 0, GuidBytes);
            return new Guid(raw);
        }

        private static byte[] Copy(byte[] payload, int payloadLength)
        {
            byte[] copy = new byte[payloadLength];
            Buffer.BlockCopy(payload, 0, copy, 0, payloadLength);
            return copy;
        }
    }
}
