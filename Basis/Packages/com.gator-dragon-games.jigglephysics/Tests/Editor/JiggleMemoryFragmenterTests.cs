using NUnit.Framework;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal class JiggleMemoryFragmenterTests {
    [Test]
    public void Allocate_HandsOutTheStartOfTheFreeSpace() {
        var fragmenter = new JiggleMemoryFragmenter(100);

        Assert.IsTrue(fragmenter.TryAllocate(10, out var start));
        Assert.AreEqual(0, start);
    }

    [Test]
    public void Allocate_HandsOutDisjointRanges() {
        var fragmenter = new JiggleMemoryFragmenter(100);

        fragmenter.TryAllocate(10, out var first);
        fragmenter.TryAllocate(10, out var second);

        Assert.AreEqual(0, first);
        Assert.AreEqual(10, second);
    }

    [Test]
    public void Allocate_FailsWhenNothingLargeEnoughRemains() {
        var fragmenter = new JiggleMemoryFragmenter(8);

        Assert.IsFalse(fragmenter.TryAllocate(9, out var start));
        Assert.AreEqual(-1, start);
    }

    [Test]
    public void Allocate_ConsumesAnExactlySizedFragment() {
        var fragmenter = new JiggleMemoryFragmenter(10);

        Assert.IsTrue(fragmenter.TryAllocate(10, out _));
        Assert.IsFalse(fragmenter.TryAllocate(1, out _));
    }

    [Test]
    public void GetIsAllocated_TracksHandedOutRanges() {
        var fragmenter = new JiggleMemoryFragmenter(20);
        fragmenter.TryAllocate(5, out _);

        Assert.IsTrue(fragmenter.GetIsAllocated(0));
        Assert.IsTrue(fragmenter.GetIsAllocated(4));
        Assert.IsFalse(fragmenter.GetIsAllocated(5));
    }

    [Test]
    public void GetIsAllocated_RejectsIndicesOutsideTheArena() {
        var fragmenter = new JiggleMemoryFragmenter(10);
        fragmenter.TryAllocate(10, out _);

        Assert.IsFalse(fragmenter.GetIsAllocated(-1));
        Assert.IsFalse(fragmenter.GetIsAllocated(10));
    }

    [Test]
    public void Free_ReturnsSpaceForReuse() {
        var fragmenter = new JiggleMemoryFragmenter(20);
        fragmenter.TryAllocate(20, out _);

        fragmenter.Free(0, 20);

        Assert.IsTrue(fragmenter.TryAllocate(20, out var start));
        Assert.AreEqual(0, start);
    }

    [Test]
    public void Free_MergesWithTheFollowingFragment() {
        var fragmenter = new JiggleMemoryFragmenter(30);
        fragmenter.TryAllocate(10, out _);
        fragmenter.TryAllocate(10, out _);

        fragmenter.Free(10, 10);

        Assert.IsTrue(fragmenter.TryAllocate(20, out var start));
        Assert.AreEqual(10, start);
    }

    [Test]
    public void Free_MergesWithThePrecedingFragment() {
        var fragmenter = new JiggleMemoryFragmenter(30);
        fragmenter.TryAllocate(30, out _);
        fragmenter.Free(20, 10);

        fragmenter.Free(10, 10);

        Assert.IsTrue(fragmenter.TryAllocate(20, out var start));
        Assert.AreEqual(10, start);
    }

    [Test]
    public void Free_MergesAcrossAHoleOnBothSides() {
        var fragmenter = new JiggleMemoryFragmenter(30);
        fragmenter.TryAllocate(30, out _);
        fragmenter.Free(0, 10);
        fragmenter.Free(20, 10);

        fragmenter.Free(10, 10);

        Assert.IsTrue(fragmenter.TryAllocate(30, out var start));
        Assert.AreEqual(0, start);
    }

    [Test]
    public void Free_KeepsDisjointFragmentsSeparate() {
        var fragmenter = new JiggleMemoryFragmenter(50);
        fragmenter.TryAllocate(50, out _);
        fragmenter.Free(0, 10);
        fragmenter.Free(30, 10);

        Assert.IsFalse(fragmenter.TryAllocate(20, out _));
        Assert.IsTrue(fragmenter.TryAllocate(10, out var start));
        Assert.AreEqual(0, start);
    }

    [Test]
    public void Free_MarksTheRangeAsUnallocated() {
        var fragmenter = new JiggleMemoryFragmenter(20);
        fragmenter.TryAllocate(10, out _);
        fragmenter.Free(0, 10);

        Assert.IsFalse(fragmenter.GetIsAllocated(0));
    }

    [Test]
    public void DoubleFree_IsRejected() {
        var fragmenter = new JiggleMemoryFragmenter(20);
        fragmenter.TryAllocate(20, out _);
        fragmenter.Free(0, 10);

        Assert.Throws<UnityException>(() => fragmenter.Free(0, 10));
    }

    [Test]
    public void OverlappingFree_IsRejected() {
        var fragmenter = new JiggleMemoryFragmenter(30);
        fragmenter.TryAllocate(30, out _);
        fragmenter.Free(10, 10);

        Assert.Throws<UnityException>(() => fragmenter.Free(5, 10));
    }

    [Test]
    public void Resize_ExtendsATrailingFragment() {
        var fragmenter = new JiggleMemoryFragmenter(10);
        fragmenter.TryAllocate(5, out _);

        fragmenter.Resize(20);

        Assert.IsTrue(fragmenter.TryAllocate(15, out var start));
        Assert.AreEqual(5, start);
    }

    [Test]
    public void Resize_AppendsAFragmentWhenTheArenaIsFull() {
        var fragmenter = new JiggleMemoryFragmenter(10);
        fragmenter.TryAllocate(10, out _);

        fragmenter.Resize(20);

        Assert.IsTrue(fragmenter.TryAllocate(10, out var start));
        Assert.AreEqual(10, start);
    }

    [Test]
    public void Resize_ToTheSameSizeIsANoOp() {
        var fragmenter = new JiggleMemoryFragmenter(10);
        fragmenter.TryAllocate(5, out _);

        fragmenter.Resize(10);

        Assert.IsTrue(fragmenter.TryAllocate(5, out var start));
        Assert.AreEqual(5, start);
        Assert.IsFalse(fragmenter.TryAllocate(1, out _));
    }

    [Test]
    public void GetHighestAllocatedIndex_ReportsMinusOneForAnEmptyArena() {
        var fragmenter = new JiggleMemoryFragmenter(10);

        Assert.AreEqual(-1, fragmenter.GetHighestAllocatedIndex());
    }

    [Test]
    public void GetHighestAllocatedIndex_TracksTheAllocatedPrefix() {
        var fragmenter = new JiggleMemoryFragmenter(10);
        fragmenter.TryAllocate(4, out _);

        Assert.AreEqual(3, fragmenter.GetHighestAllocatedIndex());
    }

    [Test]
    public void GetHighestAllocatedIndex_ReportsTheArenaEndWhenFull() {
        var fragmenter = new JiggleMemoryFragmenter(10);
        fragmenter.TryAllocate(10, out _);

        Assert.AreEqual(9, fragmenter.GetHighestAllocatedIndex());
    }

    [Test]
    public void GetHighestAllocatedIndex_ReportsTheArenaEndWhenAHoleTrailsIt() {
        var fragmenter = new JiggleMemoryFragmenter(20);
        fragmenter.TryAllocate(20, out _);
        fragmenter.Free(0, 10);

        Assert.AreEqual(19, fragmenter.GetHighestAllocatedIndex());
    }

    [Test]
    public void AllocateFreeCycles_StayConsistent() {
        var fragmenter = new JiggleMemoryFragmenter(64);
        var starts = new int[8];
        for (int i = 0; i < 8; i++) {
            Assert.IsTrue(fragmenter.TryAllocate(8, out starts[i]));
        }

        for (int i = 0; i < 8; i += 2) {
            fragmenter.Free(starts[i], 8);
        }
        for (int i = 0; i < 8; i += 2) {
            Assert.IsTrue(fragmenter.TryAllocate(8, out var reused));
            Assert.AreEqual(starts[i], reused);
        }

        Assert.IsFalse(fragmenter.TryAllocate(1, out _));
    }
}

}
