using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using static Basis.Network.Core.BasisScreenShareProtocol;

[AutoStaticsCleanup]
public static partial class BasisScreenShareClient
{
    public sealed class Remote
    {
        public ushort Sharer;
        public StreamInfo Info;
        public ScreenPose Placement;
        public int InfoVersion, PoseVersion;
        public volatile bool Ended, Watching, Hidden;
        public volatile int Route;
        public long PushHandle;
        public volatile bool ReopenRequested;
        public BasisScreenShare Screen;
        internal readonly PendingFrame[] Frames = new PendingFrame[ReorderFrames];
        internal uint NextSequence, NewestComplete;
        internal bool HasNext, HasNewestComplete;
        internal volatile bool ResetPending;
        internal readonly Queue<HeldAudio> HeldAudio = new Queue<HeldAudio>();
        internal long LastVideoPtsUs = long.MinValue;
        public volatile bool NeedKeyframe = true;
        public int FramesReceived, FramesBroken, KeyframeRequests;
        public long TransitMinUs = long.MaxValue, TransitExcessMaxUs;

        public void ResetAssembly()
        {
            ResetPending = true;
            NeedKeyframe = true;
        }
    }

    internal sealed class PendingFrame
    {
        public uint Sequence = uint.MaxValue;
        public byte[] Data, Got;
        public int Count, Received, Length, Expected;
        public long PtsUs;
        public bool Key, Complete;

        public void Begin(uint sequence, int count)
        {
            Release();
            Sequence = sequence;
            Count = count;
            Received = Length = 0;
            Expected = -1;
            PtsUs = 0;
            Key = Complete = false;
            Data = System.Buffers.ArrayPool<byte>.Shared.Rent(count * MaxChunkPayload);
            Got = System.Buffers.ArrayPool<byte>.Shared.Rent(count);
            Array.Clear(Got, 0, count);
        }

        public void Release()
        {
            if (Data != null) System.Buffers.ArrayPool<byte>.Shared.Return(Data);
            if (Got != null) System.Buffers.ArrayPool<byte>.Shared.Return(Got);
            Data = Got = null;
            Sequence = uint.MaxValue;
            Complete = false;
        }
    }

    public const int StuckFrames = 30;
    public const long AudioLeadUs = 150_000, AudioHoldMaxUs = 1_000_000;

    internal struct HeldAudio
    {
        public byte[] Data;
        public int Length;
        public long PtsUs, ArrivedUs;
    }

    public const int RouteNone = 0, RouteServer = 1, RouteDirect = 2;
    public const int DirectQueueLimit = 900;
    [ThreadStatic] static NetDataWriter threadWriter;
    static NetDataWriter Writer() => threadWriter ??= new NetDataWriter(true, 1400);
    internal delegate int FrameSink(Remote remote, ReadOnlySpan<byte> data, long ptsUs, bool key, byte kind);
    internal static FrameSink Sink = PushToDecoder;
    static readonly List<ushort> DirectScratch = new List<ushort>();

    public static event Action Changed;
    public static BasisScreenShare Local { get; private set; }
    public static bool Locked { get; private set; }
    public static bool LockedForMe => Locked && !BasisNetworkModeration.LocalPlayerHasGlobalLockBypass();
    public static int ServerMaxKbps { get; private set; }
    public static volatile int ServerCongestion;
    public static readonly Dictionary<ushort, Remote> Remotes = new Dictionary<ushort, Remote>();
    static readonly ConcurrentQueue<Action> MainThread = new ConcurrentQueue<Action>();
    [NoAutoStaticsCleanup] static readonly object RemotesLock = new object();

    public static bool Available => BasisClientMessageRegistry.IsPluginSupplied(ControlMessage) && BasisClientMessageRegistry.IsPluginSupplied(MediaMessage);
    public static int Count => (Local != null ? 1 : 0) + RemoteCount;

    public static int RemoteCount
    {
        get
        {
            lock (RemotesLock) return Remotes.Count;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register()
    {
        BasisClientMessageRegistry.RegisterClientPlugin(ControlMessage, OnControl);
        BasisClientMessageRegistry.RegisterClientPlugin(MediaMessage, OnMedia);
        BasisClientMessageRegistry.OnSupplyApplied -= OnSupplyApplied;
        BasisClientMessageRegistry.OnSupplyApplied += OnSupplyApplied;
        BasisP2PManager.OnDirectPluginMessage -= OnDirect;
        BasisP2PManager.OnDirectPluginMessage += OnDirect;
        BasisScreenShareSettings.EnsureLoaded();
    }

    static void OnSupplyApplied()
    {
        List<Remote> released = TakeRemotes();
        MainThread.Enqueue(() =>
        {
            Locked = false;
            ReleaseAll(released);
            if (Local != null) Local.OnReconnected();
        });
    }

    public static void Pump()
    {
        while (MainThread.TryDequeue(out Action action))
        {
            try { action(); }
            catch (Exception e) { BasisDebug.LogError($"[ScreenShare] {e}"); }
        }
    }

    static void RaiseChanged() => Changed?.Invoke();

    public static bool RegisterLocal(BasisScreenShare screen)
    {
        if (Local != null && Local != screen) return false;
        Local = screen;
        RaiseChanged();
        return true;
    }

    public static void UnregisterLocal(BasisScreenShare screen)
    {
        if (Local != screen) return;
        Local = null;
        RaiseChanged();
    }

    public static string SharerName(ushort sharer)
    {
        if (BasisNetworkPlayers.GetPlayerById(sharer, out BasisNetworkPlayer player) && player.Player != null && !string.IsNullOrEmpty(player.Player.SafeDisplayName)) return player.Player.SafeDisplayName;
        return "#" + sharer;
    }

    static bool Send(string name, Action<NetDataWriter> write) => BasisClientMessageRegistry.Send(name, write);

    public static bool SendStart(StreamInfo info, ScreenPose pose)
    {
        bool sent = Send(ControlMessage, w =>
        {
            w.Put(StartSub);
            info.Serialize(w);
            pose.Serialize(w);
        });
        BasisDebug.Log($"[ScreenShare] start {(sent ? "sent" : "not sent (server has no screen share)")}: {info.Width}x{info.Height} {info.Fps} fps audio {info.HasAudio}", BasisDebug.LogTag.Video);
        return sent;
    }

    public static void SendInfo(StreamInfo info) => Send(ControlMessage, w =>
    {
        w.Put(InfoSub);
        info.Serialize(w);
    });

    public static void SendStop() => Send(ControlMessage, w => w.Put(StopSub));

    public static bool SendLock(bool locked) => Send(ControlMessage, w =>
    {
        w.Put(LockSub);
        w.Put(locked);
    });

    public static void SendPose(ScreenPose pose) => Send(ControlMessage, w =>
    {
        w.Put(PoseSub);
        pose.Serialize(w);
    });

    public static void SendMove(ushort sharer, ScreenPose pose) => Send(ControlMessage, w =>
    {
        w.Put(MoveSub);
        w.Put(sharer);
        pose.Serialize(w);
    });

    static ScreenPose localPlacement;
    static int localPoseVersion;

    public static int LocalPoseVersion
    {
        get
        {
            lock (RemotesLock) return localPoseVersion;
        }
    }

    public static bool TryGetPlacement(Remote remote, ref int seen, out ScreenPose pose)
    {
        lock (RemotesLock)
        {
            pose = remote != null ? remote.Placement : localPlacement;
            int version = remote != null ? remote.PoseVersion : localPoseVersion;
            if (version == seen) return false;
            seen = version;
            return true;
        }
    }

    public static void SendWatch(ushort sharer, bool watching) => Send(ControlMessage, w =>
    {
        w.Put(WatchSub);
        w.Put(sharer);
        w.Put(watching);
    });

    public static void SendKeyframeRequest(ushort sharer) => Send(ControlMessage, w =>
    {
        w.Put(KeyframeSub);
        w.Put(sharer);
    });


    public static bool HasDirectLink(ushort player) => BasisP2PManager.TryGetP2PPeer(player, out _);

    public static int DirectBacklog(ushort player)
    {
        if (!BasisP2PManager.TryGetP2PPeer(player, out NetPeer peer)) return int.MaxValue;
        return peer.GetPacketsCountInQueue(BasisNetworkCommons.PluginReliableChannel, DeliveryMethod.ReliableOrdered);
    }

    static bool SendDirect(ushort player, string name, Action<NetDataWriter> write)
    {
        if (!BasisClientMessageRegistry.TryGetPluginId(name, out ushort id)) return false;
        NetDataWriter writer = Writer();
        writer.Reset();
        writer.Put(id);
        write(writer);
        return BasisP2PManager.SendDirectTo(player, writer, BasisNetworkCommons.PluginReliableChannel, DeliveryMethod.ReliableOrdered);
    }

    static void WriteChunkTo(NetDataWriter writer, ushort id, uint sequence, byte kind, bool key, long ptsUs, int index, int count, int length, byte[] data)
    {
        int offset = index * MaxChunkPayload, size = Math.Min(MaxChunkPayload, length - offset);
        writer.Reset();
        writer.Put(id);
        new ChunkHeader { Sequence = sequence, Kind = kind, Flags = key ? FlagKey : (byte)0, Index = (ushort)index, Count = (ushort)count, PtsUs = ptsUs, TotalLength = length }.Serialize(writer);
        writer.Put(data, offset, size);
    }

    public static bool SendFrameRelay(uint sequence, byte kind, bool key, long ptsUs, byte[] data, int length)
    {
        if (length <= 0 || length > MaxFrameBytes || !BasisClientMessageRegistry.TryGetPluginId(MediaMessage, out ushort id)) return false;
        NetPeer peer = BasisNetworkConnection.LocalPlayerPeer;
        if (peer == null) return false;
        NetDataWriter writer = Writer();
        int count = ChunkCount(length);
        for (int i = 0; i < count; i++)
        {
            WriteChunkTo(writer, id, sequence, kind, key, ptsUs, i, count, length, data);
            byte lane = kind == KindAudio ? AudioChannel : VideoLane(i);
            peer.Send(writer, lane, kind == KindAudio ? AudioDelivery : VideoDelivery);
        }
        return true;
    }

    public static bool SendDirectWatch(ushort sharer, bool watching) => SendDirect(sharer, ControlMessage, w =>
    {
        w.Put(WatchSub);
        w.Put(watching);
    });

    public static bool SendDirectKeyframeRequest(ushort sharer) => SendDirect(sharer, ControlMessage, w => w.Put(KeyframeSub));

    public static void SendFrameDirect(List<ushort> targets, uint sequence, byte kind, bool key, long ptsUs, byte[] data, int length)
    {
        if (targets.Count == 0 || length <= 0 || length > MaxFrameBytes || !BasisClientMessageRegistry.TryGetPluginId(MediaMessage, out ushort id)) return;
        NetDataWriter writer = Writer();
        int count = ChunkCount(length);
        for (int i = 0; i < count; i++)
        {
            WriteChunkTo(writer, id, sequence, kind, key, ptsUs, i, count, length, data);
            for (int t = 0; t < targets.Count; t++) BasisP2PManager.SendDirectTo(targets[t], writer, BasisNetworkCommons.PluginReliableChannel, DeliveryMethod.ReliableOrdered);
        }
    }

    static void OnDirect(ushort sender, NetPacketReader reader, DeliveryMethod delivery)
    {
        try
        {
            ConsumeDirect(sender, reader);
        }
        finally
        {
            if (reader.AvailableBytes > 0) reader.SkipBytes(reader.AvailableBytes);
        }
    }

    static void ConsumeDirect(ushort sender, NetPacketReader reader)
    {
        if (!reader.TryGetUShort(out ushort id)) return;
        if (BasisClientMessageRegistry.TryGetPluginId(MediaMessage, out ushort mediaId) && id == mediaId)
        {
            ChunkHeader header = default;
            if (!header.TryDeserialize(reader)) return;
            Remote remote;
            lock (RemotesLock)
            {
                if (!Remotes.TryGetValue(sender, out remote)) return;
            }
            if (remote.Route != RouteDirect) return;
            Accept(remote, header, reader.GetRemainingBytesSpan());
            return;
        }
        if (!BasisClientMessageRegistry.TryGetPluginId(ControlMessage, out ushort controlId) || id != controlId || !reader.TryGetByte(out byte sub)) return;
        if (sub == WatchSub && reader.TryGetBool(out bool watching)) MainThread.Enqueue(() => { if (Local != null) Local.OnDirectWatch(sender, watching); });
        else if (sub == KeyframeSub) MainThread.Enqueue(() => { if (Local != null) Local.OnDirectKeyframe(sender); });
    }

    public static void SetRoute(Remote remote, int route)
    {
        int old = remote.Route;
        if (old == route) return;
        if (old == RouteServer) SendWatch(remote.Sharer, false);
        else if (old == RouteDirect) SendDirectWatch(remote.Sharer, false);
        remote.Watching = false;
        remote.ResetAssembly();
        remote.Route = route;
        remote.Watching = route != RouteNone;
        if (route == RouteServer) SendWatch(remote.Sharer, true);
        else if (route == RouteDirect && !SendDirectWatch(remote.Sharer, true))
        {
            remote.Route = RouteServer;
            SendWatch(remote.Sharer, true);
        }
    }

    public static void RequestKeyframe(Remote remote)
    {
        Interlocked.Increment(ref remote.KeyframeRequests);
        if (remote.Route == RouteDirect) SendDirectKeyframeRequest(remote.Sharer);
        else if (remote.Route == RouteServer) SendKeyframeRequest(remote.Sharer);
    }

    public static int PendingUplink()
    {
        NetPeer peer = BasisNetworkConnection.LocalPlayerPeer;
        return peer == null ? 0 : MediaBacklog(peer);
    }

    static void OnControl(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        try
        {
            if (!reader.TryGetByte(out byte sub)) return;
            switch (sub)
            {
                case AcceptedSub:
                    ServerCongestion = 0;
                    if (reader.TryGetUInt(out uint kbps) && reader.TryGetUShort(out ushort viewers)) MainThread.Enqueue(() =>
                    {
                        BasisDebug.Log($"[ScreenShare] accepted by server: max {kbps} kb/s, {viewers} viewers", BasisDebug.LogTag.Video);
                        ServerMaxKbps = (int)Math.Min(kbps, int.MaxValue);
                        if (Local != null) Local.OnAccepted(ServerMaxKbps, viewers);
                    });
                    break;
                case RefusedSub:
                    if (reader.TryGetByte(out byte reason)) MainThread.Enqueue(() =>
                    {
                        BasisDebug.LogWarning($"[ScreenShare] refused by server, reason {reason}", BasisDebug.LogTag.Video);
                        if (Local != null) Local.OnRefused(reason);
                    });
                    break;
                case KeyframeSub:
                    MainThread.Enqueue(() => { if (Local != null) Local.RequestKeyframe(); });
                    break;
                case CongestionSub:
                    if (reader.TryGetUShort(out ushort backlog)) ServerCongestion = backlog;
                    break;
                case ViewersSub:
                    if (reader.TryGetUShort(out ushort count)) MainThread.Enqueue(() => { if (Local != null) Local.SetViewers(count); });
                    break;
                case AnnounceSub:
                    OnAnnounce(reader);
                    break;
                case PolicySub:
                    if (reader.TryGetBool(out bool locked)) MainThread.Enqueue(() =>
                    {
                        if (Locked == locked) return;
                        Locked = locked;
                        RaiseChanged();
                    });
                    break;
                case PoseSub:
                    OnPose(reader);
                    break;
                case EndedSub:
                    if (reader.TryGetUShort(out ushort sharer)) OnEnded(sharer);
                    break;
            }
        }
        finally
        {
            reader.Recycle();
        }
    }

    static void OnAnnounce(NetPacketReader reader)
    {
        StreamInfo info = default;
        ScreenPose pose = default;
        if (!reader.TryGetUShort(out ushort sharer) || !info.TryDeserialize(reader) || !pose.TryDeserialize(reader)) return;
        Remote remote;
        bool created = false;
        lock (RemotesLock)
        {
            if (!Remotes.TryGetValue(sharer, out remote))
            {
                remote = new Remote { Sharer = sharer };
                Remotes[sharer] = remote;
                created = true;
            }
            remote.Info = info;
            remote.Placement = pose;
            remote.InfoVersion++;
            remote.PoseVersion++;
            remote.NeedKeyframe = true;
        }
        if (created) MainThread.Enqueue(() =>
        {
            BasisDebug.Log($"[ScreenShare] {SharerName(sharer)} is sharing {info.Width}x{info.Height}", BasisDebug.LogTag.Video);
            Spawn(remote);
        });
    }

    static void OnPose(NetPacketReader reader)
    {
        ScreenPose pose = default;
        if (!reader.TryGetUShort(out ushort sharer) || !pose.TryDeserialize(reader)) return;
        lock (RemotesLock)
        {
            if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort self) && self == sharer)
            {
                localPlacement = pose;
                localPoseVersion++;
                return;
            }
            if (!Remotes.TryGetValue(sharer, out Remote remote)) return;
            remote.Placement = pose;
            remote.PoseVersion++;
        }
    }

    static void OnEnded(ushort sharer)
    {
        Remote remote;
        lock (RemotesLock)
        {
            if (!Remotes.TryGetValue(sharer, out remote)) return;
            Remotes.Remove(sharer);
            remote.Ended = true;
        }
        MainThread.Enqueue(() =>
        {
            Release(remote);
            RaiseChanged();
        });
    }

    static void Release(Remote remote)
    {
        if (remote.Screen != null) UnityEngine.Object.Destroy(remote.Screen.gameObject);
        remote.Screen = null;
        Interlocked.Exchange(ref remote.PushHandle, 0);
    }

    static List<Remote> TakeRemotes()
    {
        List<Remote> released;
        lock (RemotesLock)
        {
            released = new List<Remote>(Remotes.Values);
            Remotes.Clear();
        }
        foreach (Remote remote in released) remote.Ended = true;
        return released;
    }

    static void ReleaseAll(List<Remote> released)
    {
        foreach (Remote remote in released) Release(remote);
        if (released.Count > 0) RaiseChanged();
    }

    public static int HiddenCount
    {
        get
        {
            int count = 0;
            lock (RemotesLock) foreach (Remote remote in Remotes.Values) if (remote.Hidden) count++;
            return count;
        }
    }

    public static void Hide(Remote remote)
    {
        if (remote == null || remote.Hidden) return;
        remote.Hidden = true;
        SetRoute(remote, RouteNone);
        remote.Watching = false;
        Release(remote);
        RaiseChanged();
    }

    public static void ShowHidden()
    {
        List<Remote> shown = new List<Remote>();
        lock (RemotesLock)
        {
            foreach (Remote remote in Remotes.Values)
            {
                if (!remote.Hidden) continue;
                remote.Hidden = false;
                remote.NeedKeyframe = true;
                shown.Add(remote);
            }
        }
        foreach (Remote remote in shown) Spawn(remote);
        if (shown.Count > 0) RaiseChanged();
    }

    static void Spawn(Remote remote)
    {
        if (remote.Ended || remote.Hidden || remote.Screen != null) return;
        var handle = Addressables.InstantiateAsync(BasisScreenShare.SpawnUrl);
        if (handle.IsDone) OnSpawned(remote, handle);
        else handle.Completed += op => OnSpawned(remote, op);
    }

    static void OnSpawned(Remote remote, AsyncOperationHandle<GameObject> op)
    {
        GameObject go = op.Result;
        if (go == null)
        {
            BasisDebug.LogError($"[ScreenShare] could not spawn the screen for {SharerName(remote.Sharer)}: {op.OperationException?.Message}", BasisDebug.LogTag.Video);
            return;
        }
        if (remote.Ended || remote.Hidden || remote.Screen != null || !go.TryGetComponent(out BasisScreenShare screen))
        {
            Addressables.ReleaseInstance(go);
            return;
        }
        screen.ConfigureRemote(remote);
        remote.Screen = screen;
        RaiseChanged();
    }

    static void OnMedia(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        try
        {
            ConsumeMedia(reader);
        }
        finally
        {
            if (reader.AvailableBytes > 0) reader.SkipBytes(reader.AvailableBytes);
            reader.Recycle();
        }
    }

    static void ConsumeMedia(NetPacketReader reader)
    {
        if (!reader.TryGetUShort(out ushort sharer)) return;
        ChunkHeader header = default;
        if (!header.TryDeserialize(reader)) return;
        Remote remote;
        lock (RemotesLock)
        {
            if (!Remotes.TryGetValue(sharer, out remote)) return;
        }
        if (remote.Route == RouteServer) Accept(remote, header, reader.GetRemainingBytesSpan());
    }

    internal static void Accept(Remote remote, ChunkHeader header, ReadOnlySpan<byte> payload)
    {
        if (remote.ResetPending) ResetFrames(remote);
        if (header.Kind == KindAudio)
        {
            if (header.Count != 1 || !remote.Watching) return;
            byte[] copy = System.Buffers.ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(copy);
            remote.HeldAudio.Enqueue(new HeldAudio { Data = copy, Length = payload.Length, PtsUs = header.PtsUs, ArrivedUs = NowUs() });
            ReleaseAudio(remote);
            return;
        }
        uint sequence = header.Sequence;
        if (remote.HasNext && (int)(sequence - remote.NextSequence) < 0) return;
        if (remote.HasNext && (int)(sequence - remote.NextSequence) >= ReorderFrames)
        {
            Interlocked.Increment(ref remote.FramesBroken);
            ResetFrames(remote);
        }
        PendingFrame frame = remote.Frames[sequence % ReorderFrames] ??= new PendingFrame();
        if (frame.Sequence != sequence)
        {
            if (frame.Data != null && !frame.Complete) Interlocked.Increment(ref remote.FramesBroken);
            frame.Begin(sequence, header.Count);
        }
        if (frame.Complete || header.Count != frame.Count || frame.Got[header.Index] != 0) return;
        bool last = header.Index == header.Count - 1;
        if (payload.Length > MaxChunkPayload || (!last && payload.Length != MaxChunkPayload))
        {
            Interlocked.Increment(ref remote.FramesBroken);
            frame.Release();
            return;
        }
        payload.CopyTo(new Span<byte>(frame.Data, header.Index * MaxChunkPayload, payload.Length));
        frame.Got[header.Index] = 1;
        frame.Received++;
        if (header.IsKey) frame.Key = true;
        if (last) frame.Length = header.Index * MaxChunkPayload + payload.Length;
        if (header.IsFirst)
        {
            frame.PtsUs = header.PtsUs;
            frame.Expected = header.TotalLength;
        }
        if (frame.Received < frame.Count) return;
        if (frame.Expected >= 0 && frame.Expected != frame.Length)
        {
            Interlocked.Increment(ref remote.FramesBroken);
            frame.Release();
            return;
        }
        frame.Complete = true;
        if (!remote.HasNewestComplete || (int)(sequence - remote.NewestComplete) > 0)
        {
            remote.NewestComplete = sequence;
            remote.HasNewestComplete = true;
        }
        Deliver(remote);
    }

    static void Deliver(Remote remote)
    {
        while (true)
        {
            PendingFrame next = remote.HasNext ? remote.Frames[remote.NextSequence % ReorderFrames] : null;
            if (next != null && next.Sequence == remote.NextSequence && next.Complete)
            {
                Emit(remote, next);
                continue;
            }
            PendingFrame key = OldestCompleteKey(remote);
            if (key == null)
            {
                if (!remote.HasNext || (remote.HasNewestComplete && (int)(remote.NewestComplete - remote.NextSequence) >= StuckFrames)) remote.NeedKeyframe = true;
                return;
            }
            remote.NextSequence = key.Sequence;
            remote.HasNext = true;
            foreach (PendingFrame stale in remote.Frames)
            {
                if (stale == null || stale.Data == null || (int)(stale.Sequence - key.Sequence) >= 0) continue;
                if (!stale.Complete) Interlocked.Increment(ref remote.FramesBroken);
                stale.Release();
            }
        }
    }

    static PendingFrame OldestCompleteKey(Remote remote)
    {
        PendingFrame best = null;
        foreach (PendingFrame frame in remote.Frames)
        {
            if (frame == null || !frame.Complete || !frame.Key) continue;
            if (remote.HasNext && (int)(frame.Sequence - remote.NextSequence) <= 0) continue;
            if (best == null || (int)(frame.Sequence - best.Sequence) < 0) best = frame;
        }
        return best;
    }

    static void Emit(Remote remote, PendingFrame frame)
    {
        remote.NextSequence = frame.Sequence + 1;
        remote.HasNext = true;
        Interlocked.Increment(ref remote.FramesReceived);
        TrackTransit(remote, frame.PtsUs);
        if (frame.Key) remote.NeedKeyframe = false;
        if (frame.PtsUs > remote.LastVideoPtsUs) remote.LastVideoPtsUs = frame.PtsUs;
        Enqueue(remote, new ReadOnlySpan<byte>(frame.Data, 0, frame.Length), frame.PtsUs, frame.Key, KindVideo);
        frame.Release();
        ReleaseAudio(remote);
    }

    static long NowUs() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * TicksToUs);

    static void ReleaseAudio(Remote remote)
    {
        long now = NowUs();
        while (remote.HeldAudio.Count > 0)
        {
            HeldAudio held = remote.HeldAudio.Peek();
            bool videoCaughtUp = remote.LastVideoPtsUs != long.MinValue && held.PtsUs <= remote.LastVideoPtsUs + AudioLeadUs;
            if (!videoCaughtUp && now - held.ArrivedUs < AudioHoldMaxUs) return;
            remote.HeldAudio.Dequeue();
            Enqueue(remote, new ReadOnlySpan<byte>(held.Data, 0, held.Length), held.PtsUs, true, KindAudio);
            System.Buffers.ArrayPool<byte>.Shared.Return(held.Data);
        }
    }

    static void ResetFrames(Remote remote)
    {
        remote.ResetPending = false;
        foreach (PendingFrame frame in remote.Frames) frame?.Release();
        remote.HasNext = remote.HasNewestComplete = false;
        remote.LastVideoPtsUs = long.MinValue;
        while (remote.HeldAudio.Count > 0) System.Buffers.ArrayPool<byte>.Shared.Return(remote.HeldAudio.Dequeue().Data);
    }

    static readonly double TicksToUs = 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;

    static void TrackTransit(Remote remote, long ptsUs)
    {
        long transit = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * TicksToUs) - ptsUs;
        long min = Interlocked.Read(ref remote.TransitMinUs);
        if (transit < min) Interlocked.Exchange(ref remote.TransitMinUs, min = transit);
        long excess = transit - min;
        if (excess > Interlocked.Read(ref remote.TransitExcessMaxUs)) Interlocked.Exchange(ref remote.TransitExcessMaxUs, excess);
    }

    static void Enqueue(Remote remote, ReadOnlySpan<byte> data, long ptsUs, bool key, byte kind)
    {
        if (!remote.Watching) return;
        int result = Sink(remote, data, ptsUs, key, kind);
        if (result == 0) return;
        if (result == 3 || result == 4) remote.ReopenRequested = true;
        if (kind == KindVideo || result >= 3) remote.NeedKeyframe = true;
    }

    static unsafe int PushToDecoder(Remote remote, ReadOnlySpan<byte> data, long ptsUs, bool key, byte kind)
    {
        ulong handle = (ulong)Interlocked.Read(ref remote.PushHandle);
        if (handle == 0) return kind == KindVideo ? 1 : 0;
        fixed (byte* p = data)
        {
            int result = kind == KindVideo ? BasisMediaNative.bm_session_push_video(handle, p, (UIntPtr)data.Length, ptsUs, key ? 1u : 0u, remote.Info.Width, remote.Info.Height) : BasisMediaNative.bm_session_push_audio(handle, p, (UIntPtr)data.Length, ptsUs, 48000, 2);
            return result < 0 ? 4 : result;
        }
    }

    internal static void ResetForTests()
    {
        lock (RemotesLock) Remotes.Clear();
        while (MainThread.TryDequeue(out _)) { }
        Local = null;
    }
}
