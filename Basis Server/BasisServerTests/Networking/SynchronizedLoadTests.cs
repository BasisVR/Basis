using Basis.Network.Core;
using BasisNetworkServer;
using Xunit;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class SynchronizedLoadTests : IDisposable
{
    private static int peerIdCounter = 48_000;
    private static int resourceCounter;
    private readonly List<FakeNetPeer> peers = new();
    private readonly List<string> started = new();
    private DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public SynchronizedLoadTests()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        BasisNetworkPredownloadManagement.Reset();
        BasisNetworkPreloadResourceManagement.Reset();
        BasisNetworkPreloadResourceManagement.Clock = () => now;
    }

    public void Dispose()
    {
        BasisNetworkPreloadResourceManagement.Reset();
        BasisNetworkPredownloadManagement.Reset();
        BasisNetworkPreloadResourceManagement.Clock = () => DateTime.UtcNow;
        foreach (string netId in started)
        {
            if (BasisNetworkResourceManagement.UshortNetworkDatabase.TryRemove(netId, out LocalLoadResource resource)) BasisNetworkResourceManagement.NoteResourceRemoved(resource.UUIDOfCreator);
        }
        foreach (FakeNetPeer peer in peers)
        {
            NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
            BasisServerMessageRegistry.ClearSubscription(peer.Id);
        }
        NetworkServer.RebuildPeerSnapshot();
    }

    private static ushort MessageId()
    {
        Assert.True(BasisServerMessageRegistry.TryGetPluginId(BasisPredownloadProtocol.MessageName, out ushort id));
        return id;
    }

    private static void Process(FakeNetPeer peer, NetDataWriter w, byte channel)
    {
        byte[] bytes = w.AsReadOnlySpan().ToArray();
        BasisNetworkMessageProcessor.ProcessMessage(peer, NetPacketReader.Create(bytes, 0, bytes.Length, () => { }), channel, DeliveryMethod.ReliableOrdered);
    }

    private FakeNetPeer Join(bool current = true)
    {
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.7.8") { Tag = NetworkServer.AuthenticatedPeerTag };
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        peers.Add(peer);
        NetDataWriter w = new NetDataWriter();
        w.Put(BasisNetworkCommons.RegistrySub_Subscribe);
        new BasisMessageSubscribe { Ids = current ? new[] { MessageId() } : new ushort[] { 1 } }.Serialize(w);
        Process(peer, w, BasisNetworkCommons.RegistryControlChannel);
        return peer;
    }

    private void Leave(FakeNetPeer peer)
    {
        NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
        NetworkServer.RebuildPeerSnapshot();
        BasisNetworkPreloadResourceManagement.RemovePeer(peer.Id);
    }

    private static void Status(FakeNetPeer peer, string netId)
    {
        NetDataWriter w = new NetDataWriter();
        w.Put(MessageId());
        w.Put(BasisPredownloadProtocol.OpStatus);
        w.Put(netId);
        Process(peer, w, BasisNetworkCommons.PluginReliableChannel);
    }

    private static void Ready(FakeNetPeer peer, string netId, bool ready = true) => BasisNetworkPreloadResourceManagement.HandleClientReady(netId, peer.Id, ready);

    private string Existing(byte mode, bool adminLocked = false)
    {
        int n = Interlocked.Increment(ref resourceCounter);
        LocalLoadResource resource = new LocalLoadResource { Mode = mode, LoadedNetID = $"sync-existing-{n}", CombinedURL = $"https://content.invalid/existing-{n}.bee", UnlockPassword = string.Empty, UUIDOfCreator = "someone-else", IsAdminLocked = adminLocked };
        started.Add(resource.LoadedNetID);
        BasisNetworkResourceManagement.LoadResource(resource);
        Assert.True(BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey(resource.LoadedNetID));
        return resource.LoadedNetID;
    }

    private static bool Loaded(string netId) => BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey(netId);

    private static byte SwapSent(FakeNetPeer peer, string netId)
    {
        lock (peer.Sent)
        {
            foreach (var sent in peer.Sent.Where(s => s.Channel == BasisNetworkCommons.SpawnPreloadedChannel))
            {
                SpawnPreloadedMessage message = new SpawnPreloadedMessage();
                message.Deserialize(new NetDataReader(sent.Data));
                if (message.LoadedNetID == netId) return message.Swap;
            }
        }
        return byte.MaxValue;
    }

    private string Start(FakeNetPeer requester, byte mode = 0, byte swap = BasisPredownloadProtocol.SwapReplaceWorlds)
    {
        int n = Interlocked.Increment(ref resourceCounter);
        LocalLoadResource resource = new LocalLoadResource { Mode = mode, LoadedNetID = $"sync-test-{n}", CombinedURL = $"https://content.invalid/sync-{n}.bee", UnlockPassword = string.Empty, UUIDOfCreator = "tester", LoadStrategy = 2 };
        started.Add(resource.LoadedNetID);
        BasisNetworkPreloadResourceManagement.StartSynchronizedLoad(resource, requester.Id, swap);
        return resource.LoadedNetID;
    }

    private void Advance(double seconds, params FakeNetPeer[] alive)
    {
        double left = seconds;
        while (left > 0)
        {
            double step = Math.Min(3, left);
            now = now.AddSeconds(step);
            left -= step;
            foreach (string netId in started)
            {
                foreach (FakeNetPeer peer in alive) Status(peer, netId);
            }
            BasisNetworkPredownloadManagement.Flush();
        }
    }

    private static int SpawnSignals(FakeNetPeer peer, string netId)
    {
        int count = 0;
        lock (peer.Sent)
        {
            foreach (var sent in peer.Sent.Where(s => s.Channel == BasisNetworkCommons.SpawnPreloadedChannel))
            {
                SpawnPreloadedMessage message = new SpawnPreloadedMessage();
                message.Deserialize(new NetDataReader(sent.Data));
                if (message.LoadedNetID == netId) count++;
            }
        }
        return count;
    }

    private static List<byte> Strategies(FakeNetPeer peer, string netId)
    {
        List<byte> found = new();
        lock (peer.Sent)
        {
            foreach (var sent in peer.Sent.Where(s => s.Channel == BasisNetworkCommons.LoadResourceChannel))
            {
                LocalLoadResource resource = new LocalLoadResource();
                resource.Deserialize(new NetDataReader(sent.Data));
                if (resource.LoadedNetID == netId) found.Add(resource.LoadStrategy);
            }
        }
        return found;
    }

    private readonly record struct Progress(bool Closed, int Ready, int Failed, int Total, int Skipped);

    private static List<Progress> Updates(FakeNetPeer peer, string netId)
    {
        BasisNetworkPredownloadManagement.Flush();
        ushort id = MessageId();
        List<Progress> found = new();
        List<byte[]> sent;
        lock (peer.Sent) sent = peer.Sent.Where(s => s.Data.Length >= 3 && BitConverter.ToUInt16(s.Data, 0) == id).Select(s => s.Data).ToList();
        foreach (byte[] data in sent)
        {
            NetDataReader reader = new NetDataReader(data);
            reader.GetUShort();
            byte op = reader.GetByte();
            if (reader.GetString() != netId) continue;
            if (op == BasisPredownloadProtocol.OpClosed)
            {
                found.Add(new Progress(true, 0, 0, 0, 0));
                continue;
            }
            reader.GetUShort();
            found.Add(new Progress(false, reader.GetUShort(), reader.GetUShort(), reader.GetUShort(), reader.GetUShort()));
        }
        return found;
    }

    [Fact]
    public void Spawns_once_everyone_is_ready_and_shares_the_count()
    {
        List<FakeNetPeer> crowd = Enumerable.Range(0, 12).Select(_ => Join()).ToList();
        string netId = Start(crowd[0]);

        Assert.All(crowd, peer => Assert.Equal(new byte[] { 2 }, Strategies(peer, netId)));
        Assert.Equal(new Progress(false, 0, 0, 12, 0), Updates(crowd[3], netId)[^1]);

        for (int i = 0; i < 11; i++) Ready(crowd[i], netId);
        Assert.Equal(new Progress(false, 11, 0, 12, 0), Updates(crowd[3], netId)[^1]);
        Assert.All(crowd, peer => Assert.Equal(0, SpawnSignals(peer, netId)));

        Ready(crowd[11], netId);
        Assert.All(crowd, peer => Assert.Equal(1, SpawnSignals(peer, netId)));
        List<Progress> updates = Updates(crowd[3], netId);
        Assert.Equal(new Progress(false, 12, 0, 12, 0), updates[^2]);
        Assert.True(updates[^1].Closed);
        Assert.False(BasisNetworkPreloadResourceManagement.IsActive(netId));
    }

    [Fact]
    public void A_player_who_sends_no_status_is_no_longer_waited_for()
    {
        FakeNetPeer a = Join(), b = Join(), silent = Join();
        string netId = Start(a);

        Advance(15, a, b);
        Assert.True(BasisNetworkPreloadResourceManagement.TryGetCounts(netId, out _, out _, out int waiting, out int skipped));
        Assert.Equal((3, 0), (waiting, skipped));

        Advance(9, a, b);
        Assert.True(BasisNetworkPreloadResourceManagement.TryGetCounts(netId, out _, out _, out waiting, out skipped));
        Assert.Equal((2, 1), (waiting, skipped));
        Assert.Equal(new Progress(false, 0, 0, 2, 1), Updates(a, netId)[^1]);

        Ready(a, netId);
        Assert.Equal(0, SpawnSignals(a, netId));
        Ready(b, netId);
        Assert.Equal(1, SpawnSignals(a, netId));
        Assert.Equal(1, SpawnSignals(silent, netId));
        Assert.Equal(new Progress(false, 2, 0, 2, 1), Updates(a, netId)[^2]);
    }

    [Fact]
    public void Everyone_silent_still_spawns()
    {
        FakeNetPeer a = Join(), b = Join();
        string netId = Start(a);

        Advance(21);

        Assert.Equal(1, SpawnSignals(a, netId));
        Assert.Equal(1, SpawnSignals(b, netId));
    }

    [Fact]
    public void A_skipped_player_who_responds_again_is_waited_for_again()
    {
        FakeNetPeer a = Join(), back = Join();
        string netId = Start(a);
        Advance(24, a);
        Assert.True(BasisNetworkPreloadResourceManagement.TryGetCounts(netId, out _, out _, out int waiting, out int skipped));
        Assert.Equal((1, 1), (waiting, skipped));

        Status(back, netId);
        Ready(a, netId);

        Assert.Equal(0, SpawnSignals(a, netId));
        Assert.Equal(new Progress(false, 1, 0, 2, 0), Updates(a, netId)[^1]);
        Ready(back, netId);
        Assert.Equal(1, SpawnSignals(a, netId));
    }

    [Fact]
    public void A_ready_report_from_a_skipped_player_still_counts()
    {
        FakeNetPeer a = Join(), slow = Join();
        string netId = Start(a);
        Advance(24, a);

        Ready(slow, netId);

        Assert.Equal(0, SpawnSignals(a, netId));
        Assert.Equal(new Progress(false, 1, 0, 2, 0), Updates(a, netId)[^1]);
    }

    [Fact]
    public void A_client_without_status_support_is_waited_for_until_the_overall_timeout()
    {
        FakeNetPeer a = Join(), old = Join(current: false);
        string netId = Start(a);
        Ready(a, netId);

        Advance(120);
        Assert.Equal(0, SpawnSignals(a, netId));

        Advance(181);
        Assert.Equal(1, SpawnSignals(a, netId));
        Assert.Equal(1, SpawnSignals(old, netId));
    }

    [Fact]
    public void A_failed_download_does_not_hold_everyone_up()
    {
        FakeNetPeer a = Join(), broken = Join();
        string netId = Start(a);

        Ready(broken, netId, ready: false);
        Ready(a, netId);

        Assert.Equal(1, SpawnSignals(a, netId));
        Assert.Equal(new Progress(false, 1, 1, 2, 0), Updates(a, netId)[^2]);
    }

    [Fact]
    public void The_last_waiting_player_leaving_spawns_for_the_rest()
    {
        FakeNetPeer a = Join(), b = Join(), leaver = Join();
        string netId = Start(a);
        Ready(a, netId);
        Ready(b, netId);

        Leave(leaver);

        Assert.Equal(1, SpawnSignals(a, netId));
        Assert.Equal(0, SpawnSignals(leaver, netId));
    }

    [Fact]
    public void A_late_joiner_takes_part_while_waiting_and_loads_directly_afterwards()
    {
        FakeNetPeer a = Join();
        string netId = Start(a);

        FakeNetPeer during = Join();
        BasisNetworkResourceManagement.SendOutAllResources(during);
        Assert.Contains((byte)2, Strategies(during, netId));
        Ready(a, netId);
        Assert.Equal(0, SpawnSignals(a, netId));
        Assert.Equal(new Progress(false, 1, 0, 2, 0), Updates(a, netId)[^1]);
        Ready(during, netId);
        Assert.Equal(1, SpawnSignals(during, netId));

        FakeNetPeer after = Join();
        BasisNetworkResourceManagement.SendOutAllResources(after);
        Assert.Equal(new byte[] { 0 }, Strategies(after, netId));
    }

    [Fact]
    public void Removing_it_before_everyone_is_ready_cancels_the_spawn()
    {
        FakeNetPeer a = Join(), b = Join();
        string netId = Start(a);
        Ready(a, netId);

        Assert.True(BasisNetworkResourceManagement.UnloadResource(new UnLoadResource { LoadedNetID = netId, Mode = 0 }));
        Ready(b, netId);
        Advance(400);

        Assert.Equal(0, SpawnSignals(a, netId));
        Assert.True(Updates(a, netId)[^1].Closed);
    }

    [Fact]
    public void Clear_all_removes_loaded_worlds_and_props_only_at_the_swap()
    {
        FakeNetPeer a = Join(), b = Join();
        string world = Existing(1), prop = Existing(0), locked = Existing(0, adminLocked: true);
        string netId = Start(a, mode: 1, swap: BasisPredownloadProtocol.SwapClearAll);

        Ready(a, netId);
        Assert.True(Loaded(world) && Loaded(prop));

        Ready(b, netId);
        Assert.False(Loaded(world));
        Assert.False(Loaded(prop));
        Assert.True(Loaded(locked));
        Assert.True(Loaded(netId));
        Assert.Equal(BasisPredownloadProtocol.SwapClearAll, SwapSent(b, netId));
    }

    [Fact]
    public void Keep_all_leaves_everything_loaded()
    {
        FakeNetPeer a = Join();
        string world = Existing(1), prop = Existing(0);
        string netId = Start(a, mode: 1, swap: BasisPredownloadProtocol.SwapKeepAll);

        Ready(a, netId);

        Assert.True(Loaded(world) && Loaded(prop) && Loaded(netId));
        Assert.Equal(BasisPredownloadProtocol.SwapKeepAll, SwapSent(a, netId));
    }

    [Fact]
    public void The_default_swap_replaces_worlds_and_keeps_props()
    {
        FakeNetPeer a = Join();
        string world = Existing(1), prop = Existing(0);
        string netId = Start(a, mode: 1);

        Ready(a, netId);

        Assert.False(Loaded(world));
        Assert.True(Loaded(prop) && Loaded(netId));
    }
}
