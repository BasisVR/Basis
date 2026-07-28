using NUnit.Framework;
using Unity.Mathematics;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal unsafe class JiggleJobSimulateCollisionTests {
    private const float Dt = 0.02f;

    private JiggleSimHarness harness;

    [TearDown]
    public void TearDown() {
        harness?.Dispose();
        harness = null;
    }

    /// <summary>
    /// Two real points laid along +X with every elasticity off, so a depenetration applied to the
    /// last real point survives Constrain() unmodified and can be measured directly.
    /// </summary>
    private JiggleSimHarness BuildCollidable(float collisionRadius = 0.25f) {
        var tree = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f,
            JiggleTestFactory.Params(collisionRadius: collisionRadius));
        harness = new JiggleSimHarness(tree, Dt);
        return harness;
    }

    [Test]
    public void Sphere_FarAway_DoesNotMoveThePoint() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(50f, 50f, 50f), 1f);
        harness.AddGlobalCollider(0);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Sphere_OverlappingThePoint_PushesItOutwards() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(1f, -0.5f, 0f), 1f);
        harness.AddGlobalCollider(0);

        harness.Step();

        var settled = harness.Point(2).position;
        Assert.Greater(settled.y, 0f, "point should be pushed away from a collider sitting below it");
        Assert.GreaterOrEqual(math.distance(settled, new float3(1f, -0.5f, 0f)), 1f + 0.25f - 1e-3f);
    }

    [Test]
    public void Sphere_Disabled_IsIgnored() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(1f, -0.5f, 0f), 1f, enabled: false);
        harness.AddGlobalCollider(0);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Sphere_IsIgnoredWhenThePointHasNoCollisionRadius() {
        BuildCollidable(collisionRadius: 0f);
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(1f, -0.5f, 0f), 1f);
        harness.AddGlobalCollider(0);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Depenetration_IsDiscardedWhenTheNeighbourIsFullyRigid() {
        var tree = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f,
            JiggleTestFactory.Params(collisionRadius: 0.25f,
                rootElasticity: 1f, angleElasticity: 1f, lengthElasticity: 1f));
        harness = new JiggleSimHarness(tree, Dt);
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(1f, -0.5f, 0f), 1f);
        harness.AddGlobalCollider(0);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Plane_PushesAPointUpToItsRadiusAboveTheSurface() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Plane(new float3(0f, 0.5f, 0f), new float3(0f, 1f, 0f));
        harness.AddGlobalCollider(0);

        harness.Step();

        Assert.AreEqual(0.75f, harness.Point(2).position.y, 1e-3f);
    }

    [Test]
    public void Plane_LeavesPointsAlreadyClearOfTheSurfaceAlone() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Plane(new float3(0f, -5f, 0f), new float3(0f, 1f, 0f));
        harness.AddGlobalCollider(0);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Plane_RespectsANonAxisAlignedNormal() {
        BuildCollidable();
        var normal = math.normalize(new float3(0f, 1f, 1f));
        harness.sceneColliders[0] = JiggleTestFactory.Plane(new float3(1f, 0f, 0f), normal);
        harness.AddGlobalCollider(0);

        harness.Step();

        var signedDistance = math.dot(harness.Point(2).position - new float3(1f, 0f, 0f), normal);
        Assert.AreEqual(0.25f, signedDistance, 1e-3f);
    }

    [Test]
    public void Capsule_PushesAPointRadiallyOffItsShaft() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Capsule(new float3(1f, 0f, 0f), 0.5f, 4f, JiggleCollider.CapsuleAxis.Y);
        harness.AddGlobalCollider(0);

        harness.Step();

        var settled = harness.Point(2).position;
        var radial = math.length(new float2(settled.x - 1f, settled.z));
        Assert.GreaterOrEqual(radial, 0.5f + 0.25f - 1e-2f);
    }

    [Test]
    public void Capsule_BeyondItsEndCapBehavesLikeASphere() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Capsule(new float3(1f, -2f, 0f), 0.5f, 2f, JiggleCollider.CapsuleAxis.Y);
        harness.AddGlobalCollider(0);

        harness.Step();

        var capEnd = new float3(1f, -1f, 0f);
        Assert.GreaterOrEqual(math.distance(harness.Point(2).position, capEnd), 0.5f + 0.25f - 1e-2f);
    }

    [Test]
    public void Capsule_ZeroHeightCollapsesToASphereAtItsCentre() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Capsule(new float3(1f, -0.5f, 0f), 0.6f, 0f, JiggleCollider.CapsuleAxis.Y);
        harness.AddGlobalCollider(0);

        harness.Step();

        Assert.GreaterOrEqual(math.distance(harness.Point(2).position, new float3(1f, -0.5f, 0f)), 0.6f + 0.25f - 1e-2f);
    }

    /// <summary>
    /// A shaft centred at the origin only reaches the point at x=1 when it runs along X; along Z it
    /// stays a metre away and must not touch it.
    /// </summary>
    [Test]
    public void Capsule_AlongItsAxis_ReachesThePoint() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Capsule(new float3(0f, -0.6f, 0f), 0.4f, 6f, JiggleCollider.CapsuleAxis.X);
        harness.AddGlobalCollider(0);

        harness.Step();

        Assert.Greater(harness.Point(2).position.y, 0f);
    }

    [Test]
    public void Capsule_AcrossItsAxis_LeavesThePointAlone() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Capsule(new float3(0f, -0.6f, 0f), 0.4f, 6f, JiggleCollider.CapsuleAxis.Z);
        harness.AddGlobalCollider(0);

        harness.Step();

        Assert.AreEqual(0f, harness.Point(2).position.y, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void PersonalColliders_InTheTreesOwnRangeAreApplied() {
        BuildCollidable();
        harness.personalColliders[3] = JiggleTestFactory.Sphere(new float3(1f, -0.5f, 0f), 1f);
        harness.SetTreeColliderRange(3, 1);

        harness.Step();

        Assert.Greater(harness.Point(2).position.y, 0f);
    }

    [Test]
    public void PersonalColliders_OutsideTheTreesRangeAreIgnored() {
        BuildCollidable();
        harness.personalColliders[3] = JiggleTestFactory.Sphere(new float3(1f, -0.5f, 0f), 1f);
        harness.SetTreeColliderRange(0, 0);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void GridColliders_ApplyWhenTheTreeExtentOverlapsTheirCell() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(1f, -0.5f, 0f), 1f);
        harness.AddGridCollider(JiggleTestFactory.Key(new float3(1f, 0f, 0f)), 0);

        harness.Step();

        Assert.Greater(harness.Point(2).position.y, 0f);
    }

    [Test]
    public void GridColliders_InUnrelatedCellsAreIgnored() {
        BuildCollidable();
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(1f, -0.5f, 0f), 1f);
        harness.AddGridCollider(new int2(500, 500), 0);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    /// <summary>
    /// A long but physically real tree still walks the grid; the runaway guard must only fire on
    /// corrupt extents. Note the guard counts cells, not metres, so the length this tolerates scales
    /// with <see cref="JiggleSettings.BroadPhaseCellSize"/> squared.
    /// </summary>
    [Test]
    public void BroadphaseGuard_AllowsGridWalkForLongButRealExtents() {
        const float length = 20f;
        var tree = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f,
            JiggleTestFactory.Params(collisionRadius: 0.25f));
        harness = new JiggleSimHarness(tree, Dt);
        harness.SetInputPosition(2, new float3(length, 0f, 0f));
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(length, -0.5f, 0f), 1f);
        harness.AddGridCollider(JiggleTestFactory.Key(new float3(length, 0f, 0f)), 0);

        harness.Step();

        Assert.Greater(harness.Point(2).position.y, 0f);
    }

    /// <summary>
    /// Corrupt point positions produce extents spanning millions of cells. The guard skips the grid
    /// walk in that case, while global-cell colliders must still be applied.
    /// </summary>
    [Test]
    public void BroadphaseGuard_SkipsGridWalkForRunawayExtents() {
        var tree = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f,
            JiggleTestFactory.Params(collisionRadius: 0.25f));
        harness = new JiggleSimHarness(tree, Dt);
        harness.SetInputPosition(2, new float3(5000f, 0f, 0f));
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(5000f, -0.5f, 0f), 1f);
        harness.AddGridCollider(JiggleTestFactory.Key(new float3(5000f, 0f, 0f)), 0);

        harness.Step();

        Assert.AreEqual(0f, harness.Point(2).position.y, 1e-3f,
            "grid colliders must be skipped once the extent is implausible");
    }

    [Test]
    public void BroadphaseGuard_StillAppliesGlobalCollidersForRunawayExtents() {
        var tree = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f,
            JiggleTestFactory.Params(collisionRadius: 0.25f));
        harness = new JiggleSimHarness(tree, Dt);
        harness.SetInputPosition(2, new float3(5000f, 0f, 0f));
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(5000f, -0.5f, 0f), 1f);
        harness.AddGlobalCollider(0);

        harness.Step();

        Assert.Greater(harness.Point(2).position.y, 0f);
    }

    [Test]
    public void BroadphaseGuard_SurvivesExtentsThatWouldOverflowIntegerSpans() {
        var tree = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f,
            JiggleTestFactory.Params(collisionRadius: 0.25f));
        harness = new JiggleSimHarness(tree, Dt);
        var corrupt = harness.Point(2);
        corrupt.position = new float3(3e9f, 0f, 3e9f);
        harness.SetPoint(2, corrupt);

        Assert.DoesNotThrow(() => harness.Step());
        JiggleAssert.IsFinite(harness.Point(2).position);
    }

    [Test]
    public void Collisions_KeepTheChainFiniteWhenACollidersSitsOnTheChain() {
        var tree = JiggleTestTree.Chain(4, float3.zero, new float3(0f, -1f, 0f), 0.5f,
            JiggleTestFactory.Params(collisionRadius: 0.3f, angleElasticity: 0.5f, lengthElasticity: 1f,
                gravityMultiplier: 1f));
        harness = new JiggleSimHarness(tree, Dt, new float3(0f, -10f, 0f));
        harness.sceneColliders[0] = JiggleTestFactory.Sphere(new float3(0f, -1f, 0f), 0.6f);
        harness.AddGlobalCollider(0);

        harness.Step(240);

        for (int i = 0; i < harness.Tree.pointCount; i++) {
            JiggleAssert.IsFinite(harness.Point(i).position, $"point {i}");
        }
    }
}

}
