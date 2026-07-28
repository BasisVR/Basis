using Basis.Network.Core;
using Xunit;
using V = Basis.Network.Core.BasisP2PLinkHealth.ConnectedVerdict;

namespace BasisServerTests;

/// <summary>
/// Pins the direct-link (P2P) health watchdog's demotion decision — the logic that is supposed to
/// kick a dead/one-way direct link back to the server relay so a rejoined peer can be heard again.
/// This mechanism is recent and (per project notes) unverified in Unity; these tests lock its
/// thresholds so the "recover after rejoin" behaviour is at least correct at the decision level.
///
/// Thresholds mirror the constants in BasisP2PManager.
/// </summary>
public class P2PLinkHealthTests
{
    const long Grace = 2500;    // ConnectedGracePeriodMs
    const long Stale = 1500;    // StaleTimeoutMs
    const long Confirm = 4000;  // ConfirmTimeoutMs
    const long Dwell = 30000;   // HealthyDwellResetMs
    const long PunchTimeout = 6000;

    static V Eval(long connMs, long inboundMs, bool confirmed, bool flap = false)
        => BasisP2PLinkHealth.EvaluateConnected(connMs, inboundMs, confirmed, flap, Grace, Stale, Confirm, Dwell);

    // --- The core recover-after-rejoin case -------------------------------------------------

    [Fact]
    public void NoInboundPastStale_DemotesToServerRelay()
    {
        // Peer rejoined -> its old direct socket is silent -> no inbound P2P. Past the grace
        // window and the stale timeout, the link must be demoted so voice falls back to the server.
        Assert.Equal(V.DemoteStale, Eval(connMs: 5000, inboundMs: Stale + 100, confirmed: true));
    }

    [Fact]
    public void FreshInbound_StaysHealthy()
    {
        // A live peer is still sending — leave the direct link up.
        Assert.Equal(V.Healthy, Eval(connMs: 10000, inboundMs: 100, confirmed: true));
    }

    [Fact]
    public void JustUnderStale_StaysHealthy()
    {
        Assert.Equal(V.Healthy, Eval(connMs: 5000, inboundMs: Stale - 100, confirmed: true));
    }

    // --- Settle window ----------------------------------------------------------------------

    [Fact]
    public void WithinGrace_NeverDemotes_EvenWithNoInboundAndUnconfirmed()
    {
        // The grace short-circuit must win over both the stale and never-confirmed checks.
        Assert.Equal(V.Healthy, Eval(connMs: Grace - 1, inboundMs: 99999, confirmed: false));
    }

    [Fact]
    public void GraceBoundary_IsExclusive()
    {
        // age < grace is the settle window; age == grace evaluates the liveness checks.
        Assert.Equal(V.Healthy, Eval(connMs: Grace - 1, inboundMs: 99999, confirmed: true));
        Assert.Equal(V.DemoteStale, Eval(connMs: Grace, inboundMs: 99999, confirmed: true));
    }

    // --- Never-confirmed offload ------------------------------------------------------------

    [Fact]
    public void ConnectedButServerNeverConfirmed_PastConfirmTimeout_Demotes()
    {
        Assert.Equal(V.DemoteUnconfirmed, Eval(connMs: Confirm + 100, inboundMs: 100, confirmed: false));
    }

    [Fact]
    public void Unconfirmed_ButUnderConfirmTimeout_StaysHealthy()
    {
        // Past grace, receiving inbound, but the server confirm hasn't had time to arrive yet.
        Assert.Equal(V.Healthy, Eval(connMs: Confirm - 100, inboundMs: 100, confirmed: false));
    }

    [Fact]
    public void StaleTakesPriorityOverUnconfirmed()
    {
        // Both conditions true -> report the more specific "stale" verdict.
        Assert.Equal(V.DemoteStale, Eval(connMs: Confirm + 1000, inboundMs: Stale + 500, confirmed: false));
    }

    // --- Flap-counter reset -----------------------------------------------------------------

    [Fact]
    public void HealthyLongDwellWithPendingFlap_ClearsFlapCounter()
    {
        Assert.Equal(V.ClearFlapCounter, Eval(connMs: Dwell + 1000, inboundMs: 100, confirmed: true, flap: true));
    }

    [Fact]
    public void HealthyLongDwellNoFlap_StaysHealthy()
    {
        Assert.Equal(V.Healthy, Eval(connMs: Dwell + 1000, inboundMs: 100, confirmed: true, flap: false));
    }

    [Fact]
    public void HealthyShortDwellWithFlap_DoesNotClearYet()
    {
        Assert.Equal(V.Healthy, Eval(connMs: Dwell - 1000, inboundMs: 100, confirmed: true, flap: true));
    }

    // --- Punch timeout ----------------------------------------------------------------------

    [Theory]
    [InlineData(PunchTimeout - 1, false)]
    [InlineData(PunchTimeout, false)]
    [InlineData(PunchTimeout + 1, true)]
    public void PunchStalled_AtBoundary(long ageMs, bool expected)
    {
        Assert.Equal(expected, BasisP2PLinkHealth.PunchStalled(ageMs, PunchTimeout));
    }
}
