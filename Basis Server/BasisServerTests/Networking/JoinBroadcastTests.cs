using Basis.Network.Core;
using BasisNetworkCore;
using BasisNetworkCore.Security;
using BasisServerHandle;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// Join announcements are coalesced and flushed from a worker thread, so which records reach which
/// peer is decided by join order rather than by the call that produced them. These pin that rule:
/// a peer receives exactly the joins newer than its own, because everything older was already in
/// the player list it got on arrival. Getting this wrong spawns a player twice, spawns a player to
/// itself, or drops a spawn entirely — none of which the load harness can see, since its fake
/// clients ignore the spawn channels.
/// </summary>
[Collection("BasisServer shared network statics")]
public class JoinBroadcastTests
{
    private static byte[] RecordFor(ushort playerId)
    {
        // The payload is an opaque ServerReadyMessage blob to the broadcaster; only its bytes and
        // the count that frames them matter here, so a minimal distinguishable record is enough.
        NetDataWriter writer = new NetDataWriter();
        writer.Put(playerId);
        return writer.CopyData();
    }

    private static ushort CountIn(byte[] framed)
    {
        NetDataReader reader = new NetDataReader(framed);
        ServerReadyBatchMessage batch = new ServerReadyBatchMessage();
        batch.Deserialize(reader);
        return batch.Count;
    }

    private static List<ushort> PayloadIdsIn(byte[] framed)
    {
        NetDataReader reader = new NetDataReader(framed);
        ServerReadyBatchMessage batch = new ServerReadyBatchMessage();
        batch.Deserialize(reader);
        NetDataReader payload = new NetDataReader(batch.Payload);
        List<ushort> ids = new List<ushort>();
        for (int i = 0; i < batch.Count; i++)
        {
            ids.Add(payload.GetUShort());
        }
        return ids;
    }

    private static byte[] SentBatch(FakeNetPeer peer)
    {
        var sends = peer.Sent
            .Where(s => s.Channel == BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel)
            .ToList();
        Assert.Single(sends);
        return sends[0].Data;
    }

    [Fact]
    public void Flush_SendsOnlyJoinsNewerThanEachPeersOwn()
    {
        using var scope = new ServerStaticsScope();
        BasisServerHandleEvents.JoinBroadcast.Stop();

        FakeNetPeer early = new FakeNetPeer(9101, "127.0.0.1");
        FakeNetPeer middle = new FakeNetPeer(9102, "127.0.0.1");
        FakeNetPeer late = new FakeNetPeer(9103, "127.0.0.1");

        long earlySeq = BasisServerHandleEvents.JoinBroadcast.NextSeq();
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(early.Id, earlySeq);
        long middleSeq = BasisServerHandleEvents.JoinBroadcast.NextSeq();
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(middle.Id, middleSeq);
        long lateSeq = BasisServerHandleEvents.JoinBroadcast.NextSeq();
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(late.Id, lateSeq);

        NetworkServer.AuthenticatedPeers[(ushort)early.Id] = early;
        NetworkServer.AuthenticatedPeers[(ushort)middle.Id] = middle;
        NetworkServer.AuthenticatedPeers[(ushort)late.Id] = late;
        NetworkServer.RebuildPeerSnapshot();

        // middle and late are the two joins being announced in this flush.
        BasisServerHandleEvents.JoinBroadcast.Enqueue(middleSeq, middle.Id, RecordFor(9102));
        BasisServerHandleEvents.JoinBroadcast.Enqueue(lateSeq, late.Id, RecordFor(9103));

        BasisServerHandleEvents.JoinBroadcast.Flush();

        // Was already here: learns about both newcomers.
        Assert.Equal(new List<ushort> { 9102, 9103 }, PayloadIdsIn(SentBatch(early)));

        // Joined between them: gets the later one only — never a copy of itself, and never the
        // records it already received in its own arrival list.
        Assert.Equal(new List<ushort> { 9103 }, PayloadIdsIn(SentBatch(middle)));

        // Newest join: everything in this batch is at or before its own arrival, so nothing is due.
        Assert.Empty(late.Sent.Where(s => s.Channel == BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel));

        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(early.Id);
        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(middle.Id);
        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(late.Id);
    }

    [Fact]
    public void Flush_CoalescesManyJoinsIntoOneSendPerPeer()
    {
        using var scope = new ServerStaticsScope();
        BasisServerHandleEvents.JoinBroadcast.Stop();

        FakeNetPeer observer = new FakeNetPeer(9200, "127.0.0.1");
        long observerSeq = BasisServerHandleEvents.JoinBroadcast.NextSeq();
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(observer.Id, observerSeq);
        NetworkServer.AuthenticatedPeers[(ushort)observer.Id] = observer;
        NetworkServer.RebuildPeerSnapshot();

        const int joins = 25;
        for (int i = 0; i < joins; i++)
        {
            BasisServerHandleEvents.JoinBroadcast.Enqueue(BasisServerHandleEvents.JoinBroadcast.NextSeq(), 9300 + i, RecordFor((ushort)(9300 + i)));
        }

        BasisServerHandleEvents.JoinBroadcast.Flush();

        // The whole point of the coalescing: 25 joins cost one packet, not 25.
        byte[] framed = SentBatch(observer);
        Assert.Equal(joins, CountIn(framed));

        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(observer.Id);
    }

    private static List<ushort> DepartureIdsIn(FakeNetPeer peer)
    {
        var sends = peer.Sent.Where(s => s.Channel == BasisNetworkCommons.DisconnectionChannel).ToList();
        Assert.Single(sends);
        NetDataReader reader = new NetDataReader(sends[0].Data);
        List<ushort> ids = new List<ushort>();
        while (reader.AvailableBytes >= sizeof(ushort)) ids.Add(reader.GetUShort());
        return ids;
    }

    [Fact]
    public void Flush_CoalescesDeparturesIntoOneSendPerPeer()
    {
        using var scope = new ServerStaticsScope();
        BasisServerHandleEvents.JoinBroadcast.Stop();

        FakeNetPeer watcher = new FakeNetPeer(9500, "127.0.0.1");
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(watcher.Id, BasisServerHandleEvents.JoinBroadcast.NextSeq());
        NetworkServer.AuthenticatedPeers[(ushort)watcher.Id] = watcher;
        NetworkServer.RebuildPeerSnapshot();

        BasisServerHandleEvents.JoinBroadcast.EnqueueLeave(9601);
        BasisServerHandleEvents.JoinBroadcast.EnqueueLeave(9602);
        BasisServerHandleEvents.JoinBroadcast.EnqueueLeave(9603);

        BasisServerHandleEvents.JoinBroadcast.Flush();

        // Three departures, one packet — the client reads ids until the buffer runs out.
        Assert.Equal(new List<ushort> { 9601, 9602, 9603 }, DepartureIdsIn(watcher));

        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(watcher.Id);
    }

    [Fact]
    public void Flush_DropsBothWhenAPlayerLeavesBeforeItsJoinWasAnnounced()
    {
        using var scope = new ServerStaticsScope();
        BasisServerHandleEvents.JoinBroadcast.Stop();

        FakeNetPeer watcher = new FakeNetPeer(9700, "127.0.0.1");
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(watcher.Id, BasisServerHandleEvents.JoinBroadcast.NextSeq());
        NetworkServer.AuthenticatedPeers[(ushort)watcher.Id] = watcher;
        NetworkServer.RebuildPeerSnapshot();

        // Joins and leaves ride different channels, so a departure could otherwise overtake the
        // matching arrival and leave a player spawned forever. Cancelling the pair removes the race.
        const int flapper = 9701;
        BasisServerHandleEvents.JoinBroadcast.Enqueue(BasisServerHandleEvents.JoinBroadcast.NextSeq(), flapper, RecordFor((ushort)flapper));
        BasisServerHandleEvents.JoinBroadcast.EnqueueLeave(flapper);

        BasisServerHandleEvents.JoinBroadcast.Flush();

        Assert.Empty(watcher.Sent);

        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(watcher.Id);
    }

    [Fact]
    public void Flush_WithNothingPending_SendsNothing()
    {
        using var scope = new ServerStaticsScope();
        BasisServerHandleEvents.JoinBroadcast.Stop();

        FakeNetPeer peer = new FakeNetPeer(9400, "127.0.0.1");
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(peer.Id, BasisServerHandleEvents.JoinBroadcast.NextSeq());
        NetworkServer.AuthenticatedPeers[(ushort)peer.Id] = peer;
        NetworkServer.RebuildPeerSnapshot();

        BasisServerHandleEvents.JoinBroadcast.Flush();

        Assert.Empty(peer.Sent);

        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(peer.Id);
    }

    [Fact]
    public void Flush_SendsADepartureBeforeTheJoinThatReusesItsId()
    {
        using var scope = new ServerStaticsScope();
        BasisServerHandleEvents.JoinBroadcast.Stop();

        FakeNetPeer watcher = new FakeNetPeer(9900, "127.0.0.1");
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(watcher.Id, BasisServerHandleEvents.JoinBroadcast.NextSeq());
        NetworkServer.AuthenticatedPeers[(ushort)watcher.Id] = watcher;
        NetworkServer.RebuildPeerSnapshot();

        const ushort reused = 9901;
        const ushort unrelated = 9902;
        BasisServerHandleEvents.JoinBroadcast.EnqueueLeave(reused);
        BasisServerHandleEvents.JoinBroadcast.EnqueueLeave(unrelated);
        BasisServerHandleEvents.JoinBroadcast.Enqueue(BasisServerHandleEvents.JoinBroadcast.NextSeq(), reused, RecordFor(reused));

        BasisServerHandleEvents.JoinBroadcast.Flush();

        Assert.Equal(3, watcher.Sent.Count);
        Assert.Equal(BasisNetworkCommons.DisconnectionChannel, watcher.Sent[0].Channel);
        Assert.Equal(reused, new NetDataReader(watcher.Sent[0].Data).GetUShort());
        Assert.Equal(BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, watcher.Sent[1].Channel);
        Assert.Equal(new List<ushort> { reused }, PayloadIdsIn(watcher.Sent[1].Data));
        Assert.Equal(BasisNetworkCommons.DisconnectionChannel, watcher.Sent[2].Channel);
        Assert.Equal(unrelated, new NetDataReader(watcher.Sent[2].Data).GetUShort());

        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(watcher.Id);
    }

    private static void InstallServer()
    {
        NetworkServer.Configuration = new Configuration { PeerLimit = 100, BasisUserRestrictionMode = BasisUserRestrictionMode.Normal };
        NetworkServer.AuthIdentity = new MapAuthIdentity();
    }

    private static DisconnectInfo Info() => new() { Reason = DisconnectReason.RemoteConnectionClose };

    private static (FakeNetPeer Watcher, FakeNetPeer Stale, FakeNetPeer Live, long LiveSeq) ReconnectedSlot(int watcherId, int reusedId)
    {
        FakeNetPeer watcher = new FakeNetPeer(watcherId, "127.0.0.1");
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(watcher.Id, BasisServerHandleEvents.JoinBroadcast.NextSeq());
        NetworkServer.AuthenticatedPeers[(ushort)watcher.Id] = watcher;

        FakeNetPeer stale = new FakeNetPeer(reusedId, "127.0.0.1");
        FakeNetPeer live = new FakeNetPeer(reusedId, "127.0.0.1");
        long liveSeq = BasisServerHandleEvents.JoinBroadcast.NextSeq();
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(live.Id, liveSeq);
        NetworkServer.AuthenticatedPeers[(ushort)live.Id] = live;
        NetworkServer.RebuildPeerSnapshot();
        return (watcher, stale, live, liveSeq);
    }

    [Fact]
    public void StaleDisconnectAfterReconnect_KeepsTheLivePeersPendingJoin()
    {
        using var scope = new ServerStaticsScope();
        InstallServer();
        BasisServerHandleEvents.JoinBroadcast.Stop();

        const ushort reusedId = 9801;
        (FakeNetPeer watcher, FakeNetPeer stale, FakeNetPeer live, long liveSeq) = ReconnectedSlot(9800, reusedId);
        BasisServerHandleEvents.JoinBroadcast.Enqueue(liveSeq, live.Id, RecordFor(reusedId));

        BasisServerHandleEvents.HandlePeerDisconnected(stale, Info());
        BasisServerHandleEvents.JoinBroadcast.Flush();

        Assert.Equal(new List<ushort> { reusedId }, PayloadIdsIn(SentBatch(watcher)));
        Assert.DoesNotContain(watcher.Sent, s => s.Channel == BasisNetworkCommons.DisconnectionChannel);
        Assert.True(NetworkServer.AuthenticatedPeers.TryGetValue(live.Id, out NetPeer? holder));
        Assert.Same(live, holder);

        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(watcher.Id);
        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(live.Id);
    }

    [Fact]
    public void StaleDisconnectAfterReconnect_DoesNotAnnounceTheLivePeersDeparture()
    {
        using var scope = new ServerStaticsScope();
        InstallServer();
        BasisServerHandleEvents.JoinBroadcast.Stop();

        (FakeNetPeer watcher, FakeNetPeer stale, FakeNetPeer live, _) = ReconnectedSlot(9810, 9811);

        BasisServerHandleEvents.HandlePeerDisconnected(stale, Info());
        BasisServerHandleEvents.JoinBroadcast.Flush();

        Assert.Empty(watcher.Sent);
        Assert.True(NetworkServer.AuthenticatedPeers.TryGetValue(live.Id, out NetPeer? holder));
        Assert.Same(live, holder);

        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(watcher.Id);
        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(live.Id);
    }

    [Fact]
    public void KickedPeer_DepartureIsStillAnnounced_WhenItsDisconnectArrives()
    {
        using var scope = new ServerStaticsScope();
        InstallServer();
        BasisServerHandleEvents.JoinBroadcast.Stop();

        FakeNetPeer watcher = new FakeNetPeer(9820, "127.0.0.1");
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(watcher.Id, BasisServerHandleEvents.JoinBroadcast.NextSeq());
        NetworkServer.AuthenticatedPeers[(ushort)watcher.Id] = watcher;
        FakeNetPeer kicked = new FakeNetPeer(9821, "127.0.0.1");
        BasisServerHandleEvents.JoinBroadcast.RegisterPeer(kicked.Id, BasisServerHandleEvents.JoinBroadcast.NextSeq());
        NetworkServer.AuthenticatedPeers[(ushort)kicked.Id] = kicked;
        NetworkServer.RebuildPeerSnapshot();

        BasisServerHandleEvents.RejectWithReason(kicked, "kicked");
        Assert.False(NetworkServer.AuthenticatedPeers.ContainsKey(kicked.Id));

        BasisServerHandleEvents.HandlePeerDisconnected(kicked, Info());
        BasisServerHandleEvents.JoinBroadcast.Flush();

        Assert.Equal(new List<ushort> { (ushort)kicked.Id }, DepartureIdsIn(watcher));

        BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(watcher.Id);
    }
}
