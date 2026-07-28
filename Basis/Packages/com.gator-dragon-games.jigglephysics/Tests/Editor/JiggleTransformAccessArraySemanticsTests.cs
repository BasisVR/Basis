using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Jobs;
using Object = UnityEngine.Object;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Pins what TransformAccessArray.RemoveAtSwapBack actually does. ClearIfNeeded used to drain its
/// stale buffer by decrementing a count it keeps itself and removing at that index; it now empties
/// the buffer outright, but these stay because the semantics are easy to assume wrongly and any
/// return to per-slot removal depends on them — a call that removed every occurrence of a transform
/// rather than one slot would run the count ahead of the real length.
///
/// The distinction matters here more than in most projects: the bus stores the same Transform in
/// many slots on purpose. Every slot of a tree's root array is the same root bone, the virtual root
/// repeats the first bone, and every virtual tip repeats its parent's bone.
/// </summary>
[TestFixture]
internal class JiggleTransformAccessArraySemanticsTests {
    private readonly List<GameObject> spawned = new List<GameObject>();
    private TransformAccessArray array;

    [TearDown]
    public void TearDown() {
        if (array.isCreated) {
            array.Dispose();
        }
        for (int i = 0; i < spawned.Count; i++) {
            if (spawned[i]) {
                Object.DestroyImmediate(spawned[i]);
            }
        }
        spawned.Clear();
    }

    private Transform Spawn(string name) {
        var gameObject = new GameObject(name);
        spawned.Add(gameObject);
        return gameObject.transform;
    }

    private Transform[] Fill(int count) {
        array = new TransformAccessArray(count);
        var transforms = new Transform[count];
        for (int i = 0; i < count; i++) {
            transforms[i] = Spawn($"t{i}");
            array.Add(transforms[i]);
        }
        return transforms;
    }

    [Test]
    public void RemoveAtSwapBack_RemovesExactlyOneSlot() {
        Fill(4);

        array.RemoveAtSwapBack(1);

        Assert.AreEqual(3, array.length);
    }

    [Test]
    public void RemoveAtSwapBack_MovesTheLastEntryIntoTheHole() {
        var t = Fill(4);

        array.RemoveAtSwapBack(1);

        Assert.AreSame(t[0], array[0]);
        Assert.AreSame(t[3], array[1], "the last entry should backfill the removed slot");
        Assert.AreSame(t[2], array[2]);
    }

    [Test]
    public void RemoveAtSwapBack_OnTheLastIndex_IsAPlainPop() {
        var t = Fill(3);

        array.RemoveAtSwapBack(2);

        Assert.AreEqual(2, array.length);
        Assert.AreSame(t[0], array[0]);
        Assert.AreSame(t[1], array[1]);
    }

    /// <summary>
    /// The question this fixture exists for: one slot, or every slot holding that transform?
    /// </summary>
    [Test]
    public void RemoveAtSwapBack_WithTheSameTransformInEverySlot_RemovesOnlyOne() {
        var shared = Spawn("shared");
        array = new TransformAccessArray(5);
        for (int i = 0; i < 5; i++) {
            array.Add(shared);
        }

        array.RemoveAtSwapBack(0);

        Assert.AreEqual(4, array.length, "removing one duplicate must not drop the others");
    }

    /// <summary>
    /// Is the argument an index or a count? ClearIfNeeded passes a decremented count straight in, so
    /// the two readings differ by everything: RemoveAtSwapBack(5) on an eight entry array leaves 7 if
    /// it is an index, and 3 if it is a count.
    /// </summary>
    [Test]
    public void RemoveAtSwapBack_ArgumentIsAnIndexNotACount() {
        var t = Fill(8);

        array.RemoveAtSwapBack(5);

        Debug.Log($"RemoveAtSwapBack(5) on an 8 entry array: length 8 -> {array.length}");
        Assert.AreEqual(7, array.length, "an index drops one entry; a count of 5 would have left 3");
    }

    [Test]
    public void RemoveAtSwapBack_Five_DropsTheEntryAtIndexFiveAndNothingElse() {
        var t = Fill(8);

        array.RemoveAtSwapBack(5);

        for (int i = 0; i < 5; i++) {
            Assert.AreSame(t[i], array[i], $"entry {i} should be untouched");
        }
        Assert.AreSame(t[7], array[5], "the last entry backfills index 5, which is the slot that was removed");
        Assert.AreSame(t[6], array[6], "entry 6 should be untouched");
        for (int i = 0; i < array.length; i++) {
            Assert.AreNotSame(t[5], array[i], "the entry that was at index 5 should be the only one gone");
        }
    }

    /// <summary>
    /// The shape ClearIfNeeded actually relies on: passing the post-decrement count always addresses
    /// the last live slot, so the drain never reads past the end.
    /// </summary>
    [Test]
    public void RemoveAtSwapBack_PassingTheDecrementedCount_AlwaysTargetsTheTail() {
        var t = Fill(6);

        var count = 6;
        count--;
        array.RemoveAtSwapBack(count);

        Debug.Log($"passed the decremented count {count} on a 6 entry array: length -> {array.length}");
        Assert.AreEqual(5, array.length);
        for (int i = 0; i < array.length; i++) {
            Assert.AreSame(t[i], array[i], "removing the tail must not disturb anything before it");
        }
    }

    /// <summary>
    /// Direct evidence for "one, or all five?". Records the observed length after every single call
    /// over an array of five identical transforms, so the answer is the recorded sequence rather
    /// than a passing assertion. One-at-a-time gives 5,4,3,2,1,0; removing by identity would collapse
    /// to 5,0 on the first call.
    /// </summary>
    [Test]
    public void RemoveAtSwapBack_LengthSequenceOverFiveIdenticalTransforms() {
        var shared = Spawn("shared");
        array = new TransformAccessArray(5);
        for (int i = 0; i < 5; i++) {
            array.Add(shared);
        }

        var observed = new List<int> { array.length };
        var calls = 0;
        while (array.length > 0 && calls < 50) {
            array.RemoveAtSwapBack(array.length - 1);
            observed.Add(array.length);
            calls++;
        }

        Debug.Log($"five identical transforms, length after each RemoveAtSwapBack: {string.Join(" -> ", observed)}");
        Debug.Log($"calls needed to empty a 5 entry array: {calls}");
        CollectionAssert.AreEqual(new[] { 5, 4, 3, 2, 1, 0 }, observed);
        Assert.AreEqual(5, calls, "one call per entry, so it removes one and not five");
    }

    /// <summary>The same question asked from the front of the array rather than the back.</summary>
    [Test]
    public void RemoveAtSwapBack_AtIndexZero_OverFiveIdenticalTransforms_DropsExactlyOne() {
        var shared = Spawn("shared");
        array = new TransformAccessArray(5);
        for (int i = 0; i < 5; i++) {
            array.Add(shared);
        }

        array.RemoveAtSwapBack(0);
        var afterFirst = array.length;
        array.RemoveAtSwapBack(0);
        var afterSecond = array.length;

        Debug.Log($"five identical transforms, removing index 0 twice: 5 -> {afterFirst} -> {afterSecond}");
        Assert.AreEqual(4, afterFirst);
        Assert.AreEqual(3, afterSecond);
    }

    [Test]
    public void RemoveAtSwapBack_WithDuplicates_LeavesTheRemainingOccurrencesAddressable() {
        var shared = Spawn("shared");
        var other = Spawn("other");
        array = new TransformAccessArray(4);
        array.Add(shared);
        array.Add(other);
        array.Add(shared);
        array.Add(shared);

        array.RemoveAtSwapBack(1);

        Assert.AreEqual(3, array.length);
        for (int i = 0; i < array.length; i++) {
            Assert.AreSame(shared, array[i], $"slot {i} should still hold the shared transform");
        }
    }

    /// <summary>
    /// Exactly the loop ClearIfNeeded runs, over an array that is all duplicates. If a single call
    /// ever removed more than one slot, the decremented index would overshoot the real length.
    /// </summary>
    [Test]
    public void PopBackLoop_OverDuplicates_DrainsExactlyToEmpty() {
        var shared = Spawn("shared");
        const int count = 6;
        array = new TransformAccessArray(count);
        for (int i = 0; i < count; i++) {
            array.Add(shared);
        }

        var remaining = count;
        for (int i = 0; i < count; i++) {
            remaining--;
            Assert.AreEqual(remaining, array.length - 1, $"length disagreed with the tracked count at step {i}");
            array.RemoveAtSwapBack(remaining);
        }

        Assert.AreEqual(0, array.length);
        Assert.AreEqual(0, remaining);
    }

    [Test]
    public void PopBackLoop_OverDistinctTransforms_DrainsExactlyToEmpty() {
        const int count = 8;
        Fill(count);

        var remaining = count;
        for (int i = 0; i < count; i++) {
            remaining--;
            array.RemoveAtSwapBack(remaining);
        }

        Assert.AreEqual(0, array.length);
    }

    [Test]
    public void RemoveAtSwapBack_DoesNotChangeCapacity() {
        Fill(4);
        var capacity = array.capacity;

        array.RemoveAtSwapBack(0);

        Assert.AreEqual(capacity, array.capacity);
    }

    /// <summary>
    /// A destroyed transform still occupies its slot; the bus swaps in a pooled dummy rather than
    /// relying on removal, so the count has to stay stable when one dies.
    /// </summary>
    [Test]
    public void RemoveAtSwapBack_AfterATransformIsDestroyed_StillRemovesOneSlot() {
        var t = Fill(4);
        Object.DestroyImmediate(t[2].gameObject);

        array.RemoveAtSwapBack(0);

        Assert.AreEqual(3, array.length);
    }
}

}
