using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisNetworkCore;
using Xunit;

namespace BasisServerTests;

// -----------------------------------------------------------------------------
// Image/GIF bandwidth control, both directions.
//
//   upload   -- a per-sender token bucket the server enforces on relayed image
//               traffic, so a client that ignores the budget it was advertised
//               cannot spend the server's egress anyway.
//   download -- the rate cached images are replayed to one arriving player. The
//               joiner never asked for the replay, so there is no client half to
//               trust and pacing is the only control that exists.
//
// Mutates NetworkServer.Configuration, a process-wide static, so the fixture
// saves and restores it.
// -----------------------------------------------------------------------------

// Swaps NetworkServer.Configuration, so it joins the collection that serialises every other test
// touching the process-wide network statics. Without this it races BasisNetworkImageCacheTests and
// each silently reads the other's configuration.
[Collection("BasisServer shared network statics")]
public class BasisImageBandwidthGovernorTests : IDisposable
{
    private readonly Configuration _previous;

    public BasisImageBandwidthGovernorTests()
    {
        _previous = NetworkServer.Configuration;
        NetworkServer.Configuration = new Configuration();
        BasisImageBandwidthGovernor.Reset();
        // Drive the pump by hand. Left on, the background thread drains the same queue underneath
        // these tests and a rate assertion becomes a race against a 25 ms timer.
        BasisImageBandwidthGovernor.AutoPump = false;
    }

    public void Dispose()
    {
        BasisImageBandwidthGovernor.Reset();
        NetworkServer.Configuration = _previous;
    }

    private static List<BasisImageBandwidthGovernor.PendingPayload> Payloads(int count, int size)
    {
        var list = new List<BasisImageBandwidthGovernor.PendingPayload>();
        for (int i = 0; i < count; i++)
        {
            list.Add(new BasisImageBandwidthGovernor.PendingPayload((ushort)7, new byte[size]));
        }
        return list;
    }

    // ── Upload: the server-side floor under the client's own pacing ──────────────────────────

    [Fact]
    public void SenderInsideItsBudget_IsNeverDropped()
    {
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 200;

        // Well under one burst of budget, which is what an honest client looks like.
        for (int i = 0; i < 50; i++)
        {
            Assert.True(BasisImageBandwidthGovernor.TryConsumeEgress(1, 16 * 1024),
                "a sender inside its budget must never be dropped — that would break honest transfers");
        }

        Assert.Equal(0, BasisImageBandwidthGovernor.DroppedMessages);
    }

    [Fact]
    public void SenderThatIgnoresTheBudget_IsEventuallyDropped()
    {
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 1; // 125 KB/s
        NetworkServer.Configuration.ImageShareEgressEnforcementPercent = 100;

        // Far past a burst's worth. The bucket is allowed to go negative on any single charge, so
        // what is asserted is that sustained overrun stops, not that a particular call fails.
        bool dropped = false;
        for (int i = 0; i < 200; i++)
        {
            if (!BasisImageBandwidthGovernor.TryConsumeEgress(1, 64 * 1024))
            {
                dropped = true;
                break;
            }
        }

        Assert.True(dropped, "a sender well past its budget must be cut off");
        Assert.True(BasisImageBandwidthGovernor.DroppedMessages > 0);
        Assert.True(BasisImageBandwidthGovernor.DroppedBytes > 0);
    }

    [Fact]
    public void FanOutIsWhatCosts_NotPayloadSize()
    {
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 1;
        NetworkServer.Configuration.ImageShareEgressEnforcementPercent = 100;

        // One chunk to forty peers is forty times the egress of the same chunk to one, and the
        // budget is about the server's egress rather than the sender's upload. A governor that
        // charged payload size alone would let a wide fan-out through untouched, which is the exact
        // shape of the original incident.
        const int chunk = 16 * 1024;
        int wideAccepted = 0;
        for (int i = 0; i < 40 && BasisImageBandwidthGovernor.TryConsumeEgress(1, chunk * 40L); i++) wideAccepted++;

        BasisImageBandwidthGovernor.Reset();
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 1;
        NetworkServer.Configuration.ImageShareEgressEnforcementPercent = 100;

        int narrowAccepted = 0;
        for (int i = 0; i < 40 && BasisImageBandwidthGovernor.TryConsumeEgress(2, chunk); i++) narrowAccepted++;

        Assert.True(narrowAccepted > wideAccepted,
            $"a narrow fan-out must get further on the same budget; wide={wideAccepted} narrow={narrowAccepted}");
    }

    [Fact]
    public void EnforcementHeadroom_LetsAnHonestClientOvershootSlightly()
    {
        // The advertised number and the enforced one must not be equal: a client pacing to the
        // advertised rate measures against its own clock and rounds chunks its own way, so it will
        // cross the line on jitter alone. Dropping it there would be worse than the abuse this is
        // meant to catch.
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 1;
        NetworkServer.Configuration.ImageShareEgressEnforcementPercent = 300;

        int accepted = 0;
        for (int i = 0; i < 200 && BasisImageBandwidthGovernor.TryConsumeEgress(1, 16 * 1024); i++) accepted++;

        BasisImageBandwidthGovernor.Reset();
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 1;
        NetworkServer.Configuration.ImageShareEgressEnforcementPercent = 100;

        int acceptedTight = 0;
        for (int i = 0; i < 200 && BasisImageBandwidthGovernor.TryConsumeEgress(2, 16 * 1024); i++) acceptedTight++;

        Assert.True(accepted > acceptedTight,
            $"headroom must buy real slack; 300%={accepted} 100%={acceptedTight}");
    }

    [Fact]
    public void ZeroUploadBudget_DisablesEnforcementRatherThanBlockingEverything()
    {
        // 0 advertises "no opinion, keep your own default". Reading that as a zero-byte allowance
        // would silently make image sharing impossible on any server that never set the value.
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 0;

        for (int i = 0; i < 500; i++)
        {
            Assert.True(BasisImageBandwidthGovernor.TryConsumeEgress(1, 1024 * 1024));
        }
        Assert.Equal(0, BasisImageBandwidthGovernor.DroppedMessages);
    }

    [Fact]
    public void EachSenderGetsItsOwnBucket()
    {
        NetworkServer.Configuration.ImageShareEgressMegabitsPerSecond = 1;
        NetworkServer.Configuration.ImageShareEgressEnforcementPercent = 100;

        while (BasisImageBandwidthGovernor.TryConsumeEgress(1, 64 * 1024)) { }

        // One sharer exhausting itself must not stop anybody else sharing — the budget is per
        // sharer by design, and a shared bucket would let one client deny the feature to the room.
        Assert.True(BasisImageBandwidthGovernor.TryConsumeEgress(2, 64 * 1024));
    }

    // ── Download: paced cache replay ─────────────────────────────────────────────────────────

    [Fact]
    public void ZeroDownloadRate_LeavesReplayToTheCaller()
    {
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 0;

        var peer = new ImageCacheRecordingPeer(9);
        Assert.False(BasisImageBandwidthGovernor.EnqueueReplay(peer, Payloads(4, 1024)),
            "0 means unpaced, so the caller sends inline as it always did");
    }

    [Fact]
    public void PacedReplay_DeliversEveryPayloadInOrder()
    {
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 200;

        var peer = new ImageCacheRecordingPeer(9);
        var sent = new List<byte[]>();
        BasisImageBandwidthGovernor.SendPayload = (p, owner, payload) => sent.Add(payload);

        var payloads = Payloads(8, 1024);
        Assert.True(BasisImageBandwidthGovernor.EnqueueReplay(peer, payloads));

        BasisImageBandwidthGovernor.PumpOnceForTests();

        // A 200 Mb/s bucket carries far more than 8 KB in its initial burst, so one pass drains it.
        Assert.Equal(payloads.Count, sent.Count);
        Assert.False(BasisImageBandwidthGovernor.HasPendingReplay(peer.Id));
    }

    [Fact]
    public void PacedReplay_StopsAtTheRateInsteadOfSendingEverything()
    {
        // The whole point of the download control: a full cache must not land on a joining player
        // in one burst. At 1 Mb/s a single pass can afford only a fraction of this.
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 1;

        var peer = new ImageCacheRecordingPeer(9);
        int sent = 0;
        BasisImageBandwidthGovernor.SendPayload = (p, owner, payload) => sent++;

        var payloads = Payloads(400, 64 * 1024); // 25 MB of cache
        Assert.True(BasisImageBandwidthGovernor.EnqueueReplay(peer, payloads));

        BasisImageBandwidthGovernor.PumpOnceForTests();

        Assert.True(sent < payloads.Count,
            $"a 1 Mb/s replay must not deliver 25 MB in one pass; sent {sent}/{payloads.Count}");
        Assert.True(BasisImageBandwidthGovernor.HasPendingReplay(peer.Id),
            "the remainder must stay queued for later passes rather than being dropped");
    }

    [Fact]
    public void PacedReplay_WaitsWhileTheJoinersImageChannelIsBackedUp()
    {
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 200;

        var peer = new ImageCacheRecordingPeer(9) { QueuedPackets = 256 };
        int sent = 0;
        BasisImageBandwidthGovernor.SendPayload = (p, owner, payload) => sent++;

        var payloads = Payloads(8, 1024);
        Assert.True(BasisImageBandwidthGovernor.EnqueueReplay(peer, payloads));

        BasisImageBandwidthGovernor.PumpOnceForTests();
        Assert.Equal(0, sent);
        Assert.True(BasisImageBandwidthGovernor.HasPendingReplay(peer.Id));

        peer.QueuedPackets = 0;
        BasisImageBandwidthGovernor.PumpOnceForTests();
        Assert.Equal(payloads.Count, sent);
    }

    [Fact]
    public void DepartedPeer_DropsItsQueuedReplay()
    {
        NetworkServer.Configuration.ImageShareDownloadMegabitsPerSecond = 1;

        var peer = new ImageCacheRecordingPeer(9);
        BasisImageBandwidthGovernor.SendPayload = (p, owner, payload) => { };
        Assert.True(BasisImageBandwidthGovernor.EnqueueReplay(peer, Payloads(400, 64 * 1024)));

        // Nobody is waiting for these bytes any more, and holding the list would pin the cached
        // payloads for as long as the pump took to walk them.
        BasisImageBandwidthGovernor.RemovePeer(peer.Id);

        Assert.False(BasisImageBandwidthGovernor.HasPendingReplay(peer.Id));
    }
}
