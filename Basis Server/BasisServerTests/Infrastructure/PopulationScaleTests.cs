using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Pins the population/memory scaling that replaced the fixed ceilings, and the migration that
/// retires the old values from files already on disc.
///
/// The behaviour under test is a scaling *shape*, not a set of magic numbers — asserting the exact
/// output of the formula would just restate it. These check the properties that actually matter:
/// a configured value always wins, the result stays inside its declared bounds, a bigger box gets
/// a bigger allowance, and a bigger crowd gets a smaller per-peer share.
/// </summary>
[CollectionDefinition("PopulationScale", DisableParallelization = true)]
public class PopulationScaleCollection { }

/// <summary>
/// Serialised against the rest of the suite. These tests pin a process-global (the detected memory
/// figure) that the transport's queue sizing reads at runtime, so letting them run beside tests
/// that stand up a NetManager would let one silently change the other's ceilings. The profiler
/// tests learned the same lesson the harder way.
/// </summary>
[Collection("PopulationScale")]
public class PopulationScaleTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;

    // BasisPopulationScale caches the machine's memory in a static, so every test here has to put
    // it back or it leaks into whatever runs next. xUnit gives each test class its own instance,
    // but statics are process-wide.
    public void Dispose() => BasisPopulationScale.OverrideAvailableMemoryForTests(0);

    [Theory]
    [InlineData(1)]
    [InlineData(512)]
    [InlineData(99999)]
    public void ConfiguredValueAlwaysWins(int configured)
    {
        BasisPopulationScale.OverrideAvailableMemoryForTests(64 * Gb);

        // An operator who pinned a number gets that number, even an absurd one — the auto path is
        // opt-out, and silently clamping a deliberate choice would be worse than honouring it.
        Assert.Equal(configured, BasisPopulationScale.UnreliableQueuePerPeer(configured, 2000));
        Assert.Equal(configured, BasisPopulationScale.SliceCap(configured, 2000));
    }

    [Fact]
    public void UnreliableQueue_StaysWithinDeclaredBounds()
    {
        BasisPopulationScale.OverrideAvailableMemoryForTests(64 * Gb);

        foreach (int peers in new[] { 1, 50, 500, 2000, 4000, 8000, 65535 })
        {
            int depth = BasisPopulationScale.UnreliableQueuePerPeer(0, peers);
            Assert.InRange(depth,
                BasisPopulationScale.MinUnreliableQueuePerPeer,
                BasisPopulationScale.MaxUnreliableQueuePerPeer);
        }
    }

    [Fact]
    public void UnreliableQueue_ShrinksAsTheCrowdGrows()
    {
        BasisPopulationScale.OverrideAvailableMemoryForTests(64 * Gb);

        // The budget is a whole-machine total, so more peers must mean a smaller share each —
        // otherwise the worst case grows with population, which is what a fixed per-peer number
        // did and why it could not be raised safely.
        int at2000 = BasisPopulationScale.UnreliableQueuePerPeer(0, 2000);
        int at8000 = BasisPopulationScale.UnreliableQueuePerPeer(0, 8000);
        Assert.True(at8000 < at2000, $"expected 8000 peers to get less than 2000; got {at8000} vs {at2000}");
    }

    [Fact]
    public void UnreliableQueue_BiggerBoxGetsMoreHeadroom()
    {
        BasisPopulationScale.OverrideAvailableMemoryForTests(8 * Gb);
        int small = BasisPopulationScale.UnreliableQueuePerPeer(0, 2000);

        BasisPopulationScale.OverrideAvailableMemoryForTests(64 * Gb);
        int large = BasisPopulationScale.UnreliableQueuePerPeer(0, 2000);

        // This is the whole point of the change: the same build must behave differently on a small
        // VPS and a large host without anyone editing a config.
        Assert.True(large > small, $"expected a 64 GB box to allow more than an 8 GB box; got {large} vs {small}");
    }

    /// <summary>
    /// The pool has to be able to take back what the queues can let go of.
    ///
    /// It was sized from a per-peer constant of 48 while the queues were population-scaled into the
    /// millions — a 96,000-packet pool in front of 9.5 million packets of queue capacity at 1000
    /// players. The surplus was not saved memory: <c>PoolRecycle</c> dropped it, and those packets
    /// had already been promoted to gen2 by sitting in a queue, so the collector could neither
    /// compact nor return the space.
    ///
    /// Measured at 1000 clients with only this cap varied: voice delivery 63.2% -> 85.8%, bulk drops
    /// 2.94M -> 796k, GC pause ~2.1% -> ~1.2%, gen2 collections 9 -> 4. Starving the pool forces the
    /// send path to allocate, the GC then takes CPU away from that same send path, and the server
    /// falls further behind — the cap is upstream of a feedback loop, not a simple memory knob.
    /// </summary>
    [Fact]
    public void PacketPool_CanAbsorbEverythingTheQueuesHold()
    {
        BasisPopulationScale.OverrideAvailableMemoryForTests(64 * Gb);

        const int peers = 1000;
        long queueCapacity = (long)peers *
            (BasisPopulationScale.UnreliableQueuePerPeer(0, peers) +
             BasisPopulationScale.PriorityQueuePerPeer(0, peers));

        int pool = BasisPopulationScale.PacketPoolMax(0, peers, perPeer: 48);

        Assert.True(pool >= queueCapacity,
            $"pool cap {pool} cannot absorb {queueCapacity} packets of queue capacity — recycled " +
            "packets past the cap are dropped, and having lived in a queue they are already in gen2");
    }

    [Fact]
    public void UnreliableQueue_MeasuredWorkingPointIsReachable()
    {
        // 4096 per peer at 2000 players on a 64 GB host is the configuration that measured zero
        // drops, where the old fixed 256 shed roughly half of everything produced. Auto does not
        // have to pick exactly that, but it must land in the same region or the fix is theoretical.
        BasisPopulationScale.OverrideAvailableMemoryForTests(64 * Gb);
        int depth = BasisPopulationScale.UnreliableQueuePerPeer(0, 2000);
        Assert.InRange(depth, 2048, BasisPopulationScale.MaxUnreliableQueuePerPeer);
    }

    [Fact]
    public void UnreliableQueue_TinyBoxStillGetsAUsableFloor()
    {
        // A 1 GB container divided by 8000 peers is arithmetically near zero. It must clamp to the
        // floor rather than resolve to a queue so shallow it becomes a packet filter.
        BasisPopulationScale.OverrideAvailableMemoryForTests(1 * Gb);
        Assert.Equal(
            BasisPopulationScale.MinUnreliableQueuePerPeer,
            BasisPopulationScale.UnreliableQueuePerPeer(0, 8000));
    }

    [Fact]
    public void PacketPoolMax_CoversPerPeerDemandAtEightThousand()
    {
        BasisPopulationScale.OverrideAvailableMemoryForTests(64 * Gb);

        // The old fixed 262144 was below 8000 x 48 = 384000, so the pool was capped a third under
        // what its own per-peer rule asked for and every recycle past it was thrown to the GC.
        int cap = BasisPopulationScale.PacketPoolMax(0, 8000, 48);
        Assert.True(cap >= 8000 * 48, $"pool cap {cap} is below the per-peer demand of {8000 * 48}");
    }

    [Fact]
    public void SliceCap_RisesWithPopulationAndStaysBounded()
    {
        Assert.Equal(32, BasisPopulationScale.SliceCap(0, 2000));   // unchanged at the old design point
        Assert.True(BasisPopulationScale.SliceCap(0, 8000) > 32);   // more room to degrade at 8k
        Assert.InRange(BasisPopulationScale.SliceCap(0, 1_000_000), 32, 256);
    }

    [Fact]
    public void AvailableMemory_IsDetectedNotAssumed()
    {
        // The lookup is reflective because this assembly also targets netstandard2.1, where
        // GC.GetGCMemoryInfo does not exist — so a rename or signature change would silently fall
        // back to the 4 GB assumption and quietly shrink every ceiling on every real server.
        BasisPopulationScale.OverrideAvailableMemoryForTests(0);
        long detected = BasisPopulationScale.AvailableMemoryBytes;

        Assert.True(detected > 0);
        Assert.NotEqual(4L * Gb, detected);
    }
}
