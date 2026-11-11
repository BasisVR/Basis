using Basis.Network.Core;
using BasisNetworkServer;
using Xunit;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class PredownloadTrackingTests : IDisposable
{
    private static int peerIdCounter = 47_000;
    private static int resourceCounter;
    private readonly List<FakeNetPeer> peers = new();
    private DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public PredownloadTrackingTests()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        BasisNetworkPredownloadManagement.Reset();
        BasisNetworkPredownloadManagement.Clock = () => now;
    }

    public void Dispose()
    {
        BasisNetworkPredownloadManagement.Reset();
        BasisNetworkPredownloadManagement.Clock = () => DateTime.UtcNow;
        foreach (FakeNetPeer peer in peers)
        {
            NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
            BasisServerMessageRegistry.ClearSubscription(peer.Id);
        }
        NetworkServer.RebuildPeerSnapshot();
    }

    private FakeNetPeer Join(bool subscribed = true)
    {
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.7.7") { Tag = NetworkServer.AuthenticatedPeerTag };
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        peers.Add(peer);
        if (subscribed) Subscribe(peer);
        return peer;
    }

    private List<FakeNetPeer> Join(int count)
    {
        List<FakeNetPeer> joined = new();
        for (int i = 0; i < count; i++) joined.Add(Join());
        return joined;
    }

    private void Leave(FakeNetPeer peer)
    {
        BasisNetworkPredownloadManagement.RemovePeer(peer.Id);
        NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
        NetworkServer.RebuildPeerSnapshot();
    }

    private static ushort MessageId()
    {
        Assert.True(BasisServerMessageRegistry.TryGetPluginId(BasisPredownloadProtocol.MessageName, out ushort id));
        return id;
    }

    private static void Subscribe(FakeNetPeer peer, bool supportsPredownload = true)
    {
        NetDataWriter w = new NetDataWriter();
        w.Put(BasisNetworkCommons.RegistrySub_Subscribe);
        new BasisMessageSubscribe { Ids = supportsPredownload ? new[] { MessageId() } : new ushort[] { 1 } }.Serialize(w);
        byte[] bytes = w.AsReadOnlySpan().ToArray();
        BasisNetworkMessageProcessor.ProcessMessage(peer, NetPacketReader.Create(bytes, 0, bytes.Length, () => { }), BasisNetworkCommons.RegistryControlChannel, DeliveryMethod.ReliableOrdered);
    }

    private static void Report(FakeNetPeer peer, string netId, bool ready)
    {
        NetDataWriter w = new NetDataWriter();
        w.Put(MessageId());
        w.Put(BasisPredownloadProtocol.OpDone);
        w.Put(netId);
        w.Put(ready);
        byte[] bytes = w.AsReadOnlySpan().ToArray();
        BasisNetworkMessageProcessor.ProcessMessage(peer, NetPacketReader.Create(bytes, 0, bytes.Length, () => { }), BasisNetworkCommons.PluginReliableChannel, DeliveryMethod.ReliableOrdered);
    }

    private static LocalLoadResource Resource(string url = null, byte mode = 1)
    {
        int n = Interlocked.Increment(ref resourceCounter);
        return new LocalLoadResource { Mode = mode, LoadedNetID = $"predownload-test-{n}", CombinedURL = url ?? $"https://content.invalid/{n}.bee", UnlockPassword = string.Empty, UUIDOfCreator = "tester", LoadStrategy = 3 };
    }

    private static string Start(FakeNetPeer requester, LocalLoadResource resource)
    {
        BasisNetworkResourceManagement.PredownloadResource(resource, requester.Id);
        return resource.LoadedNetID;
    }

    private static List<LocalLoadResource> LoadRequests(FakeNetPeer peer)
    {
        List<LocalLoadResource> found = new();
        lock (peer.Sent)
        {
            foreach (var sent in peer.Sent.Where(s => s.Channel == BasisNetworkCommons.LoadResourceChannel))
            {
                LocalLoadResource resource = new LocalLoadResource();
                resource.Deserialize(new NetDataReader(sent.Data));
                found.Add(resource);
            }
        }
        return found;
    }

    private readonly record struct Progress(byte Op, ushort Requester, int Ready, int Failed, int Total);

    private static List<Progress> Updates(FakeNetPeer peer, string netId)
    {
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
            found.Add(op == BasisPredownloadProtocol.OpClosed ? new Progress(op, 0, 0, 0, 0) : new Progress(op, reader.GetUShort(), reader.GetUShort(), reader.GetUShort(), reader.GetUShort()));
        }
        return found;
    }

    private static Progress Latest(FakeNetPeer peer, string netId)
    {
        BasisNetworkPredownloadManagement.Flush();
        List<Progress> updates = Updates(peer, netId);
        Assert.NotEmpty(updates);
        return updates[^1];
    }

    [Fact]
    public void FortyPlayersAreCountedAsTheyFinish()
    {
        List<FakeNetPeer> crowd = Join(40);
        string netId = Start(crowd[0], Resource());

        foreach (FakeNetPeer peer in crowd)
        {
            LocalLoadResource request = Assert.Single(LoadRequests(peer));
            Assert.Equal(3, request.LoadStrategy);
            Assert.Equal(netId, request.LoadedNetID);
            Assert.Equal(new Progress(BasisPredownloadProtocol.OpProgress, (ushort)crowd[0].Id, 0, 0, 40), Latest(peer, netId));
        }

        for (int i = 0; i < 25; i++) Report(crowd[i], netId, true);
        Assert.Equal(new Progress(BasisPredownloadProtocol.OpProgress, (ushort)crowd[0].Id, 25, 0, 40), Latest(crowd[39], netId));

        for (int i = 25; i < 28; i++) Report(crowd[i], netId, false);
        Assert.Equal(new Progress(BasisPredownloadProtocol.OpProgress, (ushort)crowd[0].Id, 25, 3, 40), Latest(crowd[0], netId));

        for (int i = 28; i < 40; i++) Report(crowd[i], netId, true);
        foreach (FakeNetPeer peer in crowd) Assert.Equal(new Progress(BasisPredownloadProtocol.OpProgress, (ushort)crowd[0].Id, 37, 3, 40), Latest(peer, netId));
    }

    [Fact]
    public void ManyReportsBetweenFlushesGoOutAsOneUpdate()
    {
        List<FakeNetPeer> crowd = Join(30);
        string netId = Start(crowd[0], Resource());
        BasisNetworkPredownloadManagement.Flush();
        int before = Updates(crowd[5], netId).Count;

        foreach (FakeNetPeer peer in crowd) Report(peer, netId, true);
        BasisNetworkPredownloadManagement.Flush();
        BasisNetworkPredownloadManagement.Flush();

        List<Progress> updates = Updates(crowd[5], netId);
        Assert.InRange(updates.Count - before, 1, 2);
        Assert.Equal(30, updates[^1].Ready);
    }

    [Fact]
    public void APlayerWhoLeavesStopsBeingWaitedOn()
    {
        List<FakeNetPeer> crowd = Join(6);
        string netId = Start(crowd[0], Resource());
        for (int i = 0; i < 4; i++) Report(crowd[i], netId, true);
        Assert.Equal(6, Latest(crowd[0], netId).Total);

        Leave(crowd[5]);
        Progress afterPendingLeft = Latest(crowd[0], netId);
        Assert.Equal((4, 0, 5), (afterPendingLeft.Ready, afterPendingLeft.Failed, afterPendingLeft.Total));

        Leave(crowd[3]);
        Progress afterReadyLeft = Latest(crowd[0], netId);
        Assert.Equal((3, 0, 4), (afterReadyLeft.Ready, afterReadyLeft.Failed, afterReadyLeft.Total));
    }

    [Fact]
    public void ALateJoinerIsSentTheDownloadAndCounted()
    {
        List<FakeNetPeer> crowd = Join(5);
        string netId = Start(crowd[0], Resource());
        foreach (FakeNetPeer peer in crowd) Report(peer, netId, true);
        Assert.Equal((5, 5), (Latest(crowd[0], netId).Ready, Latest(crowd[0], netId).Total));

        FakeNetPeer late = Join();

        LocalLoadResource request = Assert.Single(LoadRequests(late));
        Assert.Equal(netId, request.LoadedNetID);
        Assert.Equal(3, request.LoadStrategy);
        Progress waiting = Latest(crowd[0], netId);
        Assert.Equal((5, 6), (waiting.Ready, waiting.Total));

        Subscribe(late);
        Assert.Single(LoadRequests(late));

        Report(late, netId, true);
        Progress done = Latest(late, netId);
        Assert.Equal((6, 6), (done.Ready, done.Total));
    }

    [Fact]
    public void AClientThatCannotReportIsNotWaitedOn()
    {
        List<FakeNetPeer> crowd = Join(3);
        FakeNetPeer old = Join(subscribed: false);
        Subscribe(old, supportsPredownload: false);
        FakeNetPeer silent = Join(subscribed: false);

        string netId = Start(crowd[0], Resource());

        Assert.Single(LoadRequests(old));
        Assert.Single(LoadRequests(silent));
        Assert.Equal(3, Latest(crowd[0], netId).Total);
        Assert.Empty(Updates(old, netId));
        Assert.Empty(Updates(silent, netId));

        Subscribe(silent);
        Assert.Equal(4, Latest(crowd[0], netId).Total);
        Report(silent, netId, true);
        Assert.Equal((1, 4), (Latest(silent, netId).Ready, Latest(silent, netId).Total));
    }

    [Fact]
    public void ReportsAreCountedOncePerPlayerAndOnlyFromThoseAsked()
    {
        List<FakeNetPeer> crowd = Join(4);
        string netId = Start(crowd[0], Resource());
        FakeNetPeer stranger = Join(subscribed: false);
        Subscribe(stranger, supportsPredownload: false);

        Report(crowd[1], netId, true);
        Report(crowd[1], netId, true);
        Report(stranger, netId, true);
        Report(crowd[2], "no-such-download", true);
        Progress counted = Latest(crowd[0], netId);
        Assert.Equal((1, 0, 4), (counted.Ready, counted.Failed, counted.Total));

        Report(crowd[1], netId, false);
        Progress flipped = Latest(crowd[0], netId);
        Assert.Equal((0, 1, 4), (flipped.Ready, flipped.Failed, flipped.Total));
    }

    [Fact]
    public void LoadingTheContentForRealEndsTheCount()
    {
        List<FakeNetPeer> crowd = Join(3);
        LocalLoadResource resource = Resource();
        string netId = Start(crowd[0], resource);
        Assert.Equal(BasisPredownloadProtocol.OpProgress, Latest(crowd[1], netId).Op);

        BasisNetworkPredownloadManagement.Fulfil(resource.CombinedURL, resource.Mode);

        Assert.Equal(BasisPredownloadProtocol.OpClosed, Latest(crowd[1], netId).Op);
        Assert.False(BasisNetworkPredownloadManagement.TryGetCounts(netId, out _, out _, out _));
        FakeNetPeer late = Join();
        Assert.Empty(LoadRequests(late));
    }

    [Fact]
    public void AskingAgainForTheSameContentStartsAFreshCount()
    {
        List<FakeNetPeer> crowd = Join(3);
        string url = "https://content.invalid/again.bee";
        string first = Start(crowd[0], Resource(url));
        foreach (FakeNetPeer peer in crowd) Report(peer, first, true);
        Assert.Equal(3, Latest(crowd[0], first).Ready);

        string second = Start(crowd[1], Resource(url));

        Assert.Equal(BasisPredownloadProtocol.OpClosed, Latest(crowd[2], first).Op);
        Progress fresh = Latest(crowd[2], second);
        Assert.Equal((0, 3, (ushort)crowd[1].Id), (fresh.Ready, fresh.Total, fresh.Requester));
        Assert.Equal(1, BasisNetworkPredownloadManagement.SessionCount);
    }

    [Fact]
    public void OldCountsExpireAndTheListStaysBounded()
    {
        List<FakeNetPeer> crowd = Join(2);
        string old = Start(crowd[0], Resource());
        now += BasisNetworkPredownloadManagement.SessionLifetime - TimeSpan.FromSeconds(1);
        Assert.Equal(BasisPredownloadProtocol.OpProgress, Latest(crowd[1], old).Op);

        now += TimeSpan.FromSeconds(2);
        Assert.Equal(BasisPredownloadProtocol.OpClosed, Latest(crowd[1], old).Op);
        Assert.Equal(0, BasisNetworkPredownloadManagement.SessionCount);

        string firstOfMany = null;
        for (int i = 0; i < BasisNetworkPredownloadManagement.MaxSessions + 3; i++)
        {
            string netId = Start(crowd[0], Resource());
            firstOfMany ??= netId;
        }
        Assert.Equal(BasisNetworkPredownloadManagement.MaxSessions, BasisNetworkPredownloadManagement.SessionCount);
        Assert.Equal(BasisPredownloadProtocol.OpClosed, Latest(crowd[1], firstOfMany).Op);
    }

    [Fact]
    public void AServerStartedDownloadHasNoRequester()
    {
        List<FakeNetPeer> crowd = Join(2);
        LocalLoadResource resource = Resource();
        BasisNetworkResourceManagement.PredownloadResource(resource);
        Assert.Equal(BasisPredownloadProtocol.ServerRequester, Latest(crowd[0], resource.LoadedNetID).Requester);
    }
}
