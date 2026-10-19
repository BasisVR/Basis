using System;
using System.Reflection;
using System.Threading;
using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Covers the two properties the core allocator exists to hold: the sum of what it hands out stays
/// inside the machine, and a lease's ceiling is measured rather than believed.
///
/// The discovery tests run against the real clock because the probe windows are real durations.
/// They are kept to the minimum number of windows that can distinguish the outcomes.
/// </summary>
[Collection("BasisCpuBudget")]
public class CoreBudgetTests
{
    /// <summary>
    /// Shortened probe window. The controller cares about the ratio between work and busy time, not
    /// how long a window lasts, and the synthetic leases below produce that ratio immediately —
    /// so the only thing the shipped 2 s buys here is a slower test.
    /// </summary>
    private const double WindowMs = 120;

    private sealed class ShortWindow : IDisposable
    {
        private readonly double _previous = BasisCpuBudget.ProbeWindowMs;
        public ShortWindow() => BasisCpuBudget.ProbeWindowMs = WindowMs;
        public void Dispose() => BasisCpuBudget.ProbeWindowMs = _previous;
    }

    /// <summary>
    /// Drives the allocator for a stretch of wall time, feeding a lease work at a rate that depends
    /// on how many cores it currently holds. That relationship is the thing under test: a lease
    /// whose rate stops improving above some width has a real ceiling there.
    /// </summary>
    private static void Run(BasisCoreLease lease, double ms, Func<int, double> ratePerBusyMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            // One notional pass: 1 ms of busy time, delivering whatever this width is worth.
            lease.ReportDemand(1.0);
            lease.AddWork((long)ratePerBusyMs(lease.Granted), 1.0);
            BasisCpuBudget.Rebalance();
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void GrantsNeverExceedTheMachine()
    {
        var leases = new[]
        {
            BasisCpuBudget.Register("test-a", 1, () => 4096, 1.0),
            BasisCpuBudget.Register("test-b", 1, () => 4096, 1.0),
            BasisCpuBudget.Register("test-c", 1, () => 4096, 5.0),
        };
        try
        {
            foreach (var l in leases) l.ReportDemand(1.0);
            for (int i = 0; i < 200; i++) BasisCpuBudget.Rebalance();

            int total = 0;
            foreach (var l in BasisCpuBudget.Leases) total += l.Granted;

            // Every lease declared an effectively unbounded ceiling and maximum demand. Without the
            // pool invariant each would take the whole box and the sum would be a multiple of it.
            Assert.True(total <= BasisCpuBudget.TotalCores,
                $"handed out {total} of {BasisCpuBudget.TotalCores} cores");
            foreach (var l in leases) Assert.True(l.Granted >= 1);
        }
        finally
        {
            foreach (var l in leases) BasisCpuBudget.Unregister(l);
        }
    }

    [Fact]
    public void LeaseThatReportsNoWorkKeepsItsDeclaredCeiling()
    {
        var lease = BasisCpuBudget.Register("test-silent", 1, () => 3, 1.0);
        try
        {
            lease.ReportDemand(1.0);
            for (int i = 0; i < 500; i++) BasisCpuBudget.Rebalance();

            // Nothing to measure against, so the declared number stands rather than the allocator
            // inventing a ceiling from load alone.
            Assert.False(lease.HasMeasuredCeiling);
            Assert.Equal(3, lease.EffectiveMax());
        }
        finally { BasisCpuBudget.Unregister(lease); }
    }

    [Fact]
    public void CeilingConvergesOnTheRealOne()
    {
        if (BasisCpuBudget.TotalCores < 8) return;   // needs room to narrow

        // Declares a ceiling of 64 but genuinely saturates at 4: past that, extra workers deliver
        // nothing. This is the send pool's shape — serialised on something that is not the CPU, so
        // the shipped number is an overestimate and only measurement can say by how much.
        const int RealCeiling = 4;
        using var _ = new ShortWindow();
        var lease = BasisCpuBudget.Register("test-capped", 1, () => 64, 8.0);
        try
        {
            // Long enough for the narrowing search to walk all the way down and be rejected once.
            Run(lease, WindowMs * 80, granted => Math.Min(granted, RealCeiling) * 100);

            Assert.True(lease.HasMeasuredCeiling, "no ceiling was measured");

            // The real assertion: it found roughly 4, not merely "less than the declared 64".
            // Anything at or above 8 means the search stalled rather than converged, and anything
            // below 4 means it narrowed past the point where throughput actually fell.
            int measured = lease.EffectiveMax();
            Assert.InRange(measured, RealCeiling, RealCeiling + 2);
        }
        finally { BasisCpuBudget.Unregister(lease); }
    }

    [Fact]
    public void CeilingStopsNarrowingWhenThroughputFalls()
    {
        if (BasisCpuBudget.TotalCores < 8) return;

        // Scales perfectly with width: every core taken away costs proportional throughput, so
        // there is no ceiling below the machine and narrowing must be rejected at the first step.
        using var _ = new ShortWindow();
        var lease = BasisCpuBudget.Register("test-scaling", 2, () => 64, 8.0);
        try
        {
            int widthBefore = lease.Granted;
            Run(lease, WindowMs * 80, granted => granted * 100);

            // A rate signal that divided by wall time instead of busy time would have narrowed this
            // to its floor — the pass delivers the same amount per second either way. Dividing by
            // busy time is what makes a perfectly-scaling pool refuse to give cores back.
            Assert.True(lease.EffectiveMax() >= widthBefore,
                $"narrowed to {lease.EffectiveMax()} from {widthBefore} despite throughput scaling with width");
        }
        finally { BasisCpuBudget.Unregister(lease); }
    }

    [Fact]
    public void IdleLeaseIsNotMeasured()
    {
        using var _ = new ShortWindow();
        var lease = BasisCpuBudget.Register("test-idle", 1, () => 64, 1.0);
        try
        {
            // Reports work but is not under pressure. Its output is set by what it is fed, so
            // narrowing it would look free and would latch a ceiling describing the load rather
            // than the machine.
            var end = DateTime.UtcNow.AddMilliseconds(WindowMs * 2);
            while (DateTime.UtcNow < end)
            {
                lease.ReportDemand(0.1);
                lease.AddWork(100, 1.0);
                BasisCpuBudget.Rebalance();
                Thread.Sleep(5);
            }

            Assert.False(lease.HasMeasuredCeiling);
        }
        finally { BasisCpuBudget.Unregister(lease); }
    }

    [Fact]
    public void RisingDemandReopensAMeasuredCeiling()
    {
        if (BasisCpuBudget.TotalCores < 8) return;

        using var _ = new ShortWindow();
        var lease = BasisCpuBudget.Register("test-surge", 1, () => 64, 8.0);
        try
        {
            // Settles on a low ceiling under light load.
            var end = DateTime.UtcNow.AddMilliseconds(WindowMs * 60);
            while (DateTime.UtcNow < end)
            {
                lease.ReportDemand(0.55);
                lease.AddWork(Math.Min(lease.Granted, 3) * 100, 1.0);
                BasisCpuBudget.Rebalance();
                Thread.Sleep(1);
            }
            Assert.True(lease.HasMeasuredCeiling, "no ceiling was measured under light load");
            int narrow = lease.EffectiveMax();

            // Load steps up. A ceiling measured on a half-empty server must not cap a full one, and
            // creeping back one core per cooldown would take minutes to recover.
            lease.ReportDemand(1.0);
            BasisCpuBudget.Rebalance();

            Assert.True(lease.EffectiveMax() > narrow,
                $"ceiling stayed at {narrow} after demand rose");
        }
        finally { BasisCpuBudget.Unregister(lease); }
    }

    [Fact]
    public void AddingASendSocketReopensTheSendCeiling()
    {
        var lease = BasisCpuBudget.ReductionSendLease;
        int before = BasisCpuBudget.SendSocketCount;
        try
        {
            BasisCpuBudget.SetSendSocketCount(before + 1);

            // The send ceiling is a fact about how many independent send paths exist. A new socket
            // means anything measured earlier described a machine that no longer exists.
            Assert.False(lease.HasMeasuredCeiling);
        }
        finally { BasisCpuBudget.SetSendSocketCount(before); }
    }

    /// <summary>
    /// The send pool sizes itself between a floor and the allocator's current grant. The grant is
    /// not a constant — it moves with load, and on a host too small to satisfy every lease's floor
    /// the allocator trims the floors to fit, so the grant can legitimately land below the number
    /// the send pool would like to start from. When it does, the floor has to yield to the grant:
    /// a pool that took more workers than the machine-wide split gave it defeats the split.
    ///
    /// Squeezing the pool from a test is what a small host does on its own — the floors of the two
    /// standing leases alone exceed what a 4-core box has left after reserved threads.
    /// </summary>
    [Fact]
    public void SendPoolSizingSurvivesAGrantBelowItsFloor()
    {
        var squeeze = new BasisCoreLease[Math.Max(8, BasisCpuBudget.TotalCores * 2)];
        for (int i = 0; i < squeeze.Length; i++)
        {
            squeeze[i] = BasisCpuBudget.Register("test-squeeze-" + i, 1, () => 4096, 8.0);
        }

        var degreeFor = typeof(BasisNetworkServer.BasisNetworkingReductionSystem.BasisServerReductionSystemEvents)
            .GetMethod("DegreeFor", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(degreeFor);

        try
        {
            foreach (var l in squeeze) l.ReportDemand(1.0);

            bool sawGrantUnderFloor = false;
            for (int step = 0; step < 200; step++)
            {
                BasisCpuBudget.Rebalance();
                if (BasisCpuBudget.ReductionSendCap < BasisCpuBudget.MinWorkersPerPool)
                {
                    sawGrantUnderFloor = true;
                }

                foreach (int players in new[] { 0, 1, 200, 4000 })
                {
                    // current is swept past both ends of the legal range: the sizing clamps it
                    // before stepping from it, so a pool running wider than the grant now allows
                    // still has to land somewhere legal.
                    foreach (int current in new[] { 0, 1, 4, BasisCpuBudget.TotalCores * 2 })
                    {
                        int degree = (int)degreeFor.Invoke(null, new object[] { players, current })!;

                        Assert.True(degree >= 1, $"degree {degree} is not a legal MaxDegreeOfParallelism");
                        Assert.True(degree <= BasisCpuBudget.TotalCores,
                            $"degree {degree} exceeds {BasisCpuBudget.TotalCores} cores");
                    }
                }
            }

            Assert.True(sawGrantUnderFloor,
                $"the squeeze never drove the grant under the floor of {BasisCpuBudget.MinWorkersPerPool}, " +
                "so this never exercised the case it exists for");
        }
        finally
        {
            foreach (var l in squeeze) BasisCpuBudget.Unregister(l);
            for (int i = 0; i < 200; i++) BasisCpuBudget.Rebalance();
        }
    }
}
