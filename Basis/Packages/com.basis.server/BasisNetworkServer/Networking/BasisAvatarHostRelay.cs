using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.Threading;
namespace Basis.Network.Server.Generic
{
    public static class BasisAvatarHostRelay
    {
        private sealed class PeerSink : IBasisAvatarHostSink
        {
            public void OfferResult(int peerId, byte[] id, bool accepted, byte reason)
            {
                Send(peerId, writer =>
                {
                    writer.Put(BasisAvatarHostProtocol.OpOfferResult);
                    BasisAvatarHostProtocol.WriteId(writer, id);
                    writer.Put(accepted);
                    writer.Put(reason);
                });
            }
            public void Fetch(int ownerPeerId, uint fetchId, byte[] id, long start, int length)
            {
                Send(ownerPeerId, writer =>
                {
                    writer.Put(BasisAvatarHostProtocol.OpFetch);
                    writer.Put(fetchId);
                    BasisAvatarHostProtocol.WriteId(writer, id);
                    writer.Put(start);
                    writer.Put(length);
                });
            }
            public void Chunk(int peerId, uint requestId, int offset, byte[] data, int dataOffset, int count) => Enqueue(peerId, requestId, offset, data, dataOffset, count);
            public void Failed(int peerId, uint requestId, byte reason)
            {
                DropStream(peerId, requestId);
                Send(peerId, writer =>
                {
                    writer.Put(BasisAvatarHostProtocol.OpFailed);
                    writer.Put(requestId);
                    writer.Put(reason);
                });
            }
        }
        private static void Send(int peerId, Action<NetDataWriter> write)
        {
            if (NetworkServer.AuthenticatedPeers.TryGetValue(peerId, out NetPeer peer)) BasisServerMessageRegistry.SendToPeer(peer, BasisAvatarHostProtocol.MessageName, write);
        }
        private struct Piece
        {
            public byte[] Data;
            public int DataOffset, WireOffset, Count;
            public uint RequestId;
        }
        private static readonly Action<NetDataWriter, Piece> WritePiece = (writer, piece) =>
        {
            writer.Put(BasisAvatarHostProtocol.OpChunk);
            writer.Put(piece.RequestId);
            writer.Put(piece.WireOffset);
            writer.Put(piece.Count);
            writer.Put(piece.Data, piece.DataOffset, piece.Count);
        };
        private const int MaxQueuedPackets = 512, PumpIntervalMs = 5;
        private static readonly object streamSync = new object();
        private static readonly Dictionary<(int PeerId, uint RequestId), Queue<Piece>> streams = new Dictionary<(int PeerId, uint RequestId), Queue<Piece>>();
        private static readonly List<(int PeerId, uint RequestId)> finished = new List<(int PeerId, uint RequestId)>();
        private static Timer pump;
        private static int pumping;
        private static readonly object askSync = new object();
        private static readonly Dictionary<(int Requester, int Owner, string Url), long> asks = new Dictionary<(int Requester, int Owner, string Url), long>();
        private static readonly List<(int Requester, int Owner, string Url)> staleAsks = new List<(int Requester, int Owner, string Url)>();
        private static long NowMs => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
        public static readonly BasisAvatarHostLedger Ledger = new BasisAvatarHostLedger(new PeerSink());
        public static void Register() => BasisServerMessageRegistry.RegisterServerPlugin(BasisAvatarHostProtocol.MessageName, DeliveryMethod.ReliableOrdered, Handle);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> directOwners = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        public static void RemovePeer(int peerId)
        {
            directOwners.TryRemove(peerId, out _);
            Ledger.RemovePeer(peerId);
            lock (askSync)
            {
                staleAsks.Clear();
                foreach ((int Requester, int Owner, string Url) key in asks.Keys)
                {
                    if (key.Requester == peerId || key.Owner == peerId) staleAsks.Add(key);
                }
                foreach ((int Requester, int Owner, string Url) key in staleAsks) asks.Remove(key);
            }
            lock (streamSync)
            {
                finished.Clear();
                foreach ((int PeerId, uint RequestId) key in streams.Keys)
                {
                    if (key.PeerId == peerId) finished.Add(key);
                }
                foreach ((int PeerId, uint RequestId) key in finished) streams.Remove(key);
            }
        }
        public static int QueuedPieces
        {
            get
            {
                lock (streamSync)
                {
                    int total = 0;
                    foreach (Queue<Piece> queue in streams.Values) total += queue.Count;
                    return total;
                }
            }
        }
        private static void Enqueue(int peerId, uint requestId, int wireOffset, byte[] data, int dataOffset, int count)
        {
            lock (streamSync)
            {
                if (!streams.TryGetValue((peerId, requestId), out Queue<Piece> queue))
                {
                    queue = new Queue<Piece>();
                    streams[(peerId, requestId)] = queue;
                }
                for (int sent = 0; sent < count; sent += BasisAvatarHostProtocol.ChunkBytes)
                {
                    queue.Enqueue(new Piece { Data = data, DataOffset = dataOffset + sent, WireOffset = wireOffset + sent, Count = Math.Min(BasisAvatarHostProtocol.ChunkBytes, count - sent) });
                }
                if (pump == null) pump = new Timer(_ => Pump(), null, 0, PumpIntervalMs);
            }
            Pump();
        }
        private static void DropStream(int peerId, uint requestId)
        {
            lock (streamSync) streams.Remove((peerId, requestId));
        }
        private static void Pump()
        {
            if (Interlocked.Exchange(ref pumping, 1) == 1) return;
            try
            {
                lock (streamSync)
                {
                    finished.Clear();
                    foreach (KeyValuePair<(int PeerId, uint RequestId), Queue<Piece>> stream in streams)
                    {
                        if (!NetworkServer.AuthenticatedPeers.TryGetValue(stream.Key.PeerId, out NetPeer peer))
                        {
                            finished.Add(stream.Key);
                            continue;
                        }
                        Queue<Piece> queue = stream.Value;
                        while (queue.Count > 0 && peer.GetPacketsCountInQueue(BasisNetworkCommons.PluginReliableChannel, DeliveryMethod.ReliableOrdered) < MaxQueuedPackets)
                        {
                            Piece piece = queue.Dequeue();
                            piece.RequestId = stream.Key.RequestId;
                            BasisServerMessageRegistry.SendToPeer(peer, BasisAvatarHostProtocol.MessageName, piece, WritePiece);
                        }
                        if (queue.Count == 0) finished.Add(stream.Key);
                    }
                    foreach ((int PeerId, uint RequestId) key in finished) streams.Remove(key);
                    if (streams.Count == 0 && pump != null)
                    {
                        pump.Dispose();
                        pump = null;
                    }
                }
            }
            finally
            {
                Volatile.Write(ref pumping, 0);
            }
        }
        private static bool TryAddAsk(int requester, int owner, string url)
        {
            long now = NowMs;
            lock (askSync)
            {
                staleAsks.Clear();
                int pending = 0;
                foreach (KeyValuePair<(int Requester, int Owner, string Url), long> ask in asks)
                {
                    if (now - ask.Value > BasisAvatarHostProtocol.AskTimeoutMs) staleAsks.Add(ask.Key);
                    else if (ask.Key.Requester == requester) pending++;
                }
                foreach ((int Requester, int Owner, string Url) key in staleAsks) asks.Remove(key);
                if (pending >= BasisAvatarHostProtocol.MaxPendingAsksPerRequester || asks.ContainsKey((requester, owner, url))) return false;
                asks[(requester, owner, url)] = now;
                return true;
            }
        }
        private static bool TakeAsk(int requester, int owner, string url)
        {
            if (url == null) return false;
            lock (askSync)
            {
                if (!asks.TryGetValue((requester, owner, url), out long at)) return false;
                asks.Remove((requester, owner, url));
                return NowMs - at <= BasisAvatarHostProtocol.AskTimeoutMs;
            }
        }
        private static void SendAnswered(int requester, int owner, string url, bool accepted, byte[] id, bool vrm)
        {
            ushort ownerId = (ushort)owner;
            Send(requester, writer =>
            {
                writer.Put(BasisAvatarHostProtocol.OpShareAnswered);
                writer.Put(ownerId);
                writer.Put(url);
                writer.Put(accepted);
                if (!accepted) return;
                BasisAvatarHostProtocol.WriteId(writer, id);
                writer.Put(vrm);
            });
        }
        public static bool MaySharePeer(int peerId) => !BasisNetworkServer.Security.BasisGlobalLockManager.AvatarHostingLocked || HasLockBypass(peerId);
        public static bool MayAskPeer(int peerId) => !BasisNetworkServer.Security.BasisGlobalLockManager.AvatarShareRequestsLocked || HasLockBypass(peerId);
        private static bool HasLockBypass(int peerId)
        {
            return NetworkServer.AuthenticatedPeers.TryGetValue(peerId, out NetPeer peer)
                && NetworkServer.AuthIdentity != null
                && NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid)
                && !string.IsNullOrEmpty(uuid)
                && BasisPermissions.PermissionManager.PermissionIntegration.HasValidRequirement(uuid, BasisPermissions.PermNodes.ModerationGlobalLock);
        }
        public static void ApplyConfiguration()
        {
            BasisAvatarHostLimits limits = CurrentLimits();
            foreach (int owner in Ledger.OwnerPeerIds())
            {
                if (!limits.Enabled || !MaySharePeer(owner)) Ledger.WithdrawOwner(owner, BasisAvatarHostProtocol.ReasonDisabled);
            }
            Ledger.TrimCache(limits.CacheBytes);
        }
        public static BasisAvatarHostLimits CurrentLimits()
        {
            Configuration config = NetworkServer.Configuration;
            if (config == null) return default;
            long range = Math.Max(0, config.AvatarHostMaxMegabytes) * 1024L * 1024L, cache = Math.Max(0, config.AvatarHostCacheMegabytes) * 1024L * 1024L;
            return new BasisAvatarHostLimits
            {
                Enabled = range > 0,
                MaxRangeBytes = range,
                CacheBytes = cache,
                InFlightBytes = Math.Max(cache, range * 2),
                RequesterBytesPerMinute = range * 2,
            };
        }
        private static void Handle(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                if (!reader.TryGetByte(out byte op)) return;
                BasisAvatarHostLimits limits = CurrentLimits();
                switch (op)
                {
                    case BasisAvatarHostProtocol.OpOffer:
                        {
                            if (!BasisAvatarHostProtocol.TryReadId(reader, out byte[] id) || !reader.TryGetLong(out long length)) return;
                            if (!MaySharePeer(peer.Id)) limits.Enabled = false;
                            if (reader.TryGetByte(out byte offerFlags) && (offerFlags & BasisAvatarHostProtocol.FlagDirect) != 0) directOwners[peer.Id] = true;
                            Ledger.Offer(peer.Id, id, length, limits);
                            break;
                        }
                    case BasisAvatarHostProtocol.OpWithdraw:
                        {
                            if (BasisAvatarHostProtocol.TryReadId(reader, out byte[] id)) Ledger.Withdraw(peer.Id, id);
                            break;
                        }
                    case BasisAvatarHostProtocol.OpRequest:
                        {
                            if (!reader.TryGetUInt(out uint requestId) || !BasisAvatarHostProtocol.TryReadId(reader, out byte[] id) || !reader.TryGetLong(out long start) || !reader.TryGetInt(out int length)) return;
                            int requester = peer.Id;
                            if (limits.Enabled && length <= limits.MaxRangeBytes && reader.TryGetByte(out byte requestFlags) && (requestFlags & BasisAvatarHostProtocol.FlagDirect) != 0
                                && Ledger.TryDirectOwner(requester, id, start, length, owner => directOwners.ContainsKey(owner) && BasisNetworkServer.BasisServerP2PBroker.IsP2POffloaded(requester, owner), out int directOwner))
                            {
                                ushort ownerId = (ushort)directOwner;
                                Send(requester, writer =>
                                {
                                    writer.Put(BasisAvatarHostProtocol.OpUseDirect);
                                    writer.Put(requestId);
                                    writer.Put(ownerId);
                                });
                                return;
                            }
                            Ledger.Request(peer.Id, requestId, id, start, length, limits);
                            break;
                        }
                    case BasisAvatarHostProtocol.OpOwnerChunk:
                        {
                            if (!reader.TryGetUInt(out uint fetchId) || !reader.TryGetInt(out int offset) || !reader.TryGetInt(out int count)) return;
                            if (count <= 0 || count > BasisAvatarHostProtocol.ChunkBytes || count > reader.AvailableBytes)
                            {
                                Ledger.OwnerFailed(peer.Id, fetchId);
                                return;
                            }
                            Ledger.OwnerChunk(peer.Id, fetchId, offset, reader.RawData, reader.Position, count, limits);
                            break;
                        }
                    case BasisAvatarHostProtocol.OpOwnerFailed:
                        {
                            if (reader.TryGetUInt(out uint fetchId)) Ledger.OwnerFailed(peer.Id, fetchId);
                            break;
                        }
                    case BasisAvatarHostProtocol.OpAskShare:
                        {
                            if (!reader.TryGetUShort(out ushort owner) || !reader.TryGetString(out string url) || string.IsNullOrEmpty(url) || url.Length > BasisAvatarHostProtocol.MaxAskUrlChars || owner == peer.Id || !NetworkServer.AuthenticatedPeers.ContainsKey(owner)) return;
                            if (!limits.Enabled || !MayAskPeer(peer.Id) || !MaySharePeer(owner) || NetworkServer.PeerProtocol(owner) < BasisNetworkVersion.ShareAskVersion)
                            {
                                SendAnswered(peer.Id, owner, url, false, null, false);
                                return;
                            }
                            if (!TryAddAsk(peer.Id, owner, url)) return;
                            ushort requester = (ushort)peer.Id;
                            Send(owner, writer =>
                            {
                                writer.Put(BasisAvatarHostProtocol.OpShareAsked);
                                writer.Put(requester);
                                writer.Put(url);
                            });
                            break;
                        }
                    case BasisAvatarHostProtocol.OpShareAnswer:
                        {
                            if (!reader.TryGetUShort(out ushort requester) || !reader.TryGetString(out string url) || !reader.TryGetBool(out bool accepted)) return;
                            byte[] id = null;
                            bool vrm = false;
                            if (accepted && (!BasisAvatarHostProtocol.TryReadId(reader, out id) || !reader.TryGetBool(out vrm))) return;
                            if (!TakeAsk(requester, peer.Id, url)) return;
                            if (accepted && !Ledger.IsOfferedBy(peer.Id, id)) accepted = false;
                            SendAnswered(requester, peer.Id, url, accepted, id, vrm);
                            break;
                        }
                }
            }
            finally
            {
                reader.Recycle();
            }
        }
    }
}
