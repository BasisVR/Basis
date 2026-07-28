using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal class JiggleColliderTests {
    [Test]
    public void Read_CapturesThePositionFromTheMatrix() {
        var collider = new JiggleCollider { type = JiggleCollider.JiggleColliderType.Sphere, radius = 1f };

        collider.Read(float4x4.Translate(new float3(3f, 4f, 5f)));

        JiggleAssert.AreEqual(new float3(3f, 4f, 5f), collider.localToWorldMatrix.c3.xyz, 1e-4f);
    }

    [Test]
    public void Read_ScalesRadiusAndHeightByAverageScale() {
        var collider = new JiggleCollider { type = JiggleCollider.JiggleColliderType.Capsule, radius = 1f, height = 2f };

        collider.Read(float4x4.Scale(new float3(2f, 4f, 6f)));

        Assert.AreEqual(4f, collider.worldRadius, 1e-4f);
        Assert.AreEqual(8f, collider.worldHeight, 1e-4f);
    }

    [Test]
    public void Read_ClampsNegativeRadiusAndHeightToZero() {
        var collider = new JiggleCollider { type = JiggleCollider.JiggleColliderType.Capsule, radius = -1f, height = -2f };

        collider.Read(float4x4.identity);

        Assert.AreEqual(0f, collider.worldRadius, 1e-6f);
        Assert.AreEqual(0f, collider.worldHeight, 1e-6f);
    }

    [Test]
    public void Read_AppliesTheLocalOffsetInWorldSpace() {
        var collider = new JiggleCollider {
            type = JiggleCollider.JiggleColliderType.Sphere, radius = 1f, localOffset = new float3(0f, 2f, 0f),
        };

        collider.Read(float4x4.Translate(new float3(1f, 0f, 0f)));

        JiggleAssert.AreEqual(new float3(1f, 2f, 0f), collider.localToWorldMatrix.c3.xyz, 1e-4f);
    }

    [Test]
    public void Read_RotatesTheLocalOffsetWithTheTransform() {
        var collider = new JiggleCollider {
            type = JiggleCollider.JiggleColliderType.Sphere, radius = 1f, localOffset = new float3(0f, 1f, 0f),
        };

        collider.Read(float4x4.TRS(float3.zero, quaternion.RotateZ(math.PI * 0.5f), new float3(1f)));

        JiggleAssert.AreEqual(new float3(-1f, 0f, 0f), collider.localToWorldMatrix.c3.xyz, 1e-3f);
    }

    [Test]
    public void GetWorldAxis_SelectsTheConfiguredColumn() {
        var collider = new JiggleCollider { capsuleAxis = JiggleCollider.CapsuleAxis.X };
        collider.Read(float4x4.identity);
        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), collider.GetWorldAxis(), 1e-4f);

        collider.capsuleAxis = JiggleCollider.CapsuleAxis.Y;
        JiggleAssert.AreEqual(new float3(0f, 1f, 0f), collider.GetWorldAxis(), 1e-4f);

        collider.capsuleAxis = JiggleCollider.CapsuleAxis.Z;
        JiggleAssert.AreEqual(new float3(0f, 0f, 1f), collider.GetWorldAxis(), 1e-4f);
    }

    [Test]
    public void GetWorldAxis_IsNormalisedUnderScale() {
        var collider = new JiggleCollider { capsuleAxis = JiggleCollider.CapsuleAxis.Y };
        collider.Read(float4x4.Scale(new float3(1f, 8f, 1f)));

        Assert.AreEqual(1f, math.length(collider.GetWorldAxis()), 1e-4f);
    }

    [Test]
    public void GetWorldAxis_FallsBackToUpForADegenerateMatrix() {
        var collider = new JiggleCollider { capsuleAxis = JiggleCollider.CapsuleAxis.Y };
        collider.Read(float4x4.Scale(float3.zero));

        JiggleAssert.AreEqual(new float3(0f, 1f, 0f), collider.GetWorldAxis(), 1e-4f);
    }
}

[TestFixture]
internal unsafe class JiggleSimulatedPointTests {
    private static JiggleSimulatedPoint Healthy() {
        return new JiggleSimulatedPoint {
            position = new float3(1f, 0f, 0f),
            lastPosition = new float3(1f, 0f, 0f),
            workingPosition = new float3(1f, 0f, 0f),
            pose = new float3(1f, 0f, 0f),
            parentPose = float3.zero,
            desiredLengthToParent = 1f,
            worldRadius = 0.1f,
            distanceFromRoot = 1f,
            parentIndex = 0,
            childrenCount = 0,
            hasTransform = true,
        };
    }

    [Test]
    public void Sanitize_RecoversNaNPositionToThePose() {
        var point = Healthy();
        point.position = new float3(float.NaN);

        point.Sanitize();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), point.position, 1e-6f);
    }

    [Test]
    public void Sanitize_RecoversInfinitePositionToThePose() {
        var point = Healthy();
        point.position = new float3(float.PositiveInfinity, 0f, 0f);

        point.Sanitize();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), point.position, 1e-6f);
    }

    /// <summary>
    /// Inf and 1e38 floats survive a plain NaN check, so Sanitize also contains finite-but-absurd
    /// positions by distance from the animated pose.
    /// </summary>
    [Test]
    public void Sanitize_ContainsFiniteButRunawayPositions() {
        var point = Healthy();
        point.position = new float3(1e18f, 0f, 0f);

        point.Sanitize();

        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), point.position, 1e-6f);
    }

    [Test]
    public void Sanitize_LeavesPlausiblePositionsAlone() {
        var point = Healthy();
        point.position = new float3(1.5f, 0.5f, 0f);

        point.Sanitize();

        JiggleAssert.AreEqual(new float3(1.5f, 0.5f, 0f), point.position, 1e-6f);
    }

    [Test]
    public void Sanitize_AllowsLongChainsToDeviateFurther() {
        var point = Healthy();
        point.distanceFromRoot = 100f;
        point.position = new float3(1f + 300f, 0f, 0f);

        point.Sanitize();

        JiggleAssert.AreEqual(new float3(301f, 0f, 0f), point.position, 1e-3f);
    }

    [Test]
    public void Sanitize_RepairsNaNPoseToTheOrigin() {
        var point = Healthy();
        point.pose = new float3(float.NaN);

        point.Sanitize();

        JiggleAssert.AreEqual(float3.zero, point.pose, 1e-6f);
    }

    [Test]
    public void Sanitize_RepairsNaNScalars() {
        var point = Healthy();
        point.desiredLengthToParent = float.NaN;
        point.worldRadius = float.NaN;
        point.distanceFromRoot = float.NaN;

        point.Sanitize();

        Assert.IsTrue(math.isfinite(point.desiredLengthToParent));
        Assert.IsTrue(math.isfinite(point.worldRadius));
        Assert.IsTrue(math.isfinite(point.distanceFromRoot));
    }

    [Test]
    public void Sanitize_CollapsesLastPositionOntoPositionWhenCorrupt() {
        var point = Healthy();
        point.lastPosition = new float3(float.NaN);

        point.Sanitize();

        JiggleAssert.AreEqual(point.position, point.lastPosition, 1e-6f);
    }

    [Test]
    public void GetIsValid_AcceptsAHealthyPoint() {
        Assert.IsTrue(Healthy().GetIsValid(4, out _));
    }

    [Test]
    public void GetIsValid_RejectsNaNPosition() {
        var point = Healthy();
        point.position = new float3(float.NaN);

        Assert.IsFalse(point.GetIsValid(4, out var reason));
        StringAssert.Contains("position", reason);
    }

    [Test]
    public void GetIsValid_RejectsAnOutOfRangeParentIndex() {
        var point = Healthy();
        point.parentIndex = 99;

        Assert.IsFalse(point.GetIsValid(4, out var reason));
        StringAssert.Contains("parentIndex", reason);
    }

    [Test]
    public void GetIsValid_AcceptsTheVirtualRootParentIndex() {
        var point = Healthy();
        point.parentIndex = -1;

        Assert.IsTrue(point.GetIsValid(4, out _));
    }

    [Test]
    public void GetIsValid_RejectsAnOutOfRangeChildCount() {
        var point = Healthy();
        point.childrenCount = JiggleSimulatedPoint.MAX_CHILDREN + 1;

        Assert.IsFalse(point.GetIsValid(4, out var reason));
        StringAssert.Contains("childrenCount", reason);
    }

    [Test]
    public void GetIsValid_RejectsAnOutOfRangeChildIndex() {
        var point = Healthy();
        point.childrenCount = 1;
        point.childrenIndices[0] = 99;

        Assert.IsFalse(point.GetIsValid(4, out var reason));
        StringAssert.Contains("childrenIndices", reason);
    }
}

[TestFixture]
internal unsafe class JiggleTreeJobDataTests {
    private JiggleTreeJobData tree;

    [SetUp]
    public void SetUp() {
        var source = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f);
        tree = new JiggleTreeJobData(7, 0, 0, 0, source.points, source.parameters);
    }

    [TearDown]
    public void TearDown() {
        if (tree.points != null) UnsafeUtility.Free(tree.points, Allocator.Persistent);
        if (tree.parameters != null) UnsafeUtility.Free(tree.parameters, Allocator.Persistent);
        tree.points = null;
        tree.parameters = null;
    }

    [Test]
    public void Constructor_CopiesPointsAndParameters() {
        Assert.AreEqual(7, tree.rootID);
        Assert.AreEqual(4u, tree.pointCount);
        JiggleAssert.AreEqual(float3.zero, tree.points[1].pose, 1e-6f);
    }

    [Test]
    public void Translate_MovesEveryPositionChannel() {
        var delta = new float3(0f, 5f, 0f);
        var before = tree.points[1];

        tree.Translate(delta);

        var after = tree.points[1];
        JiggleAssert.AreEqual(before.position + delta, after.position, 1e-5f);
        JiggleAssert.AreEqual(before.lastPosition + delta, after.lastPosition, 1e-5f);
        JiggleAssert.AreEqual(before.workingPosition + delta, after.workingPosition, 1e-5f);
        JiggleAssert.AreEqual(before.pose + delta, after.pose, 1e-5f);
        JiggleAssert.AreEqual(before.parentPose + delta, after.parentPose, 1e-5f);
    }

    [Test]
    public void Translate_PreservesRelativeGeometry() {
        var before = math.distance(tree.points[1].position, tree.points[2].position);

        tree.Translate(new float3(100f, -50f, 25f));

        Assert.AreEqual(before, math.distance(tree.points[1].position, tree.points[2].position), 1e-3f);
    }

    [Test]
    public void GetIsValid_AcceptsAWellFormedTree() {
        Assert.IsTrue(tree.GetIsValid(out _));
    }

    [Test]
    public void GetIsValid_RejectsACorruptPoint() {
        tree.points[2].position = new float3(float.NaN);

        Assert.IsFalse(tree.GetIsValid(out var reason));
        StringAssert.Contains("position", reason);
    }

    [Test]
    public void Sanitize_RepairsEveryPoint() {
        tree.points[1].position = new float3(float.NaN);
        tree.points[2].workingPosition = new float3(float.NaN);

        tree.Sanitize();

        Assert.IsTrue(tree.GetIsValid(out _));
    }

    [Test]
    public void SetParameters_ReplacesTheParameterBuffer() {
        var replacement = new JigglePointParameters[tree.pointCount];
        for (int i = 0; i < replacement.Length; i++) {
            replacement[i] = JiggleTestFactory.Params(gravityMultiplier: 3f);
        }

        tree.SetParameters(replacement);

        Assert.AreEqual(3f, tree.parameters[2].gravityMultiplier, 1e-6f);
    }
}

[TestFixture]
internal unsafe class JiggleTreeStructExtensionsTests {
    [Test]
    public void GetInputPose_AppliesTheTransformIndexOffset() {
        var source = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f);
        var tree = new JiggleTreeJobData(0, 3, 0, 0, source.points, source.parameters);
        var inputs = new NativeArray<JiggleTransform>(8, Allocator.Persistent);
        inputs[5] = JiggleTestFactory.Pose(new float3(9f, 9f, 9f));

        var pose = tree.GetInputPose(inputs, 2);

        JiggleAssert.AreEqual(new float3(9f, 9f, 9f), pose.position, 1e-5f);

        inputs.Dispose();
        UnsafeUtility.Free(tree.points, Allocator.Persistent);
        UnsafeUtility.Free(tree.parameters, Allocator.Persistent);
    }

    [Test]
    public void WriteOutputPose_SkipsSlotsMarkedVirtual() {
        var source = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f);
        var tree = new JiggleTreeJobData(0, 0, 0, 0, source.points, source.parameters);
        var outputs = new NativeArray<PoseData>(4, Allocator.Persistent);
        outputs[1] = new PoseData { pose = new JiggleTransform { isVirtual = true } };

        tree.WriteOutputPose(outputs, 1, JiggleTestFactory.Pose(new float3(5f, 5f, 5f)), float3.zero, float3.zero, 1f);

        JiggleAssert.AreEqual(float3.zero, outputs[1].pose.position, 1e-6f);

        outputs.Dispose();
        UnsafeUtility.Free(tree.points, Allocator.Persistent);
        UnsafeUtility.Free(tree.parameters, Allocator.Persistent);
    }

    [Test]
    public void WriteOutputPose_SanitizesNonFiniteOutput() {
        var source = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f);
        var tree = new JiggleTreeJobData(0, 0, 0, 0, source.points, source.parameters);
        var outputs = new NativeArray<PoseData>(4, Allocator.Persistent);
        var corrupt = new JiggleTransform {
            isVirtual = false,
            position = new float3(float.NaN),
            rotation = new quaternion(float.NaN, float.NaN, float.NaN, float.NaN),
            scale = new float3(float.NaN),
        };

        tree.WriteOutputPose(outputs, 1, corrupt, new float3(float.NaN), new float3(float.NaN), 1f);

        JiggleAssert.IsFinite(outputs[1].pose.position);
        JiggleAssert.IsFinite(outputs[1].pose.rotation);
        JiggleAssert.IsFinite(outputs[1].rootOffset);
        JiggleAssert.IsFinite(outputs[1].rootPosition);

        outputs.Dispose();
        UnsafeUtility.Free(tree.points, Allocator.Persistent);
        UnsafeUtility.Free(tree.parameters, Allocator.Persistent);
    }

    [Test]
    public void WriteOutputPose_ClearsTheVirtualFlagOnRealSlots() {
        var source = JiggleTestTree.Chain(2, float3.zero, new float3(1f, 0f, 0f), 1f);
        var tree = new JiggleTreeJobData(0, 0, 0, 0, source.points, source.parameters);
        var outputs = new NativeArray<PoseData>(4, Allocator.Persistent);

        tree.WriteOutputPose(outputs, 1, JiggleTestFactory.Pose(new float3(2f, 0f, 0f)), new float3(1f, 0f, 0f), new float3(3f, 0f, 0f), 0.5f);

        Assert.IsFalse(outputs[1].pose.isVirtual);
        JiggleAssert.AreEqual(new float3(2f, 0f, 0f), outputs[1].pose.position, 1e-5f);
        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), outputs[1].rootOffset, 1e-5f);
        JiggleAssert.AreEqual(new float3(3f, 0f, 0f), outputs[1].rootPosition, 1e-5f);
        Assert.AreEqual(0.5f, outputs[1].rootSnapStrength, 1e-5f);

        outputs.Dispose();
        UnsafeUtility.Free(tree.points, Allocator.Persistent);
        UnsafeUtility.Free(tree.parameters, Allocator.Persistent);
    }
}

[TestFixture]
internal class JiggleTreeInputParametersTests {
    private static JiggleTreeInputParameters Advanced() {
        var parameters = JiggleTreeInputParameters.Default();
        parameters.advancedToggle = true;
        return parameters;
    }

    [Test]
    public void Default_ProducesUsableValues() {
        var result = JiggleTreeInputParameters.Default().ToJigglePointParameters(0f);

        Assert.AreEqual(0.64f, result.angleElasticity, 1e-4f);
        Assert.AreEqual(1f, result.lengthElasticity, 1e-4f);
        Assert.AreEqual(1f, result.rootElasticity, 1e-4f);
    }

    [Test]
    public void AngleElasticity_IsStiffnessSquared() {
        var parameters = JiggleTreeInputParameters.Default();
        parameters.stiffness = new JiggleTreeCurvedFloat(0.5f);

        Assert.AreEqual(0.25f, parameters.ToJigglePointParameters(0f).angleElasticity, 1e-4f);
    }

    [Test]
    public void NonAdvanced_IgnoresStretchAndSoften() {
        var parameters = JiggleTreeInputParameters.Default();
        parameters.advancedToggle = false;
        parameters.stretch = new JiggleTreeCurvedFloat(0.9f);
        parameters.soften = 0.5f;
        parameters.rootStretch = 0.8f;
        parameters.ignoreRootMotion = 0.7f;

        var result = parameters.ToJigglePointParameters(0f);

        Assert.AreEqual(1f, result.lengthElasticity, 1e-4f);
        Assert.AreEqual(0f, result.elasticitySoften, 1e-4f);
        Assert.AreEqual(1f, result.rootElasticity, 1e-4f);
        Assert.AreEqual(0f, result.ignoreRootMotion, 1e-4f);
    }

    [Test]
    public void Advanced_MapsStretchToLengthElasticity() {
        var parameters = Advanced();
        parameters.stretch = new JiggleTreeCurvedFloat(0.25f);

        Assert.AreEqual(0.5625f, parameters.ToJigglePointParameters(0f).lengthElasticity, 1e-4f);
    }

    [Test]
    public void Advanced_MapsRootStretchToRootElasticity() {
        var parameters = Advanced();
        parameters.rootStretch = 0.25f;

        Assert.AreEqual(0.75f, parameters.ToJigglePointParameters(0f).rootElasticity, 1e-4f);
    }

    [Test]
    public void Advanced_SquaresSoften() {
        var parameters = Advanced();
        parameters.soften = 0.5f;

        Assert.AreEqual(0.25f, parameters.ToJigglePointParameters(0f).elasticitySoften, 1e-4f);
    }

    [Test]
    public void CollisionRadius_RequiresBothCollisionAndAdvancedToggles() {
        var parameters = Advanced();
        parameters.collisionRadius = new JiggleTreeCurvedFloat(0.3f);

        parameters.collisionToggle = false;
        Assert.AreEqual(0f, parameters.ToJigglePointParameters(0f).collisionRadius, 1e-4f);

        parameters.collisionToggle = true;
        Assert.AreEqual(0.3f, parameters.ToJigglePointParameters(0f).collisionRadius, 1e-4f);

        parameters.advancedToggle = false;
        Assert.AreEqual(0f, parameters.ToJigglePointParameters(0f).collisionRadius, 1e-4f);
    }

    [Test]
    public void AngleLimit_RequiresItsToggle() {
        var parameters = JiggleTreeInputParameters.Default();
        parameters.angleLimit = new JiggleTreeCurvedFloat(0.4f);

        parameters.angleLimitToggle = false;
        var off = parameters.ToJigglePointParameters(0f);
        Assert.IsFalse(off.angleLimited);
        Assert.AreEqual(0f, off.angleLimit, 1e-4f);

        parameters.angleLimitToggle = true;
        var on = parameters.ToJigglePointParameters(0f);
        Assert.IsTrue(on.angleLimited);
        Assert.AreEqual(0.4f, on.angleLimit, 1e-4f);
    }

    [Test]
    public void CurvedFloat_EvaluatesAlongTheChainWhenEnabled() {
        var curved = new JiggleTreeCurvedFloat(1f) {
            curveEnabled = true,
            curve = AnimationCurve.Linear(0f, 0f, 1f, 1f),
        };

        Assert.AreEqual(0f, curved.Evaluate(0f), 1e-4f);
        Assert.AreEqual(0.5f, curved.Evaluate(0.5f), 1e-4f);
        Assert.AreEqual(1f, curved.Evaluate(1f), 1e-4f);
    }

    [Test]
    public void CurvedFloat_IgnoresTheCurveWhenDisabled() {
        var curved = new JiggleTreeCurvedFloat(0.75f) {
            curveEnabled = false,
            curve = AnimationCurve.Linear(0f, 0f, 1f, 1f),
        };

        Assert.AreEqual(0.75f, curved.Evaluate(0f), 1e-4f);
        Assert.AreEqual(0.75f, curved.Evaluate(1f), 1e-4f);
    }

    [Test]
    public void NormalizedDistance_IsClampedBeforeEvaluation() {
        var parameters = JiggleTreeInputParameters.Default();
        parameters.stiffness = new JiggleTreeCurvedFloat(1f) {
            curveEnabled = true,
            curve = AnimationCurve.Linear(0f, 0f, 1f, 1f),
        };

        Assert.AreEqual(0f, parameters.ToJigglePointParameters(-5f).angleElasticity, 1e-4f);
        Assert.AreEqual(1f, parameters.ToJigglePointParameters(5f).angleElasticity, 1e-4f);
    }

    [Test]
    public void OnValidate_ClampsEveryNormalisedField() {
        var parameters = JiggleTreeInputParameters.Default();
        parameters.stiffness = new JiggleTreeCurvedFloat(5f);
        parameters.drag = new JiggleTreeCurvedFloat(-1f);
        parameters.collisionRadius = new JiggleTreeCurvedFloat(-2f);
        parameters.rootStretch = 3f;
        parameters.soften = -1f;

        parameters.OnValidate();

        Assert.AreEqual(1f, parameters.stiffness.value, 1e-4f);
        Assert.AreEqual(0f, parameters.drag.value, 1e-4f);
        Assert.AreEqual(0f, parameters.collisionRadius.value, 1e-4f);
        Assert.AreEqual(1f, parameters.rootStretch, 1e-4f);
        Assert.AreEqual(0f, parameters.soften, 1e-4f);
    }

    /// <summary>
    /// Every shipped preset serialised its retired input blend as 0, so the runtime blend must stay
    /// pinned at 1. Sourcing it from the asset would slerp each correction back to the animated pose
    /// and silently disable jiggle on every one of them.
    /// </summary>
    [Test]
    public void Blend_IsAlwaysFullyApplied() {
        var parameters = JiggleTreeInputParameters.Default();

        Assert.AreEqual(1f, parameters.ToJigglePointParameters(0f).blend, 1e-6f);
        Assert.AreEqual(1f, parameters.ToJigglePointParameters(1f).blend, 1e-6f);

        parameters.advancedToggle = true;
        Assert.AreEqual(1f, parameters.ToJigglePointParameters(0.5f).blend, 1e-6f);
    }
}

}
