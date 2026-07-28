using NUnit.Framework;
using Unity.Mathematics;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal unsafe class JiggleJobSimulateIntegrateTests {
    private const float Dt = 0.02f;
    private static readonly float3 Gravity = new float3(0f, -10f, 0f);

    private JiggleSimHarness harness;

    [TearDown]
    public void TearDown() {
        harness?.Dispose();
        harness = null;
    }

    private JiggleSimHarness BuildChain(JigglePointParameters parameters, float3 gravity = default) {
        var tree = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f, parameters);
        harness = new JiggleSimHarness(tree, Dt, gravity);
        return harness;
    }

    private static void SetVelocity(JiggleSimHarness harness, int index, float3 velocity) {
        var point = harness.Point(index);
        point.lastPosition = point.position - velocity;
        harness.SetPoint(index, point);
    }

    [Test]
    public void AtRest_WithNoForces_PointDoesNotMove() {
        BuildChain(JiggleTestFactory.Params());

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Gravity_DisplacesByAccelerationTimesDeltaTimeSquared() {
        BuildChain(JiggleTestFactory.Params(gravityMultiplier: 1f), Gravity);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, Gravity.y * Dt * Dt, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Gravity_ScalesWithGravityMultiplier() {
        BuildChain(JiggleTestFactory.Params(gravityMultiplier: 0.5f), Gravity);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, Gravity.y * Dt * Dt * 0.5f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Gravity_DoesNotMoveTheVirtualRoot() {
        BuildChain(JiggleTestFactory.Params(gravityMultiplier: 1f), Gravity);

        harness.Step();

        JiggleAssert.AreEqual(new float3(-1f, 0f, 0f), harness.Point(0).position, JiggleTestFactory.Tolerance);
    }

    /// <summary>
    /// Each substep is a whole fixed step, so gravity accrues once per substep rather than being
    /// divided among them. The old behaviour scaled the step down instead, which is why falling
    /// behind used to slow the simulation rather than let it catch up.
    /// </summary>
    [Test]
    public void Substeps_EachAdvanceAFullFixedStep() {
        BuildChain(JiggleTestFactory.Params(gravityMultiplier: 1f), Gravity);
        harness.job.substeps = 2;

        harness.Step();

        // Two Verlet steps under constant acceleration land at 3gt², not 2gt².
        JiggleAssert.AreEqual(new float3(1f, Gravity.y * Dt * Dt * 3f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    /// <summary>
    /// The property the whole substep mechanism exists for: one frame that owes two steps must land
    /// exactly where two frames owing one step each would. Without it, jiggle runs slower the lower
    /// the frame rate goes.
    /// </summary>
    [Test]
    public void Substeps_MatchRunningTheSameNumberOfSingleSteps() {
        BuildChain(JiggleTestFactory.Params(angleElasticity: 0.5f, lengthElasticity: 0.8f,
            gravityMultiplier: 1f, drag: 0.1f, airDrag: 0.05f), Gravity);
        harness.job.substeps = 1;
        harness.Step(6);
        var singleSteps = harness.Point(2).position;

        harness.Dispose();
        BuildChain(JiggleTestFactory.Params(angleElasticity: 0.5f, lengthElasticity: 0.8f,
            gravityMultiplier: 1f, drag: 0.1f, airDrag: 0.05f), Gravity);
        harness.job.substeps = 3;
        harness.Step(2);
        var substepped = harness.Point(2).position;

        JiggleAssert.AreEqual(singleSteps, substepped, 1e-5f);
    }

    [Test]
    public void SubstepsOfOne_AreTheSameAsNoSubstepping() {
        BuildChain(JiggleTestFactory.Params(gravityMultiplier: 1f, drag: 0.1f), Gravity);
        harness.job.substeps = 1;
        harness.Step(4);
        var baseline = harness.Point(2).position;

        harness.Dispose();
        BuildChain(JiggleTestFactory.Params(gravityMultiplier: 1f, drag: 0.1f), Gravity);
        harness.job.substeps = 0;
        harness.Step(4);

        JiggleAssert.AreEqual(baseline, harness.Point(2).position, 0f);
    }

    [Test]
    public void IntegrationParameters_ComeFromTheParentPoint() {
        var tree = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f, JiggleTestFactory.Params());
        harness = new JiggleSimHarness(tree, Dt, Gravity);
        harness.SetParameters(1, JiggleTestFactory.Params(gravityMultiplier: 1f));
        harness.SetParameters(2, JiggleTestFactory.Params(gravityMultiplier: 0f));

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, Gravity.y * Dt * Dt, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
        JiggleAssert.AreEqual(new float3(2f, 0f, 0f), harness.Point(3).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void IgnoreRootMotion_CarriesPointsWithTheRootDelta() {
        BuildChain(JiggleTestFactory.Params(ignoreRootMotion: 1f));
        var root = harness.Point(0);
        root.position -= new float3(0f, 1f, 0f);
        root.lastPosition -= new float3(0f, 1f, 0f);
        harness.SetPoint(0, root);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 1f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void IgnoreRootMotionZero_LeavesPointsBehindWhenTheRootMoves() {
        BuildChain(JiggleTestFactory.Params(ignoreRootMotion: 0f));
        var root = harness.Point(0);
        root.position -= new float3(0f, 1f, 0f);
        root.lastPosition -= new float3(0f, 1f, 0f);
        harness.SetPoint(0, root);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void AirDrag_DampsMotionInheritedFromTheParent() {
        BuildChain(JiggleTestFactory.Params(airDrag: 1f));
        var velocity = new float3(0f, 0f, 0.1f);
        SetVelocity(harness, 1, velocity);
        SetVelocity(harness, 2, velocity);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void AirDragZero_PreservesMotionInheritedFromTheParent() {
        BuildChain(JiggleTestFactory.Params(airDrag: 0f));
        var velocity = new float3(0f, 0f, 0.1f);
        SetVelocity(harness, 1, velocity);
        SetVelocity(harness, 2, velocity);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0.1f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void AirDrag_InterpolatesInheritedMotion() {
        BuildChain(JiggleTestFactory.Params(airDrag: 0.25f));
        var velocity = new float3(0f, 0f, 0.1f);
        SetVelocity(harness, 1, velocity);
        SetVelocity(harness, 2, velocity);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0.075f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Drag_DampsMotionRelativeToTheParent() {
        BuildChain(JiggleTestFactory.Params(drag: 1f));
        SetVelocity(harness, 2, new float3(0f, 0f, 0.1f));

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void DragZero_PreservesMotionRelativeToTheParent() {
        BuildChain(JiggleTestFactory.Params(drag: 0f));
        SetVelocity(harness, 2, new float3(0f, 0f, 0.1f));

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0.1f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Drag_DoesNotDampMotionSharedWithTheParent() {
        BuildChain(JiggleTestFactory.Params(drag: 1f));
        var velocity = new float3(0f, 0f, 0.1f);
        SetVelocity(harness, 1, velocity);
        SetVelocity(harness, 2, velocity);

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0.1f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void FinishStep_RotatesPositionIntoLastPosition() {
        BuildChain(JiggleTestFactory.Params(gravityMultiplier: 1f), Gravity);
        var before = harness.Point(2).position;

        harness.Step();

        JiggleAssert.AreEqual(before, harness.Point(2).lastPosition, JiggleTestFactory.Tolerance);
        Assert.AreNotEqual(before.y, harness.Point(2).position.y);
    }
}

}
