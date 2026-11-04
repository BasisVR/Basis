using System.Collections.Generic;
using System.Linq;
using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Xunit;

namespace BasisServerTests;

public sealed class AvatarHostLedgerTests
{
    private const int Owner = 1, Alice = 2, Bob = 3;
    private sealed class RecordingSink : IBasisAvatarHostSink
    {
        public readonly List<(int Peer, bool Accepted, byte Reason)> Offers = new();
        public readonly List<(int Owner, uint FetchId, long Start, int Length)> Fetches = new();
        public readonly Dictionary<(int Peer, uint RequestId), List<byte>> Received = new();
        public readonly List<(int Peer, uint RequestId, byte Reason)> Failures = new();
        public void OfferResult(int peerId, byte[] id, bool accepted, byte reason) => Offers.Add((peerId, accepted, reason));
        public void Fetch(int ownerPeerId, uint fetchId, byte[] id, long start, int length) => Fetches.Add((ownerPeerId, fetchId, start, length));
        public void Chunk(int peerId, uint requestId, int offset, byte[] data, int dataOffset, int count)
        {
            if (!Received.TryGetValue((peerId, requestId), out List<byte> bytes)) Received[(peerId, requestId)] = bytes = new List<byte>();
            Assert.Equal(bytes.Count, offset);
            bytes.AddRange(data.Skip(dataOffset).Take(count));
        }
        public void Failed(int peerId, uint requestId, byte reason) => Failures.Add((peerId, requestId, reason));
    }
    private static readonly byte[] Id = Enumerable.Range(1, BasisAvatarHostProtocol.IdBytes).Select(i => (byte)i).ToArray();
    private static BasisAvatarHostLimits Limits(long cache = 1 << 20) => new BasisAvatarHostLimits { Enabled = true, MaxRangeBytes = 1 << 20, CacheBytes = cache, InFlightBytes = 4 << 20, RequesterBytesPerMinute = 8 << 20 };
    private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();
    private static void Upload(BasisAvatarHostLedger ledger, uint fetchId, byte[] data, int chunk = BasisAvatarHostProtocol.ChunkBytes)
    {
        for (int offset = 0; offset < data.Length; offset += chunk) ledger.OwnerChunk(Owner, fetchId, offset, data, offset, System.Math.Min(chunk, data.Length - offset), Limits());
    }
    [Fact]
    public void OfferRejectedWhenDisabled()
    {
        RecordingSink sink = new();
        BasisAvatarHostLedger ledger = new(sink);
        ledger.Offer(Owner, Id, 1000, new BasisAvatarHostLimits { Enabled = false, MaxRangeBytes = 1 << 20 });
        Assert.Equal((Owner, false, BasisAvatarHostProtocol.ReasonDisabled), sink.Offers.Single());
        Assert.False(ledger.IsOffered(Id));
    }
    [Fact]
    public void FirstRequestFetchesFromOwnerAndSecondIsServedFromCache()
    {
        RecordingSink sink = new();
        BasisAvatarHostLedger ledger = new(sink);
        ledger.Offer(Owner, Id, 100_000, Limits());
        byte[] section = Payload(40_000);
        ledger.Request(Alice, 7, Id, 1000, section.Length, Limits());
        var fetch = Assert.Single(sink.Fetches);
        Assert.Equal((Owner, 1000L, section.Length), (fetch.Owner, fetch.Start, fetch.Length));
        Upload(ledger, fetch.FetchId, section);
        Assert.Equal(section, sink.Received[(Alice, 7)]);
        ledger.Request(Bob, 9, Id, 1000, section.Length, Limits());
        Assert.Single(sink.Fetches);
        Assert.Equal(section, sink.Received[(Bob, 9)]);
        Assert.Equal(section.Length, ledger.CachedBytes);
        Assert.Equal(0, ledger.InFlightBytes);
    }
    [Fact]
    public void RequestJoiningMidFetchGetsPrefixThenRest()
    {
        RecordingSink sink = new();
        BasisAvatarHostLedger ledger = new(sink);
        ledger.Offer(Owner, Id, 100_000, Limits());
        byte[] section = Payload(50_000);
        ledger.Request(Alice, 1, Id, 0, section.Length, Limits());
        uint fetchId = sink.Fetches.Single().FetchId;
        ledger.OwnerChunk(Owner, fetchId, 0, section, 0, 20_000, Limits());
        ledger.Request(Bob, 2, Id, 0, section.Length, Limits());
        Assert.Single(sink.Fetches);
        ledger.OwnerChunk(Owner, fetchId, 20_000, section, 20_000, 30_000, Limits());
        Assert.Equal(section, sink.Received[(Alice, 1)]);
        Assert.Equal(section, sink.Received[(Bob, 2)]);
    }
    [Fact]
    public void RangesOutsideTheOfferAreRefused()
    {
        RecordingSink sink = new();
        BasisAvatarHostLedger ledger = new(sink);
        ledger.Offer(Owner, Id, 1000, Limits());
        ledger.Request(Alice, 1, Id, 900, 200, Limits());
        ledger.Request(Alice, 2, Id, -1, 10, Limits());
        ledger.Request(Alice, 3, Enumerable.Repeat((byte)9, BasisAvatarHostProtocol.IdBytes).ToArray(), 0, 10, Limits());
        Assert.Empty(sink.Fetches);
        Assert.Equal(new byte[] { BasisAvatarHostProtocol.ReasonBadRange, BasisAvatarHostProtocol.ReasonBadRange, BasisAvatarHostProtocol.ReasonUnknownId }, sink.Failures.Select(f => f.Reason).ToArray());
    }
    [Fact]
    public void OwnerLeavingFailsWaitersAndDropsCache()
    {
        RecordingSink sink = new();
        BasisAvatarHostLedger ledger = new(sink);
        ledger.Offer(Owner, Id, 100_000, Limits());
        ledger.Request(Alice, 1, Id, 0, 100, Limits());
        Upload(ledger, sink.Fetches[0].FetchId, Payload(100));
        ledger.Request(Bob, 2, Id, 100, 500, Limits());
        ledger.RemovePeer(Owner);
        Assert.Equal((Bob, 2u, BasisAvatarHostProtocol.ReasonOwnerLeft), sink.Failures.Single());
        Assert.Equal(0, ledger.CachedBytes);
        Assert.Equal(0, ledger.InFlightBytes);
        Assert.False(ledger.IsOffered(Id));
    }
    [Fact]
    public void ChunkFromWrongPeerIsIgnoredAndOutOfOrderChunkFailsTheFetch()
    {
        RecordingSink sink = new();
        BasisAvatarHostLedger ledger = new(sink);
        ledger.Offer(Owner, Id, 100_000, Limits());
        ledger.Request(Alice, 1, Id, 0, 1000, Limits());
        uint fetchId = sink.Fetches[0].FetchId;
        byte[] data = Payload(1000);
        ledger.OwnerChunk(Bob, fetchId, 0, data, 0, 1000, Limits());
        Assert.False(sink.Received.ContainsKey((Alice, 1)));
        ledger.OwnerChunk(Owner, fetchId, 500, data, 0, 500, Limits());
        Assert.Equal((Alice, 1u, BasisAvatarHostProtocol.ReasonOwnerFailed), sink.Failures.Single());
        Assert.Equal(0, ledger.InFlightBytes);
    }
    [Fact]
    public void RequesterByteBudgetStopsRepeatedCachedPulls()
    {
        long now = 0;
        RecordingSink sink = new();
        BasisAvatarHostLedger ledger = new(sink, () => now);
        BasisAvatarHostLimits limits = Limits();
        limits.RequesterBytesPerMinute = 2000;
        ledger.Offer(Owner, Id, 100_000, limits);
        ledger.Request(Alice, 1, Id, 0, 1000, limits);
        Upload(ledger, sink.Fetches[0].FetchId, Payload(1000));
        ledger.Request(Alice, 2, Id, 0, 1000, limits);
        ledger.Request(Alice, 3, Id, 0, 1000, limits);
        Assert.Equal((Alice, 3u, BasisAvatarHostProtocol.ReasonBusy), sink.Failures.Single());
        now += 30_000;
        ledger.Request(Alice, 4, Id, 0, 1000, limits);
        Assert.True(sink.Received.ContainsKey((Alice, 4)));
    }
    [Fact]
    public void CacheEvictsLeastRecentlyUsed()
    {
        RecordingSink sink = new();
        BasisAvatarHostLedger ledger = new(sink);
        BasisAvatarHostLimits limits = Limits(cache: 2500);
        ledger.Offer(Owner, Id, 100_000, limits);
        for (uint index = 0; index < 3; index++)
        {
            ledger.Request(Alice, index, Id, index * 1000, 1000, limits);
            byte[] data = Payload(1000);
            ledger.OwnerChunk(Owner, sink.Fetches[^1].FetchId, 0, data, 0, data.Length, limits);
        }
        Assert.Equal(2000, ledger.CachedBytes);
        ledger.Request(Bob, 10, Id, 0, 1000, limits);
        Assert.Equal(4, sink.Fetches.Count);
    }
    [Fact]
    public void OwnerOfferCountIsCapped()
    {
        RecordingSink sink = new();
        BasisAvatarHostLedger ledger = new(sink);
        for (int index = 0; index <= BasisAvatarHostProtocol.MaxOffersPerOwner; index++)
        {
            byte[] id = (byte[])Id.Clone();
            id[0] = (byte)(100 + index);
            ledger.Offer(Owner, id, 1000, Limits());
        }
        Assert.Equal(BasisAvatarHostProtocol.MaxOffersPerOwner, sink.Offers.Count(o => o.Accepted));
        Assert.Equal(BasisAvatarHostProtocol.ReasonTooManyOffers, sink.Offers[^1].Reason);
    }
    [Fact]
    public void HexRoundTrip()
    {
        string hex = BasisAvatarHostProtocol.IdToHex(Id);
        Assert.Equal(32, hex.Length);
        Assert.True(BasisAvatarHostProtocol.TryHexToId(hex.ToUpperInvariant(), out byte[] back));
        Assert.Equal(Id, back);
        Assert.False(BasisAvatarHostProtocol.TryHexToId("zz" + hex.Substring(2), out _));
    }
}
