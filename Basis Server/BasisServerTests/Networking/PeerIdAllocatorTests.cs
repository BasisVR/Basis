using Basis.Network.Core;
using System.Collections.Concurrent;
using Xunit;

namespace BasisServerTests;

public class PeerIdAllocatorTests
{
    [Fact]
    public void AllocatesDenseIdsFromZero()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(0);
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enumerable.Range(0, 4).Select(_ => ids.Allocate()).ToArray());
        Assert.Equal(4, ids.LiveCount);
    }

    [Fact]
    public void ReleasedIdIsHeldBackUntilTheReuseDelayPasses()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(150);
        int first = ids.Allocate();
        ids.Allocate();
        Assert.True(ids.Release(first));
        Assert.Equal(2, ids.Allocate());
        Thread.Sleep(250);
        Assert.Equal(first, ids.Allocate());
    }

    [Fact]
    public void WithoutADelayFreedIdsAreReusedOldestFirst()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(0);
        for (int index = 0; index < 5; index++) ids.Allocate();
        ids.Release(3);
        ids.Release(1);
        Assert.Equal(3, ids.Allocate());
        Assert.Equal(1, ids.Allocate());
        Assert.Equal(5, ids.Allocate());
    }

    [Fact]
    public void ReleasingAnIdTwiceOrOneNeverHandedOutIsIgnored()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(0);
        int id = ids.Allocate();
        Assert.True(ids.Release(id));
        Assert.False(ids.Release(id));
        Assert.False(ids.Release(999));
        Assert.Equal(id, ids.Allocate());
        Assert.Equal(1, ids.Allocate());
    }

    [Fact]
    public void ResetStartsOverFromZero()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(0);
        ids.Allocate();
        ids.Allocate();
        ids.Reset();
        Assert.Equal(0, ids.LiveCount);
        Assert.Equal(0, ids.Allocate());
    }

    [Fact]
    public void ConcurrentCallersNeverHoldTheSameIdAtOnce()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(0);
        ConcurrentDictionary<int, byte> held = new ConcurrentDictionary<int, byte>();
        int collisions = 0;
        Parallel.For(0, 20000, index =>
        {
            int id = ids.Allocate();
            if (!held.TryAdd(id, 0)) Interlocked.Increment(ref collisions);
            if (index % 2 == 0)
            {
                held.TryRemove(id, out _);
                ids.Release(id);
            }
        });
        Assert.Equal(0, collisions);
        Assert.Equal(held.Count, ids.LiveCount);
    }
}
