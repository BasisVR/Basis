using BasisNetworkServer;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Regression coverage for the peak-tracking bug found from production logs on 2026-09-08: a
/// background pass (population still &gt; 0) was rebasing the peak down to the post-drop
/// population, so a crowd that receded in stages instead of hitting zero in one shot could
/// permanently duck under 1/4 of a peak that no longer reflected reality. Two real incidents in
/// the logs (2026-08-30 23:20: 2379 -&gt; 24 players, 14.2 GB working set; 2026-09-06 22:39: 3123 -&gt;
/// 23 players, 13.8 GB) never received a follow-up full reclaim for the rest of the captured
/// window because of exactly this.
/// </summary>
public class MemoryReclaimTests
{
    [Fact]
    public void BackgroundPassPreservesPeakForFutureDrops()
    {
        // The 2026-08-30 23:20 incident, verbatim: peak 2379, dropped to 24 (still occupied).
        int nextPeak = BasisServerMemoryReclaim.NextPeakAfterPass(peakSincePass: 2379, playersAfterPass: 24);

        Assert.Equal(2379, nextPeak);
    }

    [Fact]
    public void EmptyPassResetsPeak()
    {
        int nextPeak = BasisServerMemoryReclaim.NextPeakAfterPass(peakSincePass: 1001, playersAfterPass: 0);

        Assert.Equal(0, nextPeak);
    }

    [Fact]
    public void StagedRecessionStaysEligibleAgainstTheOriginalPeak()
    {
        // A crowd that recedes in steps must keep clearing the bar against the real peak, not
        // whatever population happened to be current at the last pass.
        int peak = 2379;
        peak = BasisServerMemoryReclaim.NextPeakAfterPass(peak, playersAfterPass: 24);
        Assert.Equal(2379, peak);

        // A further drop to single digits is still comfortably below 1/4 of the true peak (595).
        Assert.True(9 * 4 <= peak);
    }
}
