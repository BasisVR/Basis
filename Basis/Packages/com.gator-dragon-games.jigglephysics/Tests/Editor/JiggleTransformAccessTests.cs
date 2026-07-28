using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Covers JiggleDoubleBufferTransformAccessArray, the sliced rebuild that keeps a structural change
/// from registering thousands of transforms in one frame. Registration is budgeted per call while
/// de-registration empties the stale buffer outright, so the tests here are mostly about how a
/// rebuild behaves when it cannot finish in one go — which is the normal case during a join storm.
/// </summary>
[TestFixture]
internal class JiggleTransformAccessTests {
    private JiggleBoneScene scene;
    private JiggleDoubleBufferTransformAccessArray buffer;

    [SetUp]
    public void SetUp() {
        JiggleRuntimeStatics.Boot();
        scene = new JiggleBoneScene();
        buffer = new JiggleDoubleBufferTransformAccessArray(8);
    }

    [TearDown]
    public void TearDown() {
        buffer?.Dispose();
        buffer = null;
        JiggleRuntimeStatics.Shutdown();
        scene?.Dispose();
        scene = null;
    }

    private List<Transform> Transforms(int count) {
        var list = new List<Transform>(count);
        for (int i = 0; i < count; i++) {
            list.Add(scene.Spawn($"bone{i}"));
        }
        return list;
    }

    /// <summary>Runs a rebuild to completion, tolerating the calls a pending clear eats.</summary>
    private void BuildAll(List<Transform> transforms, int batchSize = 512) {
        var index = 0;
        for (int guard = 0; guard < 256; guard++) {
            buffer.GenerateNewAccessArrays(ref index, out var hasFinished, transforms, batchSize);
            if (hasFinished) {
                return;
            }
        }
        Assert.Fail("the rebuild never finished");
    }

    /// <summary>
    /// ClearIfNeeded empties the stale buffer and resets the count it keeps alongside it. That is
    /// only correct while the two agree, so this cycles the buffer at a batch size small enough to
    /// slice the refill, and shrinks the list as well as growing it. Any drift shows up as residue
    /// that resurfaces the next time the buffer is published.
    /// </summary>
    [Test]
    public void RepeatedRebuilds_AtASlicingBatchSize_PublishExactlyTheRequestedTransforms() {
        foreach (var size in new[] { 7, 3, 11, 1, 9, 4 }) {
            var transforms = Transforms(size);
            BuildAll(transforms, batchSize: 3);
            buffer.Flip();

            var published = buffer.GetTransformAccessArray();
            Assert.AreEqual(size, published.length, $"cycle of {size} published the wrong count");
            for (int i = 0; i < size; i++) {
                Assert.AreSame(transforms[i], published[i], $"cycle of {size}, slot {i}");
            }
        }
    }

    /// <summary>
    /// The shrink is the direction residue hides in: a big stale buffer has to drain completely
    /// before a much smaller list is published, or the leftovers ride along as extra transforms.
    /// </summary>
    [Test]
    public void RebuildAfterALargeShrink_LeavesNoResidueFromTheStaleBuffer() {
        var many = Transforms(64);
        BuildAll(many, batchSize: 5);
        buffer.Flip();
        Assert.AreEqual(64, buffer.GetTransformAccessArray().length);

        var few = Transforms(2);
        BuildAll(few, batchSize: 5);
        buffer.Flip();

        var published = buffer.GetTransformAccessArray();
        Assert.AreEqual(2, published.length, "the 64 stale entries must be fully drained first");
        Assert.AreSame(few[0], published[0]);
        Assert.AreSame(few[1], published[1]);
    }

    /// <summary>
    /// A batch size of one makes every drain and every add its own slice, which is the harshest
    /// version of the same invariant.
    /// </summary>
    [Test]
    public void RepeatedRebuilds_OneEntryPerSlice_StayExact() {
        foreach (var size in new[] { 5, 2, 6 }) {
            var transforms = Transforms(size);
            BuildAll(transforms, batchSize: 1);
            buffer.Flip();

            Assert.AreEqual(size, buffer.GetTransformAccessArray().length, $"cycle of {size}");
        }
    }

    [Test]
    public void NewBuffer_PublishesAnEmptyArray() {
        Assert.IsTrue(buffer.GetTransformAccessArray().isCreated);
        Assert.AreEqual(0, buffer.GetTransformAccessArray().length);
    }

    [Test]
    public void Generate_AddsEveryTransformInOnePass() {
        var transforms = Transforms(3);
        var index = 0;

        buffer.GenerateNewAccessArrays(ref index, out var hasFinished, transforms);

        Assert.IsTrue(hasFinished);
        Assert.AreEqual(3, index);
    }

    /// <summary>
    /// The rebuild is not visible until it flips, so a half-built array can never be handed to a job.
    /// </summary>
    [Test]
    public void Generate_DoesNotPublishUntilTheFlip() {
        var transforms = Transforms(3);
        BuildAll(transforms);

        Assert.AreEqual(0, buffer.GetTransformAccessArray().length);
        buffer.Flip();
        Assert.AreEqual(3, buffer.GetTransformAccessArray().length);
    }

    [Test]
    public void Generate_IsSlicedByTheBatchSize() {
        var transforms = Transforms(5);
        var index = 0;

        buffer.GenerateNewAccessArrays(ref index, out var afterFirst, transforms, 2);
        buffer.GenerateNewAccessArrays(ref index, out var afterSecond, transforms, 2);
        buffer.GenerateNewAccessArrays(ref index, out var afterThird, transforms, 2);

        Assert.IsFalse(afterFirst);
        Assert.IsFalse(afterSecond);
        Assert.IsTrue(afterThird);
        Assert.AreEqual(5, index);
    }

    [Test]
    public void Generate_OnAnEmptyListFinishesImmediately() {
        var index = 0;

        buffer.GenerateNewAccessArrays(ref index, out var hasFinished, new List<Transform>());

        Assert.IsTrue(hasFinished);
        Assert.AreEqual(0, index);
    }

    /// <summary>
    /// A rig destroyed between scheduling and committing leaves a hole in the list. TransformAccessArray
    /// rejects nulls outright, so the hole is padded with a pooled dummy to keep every later index in
    /// the slice pointing at the bone it was allocated for.
    /// </summary>
    [Test]
    public void Generate_SubstitutesAPooledDummyForADestroyedTransform() {
        var transforms = Transforms(2);
        Object.DestroyImmediate(transforms[1].gameObject);

        BuildAll(transforms);
        buffer.Flip();

        var access = buffer.GetTransformAccessArray();
        Assert.AreEqual(2, access.length);
        Assert.AreSame(JiggleMemoryBus.GetDummyTransform(1), access[1]);
    }

    [Test]
    public void Flip_SwapsTheTwoBuffers() {
        var first = Transforms(3);
        BuildAll(first);
        buffer.Flip();
        var second = Transforms(5);

        BuildAll(second);
        buffer.Flip();

        Assert.AreEqual(5, buffer.GetTransformAccessArray().length);
    }

    /// <summary>
    /// The buffer that just went out of service still holds the previous frame's registrations, and
    /// the first rebuild call after a flip always spends itself clearing rather than adding — the
    /// drain is one call now, but it still consumes that call.
    /// </summary>
    [Test]
    public void Generate_AfterAFlip_SpendsACallDrainingTheStaleBuffer() {
        var transforms = Transforms(3);
        BuildAll(transforms);
        buffer.Flip();
        var index = 0;

        buffer.GenerateNewAccessArrays(ref index, out var hasFinished, transforms);

        Assert.IsFalse(hasFinished);
        Assert.AreEqual(0, index, "nothing may be added while the stale buffer is still draining");
    }

    /// <summary>
    /// The drain empties the stale buffer wholesale, so it costs one call regardless of how many
    /// entries are registered and never eats into the add budget.
    /// </summary>
    [Test]
    public void ClearIfNeeded_DrainsTheStaleBufferInOneCall() {
        var transforms = Transforms(4);
        BuildAll(transforms);
        buffer.Flip();
        BuildAll(transforms);
        buffer.Flip();
        var index = 0;

        buffer.ClearIfNeeded(2);
        buffer.GenerateNewAccessArrays(ref index, out var afterFirstAdd, transforms, 2);
        buffer.GenerateNewAccessArrays(ref index, out var afterSecondAdd, transforms, 2);

        Assert.IsFalse(afterFirstAdd, "two of the four entries are registered");
        Assert.IsTrue(afterSecondAdd, "all four stale entries went in one drain, so two calls of two finish the refill");
        Assert.AreEqual(4, index);
    }

    [Test]
    public void ClearIfNeeded_WithoutAFlipDoesNothing() {
        var transforms = Transforms(3);
        BuildAll(transforms);

        buffer.ClearIfNeeded();
        buffer.Flip();

        Assert.AreEqual(3, buffer.GetTransformAccessArray().length);
    }

    [Test]
    public void Rebuild_AfterAFlipReplacesTheContents() {
        var first = Transforms(3);
        BuildAll(first);
        buffer.Flip();
        var replacement = Transforms(2);

        BuildAll(replacement);
        buffer.Flip();

        var access = buffer.GetTransformAccessArray();
        Assert.AreEqual(2, access.length);
        Assert.AreSame(replacement[0], access[0]);
        Assert.AreSame(replacement[1], access[1]);
    }

    [Test]
    public void Dispose_ReleasesBothBuffers() {
        var transforms = Transforms(2);
        BuildAll(transforms);
        buffer.Flip();

        buffer.Dispose();

        Assert.IsFalse(buffer.GetTransformAccessArray().isCreated);
        buffer = null;
    }
}

}
