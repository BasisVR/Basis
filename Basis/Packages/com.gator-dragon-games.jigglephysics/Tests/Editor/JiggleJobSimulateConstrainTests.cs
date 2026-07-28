using NUnit.Framework;
using Unity.Mathematics;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal unsafe class JiggleJobSimulateConstrainTests {
    private const float Dt = 0.02f;

    private JiggleSimHarness harness;

    [TearDown]
    public void TearDown() {
        harness?.Dispose();
        harness = null;
    }

    private JiggleSimHarness BuildChain(int realCount, JigglePointParameters parameters, float3 gravity = default) {
        var tree = JiggleTestTree.Chain(realCount, float3.zero, new float3(1f, 0f, 0f), 1f, parameters);
        harness = new JiggleSimHarness(tree, Dt, gravity);
        return harness;
    }

    private static void Displace(JiggleSimHarness harness, int index, float3 offset) {
        var point = harness.Point(index);
        point.position += offset;
        point.lastPosition += offset;
        harness.SetPoint(index, point);
    }

    [Test]
    public void RootElasticityOne_PinsTheFirstRealPointToItsPose() {
        BuildChain(2, JiggleTestFactory.Params(rootElasticity: 1f));
        Displace(harness, 1, new float3(0f, -0.5f, 0f));

        harness.Step();

        JiggleAssert.AreEqual(float3.zero, harness.Point(1).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void RootElasticityZero_LeavesTheFirstRealPointWhereIntegrationPutIt() {
        BuildChain(2, JiggleTestFactory.Params(rootElasticity: 0f));
        Displace(harness, 1, new float3(0f, -0.5f, 0f));

        harness.Step();

        JiggleAssert.AreEqual(new float3(0f, -0.5f, 0f), harness.Point(1).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void RootElasticity_LerpsBySquaredStrength() {
        BuildChain(2, JiggleTestFactory.Params(rootElasticity: 0.5f));
        Displace(harness, 1, new float3(0f, -1f, 0f));

        harness.Step();

        JiggleAssert.AreEqual(new float3(0f, -0.75f, 0f), harness.Point(1).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void VirtualRoot_TracksTheFirstRealPointByTheAnimatedBoneVector() {
        BuildChain(2, JiggleTestFactory.Params(rootElasticity: 0f));
        Displace(harness, 1, new float3(0f, -0.5f, 0f));

        harness.Step();

        var expected = harness.Point(1).position + (harness.Point(1).pose - harness.Point(2).pose);
        JiggleAssert.AreEqual(expected, harness.Point(0).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void LengthElasticityOne_RestoresTheRestLength() {
        BuildChain(2, JiggleTestFactory.Params(lengthElasticity: 1f));
        Displace(harness, 2, new float3(2f, 0f, 0f));

        harness.Step();

        var distance = math.distance(harness.Point(2).position, harness.Point(1).position);
        Assert.AreEqual(1f, distance, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void LengthElasticityZero_AllowsUnlimitedStretch() {
        BuildChain(2, JiggleTestFactory.Params(lengthElasticity: 0f));
        Displace(harness, 2, new float3(2f, 0f, 0f));

        harness.Step();

        var distance = math.distance(harness.Point(2).position, harness.Point(1).position);
        Assert.AreEqual(3f, distance, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void LengthConstraint_PullsInwardsAndPushesOutwardsAlongTheSameAxis() {
        BuildChain(2, JiggleTestFactory.Params(lengthElasticity: 1f));
        Displace(harness, 2, new float3(-0.5f, 0f, 0f));

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void AngleElasticityOne_ReturnsADeflectedPointToItsPosedDirection() {
        BuildChain(2, JiggleTestFactory.Params(angleElasticity: 1f));
        Displace(harness, 2, new float3(0f, -1f, 0f));

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void AngleElasticityZero_LeavesDeflectionUntouched() {
        BuildChain(2, JiggleTestFactory.Params(angleElasticity: 0f));
        Displace(harness, 2, new float3(0f, -1f, 0f));

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, -1f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void AngleConstraint_MovesPartwayForIntermediateElasticity() {
        BuildChain(2, JiggleTestFactory.Params(angleElasticity: 0.5f));
        Displace(harness, 2, new float3(0f, -1f, 0f));

        harness.Step();

        var settled = harness.Point(2).position;
        Assert.Greater(settled.y, -1f);
        Assert.Less(settled.y, 0f);
    }

    [Test]
    public void ElasticitySoften_WeakensCorrectionForSmallErrors() {
        BuildChain(2, JiggleTestFactory.Params(angleElasticity: 1f, elasticitySoften: 4f));
        Displace(harness, 2, new float3(0f, -0.1f, 0f));
        harness.Step();
        var softened = harness.Point(2).position.y;

        harness.Dispose();
        BuildChain(2, JiggleTestFactory.Params(angleElasticity: 1f, elasticitySoften: 0f));
        Displace(harness, 2, new float3(0f, -0.1f, 0f));
        harness.Step();
        var unsoftened = harness.Point(2).position.y;

        Assert.Less(softened, unsoftened);
    }

    /// <summary>
    /// Substeps re-solve the constraint because they re-run the whole step, not because the solve is
    /// iterated in place. The old code looped the lerp instead, which stiffened the chain rather than
    /// advancing it, so falling behind changed how the rig felt.
    /// </summary>
    [Test]
    public void Substeps_ConvergeTowardsThePoseLikeRepeatedFrames() {
        BuildChain(2, JiggleTestFactory.Params(angleElasticity: 0.5f));
        Displace(harness, 2, new float3(0f, -1f, 0f));
        harness.Step(4);
        var fourFrames = harness.Point(2).position;

        harness.Dispose();
        BuildChain(2, JiggleTestFactory.Params(angleElasticity: 0.5f));
        harness.job.substeps = 4;
        Displace(harness, 2, new float3(0f, -1f, 0f));
        harness.Step();
        var oneFrameFourSubsteps = harness.Point(2).position;

        JiggleAssert.AreEqual(fourFrames, oneFrameFourSubsteps, 1e-5f);
    }

    [Test]
    public void AngleLimit_ClampsDeflectionBeyondTheAllowedCone() {
        BuildChain(2, JiggleTestFactory.Params(angleLimited: true, angleLimit: 0.1f));
        Displace(harness, 2, new float3(0f, -1f, 0f));

        harness.Step();

        var limited = harness.Point(2).position;
        var direction = math.normalize(limited - harness.Point(1).position);
        var angle = math.acos(math.clamp(math.dot(direction, new float3(1f, 0f, 0f)), -1f, 1f));
        Assert.Less(angle, math.PI * 0.5f * 0.1f + 0.05f);
    }

    [Test]
    public void AngleLimitDisabled_LeavesLargeDeflectionsAlone() {
        BuildChain(2, JiggleTestFactory.Params(angleLimited: false, angleLimit: 0.1f));
        Displace(harness, 2, new float3(0f, -1f, 0f));

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, -1f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void AngleLimit_LeavesDeflectionInsideTheConeAlone() {
        BuildChain(2, JiggleTestFactory.Params(angleLimited: true, angleLimit: 1f));
        Displace(harness, 2, new float3(0f, -0.05f, 0f));

        harness.Step();

        JiggleAssert.AreEqual(new float3(1f, -0.05f, 0f), harness.Point(2).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void AngleLimitSoften_ReducesTheClampStrength() {
        BuildChain(2, JiggleTestFactory.Params(angleLimited: true, angleLimit: 0.1f, angleLimitSoften: 0f));
        Displace(harness, 2, new float3(0f, -1f, 0f));
        harness.Step();
        var hard = harness.Point(2).position.y;

        harness.Dispose();
        BuildChain(2, JiggleTestFactory.Params(angleLimited: true, angleLimit: 0.1f, angleLimitSoften: 1f));
        Displace(harness, 2, new float3(0f, -1f, 0f));
        harness.Step();
        var soft = harness.Point(2).position.y;

        Assert.Less(soft, hard);
    }

    /// <summary>
    /// Back-propagation reads the point's own parameters while the angle constraint that follows it
    /// reads the parent's, so the parent is left slack to keep it from immediately undoing the pull.
    /// </summary>
    [Test]
    public void BackPropagation_MovesAMidChainPointWhenItsChildIsReal() {
        BuildChain(3, JiggleTestFactory.Params());
        harness.SetParameters(1, JiggleTestFactory.Params(angleElasticity: 0f, lengthElasticity: 0f));
        harness.SetParameters(2, JiggleTestFactory.Params(angleElasticity: 1f, lengthElasticity: 1f));
        Displace(harness, 3, new float3(0f, -1f, 0f));

        harness.Step();

        Assert.Less(harness.Point(2).position.y, 0f);
    }

    [Test]
    public void BackPropagation_IsGatedByThePointsOwnAngleElasticity() {
        BuildChain(3, JiggleTestFactory.Params());
        harness.SetParameters(1, JiggleTestFactory.Params(angleElasticity: 0f, lengthElasticity: 0f));
        harness.SetParameters(2, JiggleTestFactory.Params(angleElasticity: 0f, lengthElasticity: 1f));
        Displace(harness, 3, new float3(0f, -1f, 0f));

        harness.Step();

        Assert.AreEqual(0f, harness.Point(2).position.y, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void BackPropagation_IsSkippedWhenTheChildIsVirtual() {
        BuildChain(2, JiggleTestFactory.Params(angleElasticity: 1f, lengthElasticity: 1f));
        Displace(harness, 2, new float3(0f, -1f, 0f));

        harness.Step();

        JiggleAssert.AreEqual(float3.zero, harness.Point(1).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Simulation_StaysAtRestOverManySteps() {
        BuildChain(3, JiggleTestFactory.Params(angleElasticity: 0.64f, lengthElasticity: 1f, drag: 0.1f));

        harness.Step(120);

        JiggleAssert.AreEqual(float3.zero, harness.Point(1).position, 1e-3f);
        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), harness.Point(2).position, 1e-3f);
        JiggleAssert.AreEqual(new float3(2f, 0f, 0f), harness.Point(3).position, 1e-3f);
    }

    [Test]
    public void Simulation_SettlesBackToPoseAfterADisturbance() {
        BuildChain(3, JiggleTestFactory.Params(angleElasticity: 0.64f, lengthElasticity: 1f, drag: 0.4f, airDrag: 0.4f));
        Displace(harness, 3, new float3(0f, -0.5f, 0f));

        harness.Step(400);

        JiggleAssert.AreEqual(new float3(3f, 0f, 0f), harness.Point(4).position, 1e-2f);
    }

    [Test]
    public void Simulation_StaysFiniteUnderAggressiveParameters() {
        BuildChain(4, JiggleTestFactory.Params(
            rootElasticity: 0.9f, angleElasticity: 1f, lengthElasticity: 1f,
            gravityMultiplier: 4f, drag: 0f, airDrag: 0f,
            angleLimited: true, angleLimit: 0.05f, collisionRadius: 0.3f),
            new float3(0f, -30f, 0f));

        harness.Step(300);

        for (int i = 0; i < harness.Tree.pointCount; i++) {
            JiggleAssert.IsFinite(harness.Point(i).position, $"point {i} position");
            JiggleAssert.IsFinite(harness.Point(i).lastPosition, $"point {i} lastPosition");
            JiggleAssert.IsFinite(harness.Point(i).workingPosition, $"point {i} workingPosition");
        }
    }

    [Test]
    public void Simulation_IsDeterministic() {
        BuildChain(3, JiggleTestFactory.Params(angleElasticity: 0.5f, lengthElasticity: 0.8f, gravityMultiplier: 1f, drag: 0.1f),
            new float3(0f, -10f, 0f));
        harness.Step(50);
        var first = harness.Point(3).position;

        harness.Dispose();
        BuildChain(3, JiggleTestFactory.Params(angleElasticity: 0.5f, lengthElasticity: 0.8f, gravityMultiplier: 1f, drag: 0.1f),
            new float3(0f, -10f, 0f));
        harness.Step(50);
        var second = harness.Point(3).position;

        JiggleAssert.AreEqual(first, second, 0f);
    }
}

}
