using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace GatorDragonGames.JigglePhysics.Tests {

/// <summary>
/// Covers the UpdateArrays family. Every job caches the memory bus's native arrays by value, and the
/// bus swaps those arrays out wholesale whenever it outgrows a buffer — so a job that misses a rebind
/// keeps reading a freed allocation. Each test starts from an unbound job and asserts UpdateArrays
/// binds everything the constructor binds, which is what catches a field added to one but not the
/// other.
/// </summary>
[TestFixture]
internal unsafe class JiggleJobArrayBindingTests {
    private const float FixedDeltaTime = 0.02f;

    private JiggleMemoryBus bus;

    [SetUp]
    public void SetUp() {
        bus = new JiggleMemoryBus();
    }

    [TearDown]
    public void TearDown() {
        bus?.Dispose();
        bus = null;
    }

    private static void AssertSameBuffer<T>(NativeArray<T> expected, NativeArray<T> actual, string field)
        where T : struct {
        Assert.IsTrue(expected.IsCreated, $"{field}: the reference binding was never created");
        Assert.IsTrue(actual.Equals(expected), $"{field} was not rebound to the bus's current buffer");
    }

    /// <summary>Marks the shared grid cell so a rebound reference can be told from an unbound one.</summary>
    private void MarkGlobalCell(int marker) {
        var cell = bus.globalCell.Value;
        cell.staleness = marker;
        bus.globalCell.Value = cell;
    }

    [Test]
    public void BulkTransformRead_RebindsEveryBufferTheConstructorBinds() {
        var reference = new JiggleJobBulkTransformRead(bus);
        var rebound = default(JiggleJobBulkTransformRead);

        rebound.UpdateArrays(bus);

        AssertSameBuffer(reference.simulateInputPoses, rebound.simulateInputPoses, nameof(rebound.simulateInputPoses));
    }

    [Test]
    public void BulkReadRoots_RebindsEveryBufferTheConstructorBinds() {
        var reference = new JiggleJobBulkReadRoots(bus);
        var rebound = default(JiggleJobBulkReadRoots);

        rebound.UpdateArrays(bus);

        AssertSameBuffer(reference.rootOutputPositions, rebound.rootOutputPositions, nameof(rebound.rootOutputPositions));
    }

    [Test]
    public void TransformWrite_RebindsEveryBufferTheConstructorBinds() {
        var reference = new JiggleJobTransformWrite(bus);
        var rebound = default(JiggleJobTransformWrite);

        rebound.UpdateArrays(bus);

        AssertSameBuffer(reference.previousLocalPoses, rebound.previousLocalPoses, nameof(rebound.previousLocalPoses));
        AssertSameBuffer(reference.inputInterpolatedPoses, rebound.inputInterpolatedPoses, nameof(rebound.inputInterpolatedPoses));
    }

    [Test]
    public void BulkTransformReset_RebindsEveryBufferTheConstructorBinds() {
        var reference = new JiggleJobBulkTransformReset(bus);
        var rebound = default(JiggleJobBulkTransformReset);

        rebound.UpdateArrays(bus);

        AssertSameBuffer(reference.restPoseTransforms, rebound.restPoseTransforms, nameof(rebound.restPoseTransforms));
        AssertSameBuffer(reference.previousLocalTransforms, rebound.previousLocalTransforms, nameof(rebound.previousLocalTransforms));
    }

    [Test]
    public void BulkTransformReadReset_RebindsEveryBufferTheConstructorBinds() {
        var reference = new JiggleJobBulkTransformReadReset(bus);
        var rebound = default(JiggleJobBulkTransformReadReset);

        rebound.UpdateArrays(bus);

        AssertSameBuffer(reference.restPoseTransforms, rebound.restPoseTransforms, nameof(rebound.restPoseTransforms));
        AssertSameBuffer(reference.previousLocalTransforms, rebound.previousLocalTransforms, nameof(rebound.previousLocalTransforms));
        AssertSameBuffer(reference.simulateInputPoses, rebound.simulateInputPoses, nameof(rebound.simulateInputPoses));
    }

    [Test]
    public void Interpolation_RebindsEveryBufferTheConstructorBinds() {
        var reference = new JiggleJobInterpolation(bus, 0.0, FixedDeltaTime);
        var rebound = default(JiggleJobInterpolation);

        rebound.UpdateArrays(bus);

        AssertSameBuffer(reference.previousPoses, rebound.previousPoses, nameof(rebound.previousPoses));
        AssertSameBuffer(reference.currentPoses, rebound.currentPoses, nameof(rebound.currentPoses));
        AssertSameBuffer(reference.outputInterpolatedPoses, rebound.outputInterpolatedPoses, nameof(rebound.outputInterpolatedPoses));
        AssertSameBuffer(reference.realRootPositions, rebound.realRootPositions, nameof(rebound.realRootPositions));
    }

    [Test]
    public void InputInterpolation_RebindsEveryBufferTheConstructorBinds() {
        var reference = new JiggleJobInputInterpolation(bus, 0.0, FixedDeltaTime);
        var rebound = default(JiggleJobInputInterpolation);

        rebound.UpdateArrays(bus);

        AssertSameBuffer(reference.previousInputs, rebound.previousInputs, nameof(rebound.previousInputs));
        AssertSameBuffer(reference.currentInputs, rebound.currentInputs, nameof(rebound.currentInputs));
        AssertSameBuffer(reference.outputInterpolatedPoses, rebound.outputInterpolatedPoses, nameof(rebound.outputInterpolatedPoses));
    }

    [Test]
    public void ColliderCull_RebindsEveryBufferTheConstructorBinds() {
        var reference = new JiggleJobColliderCull(bus);
        var rebound = default(JiggleJobColliderCull);

        rebound.UpdateArrays(bus);

        AssertSameBuffer(reference.jiggleColliders, rebound.jiggleColliders, nameof(rebound.jiggleColliders));
        AssertSameBuffer(reference.broadPhaseEntries, rebound.broadPhaseEntries, nameof(rebound.broadPhaseEntries));
    }

    [Test]
    public void BroadPhase_RebindsEveryBufferTheConstructorBinds() {
        MarkGlobalCell(31);
        bus.broadPhaseMap.Add(new int2(7, 7), default);
        var reference = new JiggleJobBroadPhase(bus);
        var rebound = default(JiggleJobBroadPhase);

        rebound.UpdateArrays(bus);

        AssertSameBuffer(reference.broadPhaseEntries, rebound.broadPhaseEntries, nameof(rebound.broadPhaseEntries));
        Assert.AreEqual(reference.jiggleColliderCount, rebound.jiggleColliderCount);
        Assert.IsTrue(rebound.broadPhaseMap.ContainsKey(new int2(7, 7)), "broadPhaseMap was not rebound");
        Assert.AreEqual(31, rebound.globalCell.Value.staleness, "globalCell was not rebound");
    }

    [Test]
    public void BroadPhaseClear_RebindsEveryBufferTheConstructorBinds() {
        MarkGlobalCell(17);
        bus.broadPhaseMap.Add(new int2(2, 3), default);
        var rebound = default(JiggleJobBroadPhaseClear);

        rebound.UpdateArrays(bus);

        Assert.IsTrue(rebound.broadPhaseMap.ContainsKey(new int2(2, 3)), "broadPhaseMap was not rebound");
        Assert.AreEqual(17, rebound.globalCell.Value.staleness, "globalCell was not rebound");
    }

    [Test]
    public void Simulate_RebindsEveryBufferTheConstructorBinds() {
        MarkGlobalCell(23);
        bus.broadPhaseMap.Add(new int2(5, 5), default);
        var reference = new JiggleJobSimulate(bus, FixedDeltaTime);
        var rebound = default(JiggleJobSimulate);

        rebound.UpdateArrays(bus);

        AssertSameBuffer(reference.inputPoses, rebound.inputPoses, nameof(rebound.inputPoses));
        AssertSameBuffer(reference.outputPoses, rebound.outputPoses, nameof(rebound.outputPoses));
        AssertSameBuffer(reference.jiggleTrees, rebound.jiggleTrees, nameof(rebound.jiggleTrees));
        AssertSameBuffer(reference.personalColliders, rebound.personalColliders, nameof(rebound.personalColliders));
        AssertSameBuffer(reference.sceneColliders, rebound.sceneColliders, nameof(rebound.sceneColliders));
        Assert.AreEqual(bus.sceneColliderCount, rebound.sceneColliderCount);
        Assert.IsTrue(rebound.broadPhaseMap.ContainsKey(new int2(5, 5)), "broadPhaseMap was not rebound");
        Assert.AreEqual(23, rebound.globalCell.Value.staleness, "globalCell was not rebound");
    }

    [Test]
    public void BulkColliderTransformRead_RebindsTheColliderBufferItWasGiven() {
        var reference = new JiggleJobBulkColliderTransformRead(bus.sceneColliders);
        var rebound = default(JiggleJobBulkColliderTransformRead);

        rebound.UpdateArrays(bus.sceneColliders);

        AssertSameBuffer(reference.colliders, rebound.colliders, nameof(rebound.colliders));
    }

    /// <summary>
    /// The personal and scene collider pools are separate arrays; the read job is instantiated once
    /// per pool, so rebinding must follow whichever pool it was handed rather than a fixed one.
    /// </summary>
    [Test]
    public void BulkColliderTransformRead_FollowsThePoolItIsPointedAt() {
        var job = new JiggleJobBulkColliderTransformRead(bus.sceneColliders);

        job.UpdateArrays(bus.personalColliders);

        AssertSameBuffer(bus.personalColliders, job.colliders, nameof(job.colliders));
    }
}

}
