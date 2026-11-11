using Basis.Network.Core;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using static Basis.Network.Core.BasisScreenShareProtocol;
using static BasisPermissions.PermissionManager;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace Basis.Network.Server.Generic
{
    public static class BasisNetworkScreenShare
    {
        public const int ViewerQueueLimit = 400;
        const double BurstSeconds = 2.0;
        const double KilobitsToBytes = 125.0;
        static readonly long KeyRequestInterval = Stopwatch.Frequency / 2, CongestionInterval = Stopwatch.Frequency / 4;
        sealed class Viewer
        {
            public bool NeedsKey = true;
        }
        sealed class FrameRoute
        {
            public uint Sequence = uint.MaxValue;
            public bool Cancelled;
            public readonly List<NetPeer> Targets = new List<NetPeer>();
        }
        sealed class Bucket
        {
            public double Tokens;
            public long LastTicks;
            public bool TryTake(double rate, long bytes)
            {
                if (rate <= 0) return true;
                long now = Stopwatch.GetTimestamp();
                double ceiling = rate * BurstSeconds;
                Tokens = LastTicks == 0 ? ceiling : Math.Min(ceiling, Tokens + rate * (now - LastTicks) / Stopwatch.Frequency);
                LastTicks = now;
                if (Tokens < bytes) return false;
                Tokens -= bytes;
                return true;
            }
        }
        sealed class Session
        {
            public ushort Sharer;
            public StreamInfo Info;
            public ScreenPose Placement;
            public readonly Dictionary<ushort, Viewer> Viewers = new Dictionary<ushort, Viewer>();
            public readonly Bucket Ingress = new Bucket(), Egress = new Bucket();
            public long LastKeyRequest;
            public bool DroppingVideo;
            public readonly FrameRoute[] Frames = new FrameRoute[ReorderFrames];
            public uint NewestSequence;
            public bool HasNewest;
            public long LastCongestionReport;
            public int ReportedCongestion = -1;
        }
        static readonly ConcurrentDictionary<ushort, Session> Sessions = new ConcurrentDictionary<ushort, Session>();
        static ushort controlId, mediaId;
        static bool registered;
        static long droppedChunks;
        public static long DroppedChunks => System.Threading.Interlocked.Read(ref droppedChunks);
        public static int ActiveCount => Sessions.Count;
        public static bool IsSharing(ushort sharer) => Sessions.ContainsKey(sharer);
        public static bool Locked => NetworkServer.Configuration?.ScreenShareLocked ?? false;
        public static int MaxKilobitsPerSecond => Math.Max(500, NetworkServer.Configuration?.ScreenShareMaxKilobitsPerSecond ?? 12000);

        public static int ViewerCount(ushort sharer)
        {
            if (!Sessions.TryGetValue(sharer, out Session session)) return 0;
            lock (session) return session.Viewers.Count;
        }

        public static void Register()
        {
            if (registered) return;
            registered = true;
            controlId = BasisServerMessageRegistry.RegisterServerPlugin(ControlMessage, ControlDelivery, HandleControl);
            mediaId = BasisServerMessageRegistry.RegisterServerPlugin(MediaMessage, MediaDelivery, HandleMedia);
            BasisServerMessageRegistry.PeerSubscribed += SendActiveSessions;
        }

        static void Send(NetPeer peer, ushort id, Action<NetDataWriter> write)
        {
            if (peer == null || !BasisServerMessageRegistry.IsSubscribed(peer.Id, id)) return;
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(id);
            write(writer);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.GetPluginChannelForDelivery(ControlDelivery), ControlDelivery);
            NetworkServer.ReturnWriter(writer);
        }

        static void SendControl(ushort peerId, Action<NetDataWriter> write)
        {
            if (NetworkServer.AuthenticatedPeers.TryGetValue(peerId, out NetPeer peer)) Send(peer, controlId, write);
        }

        static void BroadcastControl(ushort except, Action<NetDataWriter> write)
        {
            foreach (NetPeer peer in NetworkServer.PeerSnapshot)
            {
                if (peer == null || peer.Id == except) continue;
                Send(peer, controlId, write);
            }
        }

        static void WriteAnnounce(NetDataWriter writer, Session session)
        {
            writer.Put(AnnounceSub);
            writer.Put(session.Sharer);
            session.Info.Serialize(writer);
            session.Placement.Serialize(writer);
        }

        static void WritePolicy(NetDataWriter writer)
        {
            writer.Put(PolicySub);
            writer.Put(Locked);
        }

        static void SendActiveSessions(NetPeer peer)
        {
            Send(peer, controlId, WritePolicy);
            foreach (Session session in Sessions.Values)
            {
                if (session.Sharer == peer.Id) continue;
                lock (session) Send(peer, controlId, w => WriteAnnounce(w, session));
            }
        }

        static void RequestKeyframe(Session session, bool force)
        {
            long now = Stopwatch.GetTimestamp();
            if (!force && now - session.LastKeyRequest < KeyRequestInterval) return;
            session.LastKeyRequest = now;
            SendControl(session.Sharer, w => w.Put(KeyframeSub));
        }

        static void SendViewerCount(Session session)
        {
            int count = session.Viewers.Count;
            SendControl(session.Sharer, w =>
            {
                w.Put(ViewersSub);
                w.Put((ushort)Math.Min(count, ushort.MaxValue));
            });
        }

        static bool HasLockBypass(NetPeer peer)
        {
            return NetworkServer.AuthIdentity != null && NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) && PermissionIntegration.HasValidRequirement(uuid, PermNodes.ModerationGlobalLock);
        }

        static bool HasLockBypass(ushort peerId) => NetworkServer.AuthenticatedPeers.TryGetValue(peerId, out NetPeer peer) && HasLockBypass(peer);

        public static void SetLocked(bool locked)
        {
            Configuration configuration = NetworkServer.Configuration;
            if (configuration == null) return;
            configuration.ScreenShareLocked = locked;
            try { configuration.SaveToXml(Configuration.GetDefaultPath()); }
            catch (Exception e) { BNL.LogError($"Screen share lock was not saved: {e.Message}"); }
            BroadcastControl(ushort.MaxValue, WritePolicy);
            if (!locked) return;
            foreach (ushort sharer in new List<ushort>(Sessions.Keys))
            {
                if (HasLockBypass(sharer)) continue;
                Refuse(sharer, RefusedLocked);
                End(sharer);
            }
        }

        static void HandleLock(NetPeer peer, NetPacketReader reader)
        {
            if (!reader.TryGetBool(out bool locked) || !HasLockBypass(peer)) return;
            SetLocked(locked);
            string notification = locked ? "Screen sharing has been globally DISABLED by an admin." : "Screen sharing has been globally ENABLED by an admin.";
            BNL.Log(notification);
            NetDataWriter writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.MessageAll);
            writer.Put(notification);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.AdminChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        static bool CanShare(NetPeer peer)
        {
            return NetworkServer.AuthIdentity != null && NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) && PermissionIntegration.HasValidRequirement(uuid, PermNodes.ScreenShare);
        }

        public static void HandleControl(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
        {
            try
            {
                if (!reader.TryGetByte(out byte sub)) return;
                ushort from = (ushort)peer.Id;
                switch (sub)
                {
                    case StartSub: HandleStart(peer, from, reader); break;
                    case InfoSub: HandleInfo(from, reader); break;
                    case StopSub: End(from); break;
                    case PoseSub: HandlePose(from, reader); break;
                    case MoveSub: HandleMove(from, reader); break;
                    case WatchSub: HandleWatch(from, reader); break;
                    case KeyframeSub: HandleKeyframe(from, reader); break;
                    case LockSub: HandleLock(peer, reader); break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }

        static void Refuse(ushort to, byte reason)
        {
            BNL.Log($"Screen share refused for player {to}, reason {reason}");
            SendControl(to, w =>
            {
                w.Put(RefusedSub);
                w.Put(reason);
            });
        }

        static void HandleStart(NetPeer peer, ushort from, NetPacketReader reader)
        {
            StreamInfo info = default;
            ScreenPose pose = default;
            if (!info.TryDeserialize(reader) || !pose.TryDeserialize(reader))
            {
                Refuse(from, RefusedInvalid);
                return;
            }
            if (!CanShare(peer))
            {
                Refuse(from, RefusedNoPermission);
                return;
            }
            if (Locked && !HasLockBypass(peer))
            {
                Refuse(from, RefusedLocked);
                return;
            }
            int max = NetworkServer.Configuration?.ScreenShareMaxConcurrent ?? 8;
            if (!Sessions.ContainsKey(from) && max > 0 && Sessions.Count >= max)
            {
                Refuse(from, RefusedTooMany);
                return;
            }
            Session session = Sessions.GetOrAdd(from, id => new Session { Sharer = id });
            lock (session)
            {
                session.Info = info;
                session.Placement = pose;
                foreach (Viewer viewer in session.Viewers.Values) viewer.NeedsKey = true;
                session.HasNewest = false;
                foreach (FrameRoute route in session.Frames) if (route != null) route.Sequence = uint.MaxValue;
                int kbps = MaxKilobitsPerSecond;
                int viewers = session.Viewers.Count;
                BNL.Log($"Screen share started by player {from}: {info.Width}x{info.Height} {info.Fps} fps, audio {info.HasAudio}");
                SendControl(from, w =>
                {
                    w.Put(AcceptedSub);
                    w.Put((uint)kbps);
                    w.Put((ushort)Math.Min(viewers, ushort.MaxValue));
                });
                BroadcastControl(from, w => WriteAnnounce(w, session));
            }
        }

        static void HandleInfo(ushort from, NetPacketReader reader)
        {
            if (!Sessions.TryGetValue(from, out Session session)) return;
            StreamInfo info = default;
            if (!info.TryDeserialize(reader)) return;
            lock (session)
            {
                session.Info = info;
                foreach (Viewer viewer in session.Viewers.Values) viewer.NeedsKey = true;
                BroadcastControl(from, w => WriteAnnounce(w, session));
            }
        }

        static void HandlePose(ushort from, NetPacketReader reader)
        {
            if (!Sessions.TryGetValue(from, out Session session)) return;
            ScreenPose pose = default;
            if (!pose.TryDeserialize(reader)) return;
            lock (session) session.Placement = pose;
            BroadcastControl(from, w =>
            {
                w.Put(PoseSub);
                w.Put(from);
                pose.Serialize(w);
            });
        }

        static void HandleMove(ushort from, NetPacketReader reader)
        {
            if (!reader.TryGetUShort(out ushort sharer) || !Sessions.TryGetValue(sharer, out Session session)) return;
            ScreenPose pose = default;
            if (!pose.TryDeserialize(reader)) return;
            lock (session) session.Placement = pose;
            BroadcastControl(from, w =>
            {
                w.Put(PoseSub);
                w.Put(sharer);
                pose.Serialize(w);
            });
        }

        static void HandleWatch(ushort from, NetPacketReader reader)
        {
            if (!reader.TryGetUShort(out ushort sharer) || !reader.TryGetBool(out bool watching) || sharer == from) return;
            if (!Sessions.TryGetValue(sharer, out Session session)) return;
            lock (session)
            {
                bool changed;
                if (watching)
                {
                    changed = !session.Viewers.ContainsKey(from);
                    if (changed) session.Viewers[from] = new Viewer();
                    RequestKeyframe(session, changed);
                }
                else
                {
                    changed = session.Viewers.Remove(from);
                }
                if (changed) SendViewerCount(session);
            }
        }

        static void HandleKeyframe(ushort from, NetPacketReader reader)
        {
            if (!reader.TryGetUShort(out ushort sharer) || !Sessions.TryGetValue(sharer, out Session session)) return;
            lock (session)
            {
                if (!session.Viewers.TryGetValue(from, out Viewer viewer)) return;
                viewer.NeedsKey = true;
                RequestKeyframe(session, false);
            }
        }

        public static void End(ushort sharer)
        {
            if (!Sessions.TryRemove(sharer, out _)) return;
            BNL.Log($"Screen share by player {sharer} ended");
            BroadcastControl(sharer, w =>
            {
                w.Put(EndedSub);
                w.Put(sharer);
            });
        }

        public static void RemovePeer(int id)
        {
            ushort peerId = (ushort)id;
            End(peerId);
            foreach (Session session in Sessions.Values)
            {
                lock (session)
                {
                    if (session.Viewers.Remove(peerId)) SendViewerCount(session);
                }
            }
        }

        public static void HandleMedia(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
        {
            try
            {
                ushort from = (ushort)peer.Id;
                if (!Sessions.TryGetValue(from, out Session session)) return;
                int start = reader.Position;
                ChunkHeader header = default;
                if (!header.TryDeserialize(reader)) return;
                int length = reader.Position - start + reader.AvailableBytes;
                ReadOnlySpan<byte> chunk = new ReadOnlySpan<byte>(reader.RawData, start, length);
                lock (session)
                {
                    ReportCongestion(session);
                    FrameRoute route = null;
                    if (header.Kind == KindVideo)
                    {
                        route = RouteFor(session, header);
                        if (route == null || route.Cancelled) return;
                    }
                    if (!session.Ingress.TryTake(MaxKilobitsPerSecond * KilobitsToBytes * 1.5, length))
                    {
                        Drop(session, route);
                        return;
                    }
                    if (session.Viewers.Count == 0) return;
                    List<NetPeer> targets = route != null ? route.Targets : AudioTargets(session);
                    if (targets.Count == 0) return;
                    double egressRate = (NetworkServer.Configuration?.ScreenShareEgressMegabitsPerSecond ?? 200) * 125_000.0;
                    if (!session.Egress.TryTake(egressRate, (long)(length + 4) * targets.Count))
                    {
                        Drop(session, route);
                        return;
                    }
                    if (route != null) session.DroppingVideo = false;
                    NetDataWriter writer = NetworkServer.RentWriter(length + 8);
                    writer.Put(mediaId);
                    writer.Put(from);
                    writer.Put(chunk);
                    byte lane = LaneFor(header, out DeliveryMethod laneDelivery);
                    for (int i = 0; i < targets.Count; i++) NetworkServer.TrySend(targets[i], writer, lane, laneDelivery);
                    NetworkServer.ReturnWriter(writer);
                }
            }
            finally
            {
                if (reader.AvailableBytes > 0) reader.SkipBytes(reader.AvailableBytes);
                reader.Recycle();
            }
        }

        static FrameRoute RouteFor(Session session, ChunkHeader header)
        {
            uint sequence = header.Sequence;
            if (session.HasNewest && (int)(session.NewestSequence - sequence) >= ReorderFrames) return null;
            FrameRoute route = session.Frames[sequence % ReorderFrames] ??= new FrameRoute();
            if (route.Sequence == sequence) return route;
            if (!session.HasNewest || (int)(sequence - session.NewestSequence) > 0)
            {
                session.NewestSequence = sequence;
                session.HasNewest = true;
            }
            route.Sequence = sequence;
            route.Cancelled = false;
            route.Targets.Clear();
            ChooseVideoTargets(session, header.IsKey, route.Targets);
            return route;
        }

        static void Drop(Session session, FrameRoute route)
        {
            System.Threading.Interlocked.Increment(ref droppedChunks);
            if (route == null) return;
            route.Cancelled = true;
            foreach (NetPeer target in route.Targets)
            {
                if (session.Viewers.TryGetValue((ushort)target.Id, out Viewer viewer)) viewer.NeedsKey = true;
            }
            route.Targets.Clear();
            if (!session.DroppingVideo)
            {
                session.DroppingVideo = true;
                RequestKeyframe(session, false);
            }
        }

        [ThreadStatic] static List<NetPeer> audioScratch;

        static List<NetPeer> AudioTargets(Session session)
        {
            List<NetPeer> targets = audioScratch ??= new List<NetPeer>();
            targets.Clear();
            foreach (KeyValuePair<ushort, Viewer> pair in session.Viewers)
            {
                if (!NetworkServer.AuthenticatedPeers.TryGetValue(pair.Key, out NetPeer target) || !BasisServerMessageRegistry.IsSubscribed(target.Id, mediaId)) continue;
                if (MediaBacklog(target) <= ViewerQueueLimit) targets.Add(target);
            }
            return targets;
        }

        static void ChooseVideoTargets(Session session, bool key, List<NetPeer> targets)
        {
            bool requestKey = false;
            foreach (KeyValuePair<ushort, Viewer> pair in session.Viewers)
            {
                Viewer viewer = pair.Value;
                if (!NetworkServer.AuthenticatedPeers.TryGetValue(pair.Key, out NetPeer target) || !BasisServerMessageRegistry.IsSubscribed(target.Id, mediaId)) continue;
                if (MediaBacklog(target) > ViewerQueueLimit)
                {
                    viewer.NeedsKey = true;
                    requestKey = true;
                    continue;
                }
                if (viewer.NeedsKey && !key)
                {
                    requestKey = true;
                    continue;
                }
                if (key) viewer.NeedsKey = false;
                targets.Add(target);
            }
            if (requestKey) RequestKeyframe(session, false);
        }

        static void ReportCongestion(Session session)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - session.LastCongestionReport < CongestionInterval) return;
            session.LastCongestionReport = now;
            int worst = 0;
            foreach (ushort viewer in session.Viewers.Keys)
            {
                if (NetworkServer.AuthenticatedPeers.TryGetValue(viewer, out NetPeer target)) worst = Math.Max(worst, MediaBacklog(target));
            }
            worst = Math.Min(worst, ushort.MaxValue);
            if (worst == 0 && session.ReportedCongestion == 0) return;
            session.ReportedCongestion = worst;
            SendControl(session.Sharer, w =>
            {
                w.Put(CongestionSub);
                w.Put((ushort)worst);
            });
        }

        public static void Reset()
        {
            Sessions.Clear();
            System.Threading.Interlocked.Exchange(ref droppedChunks, 0);
        }
    }
}
