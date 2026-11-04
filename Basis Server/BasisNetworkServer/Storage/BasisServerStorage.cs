using Basis.Network.Core;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static BasisPermissions.PermissionManager;

namespace BasisNetworkServer.Storage
{
    public static class BasisServerStorage
    {
        public const int MaxInFlightPerPeer = BasisStorageProtocol.MaxInFlightPerConnection, DefaultMaxTotalMegabytes = 256;
        private static readonly ConcurrentDictionary<int, PeerSlot> peers = new ConcurrentDictionary<int, PeerSlot>();
        private static readonly object gate = new object();
        private static BasisStorageService service;
        public static BasisStorageService Service => Volatile.Read(ref service);
        public static bool IsRunning => Service != null;
        public static string StorageFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.StorageFolderName);
        public static bool Start(Configuration configuration)
        {
            lock (gate)
            {
                StopLocked();
                if (configuration == null || !configuration.StorageEnabled)
                {
                    BNL.Log("Player storage is off (StorageEnabled is false).");
                    return false;
                }
                string name = string.IsNullOrWhiteSpace(configuration.StorageBackend) ? BasisStorageBackends.FileBackendName : configuration.StorageBackend.Trim();
                if (!configuration.HasFileSupport && string.Equals(name, BasisStorageBackends.FileBackendName, StringComparison.OrdinalIgnoreCase)) name = BasisStorageBackends.MemoryBackendName;
                IBasisStorageBackend backend = null;
                try
                {
                    backend = BasisStorageBackends.Create(name, configuration, StorageFolder);
                    backend.Open();
                    BasisStorageService created = new BasisStorageService(backend, BytesPerPlayerOf(configuration), TotalBytesOf(configuration));
                    Apply(created, configuration);
                    Volatile.Write(ref service, created);
                }
                catch (Exception ex)
                {
                    backend?.Dispose();
                    BNL.LogError($"Player storage could not start the '{name}' backend, so it is off: {ex.GetBaseException().Message}");
                    return false;
                }
                BasisStorageService started = Service;
                string expiry = !started.CanExpire ? "kept until wiped (this backend cannot tell when data was last used)" : started.ExpiryDays > 0 ? $"removed after {started.ExpiryDays} days unused" : "kept forever";
                BNL.Log($"Player storage on ({started.BackendName}): {started.TotalRecords} players, {started.TotalBytes} bytes stored, {started.UsedBytes} of the {started.MaxTotalBytes / (1024 * 1024)} MB server cap in use counting {BasisStorageService.RecordOverheadBytes} bytes per player, {started.BytesPerPlayer} bytes per player. New players may start saving {started.NewOwners.PerAddress} per address every {(long)started.NewOwners.Window.TotalMinutes} minutes and {started.NewOwners.PerHour} an hour in total. Idle data is {expiry}.");
                if (!configuration.UseAuthIdentity) BNL.LogWarning("Player storage only serves verified identities, and UseAuthIdentity is off, so every storage request will be refused.");
                return true;
            }
        }
        public static void ApplyConfiguration(Configuration configuration)
        {
            BasisStorageService running = Service;
            if (configuration != null && running != null) Apply(running, configuration);
        }
        public static void Stop()
        {
            lock (gate) StopLocked();
        }
        public static void HandleRequest(NetPacketReader reader, NetPeer peer)
        {
            bool parsed;
            BasisStorageRequest request;
            try
            {
                parsed = BasisStorageProtocol.TryReadRequest(reader, out request);
            }
            catch (Exception)
            {
                parsed = false;
                request = default;
            }
            finally
            {
                reader.Recycle(true);
            }
            BasisStorageService running = Service;
            if (running == null)
            {
                Reply(peer, request, BasisStorageStatus.Unavailable);
                return;
            }
            if (!NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) || string.IsNullOrEmpty(uuid))
            {
                Reply(peer, request, BasisStorageStatus.Denied);
                return;
            }
            if (parsed && !PermissionIntegration.HasValidRequirement(uuid, PermNodes.StorageUse))
            {
                Reply(peer, request, BasisStorageStatus.Denied);
                return;
            }
            PeerSlot slot = peers.AddOrUpdate(peer.Id, _ => new PeerSlot(peer), (_, existing) => Equals(existing.Peer, peer) ? existing : new PeerSlot(peer));
            if (Interlocked.Increment(ref slot.InFlight) > MaxInFlightPerPeer)
            {
                Interlocked.Decrement(ref slot.InFlight);
                Reply(peer, request, BasisStorageStatus.Busy);
                return;
            }
            slot.Owner = uuid;
            BasisStorageRequest submitted = parsed ? request : new BasisStorageRequest { Op = BasisStorageOp.Usage };
            BasisStorageStatus queued = running.Submit(uuid, submitted, reply =>
            {
                Interlocked.Decrement(ref slot.InFlight);
                Send(peer, reply);
            }, BasisStorageNewOwnerLimiter.OriginOf(peer.Address));
            if (queued == BasisStorageStatus.Ok) return;
            Interlocked.Decrement(ref slot.InFlight);
            Reply(peer, request, queued);
        }
        public static void RemovePeer(NetPeer peer, int id)
        {
            if (!peers.TryGetValue(id, out PeerSlot slot) || !Equals(slot.Peer, peer)) return;
            ((ICollection<KeyValuePair<int, PeerSlot>>)peers).Remove(new KeyValuePair<int, PeerSlot>(id, slot));
            if (slot.Owner != null) Service?.Forget(slot.Owner);
        }
        private static void StopLocked()
        {
            BasisStorageService stopping = Interlocked.Exchange(ref service, null);
            peers.Clear();
            if (stopping == null) return;
            try { stopping.Dispose(); }
            catch (Exception ex) { BNL.LogWarning($"Player storage did not shut down cleanly: {ex.Message}"); }
        }
        private static void Reply(NetPeer peer, BasisStorageRequest request, BasisStorageStatus status)
        {
            if (request.RequestId == 0 || !BasisStorageProtocol.IsKnown(request.Op)) return;
            BasisStorageService running = Service;
            Send(peer, new BasisStorageReply { Op = request.Op, RequestId = request.RequestId, Status = status, Limit = running != null ? running.BytesPerPlayer : 0 });
        }
        private static void Send(NetPeer peer, BasisStorageReply reply)
        {
            if (peer == null || reply.RequestId == 0) return;
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                BasisStorageProtocol.WriteReply(writer, reply);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.StorageChannel, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }
        private static void Apply(BasisStorageService target, Configuration configuration)
        {
            target.Configure(BytesPerPlayerOf(configuration), TotalBytesOf(configuration));
            target.ConfigureNewOwners(configuration.StorageNewOwnersPerAddress, TimeSpan.FromMinutes(Math.Max(0, configuration.StorageNewOwnerWindowMinutes)), configuration.StorageNewOwnersPerHour);
            target.ConfigureExpiry(configuration.StorageExpiryDays);
        }
        private static int BytesPerPlayerOf(Configuration configuration) => configuration.StorageMaxBytesPerPlayer > 0 ? Math.Min(configuration.StorageMaxBytesPerPlayer, BasisStorageProtocol.MaxBytesPerPlayer) : BasisStorageProtocol.DefaultBytesPerPlayer;
        private static long TotalBytesOf(Configuration configuration) => (configuration.StorageMaxTotalMegabytes > 0 ? configuration.StorageMaxTotalMegabytes : DefaultMaxTotalMegabytes) * 1024L * 1024L;
        private sealed class PeerSlot
        {
            public readonly NetPeer Peer;
            public int InFlight;
            public volatile string Owner;
            public PeerSlot(NetPeer peer)
            {
                Peer = peer;
            }
        }
    }
}
