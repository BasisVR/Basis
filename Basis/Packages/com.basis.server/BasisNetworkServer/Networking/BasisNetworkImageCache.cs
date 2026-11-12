using Basis.Network.Core;
using BasisNetworkCore;
using BasisNetworkServer.Security;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace Basis.Network.Server.Generic
{
    /// <summary>
    /// Keeps the bytes of every shared image in server RAM so the rest of the room can be given a
    /// picture the sharer only sent to the players standing near it, instead of the sharer having to
    /// send it again to every arrival.
    ///
    /// The cache is deliberately dumb about what an image *is*: it retains the client's own scene
    /// payloads verbatim and replays them stamped with the original owner's player id. A receiving
    /// client therefore sees exactly the OpSpawn/OpChunk stream it would have got from the owner,
    /// and needs no knowledge that the server answered instead.
    ///
    /// What it hands a player first is an offer - the sharer's own spawn header, opcode swapped - and
    /// nothing moves until that client measures the distance for itself and asks. The one thing the
    /// cache does read out of a payload is where a picture has got to: a spawn header records where a
    /// picture was hung, and pictures get carried around, so the pose the room last saw is kept and
    /// written over the header's before an offer or a replay goes out. Anything else about an image
    /// stays opaque bytes.
    ///
    /// Lifetime matches what clients already do with images, so the cache can never show a joiner
    /// something the room cannot see: an entry is dropped on despawn and when its owner disconnects.
    /// </summary>
    public static class BasisNetworkImageCache
    {
        /// <summary>
        /// The image manager registers under this fixed string, so the server can resolve which
        /// dynamic NetworkID carries image traffic. Must match
        /// <c>BasisImagePickupManager.FixedNetworkIdentifier</c>.
        /// </summary>
        public const string ImageManagerIdentifier = "BasisImagePickupManager";

        // Mirrored from BasisImagePickupManager. Wire protocol — changing either side is a break.
        private const byte OpSpawn = 1;
        private const byte OpChunk = 2;
        private const byte OpTransform = 3;
        private const byte OpDespawn = 4;
        private const byte OpAnimationSpawn = 6;
        private const byte OpAnimationChunk = 7;

        /// <summary>
        /// Server → owner: "I am holding this image" / "I am no longer holding it". The owner skips
        /// re-sending held images to each arrival, which is the whole point of the buffer. Sent only
        /// to the owner and stamped with their own player id — the relay never echoes a sender to
        /// itself, so a message arriving under your own id cannot have come from another client.
        /// </summary>
        private const byte OpServerCacheState = 8;

        /// <summary>
        /// Server to client: "I am holding an image; here is its header, decide for yourself whether
        /// you want it." The payload is the sharer's own spawn header replayed verbatim with only the
        /// opcode byte changed, so the server hands over the position without ever reading it - the
        /// distance decision belongs to the client, which is the only side that knows where it is
        /// standing without being told.
        /// </summary>
        private const byte OpServerCacheOffer = 9;

        /// <summary>
        /// Client to server: "send me that one." Reaches the cache through the ordinary relay
        /// observation path, the same way a despawn does.
        /// </summary>
        private const byte OpServerCacheRequest = 10;

        /// <summary>
        /// Server to client: "player A left and player B owns their images now". Payload
        /// [ushort leaver][ushort successor], stamped with the recipient's own id.
        /// </summary>
        private const byte OpServerHandover = 11;

        /// <summary>
        /// Client to server, addressed to itself: [op][16 unused][byte enabled], the sender's Media
        /// Persistence choice. Read even while the cache itself is turned off.
        /// </summary>
        private const byte OpPersistence = 12;

        private const int OpcodeBytes = 1;
        private const int GuidBytes = 16;
        private const int HeaderBytes = OpcodeBytes + GuidBytes;

        /// <summary>Position and rotation, seven floats, written last in a spawn header.</summary>
        private const int PoseBytes = (3 + 4) * sizeof(float);

        /// <summary>opcode + guid + pose + scale. Fixed, so anything of another length is not one.</summary>
        private const int TransformBytes = HeaderBytes + PoseBytes + sizeof(float);

        /// <summary>Ceiling on a single owner name, matching the client's own read guard.</summary>
        private const int MaxOwnerNameBytes = 1024;

        /// <summary>
        /// A cached image. Stills arrive as OpSpawn + OpChunk; an animated image adds
        /// OpAnimationSpawn + OpAnimationChunk on top of the still that carries its poster frame.
        /// </summary>
        private sealed class CachedImage
        {
            public ushort OwnerId;
            public long Sequence;

            /// <summary>Who has been told this image exists. One offer per player, ever.</summary>
            public readonly HashSet<ushort> Offered = new HashSet<ushort>();

            /// <summary>
            /// Who has actually been sent the bytes. Separate from <see cref="Offered"/> because a
            /// player may sit on an offer for as long as they like before walking close enough to want
            /// it, and because a repeated request must not buy a second copy.
            /// </summary>
            public readonly HashSet<ushort> Delivered = new HashSet<ushort>();
            public readonly HashSet<ushort> AnimationDelivered = new HashSet<ushort>();

            public byte[] Spawn;
            public byte[][] Chunks;
            public int ChunksHeld;

            /// <summary>
            /// Where the pose sits inside <see cref="Spawn"/>. Walked once when the header is
            /// admitted rather than stepped over the owner name again on every offer.
            /// </summary>
            public int PoseOffset;

            /// <summary>
            /// The last pose the room was told about, or null while the picture has not moved since
            /// it was shared. A spawn header says where a picture was *put*; anybody may then pick it
            /// up and carry it off, and a joiner handed the header alone is shown the empty spot it
            /// was created in. Retained verbatim like every other payload, so replaying it needs no
            /// new receive code - and it carries scale, which a spawn header does not.
            /// </summary>
            public byte[] Transform;

            public byte[] AnimationSpawn;
            public byte[][] AnimationChunks;
            public int AnimationChunksHeld;

            public long Bytes;

            /// <summary>A still is servable once its spawn header and every chunk has landed.</summary>
            public bool StillComplete => Spawn != null && Chunks != null && ChunksHeld == Chunks.Length;

            /// <summary>
            /// An animation is only replayed alongside a complete still. A half-received animation is
            /// simply not sent — the joiner still gets the still image rather than nothing.
            /// </summary>
            public bool AnimationComplete =>
                AnimationSpawn != null && AnimationChunks != null && AnimationChunksHeld == AnimationChunks.Length;
        }

        private static readonly ConcurrentDictionary<Guid, CachedImage> Images =
            new ConcurrentDictionary<Guid, CachedImage>();

        private static readonly object Gate = new object();

        private static long _totalBytes;
        private static long _sequence;

        /// <summary>
        /// Resolved NetworkID of the image manager, or -1 until a client has asked for one. Ids are
        /// stable for the life of the database, so this is memoised rather than looked up by string
        /// on every scene message.
        /// </summary>
        private static int _managerNetId = -1;

        /// <summary>Bytes currently held. Diagnostics and tests.</summary>
        public static long TotalBytes => System.Threading.Interlocked.Read(ref _totalBytes);

        /// <summary>Number of complete or in-flight images held.</summary>
        public static int Count => Images.Count;

        /// <summary>
        /// How many held images are complete enough to hand to a joiner. Differs from
        /// <see cref="Count"/> while a share is still arriving.
        /// </summary>
        public static int ServableCount
        {
            get
            {
                int servable = 0;
                foreach (KeyValuePair<Guid, CachedImage> pair in Images)
                {
                    if (pair.Value.StillComplete)
                    {
                        servable++;
                    }
                }
                return servable;
            }
        }

        /// <summary>Bytes currently held on behalf of one player.</summary>
        public static long BytesHeldFor(ushort ownerId)
        {
            lock (Gate)
            {
                return OwnerBytes(ownerId);
            }
        }

        private static bool Enabled => NetworkServer.Configuration?.ImageCacheEnabled ?? false;

        private const long BytesPerMegabyte = 1024L * 1024L;

        /// <summary>
        /// Largest payload one image may have at the server's resolution setting: two bytes per pixel
        /// of the cap, the same ceiling clients enforce. 32 MiB at 4K, 512 MiB at 16K.
        /// </summary>
        private static long LargestImageBytes
        {
            get
            {
                long side = BasisGlobalLockManager.ImageMaxResolutionK * 1024L;
                return side * side * 2L;
            }
        }

        private static long MaxBytes
        {
            get
            {
                int configured = NetworkServer.Configuration?.ImageCacheMaxMegabytes ?? 0;
                return configured > 0 ? configured * BytesPerMegabyte : 0;
            }
        }

        /// <summary>
        /// Every owner is guaranteed this much before fair-share division applies, so a busy
        /// instance cannot shrink each person's allowance to something that fits no image at all.
        /// Raised to fit one image at the server's resolution setting; 0 turns the floor off.
        /// </summary>
        private static long MinimumOwnerBytes
        {
            get
            {
                int configured = NetworkServer.Configuration?.ImageCacheMinimumPerOwnerMegabytes ?? 0;
                return configured > 0 ? Math.Max(configured * BytesPerMegabyte, LargestImageBytes) : 0;
            }
        }

        public static void Reset()
        {
            lock (Gate)
            {
                Images.Clear();
                System.Threading.Interlocked.Exchange(ref _totalBytes, 0);
                _sequence = 0;
                _managerNetId = -1;
            }
        }

        /// <summary>
        /// True when this scene message is image traffic. Called for every scene message, so it is
        /// a memoised integer compare after the first resolve.
        /// </summary>
        public static bool IsImageTraffic(ushort messageIndex)
        {
            int known = System.Threading.Volatile.Read(ref _managerNetId);
            if (known >= 0)
            {
                return messageIndex == known;
            }

            if (BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue(ImageManagerIdentifier, out ushort resolved))
            {
                System.Threading.Volatile.Write(ref _managerNetId, resolved);
                return messageIndex == resolved;
            }

            return false;
        }

        public static bool IsAnimationPayload(byte[] payload, int payloadLength)
        {
            return payload != null
                && payloadLength > 0
                && (payload[0] == OpAnimationSpawn || payload[0] == OpAnimationChunk);
        }

        public static bool IsGifBlockedFor(NetPeer peer)
        {
            if (!BasisGlobalLockManager.GifsLocked)
            {
                return false;
            }

            string uuid = null;
            if (peer != null && NetworkServer.AuthIdentity != null)
            {
                NetworkServer.AuthIdentity.NetIDToUUID(peer, out uuid);
            }
            return IsGifBlockedForUuid(uuid);
        }

        public static bool IsGifBlockedForUuid(string uuid)
        {
            return BasisGlobalLockManager.GifsLocked
                && (string.IsNullOrEmpty(uuid) || !PermissionIntegration.HasValidRequirement(uuid, PermNodes.ModerationGlobalLock));
        }

        /// <summary>
        /// Feeds one relayed image payload to the cache. The caller keeps relaying exactly as before
        /// — this only observes, so a cache that rejects or misparses a message can never stop the
        /// live send. <paramref name="payload"/> is copied where retained; the caller may reuse it.
        /// </summary>
        public static void Observe(
            ushort senderId,
            byte[] payload,
            int payloadLength,
            ushort[] recipients = null,
            int recipientsSize = 0
        )
        {
            if (payload == null || payloadLength < HeaderBytes)
            {
                return;
            }
            if (payload[0] == OpPersistence)
            {
                if (payloadLength > HeaderBytes && recipientsSize == 1 && recipients != null && recipients[0] == senderId)
                {
                    BasisMediaPersistenceRegistry.Set(senderId, payload[HeaderBytes] != 0);
                }
                return;
            }
            if (!Enabled)
            {
                return;
            }

            try
            {
                byte opcode = payload[0];
                switch (opcode)
                {
                    case OpSpawn:
                        ObserveSpawn(senderId, payload, payloadLength, recipients, recipientsSize);
                        break;
                    case OpChunk:
                        ObserveChunk(senderId, payload, payloadLength, animation: false);
                        break;
                    case OpTransform:
                        ObserveTransform(payload, payloadLength);
                        break;
                    case OpServerCacheRequest:
                        ObserveRequest(senderId, payload, payloadLength);
                        break;
                    case OpAnimationSpawn:
                        ObserveAnimationSpawn(senderId, payload, payloadLength);
                        break;
                    case OpAnimationChunk:
                        ObserveChunk(senderId, payload, payloadLength, animation: true);
                        break;
                    case OpDespawn:
                        Remove(ReadGuid(payload), senderId, ownerOnly: true);
                        break;
                }
            }
            catch (Exception e)
            {
                // Never let a malformed payload take down the relay thread; the live send already
                // happened and a client that sends nonsense simply goes uncached.
                BNL.LogError($"Image cache ignored a malformed payload from {senderId}: {e.Message}");
            }
        }

        private static void ObserveSpawn(
            ushort senderId,
            byte[] payload,
            int payloadLength,
            ushort[] recipients,
            int recipientsSize
        )
        {
            Guid id = ReadGuid(payload);
            if (!TryReadSpawnHeader(payload, payloadLength, out int totalChunks, out int poseOffset) || totalChunks <= 0)
            {
                return;
            }
            if (poseOffset + PoseBytes > payloadLength)
            {
                return;
            }

            lock (Gate)
            {
                if (Images.ContainsKey(id))
                {
                    // Already tracked. A join re-send repeats the spawn header; keep the first copy.
                    return;
                }

                // Charge the chunk-array backbone, not just the header. totalChunks is client-supplied
                // and `new byte[totalChunks][]` costs totalChunks references; accounting only
                // payloadLength let a client register an enormous totalChunks (an unbounded
                // allocation, and gigabytes of retained backbone) while the cap saw a few bytes. With
                // the backbone charged, an implausible count trips `cost > cap` inside TryReserve and
                // is refused before anything is allocated.
                long cost = (long)payloadLength + (long)totalChunks * IntPtr.Size;
                if (!TryReserve(senderId, cost, id))
                {
                    return;
                }

                CachedImage entry = new CachedImage
                {
                    OwnerId = senderId,
                    Sequence = ++_sequence,
                    Spawn = Copy(payload, payloadLength),
                    PoseOffset = poseOffset,
                    Chunks = new byte[totalChunks][],
                    Bytes = cost,
                };
                SeedAlreadyHeldLocked(entry, recipients, recipientsSize);
                Images[id] = entry;
                System.Threading.Interlocked.Add(ref _totalBytes, cost);
            }
        }

        private static void ObserveAnimationSpawn(ushort senderId, byte[] payload, int payloadLength)
        {
            // opcode + guid + format(1) + totalBytes(4) + totalChunks(4) + epoch(8)
            const int ChunkCountOffset = HeaderBytes + 1 + 4;
            if (payloadLength < ChunkCountOffset + 4)
            {
                return;
            }

            Guid id = ReadGuid(payload);
            int totalChunks = BitConverter.ToInt32(payload, ChunkCountOffset);
            if (totalChunks <= 0)
            {
                return;
            }

            lock (Gate)
            {
                if (!Images.TryGetValue(id, out CachedImage entry) || entry.OwnerId != senderId)
                {
                    return;
                }
                if (entry.AnimationSpawn != null)
                {
                    return;
                }

                // Charge the animation chunk-array backbone too — same unbounded-allocation guard as
                // the still spawn above.
                long cost = (long)payloadLength + (long)totalChunks * IntPtr.Size;
                if (!TryReserve(senderId, cost, id))
                {
                    return;
                }

                entry.AnimationSpawn = Copy(payload, payloadLength);
                entry.AnimationChunks = new byte[totalChunks][];
                entry.Bytes += cost;
                System.Threading.Interlocked.Add(ref _totalBytes, cost);
            }
        }

        /// <summary>
        /// Remembers where a picture has got to. Taken from whoever sent it rather than from the
        /// owner alone, because control of a card passes to whoever picks it up, so the player
        /// moving one is very often not the player who shared it. That is no wider a trust surface
        /// than it appears: the relay has already handed these exact bytes to the whole room, and
        /// all the cache does is keep what everybody has already been told.
        /// </summary>
        private static void ObserveTransform(byte[] payload, int payloadLength)
        {
            if (payloadLength != TransformBytes)
            {
                return;
            }

            Guid id = ReadGuid(payload);
            lock (Gate)
            {
                if (!Images.TryGetValue(id, out CachedImage entry))
                {
                    return;
                }

                if (entry.Transform == null)
                {
                    // Charged once. Every later pose overwrites a buffer of the same size, so a card
                    // being dragged around the room costs the buffer nothing after the first update.
                    if (!TryReserve(entry.OwnerId, TransformBytes, id))
                    {
                        return;
                    }
                    entry.Bytes += TransformBytes;
                    System.Threading.Interlocked.Add(ref _totalBytes, TransformBytes);
                }

                entry.Transform = Copy(payload, payloadLength);
            }
        }

        private static void ObserveChunk(ushort senderId, byte[] payload, int payloadLength, bool animation)
        {
            // opcode + guid + chunkIndex(4) + length(4) + bytes
            const int ChunkIndexOffset = HeaderBytes;
            if (payloadLength < ChunkIndexOffset + 8)
            {
                return;
            }

            Guid id = ReadGuid(payload);
            int chunkIndex = BitConverter.ToInt32(payload, ChunkIndexOffset);
            if (chunkIndex < 0)
            {
                return;
            }

            bool becameServable = false;
            bool animationCompleted = false;
            lock (Gate)
            {
                if (!Images.TryGetValue(id, out CachedImage entry) || entry.OwnerId != senderId)
                {
                    return;
                }

                byte[][] slots = animation ? entry.AnimationChunks : entry.Chunks;
                if (slots == null || chunkIndex >= slots.Length || slots[chunkIndex] != null)
                {
                    return;
                }

                long cost = payloadLength;
                if (!TryReserve(senderId, cost, id))
                {
                    return;
                }

                slots[chunkIndex] = Copy(payload, payloadLength);
                if (animation)
                {
                    entry.AnimationChunksHeld++;
                }
                else
                {
                    entry.ChunksHeld++;
                }
                entry.Bytes += cost;
                System.Threading.Interlocked.Add(ref _totalBytes, cost);

                becameServable = !animation && entry.StillComplete;
                animationCompleted = animation && entry.AnimationComplete && entry.StillComplete;
            }

            if (becameServable)
            {
                NotifyOwner(senderId, id, held: true);
                OfferToRoom(id);
            }
            if (animationCompleted)
            {
                DeliverPendingAnimation(id);
            }
        }

        /// <summary>
        /// Makes room for <paramref name="cost"/> on behalf of <paramref name="ownerId"/>. Fairness
        /// is per owner: an owner over their share evicts their OWN oldest images and never anyone
        /// else's, so one person filling the buffer cannot push out everybody else's pictures.
        /// Caller must hold <see cref="Gate"/>.
        /// </summary>
        private static bool TryReserve(ushort ownerId, long cost, Guid exclude)
        {
            long cap = MaxBytes;
            if (cap <= 0 || cost <= 0 || cost > cap)
            {
                return false;
            }

            long share = OwnerShare(ownerId, cap);
            if (cost > share)
            {
                // No amount of evicting this owner's other images makes a single oversized one fit.
                return false;
            }

            while (OwnerBytes(ownerId) + cost > share)
            {
                if (!EvictOldestOwnedBy(ownerId, exclude))
                {
                    return false;
                }
            }

            while (System.Threading.Interlocked.Read(ref _totalBytes) + cost > cap)
            {
                if (!EvictOldestOfHeaviestOwner(exclude))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Each distinct owner gets an equal slice of the buffer, never less than the configured
        /// minimum. The owner being admitted is counted even when they hold nothing yet, so the
        /// first image of a new sharer is sized against the room they are about to join.
        /// </summary>
        private static long OwnerShare(ushort ownerId, long cap)
        {
            HashSet<ushort> owners = new HashSet<ushort> { ownerId };
            foreach (KeyValuePair<Guid, CachedImage> pair in Images)
            {
                owners.Add(pair.Value.OwnerId);
            }

            long share = cap / Math.Max(1, owners.Count);
            long floor = Math.Min(cap, MinimumOwnerBytes);
            return Math.Max(share, floor);
        }

        private static long OwnerBytes(ushort ownerId)
        {
            long total = 0;
            foreach (KeyValuePair<Guid, CachedImage> pair in Images)
            {
                if (pair.Value.OwnerId == ownerId)
                {
                    total += pair.Value.Bytes;
                }
            }
            return total;
        }

        private static bool EvictOldestOwnedBy(ushort ownerId, Guid exclude)
        {
            Guid oldest = Guid.Empty;
            long oldestSequence = long.MaxValue;
            bool found = false;

            foreach (KeyValuePair<Guid, CachedImage> pair in Images)
            {
                if (pair.Value.OwnerId != ownerId || pair.Key == exclude)
                {
                    continue;
                }
                if (pair.Value.Sequence < oldestSequence)
                {
                    oldestSequence = pair.Value.Sequence;
                    oldest = pair.Key;
                    found = true;
                }
            }

            return found && DropLocked(oldest);
        }

        private static bool EvictOldestOfHeaviestOwner(Guid exclude)
        {
            Dictionary<ushort, long> byOwner = new Dictionary<ushort, long>();
            foreach (KeyValuePair<Guid, CachedImage> pair in Images)
            {
                byOwner.TryGetValue(pair.Value.OwnerId, out long held);
                byOwner[pair.Value.OwnerId] = held + pair.Value.Bytes;
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

            return heaviestBytes >= 0 && EvictOldestOwnedBy(heaviest, exclude);
        }

        /// <summary>Caller must hold <see cref="Gate"/>.</summary>
        private static bool DropLocked(Guid id)
        {
            if (!Images.TryRemove(id, out CachedImage entry))
            {
                return false;
            }
            System.Threading.Interlocked.Add(ref _totalBytes, -entry.Bytes);

            // Tell the owner they are back on the hook for this one. Without it an evicted image
            // would silently stop reaching new arrivals: the owner still believes we hold it and
            // skips re-sending, and we no longer have anything to send.
            if (entry.StillComplete)
            {
                NotifyOwner(entry.OwnerId, id, held: false);
            }
            return true;
        }

        /// <summary>
        /// Tells one player whether the server is holding a given image of theirs. Best effort: a
        /// peer that has already gone simply is not there to tell.
        /// </summary>
        private static void NotifyOwner(ushort ownerId, Guid id, bool held)
        {
            int managerNetId = System.Threading.Volatile.Read(ref _managerNetId);
            if (managerNetId < 0)
            {
                return;
            }
            if (!NetworkServer.AuthenticatedPeers.TryGetValue(ownerId, out NetPeer owner))
            {
                return;
            }

            byte[] payload = new byte[HeaderBytes + 1];
            payload[0] = OpServerCacheState;
            Buffer.BlockCopy(id.ToByteArray(), 0, payload, OpcodeBytes, GuidBytes);
            payload[HeaderBytes] = held ? (byte)1 : (byte)0;

            NetDataWriter writer = NetworkServer.RentWriter();
            SendPayload(owner, writer, (ushort)managerNetId, ownerId, payload);
            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Drops a cached image. <paramref name="ownerOnly"/> restricts it to the player who shared
        /// it, which is what an OpDespawn off the wire gets: anyone may ask, only the owner's word
        /// removes the server's copy.
        /// </summary>
        public static bool Remove(Guid id, ushort requesterId, bool ownerOnly)
        {
            lock (Gate)
            {
                if (!Images.TryGetValue(id, out CachedImage entry))
                {
                    return false;
                }
                if (ownerOnly && entry.OwnerId != requesterId)
                {
                    return false;
                }
                return DropLocked(id);
            }
        }

        /// <summary>
        /// Drops everything a departing player shared. Clients already destroy that player's images
        /// on disconnect, so keeping them cached would hand a joiner pictures nobody else can see.
        /// With <see cref="BasisGlobalLockManager.KeepSharedMediaOnLeave"/> on, clients keep them
        /// instead, so every complete image is handed to the successor and re-offered to the room,
        /// and only half-received ones are dropped.
        /// </summary>
        public static void RemovePlayerImages(int peerId)
        {
            ushort ownerId = (ushort)peerId;
            bool found = Basis.Network.Server.Ownership.BasisNetworkOwnership.TrySelectSuccessor(peerId, out ushort successor, out List<NetPeer> recipients);
            bool handOver = found && BasisMediaPersistenceRegistry.ShouldKeep(peerId);
            List<Guid> handed = new List<Guid>();
            lock (Gate)
            {
                List<Guid> doomed = new List<Guid>();
                foreach (KeyValuePair<Guid, CachedImage> pair in Images)
                {
                    if (pair.Value.OwnerId != ownerId)
                    {
                        continue;
                    }
                    if (handOver && pair.Value.StillComplete)
                    {
                        CachedImage entry = pair.Value;
                        entry.OwnerId = successor;
                        entry.Offered.Clear();
                        entry.Delivered.Clear();
                        entry.AnimationDelivered.Clear();
                        handed.Add(pair.Key);
                        continue;
                    }
                    doomed.Add(pair.Key);
                }

                for (int index = 0; index < doomed.Count; index++)
                {
                    DropLocked(doomed[index]);
                }

                foreach (KeyValuePair<Guid, CachedImage> pair in Images)
                {
                    pair.Value.Offered.Remove(ownerId);
                    pair.Value.Delivered.Remove(ownerId);
                    pair.Value.AnimationDelivered.Remove(ownerId);
                }
            }

            SendHandover(ownerId, handOver ? successor : BasisMediaPersistenceRegistry.NoSuccessor, recipients, handed);
        }

        /// <summary>
        /// Tells every remaining player who owns the leaver's images now, or that nobody does
        /// (<see cref="BasisMediaPersistenceRegistry.NoSuccessor"/>) and they should go. On a handover
        /// it then tells the successor which of them the server is holding and re-offers those to the
        /// room. The notice goes out even when nothing was cached, because clients also hold images the
        /// server never did. Each copy is stamped with its recipient's own id, which only the server can do.
        /// </summary>
        private static void SendHandover(ushort leaverId, ushort successor, List<NetPeer> recipients, List<Guid> handed)
        {
            int managerNetId = System.Threading.Volatile.Read(ref _managerNetId);
            if (managerNetId < 0)
            {
                if (!BasisNetworkIDDatabase.UshortNetworkDatabase.TryGetValue(ImageManagerIdentifier, out ushort resolved))
                {
                    return;
                }
                System.Threading.Volatile.Write(ref _managerNetId, resolved);
                managerNetId = resolved;
            }

            byte[] notice = new byte[OpcodeBytes + sizeof(ushort) * 2];
            notice[0] = OpServerHandover;
            BitConverter.TryWriteBytes(new Span<byte>(notice, OpcodeBytes, sizeof(ushort)), leaverId);
            BitConverter.TryWriteBytes(new Span<byte>(notice, OpcodeBytes + sizeof(ushort), sizeof(ushort)), successor);

            NetDataWriter writer = NetworkServer.RentWriter();
            for (int index = 0; index < recipients.Count; index++)
            {
                NetPeer peer = recipients[index];
                if (peer != null)
                {
                    SendPayload(peer, writer, (ushort)managerNetId, (ushort)peer.Id, notice);
                }
            }
            NetworkServer.ReturnWriter(writer);

            for (int index = 0; index < handed.Count; index++)
            {
                NotifyOwner(successor, handed[index], held: true);
            }

            if (handed.Count > 0 && Enabled)
            {
                for (int index = 0; index < recipients.Count; index++)
                {
                    NetPeer peer = recipients[index];
                    if (peer == null || peer.Id < 0 || peer.Id > ushort.MaxValue)
                    {
                        continue;
                    }
                    ushort recipient = (ushort)peer.Id;
                    List<byte[]> offers = new List<byte[]>();
                    lock (Gate)
                    {
                        for (int item = 0; item < handed.Count; item++)
                        {
                            if (Images.TryGetValue(handed[item], out CachedImage entry) && ShouldOfferLocked(entry, recipient))
                            {
                                entry.Offered.Add(recipient);
                                offers.Add(BuildOffer(entry));
                            }
                        }
                    }
                    SendOffers(peer, (ushort)managerNetId, offers);
                }
            }

            if (successor != BasisMediaPersistenceRegistry.NoSuccessor)
            {
                BNL.Log($"Player {leaverId} left; their shared images now belong to player {successor} ({handed.Count} held by the server).");
            }
        }

        /// <summary>
        /// Tells an arriving peer what the room is holding, in the order it was shared, and stops
        /// there. Each offer is the sharer's own spawn header - tens of bytes - so a joiner arriving
        /// into an instance full of pictures pays for a catalogue rather than a gallery, and decides
        /// locally which entries are close enough to be worth asking for.
        /// </summary>
        public static void OfferCachedImagesToPeer(NetPeer newConnection)
        {
            if (!Enabled || newConnection == null)
            {
                return;
            }

            int managerNetId = System.Threading.Volatile.Read(ref _managerNetId);
            if (managerNetId < 0)
            {
                return;
            }

            int peerId = newConnection.Id;
            if (peerId < 0 || peerId > ushort.MaxValue)
            {
                return;
            }
            ushort recipient = (ushort)peerId;

            List<byte[]> offers = new List<byte[]>();
            lock (Gate)
            {
                List<KeyValuePair<Guid, CachedImage>> ordered =
                    new List<KeyValuePair<Guid, CachedImage>>(Images);
                ordered.Sort((left, right) => left.Value.Sequence.CompareTo(right.Value.Sequence));

                for (int index = 0; index < ordered.Count; index++)
                {
                    CachedImage entry = ordered[index].Value;
                    if (!ShouldOfferLocked(entry, recipient))
                    {
                        continue;
                    }
                    entry.Offered.Add(recipient);
                    offers.Add(BuildOffer(entry));
                }
            }

            SendOffers(newConnection, (ushort)managerNetId, offers);
        }

        /// <summary>
        /// Tells everyone already in the room about an image that has just finished arriving. The
        /// sharer only sent it to the players it considered close enough, so without this the rest of
        /// the room would never learn the picture exists.
        /// </summary>
        private static void OfferToRoom(Guid id)
        {
            if (!Enabled)
            {
                return;
            }

            int managerNetId = System.Threading.Volatile.Read(ref _managerNetId);
            if (managerNetId < 0)
            {
                return;
            }

            foreach (KeyValuePair<int, NetPeer> pair in NetworkServer.AuthenticatedPeers)
            {
                int peerId = pair.Key;
                if (peerId < 0 || peerId > ushort.MaxValue || pair.Value == null)
                {
                    continue;
                }
                ushort recipient = (ushort)peerId;

                byte[] offer;
                lock (Gate)
                {
                    if (!Images.TryGetValue(id, out CachedImage entry) || !ShouldOfferLocked(entry, recipient))
                    {
                        continue;
                    }
                    entry.Offered.Add(recipient);
                    offer = BuildOffer(entry);
                }

                NetDataWriter writer = NetworkServer.RentWriter();
                SendPayload(pair.Value, writer, (ushort)managerNetId, recipient, offer);
                NetworkServer.ReturnWriter(writer);
            }
        }

        private static bool ShouldOfferLocked(CachedImage entry, ushort recipient)
        {
            return entry.StillComplete
                && entry.OwnerId != recipient
                && !entry.Offered.Contains(recipient)
                && !entry.Delivered.Contains(recipient);
        }

        /// <summary>
        /// An offer is the spawn header with one byte changed. The position inside it is what the
        /// client measures its distance against, so it has to say where the picture is now rather
        /// than where it was first hung.
        /// </summary>
        private static byte[] BuildOffer(CachedImage entry)
        {
            byte[] offer = BuildSpawn(entry);
            offer[0] = OpServerCacheOffer;
            return offer;
        }

        /// <summary>
        /// The retained spawn header with the latest pose written over the one it was shared at.
        /// Copying rather than mutating is what makes that safe to do at all: the retained header
        /// stays exactly as the sharer wrote it, and a replay already queued for somebody else keeps
        /// the bytes it was handed instead of having a pose change torn through it mid-send.
        /// </summary>
        private static byte[] BuildSpawn(CachedImage entry)
        {
            byte[] spawn = new byte[entry.Spawn.Length];
            Buffer.BlockCopy(entry.Spawn, 0, spawn, 0, entry.Spawn.Length);
            if (entry.Transform != null && entry.PoseOffset > 0 && entry.PoseOffset + PoseBytes <= spawn.Length)
            {
                Buffer.BlockCopy(entry.Transform, HeaderBytes, spawn, entry.PoseOffset, PoseBytes);
            }
            return spawn;
        }

        /// <summary>
        /// Offers go out unmetered and stamped with the recipient's own id rather than the sharer's.
        /// They are a handful of bytes each, and the client only trusts an offer that arrives under
        /// its own id - the relay never lets one client forge that.
        /// </summary>
        private static void SendOffers(NetPeer peer, ushort managerNetId, List<byte[]> offers)
        {
            if (offers.Count == 0)
            {
                return;
            }

            int peerId = peer.Id;
            NetDataWriter writer = NetworkServer.RentWriter();
            for (int index = 0; index < offers.Count; index++)
            {
                SendPayload(peer, writer, managerNetId, (ushort)peerId, offers[index]);
            }
            NetworkServer.ReturnWriter(writer);

            BNL.Log($"Image cache offered {offers.Count} image(s) to peer {peerId}.");
        }

        /// <summary>
        /// A client asking for one of the images it was offered. This is the only thing that moves
        /// image bytes out of the cache, and it moves them once per player per image.
        /// </summary>
        private static void ObserveRequest(ushort senderId, byte[] payload, int payloadLength)
        {
            if (payloadLength < HeaderBytes)
            {
                return;
            }
            ServeRequestedImage(senderId, ReadGuid(payload));
        }

        internal static void ServeRequestedImage(ushort requesterId, Guid id)
        {
            if (!Enabled)
            {
                return;
            }

            int managerNetId = System.Threading.Volatile.Read(ref _managerNetId);
            if (managerNetId < 0)
            {
                return;
            }

            if (!NetworkServer.AuthenticatedPeers.TryGetValue(requesterId, out NetPeer peer) || peer == null)
            {
                return;
            }

            bool includeAnimation = !IsGifBlockedFor(peer);

            // Flatten in the order the room was built, so the pump can meter the stream without
            // knowing anything about images. Ordering matters on the wire: a chunk before its spawn
            // header is discarded by the receiver.
            List<BasisImageBandwidthGovernor.PendingPayload> queued =
                new List<BasisImageBandwidthGovernor.PendingPayload>();

            lock (Gate)
            {
                if (!Images.TryGetValue(id, out CachedImage entry))
                {
                    return;
                }
                if (!entry.StillComplete || entry.OwnerId == requesterId)
                {
                    return;
                }
                if (!entry.Delivered.Add(requesterId))
                {
                    return;
                }
                entry.Offered.Add(requesterId);

                queued.Add(new BasisImageBandwidthGovernor.PendingPayload(entry.OwnerId, BuildSpawn(entry)));
                if (entry.Transform != null)
                {
                    // Ahead of the chunks rather than after them: the receiver raises its card off
                    // the header, so pose and scale land while the picture is still loading instead
                    // of the card standing somewhere wrong until the last chunk arrives.
                    queued.Add(new BasisImageBandwidthGovernor.PendingPayload(entry.OwnerId, entry.Transform));
                }
                for (int chunk = 0; chunk < entry.Chunks.Length; chunk++)
                {
                    queued.Add(new BasisImageBandwidthGovernor.PendingPayload(entry.OwnerId, entry.Chunks[chunk]));
                }

                if (includeAnimation && entry.AnimationComplete)
                {
                    entry.AnimationDelivered.Add(requesterId);
                    AppendAnimationLocked(entry, queued);
                }
            }

            DeliverQueued(peer, requesterId, queued);
        }

        public static void ResumeAnimationsAfterUnlock()
        {
            if (!Enabled || BasisGlobalLockManager.GifsLocked)
            {
                return;
            }

            List<Guid> ids = new List<Guid>();
            lock (Gate)
            {
                foreach (KeyValuePair<Guid, CachedImage> pair in Images)
                {
                    if (pair.Value.StillComplete && pair.Value.AnimationComplete)
                    {
                        ids.Add(pair.Key);
                    }
                }
            }

            for (int index = 0; index < ids.Count; index++)
            {
                DeliverPendingAnimation(ids[index]);
            }
        }

        private static void DeliverPendingAnimation(Guid id)
        {
            if (!Enabled)
            {
                return;
            }

            List<ushort> pending = new List<ushort>();
            lock (Gate)
            {
                if (!Images.TryGetValue(id, out CachedImage entry) || !entry.StillComplete || !entry.AnimationComplete)
                {
                    return;
                }
                foreach (ushort recipient in entry.Delivered)
                {
                    if (recipient != entry.OwnerId && !entry.AnimationDelivered.Contains(recipient))
                    {
                        pending.Add(recipient);
                    }
                }
            }

            for (int index = 0; index < pending.Count; index++)
            {
                ushort recipient = pending[index];
                if (!NetworkServer.AuthenticatedPeers.TryGetValue(recipient, out NetPeer peer) || peer == null || IsGifBlockedFor(peer))
                {
                    continue;
                }

                List<BasisImageBandwidthGovernor.PendingPayload> queued =
                    new List<BasisImageBandwidthGovernor.PendingPayload>();
                lock (Gate)
                {
                    if (!Images.TryGetValue(id, out CachedImage entry) || !entry.AnimationComplete || !entry.AnimationDelivered.Add(recipient))
                    {
                        continue;
                    }
                    AppendAnimationLocked(entry, queued);
                }
                DeliverQueued(peer, recipient, queued);
            }
        }

        private static void AppendAnimationLocked(CachedImage entry, List<BasisImageBandwidthGovernor.PendingPayload> queued)
        {
            queued.Add(new BasisImageBandwidthGovernor.PendingPayload(entry.OwnerId, entry.AnimationSpawn));
            for (int chunk = 0; chunk < entry.AnimationChunks.Length; chunk++)
            {
                queued.Add(new BasisImageBandwidthGovernor.PendingPayload(entry.OwnerId, entry.AnimationChunks[chunk]));
            }
        }

        private static void DeliverQueued(NetPeer peer, ushort recipientId, List<BasisImageBandwidthGovernor.PendingPayload> queued)
        {
            if (queued.Count == 0)
            {
                return;
            }

            int managerNetId = System.Threading.Volatile.Read(ref _managerNetId);
            if (managerNetId < 0)
            {
                return;
            }

            // Paced when the operator has set a download rate, inline when they have not. Sending
            // inline is the historical behaviour and stays reachable deliberately: a small instance
            // on a fast LAN has nothing to gain from metering, and 0 should mean "as fast as it
            // will go" rather than some hidden default.
            BasisImageBandwidthGovernor.SendPayload = ReplaySinglePayload;
            if (BasisImageBandwidthGovernor.EnqueueReplay(peer, queued))
            {
                BNL.Log($"Image cache queued {queued.Count} payload(s) for requesting peer {recipientId} (paced).");
                return;
            }

            NetDataWriter writer = NetworkServer.RentWriter();
            int sent = 0;
            for (int index = 0; index < queued.Count; index++)
            {
                sent += SendPayload(peer, writer, (ushort)managerNetId, queued[index].OwnerId, queued[index].Payload);
            }
            NetworkServer.ReturnWriter(writer);

            if (sent > 0)
            {
                BNL.Log($"Image cache served {sent} payload(s) to requesting peer {recipientId}.");
            }
        }

        /// <summary>
        /// Pump callback: one metered payload, rented writer and all. Resolves the manager id at
        /// send time rather than capturing it, because a replay now spans many milliseconds and the
        /// id can be reset by a despawn of the whole manager in between.
        /// </summary>
        private static void ReplaySinglePayload(NetPeer peer, ushort ownerId, byte[] payload)
        {
            int managerNetId = System.Threading.Volatile.Read(ref _managerNetId);
            if (managerNetId < 0)
            {
                return;
            }

            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                SendPayload(peer, writer, (ushort)managerNetId, ownerId, payload);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        private static int SendPayload(NetPeer peer, NetDataWriter writer, ushort managerNetId, ushort ownerId, byte[] payload)
        {
            if (payload == null)
            {
                return 0;
            }

            ServerSceneDataMessage message = new ServerSceneDataMessage
            {
                sceneDataMessage = new RemoteSceneDataMessage
                {
                    messageIndex = managerNetId,
                    payload = payload,
                    payloadLength = payload.Length,
                },
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = ownerId,
                },
            };

            writer.Reset();
            message.Serialize(writer);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.DirectSceneServerChannel, DeliveryMethod.ReliableOrdered);
            return 1;
        }

        private static Guid ReadGuid(byte[] payload)
        {
            byte[] raw = new byte[GuidBytes];
            Buffer.BlockCopy(payload, OpcodeBytes, raw, 0, GuidBytes);
            return new Guid(raw);
        }

        private static byte[] Copy(byte[] payload, int payloadLength)
        {
            byte[] copy = new byte[payloadLength];
            Buffer.BlockCopy(payload, 0, copy, 0, payloadLength);
            return copy;
        }

        /// <summary>
        /// Reads totalChunks out of an OpSpawn header, and where the pose that follows it begins.
        /// The owner name in front of both is a BinaryWriter string — a 7-bit encoded byte length
        /// then UTF8 — so the fields after it sit at a variable offset and have to be walked to
        /// rather than indexed.
        /// </summary>
        private static bool TryReadSpawnHeader(byte[] payload, int payloadLength, out int totalChunks, out int poseOffset)
        {
            totalChunks = 0;
            poseOffset = 0;

            int offset = HeaderBytes + 2; // opcode + guid + ushort ownerId
            if (!TrySkipWireString(payload, payloadLength, ref offset))
            {
                return false;
            }

            // width, height, totalBytes, totalChunks
            offset += 4 + 4 + 4;
            if (offset + 4 > payloadLength)
            {
                return false;
            }

            totalChunks = BitConverter.ToInt32(payload, offset);
            poseOffset = offset + 4;
            return true;
        }

        /// <summary>
        /// Marks whoever the sharer already sent this image to as holding it. The relay knows
        /// precisely who that was, and without seeding it here the cache would offer the same picture
        /// back to everyone who was standing nearby when it was shared. An untargeted share went to
        /// the whole room, so everyone counts.
        /// </summary>
        private static void SeedAlreadyHeldLocked(CachedImage entry, ushort[] recipients, int recipientsSize)
        {
            if (recipients != null && recipientsSize > 0)
            {
                int count = Math.Min(recipientsSize, recipients.Length);
                for (int index = 0; index < count; index++)
                {
                    entry.Offered.Add(recipients[index]);
                    entry.Delivered.Add(recipients[index]);
                    entry.AnimationDelivered.Add(recipients[index]);
                }
                return;
            }

            foreach (KeyValuePair<int, NetPeer> pair in NetworkServer.AuthenticatedPeers)
            {
                int peerId = pair.Key;
                if (peerId >= 0 && peerId <= ushort.MaxValue)
                {
                    entry.Offered.Add((ushort)peerId);
                    entry.Delivered.Add((ushort)peerId);
                    entry.AnimationDelivered.Add((ushort)peerId);
                }
            }
        }

        private static bool TrySkipWireString(byte[] payload, int payloadLength, ref int offset)
        {
            int length = 0;
            int shift = 0;
            while (true)
            {
                if (offset >= payloadLength || shift > 4 * 7)
                {
                    return false;
                }

                byte piece = payload[offset++];
                length |= (piece & 0x7F) << shift;
                if ((piece & 0x80) == 0)
                {
                    break;
                }
                shift += 7;
            }

            if (length < 0 || length > MaxOwnerNameBytes || offset + length > payloadLength)
            {
                return false;
            }

            offset += length;
            return true;
        }
    }
}
