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
        internal byte[] Assembly;
        internal int AssemblyLength, AssemblyExpected;
        internal uint AssemblySequence, LastVideoSequence = uint.MaxValue;
        internal ushort NextIndex;
        internal long AssemblyPts;
        internal bool AssemblyKey, Assembling;
        public volatile bool NeedKeyframe = true;

        public void ResetAssembly()
        {
            Assembling = false;
            LastVideoSequence = uint.MaxValue;
            NeedKeyframe = true;
        }
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
        MainThread.Enqueue(() =>
        {
            Locked = false;
            ClearRemotes();
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
            peer.Send(writer, BasisNetworkCommons.PluginReliableChannel, MediaDelivery);
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
        if (remote.Route == RouteDirect) SendDirectKeyframeRequest(remote.Sharer);
        else if (remote.Route == RouteServer) SendKeyframeRequest(remote.Sharer);
    }

    public static int PendingUplink()
    {
        NetPeer peer = BasisNetworkConnection.LocalPlayerPeer;
        return peer == null ? 0 : peer.GetPacketsCountInQueue(BasisNetworkCommons.GetPluginChannelForDelivery(MediaDelivery), MediaDelivery);
    }

    static void OnControl(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        try
        {
            if (!reader.TryGetByte(out byte sub)) return;
            switch (sub)
            {
                case AcceptedSub:
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

    static void ClearRemotes()
    {
        List<Remote> released;
        lock (RemotesLock)
        {
            released = new List<Remote>(Remotes.Values);
            Remotes.Clear();
        }
        foreach (Remote remote in released)
        {
            remote.Ended = true;
            Release(remote);
        }
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
        handle.Completed += op =>
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
        };
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
        if (header.Kind == KindAudio)
        {
            if (header.Count != 1) return;
            Enqueue(remote, payload, header.PtsUs, true, KindAudio);
            return;
        }
        if (header.IsFirst)
        {
            if (remote.Assembling) remote.NeedKeyframe = true;
            bool continuous = remote.LastVideoSequence != uint.MaxValue && header.Sequence == unchecked(remote.LastVideoSequence + 1);
            if (!header.IsKey && (remote.NeedKeyframe || !continuous))
            {
                remote.NeedKeyframe = true;
                remote.Assembling = false;
                return;
            }
            if (remote.Assembly == null || remote.Assembly.Length < header.TotalLength) remote.Assembly = new byte[Mathf.NextPowerOfTwo(header.TotalLength)];
            remote.Assembling = true;
            remote.AssemblySequence = header.Sequence;
            remote.AssemblyExpected = header.TotalLength;
            remote.AssemblyLength = 0;
            remote.AssemblyPts = header.PtsUs;
            remote.AssemblyKey = header.IsKey;
            remote.NextIndex = 0;
        }
        if (!remote.Assembling || header.Sequence != remote.AssemblySequence || header.Index != remote.NextIndex || remote.AssemblyLength + payload.Length > remote.AssemblyExpected)
        {
            remote.Assembling = false;
            remote.NeedKeyframe = true;
            return;
        }
        payload.CopyTo(new Span<byte>(remote.Assembly, remote.AssemblyLength, payload.Length));
        remote.AssemblyLength += payload.Length;
        remote.NextIndex++;
        if (remote.NextIndex < header.Count) return;
        remote.Assembling = false;
        if (remote.AssemblyLength != remote.AssemblyExpected)
        {
            remote.NeedKeyframe = true;
            return;
        }
        remote.LastVideoSequence = remote.AssemblySequence;
        if (remote.AssemblyKey) remote.NeedKeyframe = false;
        Enqueue(remote, new ReadOnlySpan<byte>(remote.Assembly, 0, remote.AssemblyLength), remote.AssemblyPts, remote.AssemblyKey, KindVideo);
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
