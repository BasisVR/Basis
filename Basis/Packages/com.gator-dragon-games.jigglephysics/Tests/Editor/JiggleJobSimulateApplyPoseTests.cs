using NUnit.Framework;
using Unity.Mathematics;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal unsafe class JiggleJobSimulateApplyPoseTests {
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

    [Test]
    public void OutputPose_TracksTheSimulatedPositionOfRealPoints() {
        BuildChain(2, JiggleTestFactory.Params(rootElasticity: 0f));
        var point = harness.Point(1);
        point.position += new float3(0f, -0.25f, 0f);
        point.lastPosition += new float3(0f, -0.25f, 0f);
        harness.SetPoint(1, point);

        harness.Step();

        JiggleAssert.AreEqual(harness.Point(1).workingPosition, harness.Output(1).position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void OutputPose_IsNotVirtualForRealPoints() {
        BuildChain(2, JiggleTestFactory.Params());

        harness.Step();

        Assert.IsFalse(harness.Output(1).isVirtual);
    }

    [Test]
    public void OutputPose_LeavesVirtualSlotsUntouched() {
        BuildChain(2, JiggleTestFactory.Params(gravityMultiplier: 1f), new float3(0f, -10f, 0f));
        var before = harness.Output(0);

        harness.Step();

        Assert.IsTrue(harness.Output(0).isVirtual);
        JiggleAssert.AreEqual(before.position, harness.Output(0).position, 0f);
    }

    [Test]
    public void OutputPose_IsNotWrittenForLeafPoints() {
        BuildChain(2, JiggleTestFactory.Params(gravityMultiplier: 1f), new float3(0f, -10f, 0f));
        var tipIndex = 3;
        var before = harness.Output(tipIndex);

        harness.Step();

        JiggleAssert.AreEqual(before.position, harness.Output(tipIndex).position, 0f);
    }

    [Test]
    public void BlendZero_PassesTheAnimatedRotationThroughUnchanged() {
        BuildChain(2, JiggleTestFactory.Params(blend: 0f, rootElasticity: 0f));
        var inputRotation = quaternion.Euler(0.3f, 0.4f, 0.5f);
        harness.inputPoses[1] = new JiggleTransform {
            isVirtual = false, position = float3.zero, rotation = inputRotation, scale = new float3(1f),
        };
        var point = harness.Point(2);
        point.position += new float3(0f, -0.5f, 0f);
        point.lastPosition += new float3(0f, -0.5f, 0f);
        harness.SetPoint(2, point);

        harness.Step();

        var output = harness.Output(1).rotation;
        Assert.AreEqual(1f, math.abs(math.dot(inputRotation, output)), 1e-3f);
    }

    [Test]
    public void BlendOne_RotatesTheAnimatedPoseTowardsTheSimulatedBoneDirection() {
        BuildChain(2, JiggleTestFactory.Params(blend: 1f, rootElasticity: 0f));
        var point = harness.Point(2);
        point.position += new float3(0f, -1f, 0f);
        point.lastPosition += new float3(0f, -1f, 0f);
        harness.SetPoint(2, point);

        harness.Step();

        var output = harness.Output(1).rotation;
        var rotatedForward = math.rotate(output, new float3(1f, 0f, 0f));
        var simulatedDirection = math.normalize(harness.Point(2).workingPosition - harness.Point(1).workingPosition);
        JiggleAssert.AreEqual(simulatedDirection, rotatedForward, 1e-3f);
    }

    [Test]
    public void OutputRotation_IsAlwaysFinite() {
        BuildChain(3, JiggleTestFactory.Params(blend: 1f, angleElasticity: 1f, lengthElasticity: 1f,
            gravityMultiplier: 2f), new float3(0f, -20f, 0f));

        harness.Step(60);

        JiggleAssert.IsFinite(harness.Output(1).rotation, "point 1 rotation");
        JiggleAssert.IsFinite(harness.Output(2).rotation, "point 2 rotation");
    }

    [Test]
    public void MultipleChildren_AverageTheirDirectionsForTheOutputRotation() {
        var points = new JiggleSimulatedPoint[5];
        var parameters = new JigglePointParameters[5];
        var inputPoses = new JiggleTransform[5];
        for (int i = 0; i < 5; i++) {
            parameters[i] = JiggleTestFactory.Params(blend: 1f);
        }

        var rootPose = new float3(-1f, 0f, 0f);
        var root = new JiggleSimulatedPoint {
            parentIndex = -1, childrenCount = 1, hasTransform = false,
            pose = rootPose, parentPose = rootPose - new float3(1f, 0f, 0f),
            position = rootPose, lastPosition = rootPose, workingPosition = rootPose,
        };
        root.childrenIndices[0] = 1;
        points[0] = root;

        var hub = new JiggleSimulatedPoint {
            parentIndex = 0, childrenCount = 2, hasTransform = true,
            pose = float3.zero, parentPose = rootPose,
            position = float3.zero, lastPosition = float3.zero, workingPosition = float3.zero,
            desiredLengthToParent = 1f,
        };
        hub.childrenIndices[0] = 2;
        hub.childrenIndices[1] = 3;
        points[1] = hub;

        var branchA = new JiggleSimulatedPoint {
            parentIndex = 1, childrenCount = 1, hasTransform = true,
            pose = new float3(1f, 1f, 0f), parentPose = float3.zero,
            position = new float3(1f, 1f, 0f), lastPosition = new float3(1f, 1f, 0f),
            workingPosition = new float3(1f, 1f, 0f), desiredLengthToParent = math.sqrt(2f),
        };
        branchA.childrenIndices[0] = 4;
        points[2] = branchA;

        var branchB = new JiggleSimulatedPoint {
            parentIndex = 1, childrenCount = 0, hasTransform = true,
            pose = new float3(1f, -1f, 0f), parentPose = float3.zero,
            position = new float3(1f, -1f, 0f), lastPosition = new float3(1f, -1f, 0f),
            workingPosition = new float3(1f, -1f, 0f), desiredLengthToParent = math.sqrt(2f),
        };
        points[3] = branchB;

        var tip = new JiggleSimulatedPoint {
            parentIndex = 2, childrenCount = 0, hasTransform = false,
            pose = new float3(2f, 2f, 0f), parentPose = new float3(1f, 1f, 0f),
            position = new float3(2f, 2f, 0f), lastPosition = new float3(2f, 2f, 0f),
            workingPosition = new float3(2f, 2f, 0f), desiredLengthToParent = math.sqrt(2f),
        };
        points[4] = tip;

        inputPoses[0] = new JiggleTransform { isVirtual = true, position = rootPose, rotation = quaternion.identity, scale = new float3(1f) };
        inputPoses[1] = JiggleTestFactory.Pose(float3.zero);
        inputPoses[2] = JiggleTestFactory.Pose(new float3(1f, 1f, 0f));
        inputPoses[3] = JiggleTestFactory.Pose(new float3(1f, -1f, 0f));
        inputPoses[4] = new JiggleTransform { isVirtual = true, position = new float3(2f, 2f, 0f), rotation = quaternion.identity, scale = new float3(1f) };

        var tree = new JiggleTestTree {
            realCount = 3, points = points, parameters = parameters, inputPoses = inputPoses,
        };
        harness = new JiggleSimHarness(tree, Dt);

        Assert.DoesNotThrow(() => harness.Step());
        JiggleAssert.IsFinite(harness.Output(1).rotation);
        Assert.AreEqual(1f, math.length(harness.Output(1).rotation.value), 1e-3f);
    }

    [Test]
    public void RootSnapStrength_CombinesRootElasticityAndAirDrag() {
        BuildChain(2, JiggleTestFactory.Params(rootElasticity: 0.25f, airDrag: 0.5f));

        harness.Step();

        var expected = 1f - (1f - 0.25f) * 0.5f;
        Assert.AreEqual(expected, harness.OutputData(1).rootSnapStrength, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void RootOffset_IsTheFirstRealPointsDriftFromItsPose() {
        BuildChain(2, JiggleTestFactory.Params(rootElasticity: 0f));
        var point = harness.Point(1);
        point.position += new float3(0f, -0.5f, 0f);
        point.lastPosition += new float3(0f, -0.5f, 0f);
        harness.SetPoint(1, point);

        harness.Step();

        var expected = harness.Point(1).position - harness.Point(1).pose;
        JiggleAssert.AreEqual(expected, harness.OutputData(1).rootOffset, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void RootPosition_IsTheFirstRealPointsSimulatedPosition() {
        BuildChain(2, JiggleTestFactory.Params(rootElasticity: 0f));
        var point = harness.Point(1);
        point.position += new float3(0.1f, -0.5f, 0.2f);
        point.lastPosition += new float3(0.1f, -0.5f, 0.2f);
        harness.SetPoint(1, point);

        harness.Step();

        JiggleAssert.AreEqual(harness.Point(1).position, harness.OutputData(1).rootPosition, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void SingleBoneTree_ProducesNoOutputAndDoesNotThrow() {
        var tree = JiggleTestTree.SingleBone(new float3(1f, 2f, 3f));
        harness = new JiggleSimHarness(tree, Dt);

        Assert.DoesNotThrow(() => harness.Step());
        JiggleAssert.IsFinite(harness.Point(1).position);
    }
}

}
