using Basis.Network.Core;
using BasisNetworkCore;
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
}
