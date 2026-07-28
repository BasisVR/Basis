using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace GatorDragonGames.JigglePhysics.Tests {

[TestFixture]
internal class JiggleJobInterpolationTests {
    private NativeArray<PoseData> previous;
    private NativeArray<PoseData> current;
    private NativeArray<JiggleTransform> output;
    private NativeArray<float3> rootPositions;

    [SetUp]
    public void SetUp() {
        previous = new NativeArray<PoseData>(1, Allocator.Persistent);
        current = new NativeArray<PoseData>(1, Allocator.Persistent);
        output = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        rootPositions = new NativeArray<float3>(1, Allocator.Persistent);
    }

    [TearDown]
    public void TearDown() {
        if (previous.IsCreated) previous.Dispose();
        if (current.IsCreated) current.Dispose();
        if (output.IsCreated) output.Dispose();
        if (rootPositions.IsCreated) rootPositions.Dispose();
    }

    private static PoseData Pose(float3 position, float rootSnapStrength = 0f, float3 rootPosition = default, float3 rootOffset = default) {
        return new PoseData {
            pose = new JiggleTransform {
                isVirtual = false, position = position, rotation = quaternion.identity, scale = new float3(1f),
            },
            rootPosition = rootPosition,
            rootOffset = rootOffset,
            rootSnapStrength = rootSnapStrength,
        };
    }

    private JiggleJobInterpolation BuildJob(double previousTimeStamp, double timeStamp, double currentTime, float fixedDeltaTime = 0f) {
        var job = new JiggleJobInterpolation {
            previousPoses = previous,
            currentPoses = current,
            outputInterpolatedPoses = output,
            realRootPositions = rootPositions,
            previousTimeStamp = previousTimeStamp,
            timeStamp = timeStamp,
            currentTime = currentTime,
        };
        job.SetFixedDeltaTime(fixedDeltaTime);
        return job;
    }

    [Test]
    public void EqualTimestamps_SnapToTheCurrentPose() {
        previous[0] = Pose(float3.zero);
        current[0] = Pose(new float3(0f, 5f, 0f));

        BuildJob(1.0, 1.0, 1.0).Execute(0);

        JiggleAssert.AreEqual(new float3(0f, 5f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void MidwayBetweenTimestamps_BlendsHalfway() {
        previous[0] = Pose(float3.zero);
        current[0] = Pose(new float3(0f, 4f, 0f));

        BuildJob(0.0, 1.0, 0.5).Execute(0);

        JiggleAssert.AreEqual(new float3(0f, 2f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void TimeBeforeThePreviousSample_ClampsToThePreviousPose() {
        previous[0] = Pose(float3.zero);
        current[0] = Pose(new float3(0f, 4f, 0f));

        BuildJob(0.0, 1.0, -5.0).Execute(0);

        JiggleAssert.AreEqual(float3.zero, output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void TimeBeyondTheCurrentSample_ClampsToTheCurrentPose() {
        previous[0] = Pose(float3.zero);
        current[0] = Pose(new float3(0f, 4f, 0f));

        BuildJob(0.0, 1.0, 50.0).Execute(0);

        JiggleAssert.AreEqual(new float3(0f, 4f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void TimeCorrection_ShiftsTheSampledPointBackByTwoFixedSteps() {
        previous[0] = Pose(float3.zero);
        current[0] = Pose(new float3(0f, 10f, 0f));

        BuildJob(0.0, 1.0, 1.0, fixedDeltaTime: 0.25f).Execute(0);

        JiggleAssert.AreEqual(new float3(0f, 5f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void RootSnapStrengthZero_LeavesTheInterpolatedPoseAlone() {
        previous[0] = Pose(new float3(0f, 1f, 0f), rootSnapStrength: 0f, rootPosition: float3.zero);
        current[0] = Pose(new float3(0f, 1f, 0f), rootSnapStrength: 0f, rootPosition: float3.zero);
        rootPositions[0] = new float3(100f, 100f, 100f);

        BuildJob(0.0, 1.0, 1.0).Execute(0);

        JiggleAssert.AreEqual(new float3(0f, 1f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void RootSnapStrengthOne_MovesThePoseOntoTheRealRoot() {
        previous[0] = Pose(new float3(0f, 1f, 0f), rootSnapStrength: 1f, rootPosition: float3.zero);
        current[0] = Pose(new float3(0f, 1f, 0f), rootSnapStrength: 1f, rootPosition: float3.zero);
        rootPositions[0] = new float3(3f, 0f, 0f);

        BuildJob(0.0, 1.0, 1.0).Execute(0);

        JiggleAssert.AreEqual(new float3(3f, 1f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void RootOffset_IsAddedAlongsideTheRootSnap() {
        previous[0] = Pose(new float3(0f, 1f, 0f), rootSnapStrength: 1f, rootPosition: float3.zero, rootOffset: new float3(0f, 0f, 2f));
        current[0] = Pose(new float3(0f, 1f, 0f), rootSnapStrength: 1f, rootPosition: float3.zero, rootOffset: new float3(0f, 0f, 2f));
        rootPositions[0] = float3.zero;

        BuildJob(0.0, 1.0, 1.0).Execute(0);

        JiggleAssert.AreEqual(new float3(0f, 1f, 2f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void RootSnap_ScalesWithPartialStrength() {
        previous[0] = Pose(new float3(0f, 1f, 0f), rootSnapStrength: 0.5f, rootPosition: float3.zero);
        current[0] = Pose(new float3(0f, 1f, 0f), rootSnapStrength: 0.5f, rootPosition: float3.zero);
        rootPositions[0] = new float3(4f, 0f, 0f);

        BuildJob(0.0, 1.0, 1.0).Execute(0);

        JiggleAssert.AreEqual(new float3(2f, 1f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Rotation_IsSlerpedBetweenSamples() {
        var a = previous[0];
        a.pose = new JiggleTransform { isVirtual = false, position = float3.zero, rotation = quaternion.identity, scale = new float3(1f) };
        previous[0] = a;
        var b = current[0];
        b.pose = new JiggleTransform {
            isVirtual = false, position = float3.zero,
            rotation = quaternion.RotateY(math.PI * 0.5f), scale = new float3(1f),
        };
        current[0] = b;

        BuildJob(0.0, 1.0, 0.5).Execute(0);

        var expected = math.slerp(quaternion.identity, quaternion.RotateY(math.PI * 0.5f), 0.5f);
        Assert.AreEqual(1f, math.abs(math.dot(expected, output[0].rotation)), 1e-3f);
    }

    [Test]
    public void PoseDataLerp_BlendsEveryChannel() {
        var a = Pose(float3.zero, rootSnapStrength: 0f, rootPosition: float3.zero, rootOffset: float3.zero);
        var b = Pose(new float3(2f, 2f, 2f), rootSnapStrength: 1f, rootPosition: new float3(4f, 0f, 0f), rootOffset: new float3(0f, 6f, 0f));

        var mid = PoseData.Lerp(a, b, 0.5f);

        JiggleAssert.AreEqual(new float3(1f, 1f, 1f), mid.pose.position, JiggleTestFactory.Tolerance);
        JiggleAssert.AreEqual(new float3(2f, 0f, 0f), mid.rootPosition, JiggleTestFactory.Tolerance);
        JiggleAssert.AreEqual(new float3(0f, 3f, 0f), mid.rootOffset, JiggleTestFactory.Tolerance);
        Assert.AreEqual(0.5f, mid.rootSnapStrength, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void JiggleTransformLerp_KeepsTheFirstOperandsVirtualFlag() {
        var a = new JiggleTransform { isVirtual = true, position = float3.zero, rotation = quaternion.identity, scale = new float3(1f) };
        var b = new JiggleTransform { isVirtual = false, position = new float3(2f, 0f, 0f), rotation = quaternion.identity, scale = new float3(3f) };

        var mid = JiggleTransform.Lerp(a, b, 0.5f);

        Assert.IsTrue(mid.isVirtual);
        JiggleAssert.AreEqual(new float3(1f, 0f, 0f), mid.position, JiggleTestFactory.Tolerance);
        JiggleAssert.AreEqual(new float3(2f), mid.scale, JiggleTestFactory.Tolerance);
    }
}

[TestFixture]
internal class JiggleJobInputInterpolationTests {
    private NativeArray<JiggleTransform> previous;
    private NativeArray<JiggleTransform> current;
    private NativeArray<JiggleTransform> output;

    [SetUp]
    public void SetUp() {
        previous = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        current = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
        output = new NativeArray<JiggleTransform>(1, Allocator.Persistent);
    }

    [TearDown]
    public void TearDown() {
        if (previous.IsCreated) previous.Dispose();
        if (current.IsCreated) current.Dispose();
        if (output.IsCreated) output.Dispose();
    }

    private JiggleJobInputInterpolation BuildJob(double previousTimeStamp, double timeStamp, double currentTime) {
        return new JiggleJobInputInterpolation {
            previousInputs = previous,
            currentInputs = current,
            outputInterpolatedPoses = output,
            previousTimeStamp = previousTimeStamp,
            timeStamp = timeStamp,
            currentTime = currentTime,
        };
    }

    [Test]
    public void EqualTimestamps_PassTheCurrentInputStraightThrough() {
        previous[0] = JiggleTestFactory.Pose(float3.zero);
        current[0] = JiggleTestFactory.Pose(new float3(0f, 9f, 0f));

        BuildJob(2.0, 2.0, 2.0).Execute(0);

        JiggleAssert.AreEqual(new float3(0f, 9f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void MidwayBetweenTimestamps_BlendsHalfway() {
        previous[0] = JiggleTestFactory.Pose(float3.zero);
        current[0] = JiggleTestFactory.Pose(new float3(0f, 8f, 0f));

        BuildJob(0.0, 1.0, 0.5).Execute(0);

        JiggleAssert.AreEqual(new float3(0f, 4f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void HighFrameRate_DoesNotExtrapolateBehindThePreviousSample() {
        previous[0] = JiggleTestFactory.Pose(float3.zero);
        current[0] = JiggleTestFactory.Pose(new float3(0f, 10f, 0f));

        BuildJob(0.0, 1.0, -20.0).Execute(0);

        JiggleAssert.AreEqual(float3.zero, output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void LowFrameRate_DoesNotExtrapolatePastTheCurrentSample() {
        previous[0] = JiggleTestFactory.Pose(float3.zero);
        current[0] = JiggleTestFactory.Pose(new float3(0f, 10f, 0f));

        BuildJob(0.0, 1.0, 20.0).Execute(0);

        JiggleAssert.AreEqual(new float3(0f, 10f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void SimulationClock_IsSampledWithoutAFixedStepOffset() {
        previous[0] = JiggleTestFactory.Pose(float3.zero);
        current[0] = JiggleTestFactory.Pose(new float3(0f, 10f, 0f));

        BuildJob(0.0, 1.0, 1.0).Execute(0);

        JiggleAssert.AreEqual(new float3(0f, 10f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void SteadySimCadence_SamplesTheSameFractionOnConsecutiveTicks() {
        const double render = 1.0 / 90.0;
        const double step = 1.0 / 60.0;
        previous[0] = JiggleTestFactory.Pose(float3.zero);
        current[0] = JiggleTestFactory.Pose(new float3(0f, 10f, 0f));

        BuildJob(2 * render, 4 * render, 2 * step).Execute(0);
        JiggleAssert.AreEqual(new float3(0f, 5f, 0f), output[0].position, JiggleTestFactory.Tolerance);

        BuildJob(4 * render, 5 * render, 3 * step).Execute(0);
        JiggleAssert.AreEqual(new float3(0f, 5f, 0f), output[0].position, JiggleTestFactory.Tolerance);
    }

    [Test]
    public void Scale_IsInterpolatedAlongsidePosition() {
        previous[0] = new JiggleTransform { isVirtual = false, position = float3.zero, rotation = quaternion.identity, scale = new float3(1f) };
        current[0] = new JiggleTransform { isVirtual = false, position = float3.zero, rotation = quaternion.identity, scale = new float3(3f) };

        BuildJob(0.0, 1.0, 0.5).Execute(0);

        JiggleAssert.AreEqual(new float3(2f), output[0].scale, JiggleTestFactory.Tolerance);
    }
}

}
