using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    [AutoStaticsCleanup]
    public static partial class BasisServerStorageClient
    {
        public const string MessageName = "basis.core.storage";
        public const float TimeoutSeconds = 15f;
        public const int MaxInFlight = BasisStorageProtocol.MaxInFlightPerConnection, MaxInFlightPerOwner = 8;
        private static readonly Dictionary<ushort, Pending> pending = new Dictionary<ushort, Pending>();
        private static readonly List<Pending> completed = new List<Pending>();
        private static readonly List<Pending> sweep = new List<Pending>();
        private static ushort lastId;
        private static bool ticking;
        public static int LastUsed { get; private set; }
        public static int LastLimit { get; private set; }
        public static bool IsConnected => BasisNetworkConnection.LocalPlayerPeer != null && BasisNetworkConnection.LocalPlayerIsConnected;
        public static bool IsSupported => IsConnected && Supports(BasisNetworkConnection.LocalPlayerPeer) == true;
        public static void Get(string scope, string key, Action<BasisStorageReply> done, UnityEngine.Object owner = null) => Submit(new BasisStorageRequest { Op = BasisStorageOp.Get, Scope = scope, Key = key }, done, owner);
        public static void Set(string scope, string key, byte[] value, Action<BasisStorageReply> done, UnityEngine.Object owner = null) => Submit(new BasisStorageRequest { Op = BasisStorageOp.Set, Scope = scope, Key = key, Value = value == null ? null : (byte[])value.Clone() }, done, owner);
        public static void Delete(string scope, string key, Action<BasisStorageReply> done, UnityEngine.Object owner = null) => Submit(new BasisStorageRequest { Op = BasisStorageOp.Delete, Scope = scope, Key = key }, done, owner);
        public static void Keys(string scope, Action<BasisStorageReply> done, UnityEngine.Object owner = null) => Submit(new BasisStorageRequest { Op = BasisStorageOp.Keys, Scope = scope }, done, owner);
        public static void Clear(string scope, Action<BasisStorageReply> done, UnityEngine.Object owner = null) => Submit(new BasisStorageRequest { Op = BasisStorageOp.Clear, Scope = scope }, done, owner);
        public static void Usage(string scope, Action<BasisStorageReply> done, UnityEngine.Object owner = null) => Submit(new BasisStorageRequest { Op = BasisStorageOp.Usage, Scope = scope }, done, owner);
        public static void Refuse(BasisStorageOp op, BasisStorageStatus status, Action<BasisStorageReply> done, UnityEngine.Object owner = null) => Finish(new Pending { Request = new BasisStorageRequest { Op = op }, Done = done, Owner = owner, HasOwner = !ReferenceEquals(owner, null) }, status);
        public static void HandleReply(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            bool parsed;
            BasisStorageReply reply;
            try
            {
                parsed = BasisStorageProtocol.TryReadReply(reader, out reply);
            }
            catch (Exception)
            {
                parsed = false;
                reply = default;
            }
            finally
            {
                reader.Recycle(true);
            }
            if (parsed) BasisDeviceManagement.EnqueueOnMainThread(() => Complete(peer, reply));
        }
        private static void Submit(BasisStorageRequest request, Action<BasisStorageReply> done, UnityEngine.Object owner)
        {
            Pending entry = new Pending { Request = request, Done = done, Owner = owner, HasOwner = !ReferenceEquals(owner, null), Peer = BasisNetworkConnection.LocalPlayerPeer, Deadline = Time.realtimeSinceStartup + TimeoutSeconds };
            BasisStorageStatus status = Check(request);
            if (status == BasisStorageStatus.Ok && !IsConnected) status = BasisStorageStatus.NotConnected;
            if (status == BasisStorageStatus.Ok && (pending.Count >= MaxInFlight || (entry.HasOwner && InFlightFor(owner) >= MaxInFlightPerOwner))) status = BasisStorageStatus.Busy;
            if (status == BasisStorageStatus.Ok && Supports(entry.Peer) == false) status = BasisStorageStatus.Unavailable;
            if (status != BasisStorageStatus.Ok)
            {
                Finish(entry, status);
                return;
            }
            entry.Request.RequestId = NextId();
            pending.Add(entry.Request.RequestId, entry);
            if (Supports(entry.Peer) == true) Send(entry);
            StartTicking();
        }
        private static BasisStorageStatus Check(BasisStorageRequest request)
        {
            if (!BasisStorageProtocol.IsValidName(request.Scope)) return BasisStorageStatus.Invalid;
            if (BasisStorageProtocol.HasKey(request.Op) && !BasisStorageProtocol.IsValidName(request.Key)) return BasisStorageStatus.Invalid;
            if (request.Op == BasisStorageOp.Set && (request.Value == null || request.Value.Length > BasisStorageProtocol.MaxValueBytes)) return BasisStorageStatus.Invalid;
            return BasisStorageStatus.Ok;
        }
        private static bool? Supports(NetPeer peer)
        {
            if (peer == null) return false;
            if (!Equals(BasisClientMessageRegistry.LastSupplyPeer, peer)) return null;
            SerializableBasis.BasisMessageDescriptor[] supply = BasisClientMessageRegistry.LastSupply;
            if (supply == null) return null;
            for (int i = 0; i < supply.Length; i++)
            {
                if (supply[i].Channel == BasisNetworkCommons.StorageChannel && supply[i].Name == MessageName) return true;
            }
            return false;
        }
        private static int InFlightFor(UnityEngine.Object owner)
        {
            int count = 0;
            foreach (Pending entry in pending.Values)
            {
                if (ReferenceEquals(entry.Owner, owner)) count++;
            }
            return count;
        }
        private static ushort NextId()
        {
            do
            {
                lastId++;
            }
            while (lastId == 0 || pending.ContainsKey(lastId));
            return lastId;
        }
        private static void Send(Pending entry)
        {
            NetDataWriter writer = new NetDataWriter(true, 160 + (entry.Request.Value != null ? entry.Request.Value.Length : 0));
            BasisStorageProtocol.WriteRequest(writer, entry.Request);
            entry.Peer.Send(writer, BasisNetworkCommons.StorageChannel, DeliveryMethod.ReliableOrdered);
            entry.Sent = true;
        }
        private static void Complete(NetPeer peer, BasisStorageReply reply)
        {
            if (!pending.TryGetValue(reply.RequestId, out Pending entry) || !entry.Sent || entry.Request.Op != reply.Op || !Equals(entry.Peer, peer)) return;
            pending.Remove(reply.RequestId);
            if (reply.Limit > 0)
            {
                LastUsed = reply.Used;
                LastLimit = reply.Limit;
            }
            entry.Reply = reply;
            Deliver(entry);
        }
        private static void Finish(Pending entry, BasisStorageStatus status)
        {
            entry.Reply = new BasisStorageReply { Op = entry.Request.Op, RequestId = entry.Request.RequestId, Status = status, Used = LastUsed, Limit = LastLimit };
            completed.Add(entry);
            StartTicking();
        }
        private static void Deliver(Pending entry)
        {
            if (entry.Done == null || (entry.HasOwner && entry.Owner == null)) return;
            try
            {
                entry.Done(entry.Reply);
            }
            catch (Exception e)
            {
                BasisDebug.LogErrorUnreported($"[BasisServerStorageClient] A storage callback threw: {e}", BasisDebug.LogTag.Networking);
            }
        }
        private static void StartTicking()
        {
            if (ticking) return;
            ticking = true;
            BasisFrameClock.OnTick += Tick;
            BasisFrameClock.AddRequest();
        }
        private static void StopTicking()
        {
            if (!ticking) return;
            ticking = false;
            BasisFrameClock.OnTick -= Tick;
            BasisFrameClock.RemoveRequest();
        }
        private static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            NetPeer current = BasisNetworkConnection.LocalPlayerPeer;
            bool connected = IsConnected;
            sweep.Clear();
            sweep.AddRange(completed);
            completed.Clear();
            foreach (Pending entry in pending.Values)
            {
                BasisStorageStatus status = BasisStorageStatus.Ok;
                if (!connected || !Equals(entry.Peer, current)) status = BasisStorageStatus.NotConnected;
                else if (now >= entry.Deadline) status = BasisStorageStatus.TimedOut;
                else if (!entry.Sent)
                {
                    bool? supported = Supports(current);
                    if (supported == false) status = BasisStorageStatus.Unavailable;
                    else if (supported == true) Send(entry);
                }
                if (status == BasisStorageStatus.Ok) continue;
                entry.Reply = new BasisStorageReply { Op = entry.Request.Op, RequestId = entry.Request.RequestId, Status = status, Used = LastUsed, Limit = LastLimit };
                sweep.Add(entry);
            }
            Pending[] finished = sweep.ToArray();
            sweep.Clear();
            foreach (Pending entry in finished)
            {
                if (pending.TryGetValue(entry.Request.RequestId, out Pending held) && ReferenceEquals(held, entry)) pending.Remove(entry.Request.RequestId);
            }
            foreach (Pending entry in finished) Deliver(entry);
            if (pending.Count == 0 && completed.Count == 0) StopTicking();
        }
        private sealed class Pending
        {
            public BasisStorageRequest Request;
            public BasisStorageReply Reply;
            public NetPeer Peer;
            public UnityEngine.Object Owner;
            public bool HasOwner, Sent;
            public float Deadline;
            public Action<BasisStorageReply> Done;
        }
    }
}
